// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Provides information from a loaded ApiSchema.json document
/// </summary>
public class OpenApiDocument(ILogger _logger, string[]? excludedDomains = null)
{
    private readonly string[] _excludedDomains = excludedDomains ?? [];

    /// <summary>
    /// Determines if a path should be excluded based on the excluded domains configuration
    /// </summary>
    private bool ShouldExcludePath(JsonNode pathValue)
    {
        if (_excludedDomains.Length == 0)
        {
            return false;
        }

        // Check if the path has the x-Ed-Fi-domains extension property
        if (pathValue["x-Ed-Fi-domains"] is JsonArray domainsArray)
        {
            var domainsList = domainsArray
                .Where(node =>
                    node != null
                    && node.AsValue().TryGetValue(out string? domainName)
                    && !string.IsNullOrWhiteSpace(domainName)
                )
                .Select(node => node?.GetValue<string>())
                .ToList();

            // If there are valid domains and all of them are in the excluded list, exclude the path
            if (
                domainsList.Count > 0
                && domainsList.TrueForAll(d => _excludedDomains.Contains(d, StringComparer.OrdinalIgnoreCase))
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Filters out paths from a JsonObject based on excluded domains
    /// </summary>
    private void FilterPathsByDomain(JsonObject paths)
    {
        if (_excludedDomains.Length == 0)
        {
            return;
        }

        var pathsToRemove = paths
            .Where(kvp => kvp.Value != null && ShouldExcludePath(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var pathToRemove in pathsToRemove)
        {
            paths.Remove(pathToRemove);
            _logger.LogDebug(
                "Excluded path '{Path}' from OpenAPI specification due to domain filtering",
                pathToRemove
            );
        }
    }

    /// <summary>
    /// Removes tags that are not referenced by any paths in the OpenAPI specification
    /// </summary>
    private void RemoveUnusedTags(JsonNode openApiSpecification)
    {
        if (
            openApiSpecification["tags"] is not JsonArray tags
            || openApiSpecification["paths"] is not JsonObject paths
        )
        {
            return;
        }

        // Collect all tag names used in paths
        HashSet<string> usedTagNames = [];

        foreach ((string pathKey, JsonNode? pathValue) in paths)
        {
            if (pathValue is not JsonObject pathObject)
            {
                continue;
            }

            // Check each HTTP method in the path
            foreach ((string methodKey, JsonNode? methodValue) in pathObject)
            {
                if (
                    methodValue is not JsonObject methodObject
                    || methodObject["tags"] is not JsonArray pathTags
                )
                {
                    continue;
                }

                // Add all tag names from this path's tags array
                foreach (JsonNode? tag in pathTags)
                {
                    if (
                        tag != null
                        && tag.AsValue().TryGetValue(out string? tagName)
                        && !string.IsNullOrWhiteSpace(tagName)
                    )
                    {
                        usedTagNames.Add(tagName);
                    }
                }
            }
        }

        // Remove unused tags
        var tagsToRemove = new List<int>();
        for (int i = 0; i < tags.Count; i++)
        {
            if (
                tags[i] is JsonObject tagObject
                && tagObject["name"]?.GetValue<string>() is string tagName
                && !usedTagNames.Contains(tagName)
            )
            {
                tagsToRemove.Add(i);
                _logger.LogDebug("Removed unused tag '{TagName}' from OpenAPI specification", tagName);
            }
        }

        for (int i = tagsToRemove.Count - 1; i >= 0; i--)
        {
            tags.RemoveAt(tagsToRemove[i]);
        }
    }

    /// <summary>
    /// Inserts exts from extension OpenAPI fragments into the _ext section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertExts(
        JsonObject openApiExtensionFragmentList,
        JsonNode openApiCoreResources,
        string projectName
    )
    {
        var extensionContext = CreateExtensionContext(openApiCoreResources);

        foreach ((string componentSchemaName, JsonNode? extObject) in openApiExtensionFragmentList)
        {
            ProcessExtensionComponent(componentSchemaName, extObject, extensionContext, projectName);
        }
    }

    /// <summary>
    /// Creates the extension processing context
    /// </summary>
    private ExtensionContext CreateExtensionContext(JsonNode openApiCoreResources)
    {
        return new ExtensionContext(
            openApiCoreResources,
            openApiCoreResources.SelectNodeFromPath("$.components.schemas", _logger)?.AsObject()
                ?? throw new InvalidOperationException(
                    "OpenAPI core resources missing 'components.schemas'."
                ),
            _logger
        );
    }

    /// <summary>
    /// Processes a single extension component
    /// </summary>
    private void ProcessExtensionComponent(
        string componentSchemaName,
        JsonNode? extObject,
        ExtensionContext context,
        string projectName
    )
    {
        var validatedExtension = ValidateExtensionObject(componentSchemaName, extObject);
        var schemaLocation = GetSchemaLocation(componentSchemaName, context);

        if (validatedExtension.ExtensionProperties == null)
        {
            ProcessDirectExtensionSchema(
                validatedExtension.ExtensionObject,
                componentSchemaName,
                projectName,
                schemaLocation,
                context.ComponentsSchemas
            );
            return;
        }

        ProcessPropertyBasedExtension(
            validatedExtension,
            componentSchemaName,
            schemaLocation,
            context,
            projectName
        );
    }

    /// <summary>
    /// Validates and extracts extension object information
    /// </summary>
    private ValidatedExtension ValidateExtensionObject(string componentSchemaName, JsonNode? extObject)
    {
        if (extObject == null)
        {
            throw new InvalidOperationException(
                $"OpenAPI extension fragment has empty exts schema name '{componentSchemaName}'. Extension fragment validation failed?"
            );
        }

        if (extObject is not JsonObject extObjectAsObject)
        {
            _logger.LogError(
                "Extension object for '{ComponentSchemaName}' is not a JsonObject. Type: {ExtObjectType}, Value: {ExtObjectValue}",
                componentSchemaName,
                extObject.GetType().Name,
                extObject.ToString()
            );
            throw new InvalidOperationException(
                $"Extension object for '{componentSchemaName}' is not a JsonObject"
            );
        }

        return new ValidatedExtension(extObjectAsObject, extObjectAsObject["properties"]?.AsObject());
    }

    /// <summary>
    /// Gets the schema location for extension processing
    /// </summary>
    private static JsonObject GetSchemaLocation(string componentSchemaName, ExtensionContext context)
    {
        return context
                .OpenApiCoreResources.SelectNodeFromPath(
                    $"$.components.schemas.{componentSchemaName}.properties",
                    context.Logger
                )
                ?.AsObject()
            ?? throw new InvalidOperationException(
                $"OpenAPI extension fragment expects Core to have '$.components.schemas.{componentSchemaName}.properties'. Extension fragment validation failed?"
            );
    }

    /// <summary>
    /// Processes property-based extensions with conflict analysis
    /// </summary>
    private void ProcessPropertyBasedExtension(
        ValidatedExtension validatedExtension,
        string componentSchemaName,
        JsonObject schemaLocation,
        ExtensionContext context,
        string projectName
    )
    {
        var conflictAnalysis = AnalyzePropertyConflicts(
            validatedExtension.ExtensionProperties!,
            schemaLocation
        );

        CreateMainExtensionIfNeeded(
            validatedExtension.ExtensionObject,
            componentSchemaName,
            conflictAnalysis.NonConflictingProperties,
            schemaLocation,
            context.ComponentsSchemas,
            projectName
        );

        ProcessPropertyRedirections(
            conflictAnalysis.ConflictingProperties,
            validatedExtension.ExtensionProperties!,
            context.ComponentsSchemas,
            projectName
        );
    }

    /// <summary>
    /// Analyzes properties for conflicts with core schema
    /// </summary>
    private PropertyConflictAnalysis AnalyzePropertyConflicts(
        JsonObject extensionProperties,
        JsonObject schemaLocation
    )
    {
        var conflictingProperties = new List<PropertyRedirection>();
        var nonConflictingProperties = new JsonObject();

        foreach ((string propertyName, JsonNode? propertyValue) in extensionProperties)
        {
            if (propertyValue == null)
            {
                continue;
            }

            if (schemaLocation.ContainsKey(propertyName))
            {
                var referencedSchemaName = ExtractReferencedSchemaName(schemaLocation[propertyName]);
                if (!string.IsNullOrEmpty(referencedSchemaName))
                {
                    conflictingProperties.Add(new PropertyRedirection(propertyName, referencedSchemaName));
                }
                else
                {
                    _logger.LogWarning(
                        "Property '{PropertyName}' exists in both core and extension but no referenced schema found. Extension property will be ignored.",
                        propertyName
                    );
                }
            }
            else
            {
                nonConflictingProperties[propertyName] = propertyValue.DeepClone();
            }
        }

        return new PropertyConflictAnalysis(conflictingProperties, nonConflictingProperties);
    }

    /// <summary>
    /// Creates main extension schema if there are non-conflicting properties
    /// </summary>
    private static void CreateMainExtensionIfNeeded(
        JsonObject originalExtensionObject,
        string componentSchemaName,
        JsonObject nonConflictingProperties,
        JsonObject schemaLocation,
        JsonObject componentsSchemas,
        string projectName
    )
    {
        if (nonConflictingProperties.Count == 0)
        {
            return;
        }

        var schemaNames = CreateSchemaNames(componentSchemaName, projectName);
        var filteredExtension = CreateFilteredExtension(originalExtensionObject, nonConflictingProperties);

        SetupExtensionSchema(schemaLocation, componentsSchemas, schemaNames, projectName);
        AddProjectSchema(componentsSchemas, schemaNames.ProjectExtensionSchemaName, filteredExtension);
    }

    /// <summary>
    /// Processes redirected properties to their referenced schemas
    /// </summary>
    private void ProcessPropertyRedirections(
        List<PropertyRedirection> redirections,
        JsonObject extensionProperties,
        JsonObject componentsSchemas,
        string projectName
    )
    {
        foreach (var redirection in redirections)
        {
            var propertyValue = extensionProperties[redirection.PropertyName];
            if (propertyValue != null)
            {
                ProcessRedirectedProperty(
                    redirection.PropertyName,
                    redirection.ReferencedSchemaName,
                    propertyValue,
                    componentsSchemas,
                    projectName
                );
            }
        }
    }

    /// <summary>
    /// Creates schema names for extensions
    /// </summary>
    private static ExtensionSchemaNames CreateSchemaNames(string componentSchemaName, string projectName)
    {
        return new ExtensionSchemaNames(
            $"{componentSchemaName}Extension",
            $"{projectName}_{componentSchemaName}Extension"
        );
    }

    /// <summary>
    /// Creates filtered extension object without conflicting properties
    /// </summary>
    private static JsonObject CreateFilteredExtension(
        JsonObject originalExtension,
        JsonObject filteredProperties
    )
    {
        var filteredExtension = new JsonObject
        {
            ["type"] = originalExtension["type"]?.DeepClone() ?? "object",
            ["properties"] = filteredProperties.DeepClone(),
        };

        // Copy additional schema properties that should be preserved
        var propertiesToCopy = new[] { "description", "required" };

        foreach (var propertyName in propertiesToCopy)
        {
            if (originalExtension[propertyName] != null)
            {
                // For "required" array, filter to only include properties that exist in filteredProperties
                if (propertyName == "required" && originalExtension[propertyName] is JsonArray requiredArray)
                {
                    var filteredRequired = new JsonArray();
                    foreach (JsonNode? item in requiredArray)
                    {
                        if (
                            item?.GetValue<string>() is string requiredProp
                            && filteredProperties.ContainsKey(requiredProp)
                        )
                        {
                            filteredRequired.Add(requiredProp);
                        }
                    }

                    // Only add required if it has items
                    if (filteredRequired.Count > 0)
                    {
                        filteredExtension[propertyName] = filteredRequired;
                    }
                }
                else
                {
                    filteredExtension[propertyName] = originalExtension[propertyName]!.DeepClone();
                }
            }
        }

        return filteredExtension;
    }

    /// <summary>
    /// Sets up the extension schema structure
    /// </summary>
    private static void SetupExtensionSchema(
        JsonObject schemaLocation,
        JsonObject componentsSchemas,
        ExtensionSchemaNames schemaNames,
        string projectName
    )
    {
        AddExtensionReference(schemaLocation, schemaNames.ExtensionSchemaName);
        CreateExtensionSchemaIfNeeded(componentsSchemas, schemaNames.ExtensionSchemaName);
        AddProjectReference(componentsSchemas, schemaNames, projectName);
    }

    /// <summary>
    /// Adds _ext reference to main schema
    /// </summary>
    private static void AddExtensionReference(JsonObject schemaLocation, string extensionSchemaName)
    {
        if (schemaLocation["_ext"] == null)
        {
            schemaLocation.Add(
                "_ext",
                JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{extensionSchemaName}\" }}")
            );
        }
    }

    /// <summary>
    /// Creates extension schema if it doesn't exist
    /// </summary>
    private static void CreateExtensionSchemaIfNeeded(
        JsonObject componentsSchemas,
        string extensionSchemaName
    )
    {
        if (!componentsSchemas.ContainsKey(extensionSchemaName))
        {
            componentsSchemas.Add(
                extensionSchemaName,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            );
        }
    }

    /// <summary>
    /// Adds project reference to extension schema
    /// </summary>
    private static void AddProjectReference(
        JsonObject componentsSchemas,
        ExtensionSchemaNames schemaNames,
        string projectName
    )
    {
        var extensionSchema = componentsSchemas[schemaNames.ExtensionSchemaName]!.AsObject();
        var extensionProperties = extensionSchema["properties"]!.AsObject();

        if (!extensionProperties.ContainsKey(projectName))
        {
            extensionProperties.Add(
                projectName,
                JsonNode.Parse(
                    $"{{ \"$ref\": \"#/components/schemas/{schemaNames.ProjectExtensionSchemaName}\" }}"
                )
            );
        }
    }

    /// <summary>
    /// Adds project-specific schema to components
    /// </summary>
    private static void AddProjectSchema(JsonObject componentsSchemas, string schemaName, JsonObject schema)
    {
        if (componentsSchemas.ContainsKey(schemaName))
        {
            throw new InvalidOperationException(
                $"Duplicate schema name '{schemaName}' encountered while building OpenAPI document."
            );
        }
        componentsSchemas.Add(schemaName, schema);
    }

    /// <summary>
    /// Processes direct extension schemas (for backwards compatibility with simple extensions)
    /// </summary>
    private static void ProcessDirectExtensionSchema(
        JsonObject extObjectAsObject,
        string componentSchemaName,
        string projectName,
        JsonObject locationForExt,
        JsonObject componentsSchemas
    )
    {
        // Create main extension schema for the direct extension
        var schemaNames = CreateSchemaNames(componentSchemaName, projectName);

        // Add _ext to main schema if not already there
        if (locationForExt["_ext"] == null)
        {
            locationForExt.Add(
                "_ext",
                JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{schemaNames.ExtensionSchemaName}\" }}")
            );
        }

        // Create or get extension schema
        if (!componentsSchemas.ContainsKey(schemaNames.ExtensionSchemaName))
        {
            componentsSchemas.Add(
                schemaNames.ExtensionSchemaName,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            );
        }

        JsonObject extensionSchema =
            componentsSchemas[schemaNames.ExtensionSchemaName]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Extension schema '{schemaNames.ExtensionSchemaName}' is not an object."
            );

        JsonObject mainExtensionProperties =
            extensionSchema["properties"]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Extension schema '{schemaNames.ExtensionSchemaName}' missing 'properties'."
            );

        // Add reference to the specific project schema
        if (!mainExtensionProperties.ContainsKey(projectName))
        {
            mainExtensionProperties.Add(
                projectName,
                JsonNode.Parse(
                    $"{{ \"$ref\": \"#/components/schemas/{schemaNames.ProjectExtensionSchemaName}\" }}"
                )
            );
        }

        // Add the specific project schema with the direct extension content.
        if (componentsSchemas.ContainsKey(schemaNames.ProjectExtensionSchemaName))
        {
            throw new InvalidOperationException(
                $"Duplicate project extension schema '{schemaNames.ProjectExtensionSchemaName}'. "
                    + "This indicates a schema authoring error — the same project registered duplicate extension content."
            );
        }
        componentsSchemas.Add(schemaNames.ProjectExtensionSchemaName, extObjectAsObject.DeepClone());
    }

    /// <summary>
    /// Processes a single redirected property
    /// </summary>
    private void ProcessRedirectedProperty(
        string propertyName,
        string referencedSchemaName,
        JsonNode extensionPropertyValue,
        JsonObject componentsSchemas,
        string projectName
    )
    {
        // Get the referenced schema
        if (componentsSchemas[referencedSchemaName] is not JsonObject referencedSchema)
        {
            _logger.LogWarning(
                "Referenced schema '{ReferencedSchemaName}' not found for redirected property '{PropertyName}'",
                referencedSchemaName,
                propertyName
            );
            return;
        }

        // Ensure the referenced schema has properties
        if (referencedSchema["properties"] == null)
        {
            referencedSchema["properties"] = new JsonObject();
        }

        JsonObject referencedSchemaProperties = referencedSchema["properties"]!.AsObject();

        // Create extension schema for the referenced schema
        var schemaNames = CreateSchemaNames(referencedSchemaName, projectName);

        SetupReferencedExtensionSchema(
            referencedSchemaProperties,
            componentsSchemas,
            schemaNames.ExtensionSchemaName,
            schemaNames.ProjectExtensionSchemaName,
            projectName
        );

        // Create the project-specific referenced extension schema
        CreateProjectSpecificExtensionSchema(
            componentsSchemas,
            schemaNames.ProjectExtensionSchemaName,
            extensionPropertyValue,
            propertyName,
            referencedSchemaName,
            projectName
        );
    }

    /// <summary>
    /// Sets up the referenced extension schema structure
    /// </summary>
    private static void SetupReferencedExtensionSchema(
        JsonObject referencedSchemaProperties,
        JsonObject componentsSchemas,
        string referencedExtensionSchemaName,
        string projectReferencedExtensionSchemaName,
        string projectName
    )
    {
        // Add _ext to referenced schema if not already there
        if (referencedSchemaProperties["_ext"] == null)
        {
            referencedSchemaProperties.Add(
                "_ext",
                JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{referencedExtensionSchemaName}\" }}")
            );
        }

        // Create or get referenced extension schema
        if (!componentsSchemas.ContainsKey(referencedExtensionSchemaName))
        {
            componentsSchemas.Add(
                referencedExtensionSchemaName,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            );
        }

        JsonObject referencedExtensionSchema =
            componentsSchemas[referencedExtensionSchemaName]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Referenced extension schema '{referencedExtensionSchemaName}' is not an object."
            );

        JsonObject referencedExtensionProperties =
            referencedExtensionSchema["properties"]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Referenced extension schema '{referencedExtensionSchemaName}' missing 'properties'."
            );

        // Add reference to the specific project schema for the referenced extension
        if (!referencedExtensionProperties.ContainsKey(projectName))
        {
            referencedExtensionProperties.Add(
                projectName,
                JsonNode.Parse(
                    $"{{ \"$ref\": \"#/components/schemas/{projectReferencedExtensionSchemaName}\" }}"
                )
            );
        }
    }

    /// <summary>
    /// Creates project-specific extension schema for redirected properties
    /// </summary>
    private void CreateProjectSpecificExtensionSchema(
        JsonObject componentsSchemas,
        string projectReferencedExtensionSchemaName,
        JsonNode extensionPropertyValue,
        string propertyName,
        string referencedSchemaName,
        string projectName
    )
    {
        if (componentsSchemas.ContainsKey(projectReferencedExtensionSchemaName))
        {
            return;
        }

        // The extension property should contain the properties for the referenced schema
        ExtensionSchemaResult extensionSchemaResult = CreateExtensionSchemaForProperty(
            extensionPropertyValue,
            propertyName,
            _logger
        );

        JsonObject redirectedExtensionObject = extensionSchemaResult.Schema;

        // Check if we need to resolve a referenced schema
        if (extensionSchemaResult.ReferencedSchemaName is string resolvedSchemaName)
        {
            ResolveReferencedSchema(
                redirectedExtensionObject,
                resolvedSchemaName,
                componentsSchemas,
                referencedSchemaName,
                projectName
            );
        }

        // Validate and add the schema
        AddValidatedExtensionSchema(
            componentsSchemas,
            projectReferencedExtensionSchemaName,
            redirectedExtensionObject,
            propertyName
        );
    }

    /// <summary>
    /// Resolves a referenced schema and extracts its properties
    /// </summary>
    private void ResolveReferencedSchema(
        JsonObject redirectedExtensionObject,
        string resolvedSchemaName,
        JsonObject componentsSchemas,
        string referencedSchemaName,
        string projectName
    )
    {
        // Get the referenced schema and extract its properties
        if (
            componentsSchemas[resolvedSchemaName]?.AsObject() is not JsonObject resolvedSchemaObj
            || resolvedSchemaObj["properties"] is not JsonObject resolvedProperties
        )
        {
            _logger.LogWarning(
                "Referenced schema '{ResolvedSchemaName}' not found or has no properties",
                resolvedSchemaName
            );
            return;
        }

        // Check if the resolved schema has _ext structure and extract extension properties
        if (
            resolvedProperties["_ext"] is JsonObject extObj
            && extObj["$ref"]?.GetValue<string>() is string extRef
        )
        {
            ProcessExtensionReference(
                redirectedExtensionObject,
                extRef,
                componentsSchemas,
                projectName,
                resolvedSchemaName
            );
        }
        else
        {
            // Look for direct extension properties that don't belong to the core schema
            JsonObject potentialExtensionProperties = ExtractExtensionProperties(
                resolvedProperties,
                referencedSchemaName,
                projectName,
                componentsSchemas
            );

            redirectedExtensionObject["properties"] =
                potentialExtensionProperties.Count > 0 ? potentialExtensionProperties : new JsonObject();
        }
    }

    /// <summary>
    /// Processes extension references to extract final properties
    /// </summary>
    private void ProcessExtensionReference(
        JsonObject redirectedExtensionObject,
        string extRef,
        JsonObject componentsSchemas,
        string projectName,
        string resolvedSchemaName
    )
    {
        // Extract the extension schema name from the $ref
        string? extSchemaName = ExtractSchemaNameFromRef(extRef);
        if (
            !string.IsNullOrEmpty(extSchemaName)
            && componentsSchemas[extSchemaName]?.AsObject() is JsonObject extSchemaObj
            && extSchemaObj["properties"] is JsonObject extSchemaProperties
            && extSchemaProperties[projectName] is JsonObject projectExtObj
            && projectExtObj["$ref"]?.GetValue<string>() is string projectExtRef
        )
        {
            // Get the actual extension properties
            string? projectExtSchemaName = ExtractSchemaNameFromRef(projectExtRef);
            if (
                !string.IsNullOrEmpty(projectExtSchemaName)
                && componentsSchemas[projectExtSchemaName]?.AsObject() is JsonObject projectExtSchemaObj
                && projectExtSchemaObj["properties"] is JsonObject finalExtProperties
            )
            {
                redirectedExtensionObject["properties"] = finalExtProperties.DeepClone();
            }
            else
            {
                _logger.LogWarning(
                    "Could not resolve final extension schema '{ProjectExtSchemaName}' for referenced schema '{ResolvedSchemaName}'",
                    projectExtSchemaName,
                    resolvedSchemaName
                );
            }
        }
        else
        {
            _logger.LogWarning(
                "Could not resolve extension schema reference for '{ResolvedSchemaName}'",
                resolvedSchemaName
            );
        }
    }

    /// <summary>
    /// Validates and adds extension schema to components
    /// </summary>
    private void AddValidatedExtensionSchema(
        JsonObject componentsSchemas,
        string schemaName,
        JsonObject schema,
        string propertyName
    )
    {
        if (ValidateOpenApiSchema(schema, schemaName, _logger))
        {
            componentsSchemas.Add(schemaName, schema);
        }
        else
        {
            _logger.LogError(
                "Failed to create valid redirected extension schema '{SchemaName}' for property '{PropertyName}'. Using minimal schema.",
                schemaName,
                propertyName
            );

            // Create minimal valid schema as fallback
            componentsSchemas.Add(
                schemaName,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            );
        }
    }

    /// <summary>
    /// Extracts extension properties from a resolved schema by filtering out core properties
    /// </summary>
    private JsonObject ExtractExtensionProperties(
        JsonObject resolvedProperties,
        string referencedSchemaName,
        string projectName,
        JsonObject componentsSchemas
    )
    {
        var potentialExtensionProperties = new JsonObject();

        // Get the core schema properties dynamically
        HashSet<string> coreSchemaProperties = GetCoreSchemaProperties(
            referencedSchemaName,
            componentsSchemas
        );

        // Filter out core properties from the resolved schema
        foreach ((string propertyKey, JsonNode? propertyValue) in resolvedProperties)
        {
            // Handle _ext property specially - extract extension properties from it
            if (propertyKey == "_ext" && propertyValue is JsonObject extJsonObject)
            {
                ExtractProjectExtensionProperties(extJsonObject, projectName, potentialExtensionProperties);
            }
            // Handle regular properties that are not core properties
            else if (
                !coreSchemaProperties.Contains(propertyKey)
                && propertyKey != "_ext"
                && propertyValue != null
            )
            {
                potentialExtensionProperties[propertyKey] = propertyValue.DeepClone();
            }
        }

        return potentialExtensionProperties;
    }

    /// <summary>
    /// Gets the core schema property names for a given schema
    /// </summary>
    private HashSet<string> GetCoreSchemaProperties(string coreSchemaName, JsonObject componentsSchemas)
    {
        HashSet<string> coreSchemaProperties = new HashSet<string>();

        if (
            componentsSchemas[coreSchemaName]?.AsObject() is JsonObject coreSchemaObj
            && coreSchemaObj["properties"] is JsonObject coreProperties
        )
        {
            // Extract property names from the core schema
            foreach ((string corePropertyKey, JsonNode? _) in coreProperties)
            {
                if (corePropertyKey != "_ext") // Exclude _ext as it's added by our extension logic
                {
                    coreSchemaProperties.Add(corePropertyKey);
                }
            }
        }
        else
        {
            _logger.LogWarning(
                "Could not find core schema '{CoreSchemaName}' to determine core properties",
                coreSchemaName
            );
        }

        return coreSchemaProperties;
    }

    /// <summary>
    /// Extracts properties from project-specific extensions
    /// </summary>
    private void ExtractProjectExtensionProperties(
        JsonObject extJsonObject,
        string projectName,
        JsonObject potentialExtensionProperties
    )
    {
        // Look for the project-specific extension (e.g., "sample") inside properties
        if (
            extJsonObject["properties"] is JsonObject extProperties
            && extProperties[projectName] is JsonObject projectExtObject
        )
        {
            // Extract properties from the project extension
            if (projectExtObject["properties"] is JsonObject projectExtProperties)
            {
                // Add all properties from the project extension
                foreach ((string extPropertyKey, JsonNode? extPropertyValue) in projectExtProperties)
                {
                    if (extPropertyValue != null)
                    {
                        potentialExtensionProperties[extPropertyKey] = extPropertyValue.DeepClone();
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Project extension '{ProjectName}' found but missing 'properties' section",
                    projectName
                );
            }
        }
        else
        {
            _logger.LogWarning("Project extension '{ProjectName}' not found in _ext.properties", projectName);
        }
    }

    /// <summary>
    /// Creates a valid extension schema for a redirected property
    /// </summary>
    private static ExtensionSchemaResult CreateExtensionSchemaForProperty(
        JsonNode extensionPropertyValue,
        string propertyName,
        ILogger logger
    )
    {
        if (extensionPropertyValue is JsonObject extPropertyObj)
        {
            return ProcessExtensionObjectProperty(extPropertyObj, propertyName, logger);
        }

        if (extensionPropertyValue is JsonArray)
        {
            logger.LogError(
                "Extension property '{PropertyName}' is a JsonArray, which is not supported for schema extension",
                propertyName
            );
            return CreateFallbackSchema(propertyName, logger);
        }

        logger.LogError(
            "Extension property '{PropertyName}' has unexpected type: {ValueType}",
            propertyName,
            extensionPropertyValue?.GetType().Name ?? "null"
        );

        return CreateFallbackSchema(propertyName, logger);
    }

    /// <summary>
    /// Processes extension object properties and returns appropriate schema
    /// </summary>
    private static ExtensionSchemaResult ProcessExtensionObjectProperty(
        JsonObject extPropertyObj,
        string propertyName,
        ILogger logger
    )
    {
        // Check if it has the OpenAPI schema structure with 'properties'
        if (extPropertyObj["properties"] is JsonObject nestedProperties)
        {
            return new ExtensionSchemaResult(
                new JsonObject
                {
                    ["type"] = extPropertyObj["type"]?.DeepClone() ?? "object",
                    ["properties"] = nestedProperties.DeepClone(),
                }
            );
        }

        // Check if it's an array definition with a $ref in items
        if (
            extPropertyObj["type"]?.GetValue<string>() == "array"
            && extPropertyObj["items"] is JsonObject refItemsObj
            && refItemsObj["$ref"]?.GetValue<string>() is string refString
        )
        {
            return ProcessArrayWithReference(refString, propertyName, logger);
        }

        // Check if it's an array definition with item properties
        if (
            extPropertyObj["type"]?.GetValue<string>() == "array"
            && extPropertyObj["items"] is JsonObject itemsObj
            && itemsObj["properties"] is JsonObject itemProperties
        )
        {
            return new ExtensionSchemaResult(
                new JsonObject { ["type"] = "object", ["properties"] = itemProperties.DeepClone() }
            );
        }

        // If it's directly the properties object
        if (!extPropertyObj.ContainsKey("type") && !extPropertyObj.ContainsKey("properties"))
        {
            return new ExtensionSchemaResult(
                new JsonObject { ["type"] = "object", ["properties"] = extPropertyObj.DeepClone() }
            );
        }

        logger.LogWarning(
            "Extension property '{PropertyName}' has unexpected object structure. Keys: {Keys}",
            propertyName,
            string.Join(", ", extPropertyObj.Select(kvp => kvp.Key))
        );

        return CreateFallbackSchema(propertyName, logger);
    }

    /// <summary>
    /// Processes array extensions with schema references
    /// </summary>
    private static ExtensionSchemaResult ProcessArrayWithReference(
        string refString,
        string propertyName,
        ILogger logger
    )
    {
        string? schemaName = ExtractSchemaNameFromRef(refString);
        if (!string.IsNullOrEmpty(schemaName))
        {
            return new ExtensionSchemaResult(
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                schemaName
            );
        }

        logger.LogWarning(
            "Extension property '{PropertyName}' has array with $ref but couldn't extract schema name from: {RefString}",
            propertyName,
            refString
        );

        return CreateFallbackSchema(propertyName, logger);
    }

    /// <summary>
    /// Creates a fallback schema when processing fails
    /// </summary>
    private static ExtensionSchemaResult CreateFallbackSchema(string propertyName, ILogger logger)
    {
        logger.LogWarning(
            "Creating empty extension schema for property '{PropertyName}' due to unexpected structure",
            propertyName
        );

        return new ExtensionSchemaResult(
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
        );
    }

    /// <summary>
    /// Validates that a JsonObject represents a valid OpenAPI schema
    /// </summary>
    private static bool ValidateOpenApiSchema(JsonObject schema, string schemaName, ILogger logger)
    {
        // Check required fields
        if (schema["type"] == null)
        {
            logger.LogError("Schema '{SchemaName}' is missing required 'type' property", schemaName);
            return false;
        }

        string? schemaType = schema["type"]?.GetValue<string>();
        if (string.IsNullOrEmpty(schemaType))
        {
            logger.LogError("Schema '{SchemaName}' has invalid 'type' property", schemaName);
            return false;
        }

        // For object types, validate properties
        if (schemaType == "object")
        {
            if (schema["properties"] == null)
            {
                logger.LogError("Object schema '{SchemaName}' is missing 'properties'", schemaName);
                return false;
            }

            if (schema["properties"] is not JsonObject)
            {
                logger.LogError(
                    "Object schema '{SchemaName}' has invalid 'properties' - must be an object",
                    schemaName
                );
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts the referenced schema name from a property definition
    /// </summary>
    private static string? ExtractReferencedSchemaName(JsonNode? propertyNode)
    {
        if (propertyNode == null)
        {
            return null;
        }

        // Handle direct $ref
        string? directRef = propertyNode["$ref"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(directRef))
        {
            return ExtractSchemaNameFromRef(directRef);
        }

        // Handle array items with $ref
        string? arrayItemRef = propertyNode["items"]?["$ref"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(arrayItemRef))
        {
            return ExtractSchemaNameFromRef(arrayItemRef);
        }

        return null;
    }

    /// <summary>
    /// Extracts schema name from a $ref string like "#/components/schemas/EdFi_Contact_Address"
    /// </summary>
    private static string? ExtractSchemaNameFromRef(string refString)
    {
        const string SchemaPrefix = "#/components/schemas/";
        if (refString.StartsWith(SchemaPrefix))
        {
            return refString.Substring(SchemaPrefix.Length);
        }
        return null;
    }

    /// <summary>
    /// Inserts _ext for common type extensions specified via commonExtensionOverrides.
    /// Navigates the OpenAPI spec's $ref chains to find the target component schema
    /// and adds extension references using the schemaFragment data.
    /// </summary>
    private static void InsertCommonExtensionOverrides(
        JsonArray overrides,
        JsonObject componentsSchemas,
        string coreComponentSchemaName,
        string resourceName
    )
    {
        foreach (JsonNode? overrideEntry in overrides)
        {
            if (overrideEntry is null)
            {
                throw new InvalidOperationException(
                    $"Null override entry found in commonExtensionOverrides for resource '{resourceName}'"
                );
            }

            if (overrideEntry is not JsonObject)
            {
                throw new InvalidOperationException(
                    $"Expected a JsonObject override entry for resource '{resourceName}', but encountered {overrideEntry.GetType().Name}."
                );
            }

            var insertionLocations = overrideEntry["insertionLocations"]?.AsArray();
            var schemaFragment = overrideEntry["schemaFragment"]?.AsObject();

            if (insertionLocations is null || schemaFragment is null)
            {
                string missing;
                if (insertionLocations is null && schemaFragment is null)
                {
                    missing = "'insertionLocations' and 'schemaFragment'";
                }
                else if (insertionLocations is null)
                {
                    missing = "'insertionLocations'";
                }
                else
                {
                    missing = "'schemaFragment'";
                }
                throw new InvalidOperationException(
                    $"Common extension override entry in resource '{resourceName}' "
                        + $"is missing required field(s): {missing}."
                );
            }

            if (insertionLocations.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Common extension override entry in resource '{resourceName}' "
                        + $"has an empty 'insertionLocations' array. "
                        + $"An override entry must have at least one insertion location."
                );
            }

            var fragmentProperties = schemaFragment["properties"]?.AsObject();
            if (fragmentProperties is null)
            {
                var insertionPaths = string.Join(
                    ", ",
                    insertionLocations.Select(loc => loc?.GetValue<string>() ?? "(null)")
                );
                var fragmentKeys = string.Join(", ", schemaFragment.Select(kvp => kvp.Key));
                throw new InvalidOperationException(
                    $"Common extension override entry in resource '{resourceName}' "
                        + $"(insertionLocations: [{insertionPaths}]) "
                        + $"has a 'schemaFragment' missing the required 'properties' field. Fragment keys: [{fragmentKeys}]."
                );
            }

            HashSet<string> addedProjectSchemas = [];

            foreach (JsonNode? location in insertionLocations)
            {
                var jsonPath = location?.GetValue<string>();
                if (string.IsNullOrEmpty(jsonPath))
                {
                    var insertionPaths = string.Join(
                        ", ",
                        insertionLocations.Select(loc => loc?.GetValue<string>() ?? "(null)")
                    );
                    throw new InvalidOperationException(
                        $"Common extension override entry in resource '{resourceName}' "
                            + $"(insertionLocations: [{insertionPaths}]) "
                            + $"has a null or empty insertion location."
                    );
                }

                var targetSchemaName = ResolveComponentSchemaFromInsertionLocation(
                    componentsSchemas,
                    coreComponentSchemaName,
                    jsonPath,
                    resourceName
                );

                if (targetSchemaName is null)
                {
                    throw new InvalidOperationException(
                        $"Common extension override entry in resource '{resourceName}': "
                            + $"could not resolve insertion location '{jsonPath}' to a component schema starting from '{coreComponentSchemaName}'."
                    );
                }

                var targetSchema = componentsSchemas[targetSchemaName]?.AsObject();
                if (targetSchema?["properties"] is not JsonObject targetProperties)
                {
                    throw new InvalidOperationException(
                        $"Common extension override entry in resource '{resourceName}': "
                            + $"target component schema '{targetSchemaName}' resolved from path '{jsonPath}' has no 'properties' object."
                    );
                }

                foreach ((string projectKey, JsonNode? projectSchemaValue) in fragmentProperties)
                {
                    if (projectSchemaValue is not JsonObject projectSchema)
                    {
                        throw new InvalidOperationException(
                            $"Common extension override entry in resource '{resourceName}': "
                                + $"fragment property '{projectKey}' in schema '{targetSchemaName}' is not a valid JSON object."
                        );
                    }

                    var schemaNames = CreateSchemaNames(targetSchemaName, projectKey);

                    SetupExtensionSchema(targetProperties, componentsSchemas, schemaNames, projectKey);

                    if (!addedProjectSchemas.Add(schemaNames.ProjectExtensionSchemaName))
                    {
                        // Same schema from another insertion location in this entry — idempotent skip
                        continue;
                    }

                    AddProjectSchema(
                        componentsSchemas,
                        schemaNames.ProjectExtensionSchemaName,
                        projectSchema.DeepClone().AsObject()
                    );
                }
            }
        }
    }

    /// <summary>
    /// Tries to find the core component schema name (e.g., "EdFi_Contact") by matching {Project}_{ResourceName}
    /// in the existing component schemas. Returns null if no match is found.
    /// </summary>
    private static string? TryFindCoreSchemaName(
        JsonObject componentsSchemas,
        string resourceName,
        string coreProjectName
    )
    {
        var suffix = $"_{resourceName}";

        var matches = componentsSchemas
            .Select(kvp => kvp.Key)
            .Where(key =>
                key.EndsWith(suffix, StringComparison.Ordinal)
                && key.IndexOf('_') == key.Length - suffix.Length
            )
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        var matched = matches.Count switch
        {
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Resource '{resourceName}' has commonExtensionOverrides but matched multiple core schemas: [{string.Join(", ", matches)}]."
            ),
        };

        var actualPrefix = matched[..matched.IndexOf('_')];
        var expectedPrefix = coreProjectName.Replace("-", "");

        if (!string.Equals(actualPrefix, expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resource '{resourceName}' matched schema '{matched}' but prefix '{actualPrefix}' does not match expected core project prefix '{expectedPrefix}'."
            );
        }

        return matched;
    }

    /// <summary>
    /// Resolves an insertion location JSONPath (e.g., "$.properties.addresses.items") to the target
    /// OpenAPI component schema name by navigating the spec's $ref chains.
    /// </summary>
    private static string? ResolveComponentSchemaFromInsertionLocation(
        JsonObject componentsSchemas,
        string startSchemaName,
        string insertionLocation,
        string resourceName
    )
    {
        var segments = insertionLocation.Split('.').Where(s => s != "$").ToArray();

        JsonNode? current = componentsSchemas[startSchemaName];
        string resolvedSchemaName = startSchemaName;

        foreach (var segment in segments)
        {
            if (current is null)
            {
                return null;
            }

            (current, resolvedSchemaName) = ResolveRef(current, componentsSchemas, resolvedSchemaName);
            if (current is null)
            {
                return null;
            }

            current = current[segment];
        }

        if (current is null)
        {
            return null;
        }

        // Final $ref resolution at the target node.
        // If the target has a $ref, follow it to get the referenced schema name.
        // If it's an inline definition (no $ref), throw — we require a component $ref.
        var schemaNameBeforeResolve = resolvedSchemaName;
        (_, resolvedSchemaName) = ResolveRef(current, componentsSchemas, resolvedSchemaName);

        if (resolvedSchemaName == schemaNameBeforeResolve)
        {
            throw new InvalidOperationException(
                $"Insertion location '{insertionLocation}' for resource '{resourceName}' "
                    + $"resolved to an inline definition at schema '{resolvedSchemaName}' "
                    + $"instead of a component $ref."
            );
        }

        return resolvedSchemaName;
    }

    /// <summary>
    /// If the node contains a $ref pointing to a component schema, resolves it and returns
    /// the referenced node and updated schema name. Otherwise returns the original node unchanged.
    /// </summary>
    private static (JsonNode? Node, string SchemaName) ResolveRef(
        JsonNode? node,
        JsonObject componentsSchemas,
        string currentSchemaName
    )
    {
        if (node is null)
        {
            return (null, currentSchemaName);
        }

        if (node is not JsonObject)
        {
            throw new InvalidOperationException(
                $"Expected a JsonObject node while resolving $ref at schema '{currentSchemaName}', but encountered {node.GetType().Name}."
            );
        }

        var refValue = node["$ref"]?.GetValue<string>();
        if (refValue is null)
        {
            return (node, currentSchemaName);
        }

        var resolved = ExtractSchemaNameFromRef(refValue);
        if (resolved is null)
        {
            return (node, currentSchemaName);
        }

        return (componentsSchemas[resolved], resolved);
    }

    /// <summary>
    /// Inserts new endpoint paths from extension OpenAPI fragments into the paths section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertNewPaths(JsonObject newPaths, JsonNode openApiCoreResources)
    {
        foreach ((string pathName, JsonNode? pathObject) in newPaths)
        {
            if (pathObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newPaths path name '{pathName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForPaths = openApiCoreResources
                .SelectRequiredNodeFromPath("$.paths", _logger)
                .AsObject();

            // If pathName has already been added by another extension, we don't support a second one
            if (locationForPaths[pathName] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second path '$.paths.{pathName}', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForPaths.Add(pathName, pathObject.DeepClone());
        }
    }

    /// <summary>
    /// Inserts new schema objects from extension OpenAPI fragments into the components.schemas section of the
    /// core OpenAPI specification.
    /// </summary>
    private void InsertNewSchemas(JsonObject newSchemas, JsonNode openApiCoreResources)
    {
        foreach ((string schemaName, JsonNode? schemaObject) in newSchemas)
        {
            if (schemaObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newSchemas path name '{schemaName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForSchemas = openApiCoreResources
                .SelectRequiredNodeFromPath("$.components.schemas", _logger)
                .AsObject();

            // If schemaName has already been added by another extension, we don't support a second one
            if (locationForSchemas[schemaName] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second schema '$.components.schemas.{schemaName}', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForSchemas.Add(schemaName, schemaObject.DeepClone());
        }
    }

    /// <summary>
    /// Inserts new global tag objects from extension OpenAPI fragments into the tags section of the
    /// core OpenAPI specification.
    /// </summary>
    private void InsertNewTags(JsonArray newTagObjects, JsonNode openApiCoreResources)
    {
        // This is where the extension tags will be added
        JsonArray globalTags = openApiCoreResources.SelectRequiredNodeFromPath("$.tags", _logger).AsArray();

        // Helper to test for tag uniqueness
        HashSet<string> existingTagNames = [];
        foreach (JsonNode? globalTag in globalTags)
        {
            if (globalTag == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI specification has empty global tag. Extension fragment validation failed?"
                );
            }

            string tagName =
                globalTag["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"OpenAPI specification has newTag with no name. Extension fragment validation failed?"
                );
            existingTagNames.Add(tagName);
        }

        foreach (JsonNode? newTagObject in newTagObjects)
        {
            if (newTagObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newTag. Extension fragment validation failed?"
                );
            }

            string tagObjectName =
                newTagObject["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment has newTag with no name. Extension fragment validation failed?"
                );

            // If tag has already been added by another extension, we don't support a second one
            if (existingTagNames.Contains(tagObjectName))
            {
                _logger.LogDebug(
                    "OpenAPI extension fragment tried to add a second tag named '{TagObjectName}', skipping.",
                    tagObjectName
                );
            }
            else
            {
                globalTags.Add(newTagObject.DeepClone());
            }
        }
    }

    /// <summary>
    /// Merges OpenAPI fragments from source into target
    /// </summary>
    private static void MergeOpenApiFragments(JsonNode source, JsonNode target)
    {
        // Merge components
        if (
            source["components"] is JsonObject sourceComponents
            && target["components"] is JsonObject targetComponents
        )
        {
            foreach ((string componentSchemaName, JsonNode? extObject) in sourceComponents)
            {
                if (
                    targetComponents[componentSchemaName] is JsonObject targetSection
                    && extObject is JsonObject sourceSection
                )
                {
                    // Merge the sections
                    foreach ((string itemKey, JsonNode? itemValue) in sourceSection)
                    {
                        if (itemValue != null)
                        {
                            targetSection[itemKey] = itemValue.DeepClone();
                        }
                    }
                }
                else if (extObject != null)
                {
                    targetComponents[componentSchemaName] = extObject.DeepClone();
                }
            }
        }

        // Merge paths
        if (source["paths"] is JsonObject sourcePaths && target["paths"] is JsonObject targetPaths)
        {
            foreach ((string pathKey, JsonNode? pathValue) in sourcePaths)
            {
                if (pathValue != null)
                {
                    targetPaths[pathKey] = pathValue.DeepClone();
                }
            }
        }

        // Merge tags
        if (source["tags"] is JsonArray sourceTags && target["tags"] is JsonArray targetTags)
        {
            foreach (JsonNode? tag in sourceTags)
            {
                if (tag != null)
                {
                    targetTags.Add(tag.DeepClone());
                }
            }
        }
    }

    /// <summary>
    /// Collects OpenAPI fragments from resource schemas based on document type
    /// </summary>
    private void CollectFragmentsFromResourceSchemas(
        ApiSchemaDocumentNodes apiSchemas,
        OpenApiDocumentType documentType,
        JsonNode targetDocument,
        bool isExtensionProject
    )
    {
        ProjectSchema projectSchema = new(apiSchemas.CoreApiSchemaRootNode["projectSchema"]!, _logger);
        IEnumerable<JsonNode> resourceSchemaNodes = projectSchema.GetAllResourceSchemaNodes();

        foreach (JsonNode resourceSchemaNode in resourceSchemaNodes)
        {
            ResourceSchema resourceSchema = new(resourceSchemaNode);

            // Skip if this resource doesn't have OpenAPI fragments
            if (resourceSchema.OpenApiFragments == null)
            {
                continue;
            }

            JsonNode fragmentsNode = resourceSchema.OpenApiFragments;
            string documentTypeKey =
                documentType == OpenApiDocumentType.Resource ? "resources" : "descriptors";

            // Skip if this resource doesn't have fragments for this document type
            if (fragmentsNode[documentTypeKey] == null)
            {
                continue;
            }

            JsonNode fragment = fragmentsNode[documentTypeKey]!;

            // For extension projects with resource extensions, process exts
            if (
                isExtensionProject
                && resourceSchema.IsResourceExtension
                && fragment["exts"] is JsonObject exts
            )
            {
                InsertExts(exts, targetDocument, projectSchema.ProjectName.Value.ToLower());
            }
            // For non-resource-extensions, merge the fragment directly
            else if (!resourceSchema.IsResourceExtension)
            {
                MergeOpenApiFragments(fragment, targetDocument);
            }
        }
    }

    /// <summary>
    /// Collects abstract resource fragments and merges them into the document
    /// </summary>
    private void CollectAbstractResourceFragments(
        ApiSchemaDocumentNodes apiSchemas,
        OpenApiDocumentType documentType,
        JsonNode targetDocument
    )
    {
        // Only merge abstract resources for resources document, not descriptors
        if (documentType != OpenApiDocumentType.Resource)
        {
            return;
        }

        ProjectSchema projectSchema = new(apiSchemas.CoreApiSchemaRootNode["projectSchema"]!, _logger);

        IEnumerable<JsonNode> fragmentsToMerge = projectSchema
            .AbstractResources.Where(abstractResource => abstractResource.OpenApiFragment != null)
            .Select(abstractResource => abstractResource.OpenApiFragment!);

        foreach (JsonNode fragment in fragmentsToMerge)
        {
            MergeOpenApiFragments(fragment, targetDocument);
        }
    }

    public enum OpenApiDocumentType
    {
        Resource,
        Descriptor,
    }

    /// <summary>
    /// Creates an OpenAPI specification derived from the given core and extension ApiSchemas
    /// </summary>
    public JsonNode CreateDocument(ApiSchemaDocumentNodes apiSchemas, OpenApiDocumentType openApiDocumentType)
    {
        // Get the base OpenAPI document as a starting point
        string documentTypeKey =
            openApiDocumentType == OpenApiDocumentType.Resource ? "resources" : "descriptors";
        string baseDocumentPath = $"$.projectSchema.openApiBaseDocuments.{documentTypeKey}";

        JsonNode openApiSpecification = apiSchemas
            .CoreApiSchemaRootNode.SelectRequiredNodeFromPath(baseDocumentPath, _logger)
            .DeepClone();

        // Collect fragments from core project resource schemas
        CollectFragmentsFromResourceSchemas(apiSchemas, openApiDocumentType, openApiSpecification, false);

        // Collect abstract resource fragments (only for resources document)
        CollectAbstractResourceFragments(apiSchemas, openApiDocumentType, openApiSpecification);

        // Lazily resolve core project name — only needed when extensions have commonExtensionOverrides
        string? resolvedCoreProjectName = null;
        string GetCoreProjectName()
        {
            resolvedCoreProjectName ??= new ProjectSchema(
                apiSchemas.CoreApiSchemaRootNode["projectSchema"]!,
                _logger
            )
                .ProjectName
                .Value;
            return resolvedCoreProjectName;
        }

        // Process each extension project
        foreach (JsonNode extensionApiSchemaRootNode in apiSchemas.ExtensionApiSchemaRootNodes)
        {
            ProjectSchema extensionProjectSchema = new(extensionApiSchemaRootNode["projectSchema"]!, _logger);
            IEnumerable<JsonNode> extensionResourceSchemaNodes =
                extensionProjectSchema.GetAllResourceSchemaNodes();

            foreach (JsonNode resourceSchemaNode in extensionResourceSchemaNodes)
            {
                ResourceSchema resourceSchema = new(resourceSchemaNode);
                string resourceName = resourceSchema.ResourceName.Value;

                // 1) Process fragment-based operations (paths, schemas, tags, exts)
                //    — only when openApiFragments exists AND has the current doc-type key
                if (resourceSchema.OpenApiFragments is not null)
                {
                    JsonNode fragmentsNode = resourceSchema.OpenApiFragments;

                    if (fragmentsNode[documentTypeKey] is not null)
                    {
                        JsonNode fragment = fragmentsNode[documentTypeKey]!;

                        // Process paths if present
                        if (fragment["paths"] is JsonObject paths)
                        {
                            InsertNewPaths(paths, openApiSpecification);
                        }

                        // Process schemas if present (located under components)
                        if (fragment["components"]?["schemas"] is JsonObject schemas)
                        {
                            InsertNewSchemas(schemas, openApiSpecification);
                        }

                        // Process tags if present
                        if (fragment["tags"] is JsonArray tags)
                        {
                            InsertNewTags(tags, openApiSpecification);
                        }

                        // Handle resource extensions (exts)
                        var fragmentExts = fragment["exts"] as JsonObject;

                        if (resourceSchema.IsResourceExtension && fragmentExts is not null)
                        {
                            InsertExts(
                                fragmentExts,
                                openApiSpecification,
                                extensionProjectSchema.ProjectName.Value.ToLower()
                            );
                        }
                    }
                }

                // 2) Process commonExtensionOverrides — INDEPENDENT of fragments
                if (
                    resourceSchemaNode["commonExtensionOverrides"] is JsonArray { Count: > 0 } commonOverrides
                )
                {
                    var componentsSchemas =
                        openApiSpecification["components"]?["schemas"] as JsonObject
                        ?? throw new InvalidOperationException(
                            "components/schemas not found in OpenAPI specification."
                        );

                    var coreSchemaName = TryFindCoreSchemaName(
                        componentsSchemas,
                        resourceName,
                        GetCoreProjectName()
                    );

                    if (coreSchemaName is null)
                    {
                        // Target resource schemas not in this spec (e.g., Contact not in descriptors)
                        _logger.LogDebug(
                            "Resource '{ResourceName}' has commonExtensionOverrides but no matching core schema in {DocType} spec — skipping",
                            LoggingSanitizer.SanitizeForLogging(resourceName),
                            documentTypeKey
                        );
                        continue;
                    }

                    InsertCommonExtensionOverrides(
                        commonOverrides,
                        componentsSchemas,
                        coreSchemaName,
                        resourceName
                    );
                }
            }
        }

        // Apply domain filtering to exclude specified domains from the OpenAPI specification
        if (openApiSpecification["paths"] is JsonObject specificationPaths)
        {
            FilterPathsByDomain(specificationPaths);
        }

        // Remove unused tags after domain filtering
        RemoveUnusedTags(openApiSpecification);

        return openApiSpecification;
    }
}
