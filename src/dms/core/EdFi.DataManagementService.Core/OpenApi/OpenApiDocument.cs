// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
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
        foreach ((string componentSchemaName, JsonNode? extObject) in openApiExtensionFragmentList)
        {
            if (extObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty exts schema name '{componentSchemaName}'. Extension fragment validation failed?"
                );
            }

            // Validate that extObject is actually an object
            if (extObject is not JsonObject extObjectAsObject)
            {
                _logger.LogError(
                    "Extension object for '{ComponentSchemaName}' is not a JsonObject. Type: {ExtObjectType}, Value: {ExtObjectValue}",
                    componentSchemaName,
                    extObject.GetType().Name,
                    extObject.ToString()
                );
                continue;
            }

            // Get the component.schema location for _ext insert
            JsonObject locationForExt =
                openApiCoreResources
                    .SelectNodeFromPath($"$.components.schemas.{componentSchemaName}.properties", _logger)
                    ?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment expects Core to have '$.components.schemas.{componentSchemaName}.properties'. Extension fragment validation failed?"
                );

            JsonObject componentsSchemas =
                openApiCoreResources.SelectNodeFromPath("$.components.schemas", _logger)?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI core resources missing 'components.schemas'."
                );

            // Get the extension properties to analyze for conflicts
            JsonObject? extensionProperties = extObjectAsObject["properties"]?.AsObject();

            // If there's no "properties" field, treat the extObjectAsObject as a direct schema definition
            if (extensionProperties == null)
            {
                // This is a direct schema definition (legacy/simple extension format)
                // Create the extension schema directly
                ProcessDirectExtensionSchema(
                    extObjectAsObject,
                    componentSchemaName,
                    projectName,
                    locationForExt,
                    componentsSchemas
                );
                continue;
            }

            // Track properties that should be redirected to referenced schemas
            var propertiesToRedirect = new List<PropertyRedirection>();

            // Check for property conflicts and identify referenced schemas
            foreach ((string extensionPropertyName, JsonNode? extensionPropertyValue) in extensionProperties)
            {
                if (extensionPropertyValue == null)
                {
                    continue;
                }

                // Check if this property already exists in the core schema
                if (locationForExt.ContainsKey(extensionPropertyName))
                {
                    JsonNode? coreProperty = locationForExt[extensionPropertyName];

                    // Try to extract the referenced schema name from the core property
                    string? referencedSchemaName = ExtractReferencedSchemaName(coreProperty);

                    if (!string.IsNullOrEmpty(referencedSchemaName))
                    {
                        propertiesToRedirect.Add(
                            new PropertyRedirection(extensionPropertyName, referencedSchemaName)
                        );
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Property '{PropertyName}' exists in both core and extension for '{ComponentSchemaName}' but no referenced schema found. Extension property will be ignored.",
                            extensionPropertyName,
                            componentSchemaName
                        );
                    }
                }
            }

            // Create main extension schema for non-conflicting properties
            string extensionSchemaName = $"{componentSchemaName}Extension";
            string projectExtensionSchemaName = $"{projectName}_{componentSchemaName}Extension";

            // Create filtered extension object without redirected properties
            JsonObject filteredExtensionObject = new JsonObject
            {
                ["type"] = extObjectAsObject["type"]?.DeepClone() ?? "object",
                ["properties"] = new JsonObject(),
            };

            JsonObject filteredExtensionProperties = filteredExtensionObject["properties"]!.AsObject();

            // Add non-conflicting properties to the main extension
            foreach ((string extensionPropertyName, JsonNode? extensionPropertyValue) in extensionProperties)
            {
                if (
                    extensionPropertyValue != null
                    && !propertiesToRedirect.Exists(pr => pr.PropertyName == extensionPropertyName)
                )
                {
                    filteredExtensionProperties[extensionPropertyName] = extensionPropertyValue.DeepClone();
                }
            }

            // Only create main extension schema if there are non-conflicting properties
            if (filteredExtensionProperties.Count > 0)
            {
                // Add _ext to main schema if not already there
                if (locationForExt["_ext"] == null)
                {
                    locationForExt.Add(
                        "_ext",
                        JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{extensionSchemaName}\" }}")
                    );
                }

                // Create or get extension schema
                if (!componentsSchemas.ContainsKey(extensionSchemaName))
                {
                    componentsSchemas.Add(
                        extensionSchemaName,
                        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    );
                }

                JsonObject extensionSchema =
                    componentsSchemas[extensionSchemaName]?.AsObject()
                    ?? throw new InvalidOperationException(
                        $"Extension schema '{extensionSchemaName}' is not an object."
                    );

                JsonObject mainExtensionProperties =
                    extensionSchema["properties"]?.AsObject()
                    ?? throw new InvalidOperationException(
                        $"Extension schema '{extensionSchemaName}' missing 'properties'."
                    );

                // Add reference to the specific project schema
                if (!mainExtensionProperties.ContainsKey(projectName))
                {
                    mainExtensionProperties.Add(
                        projectName,
                        JsonNode.Parse(
                            $"{{ \"$ref\": \"#/components/schemas/{projectExtensionSchemaName}\" }}"
                        )
                    );
                }

                // Add the specific project schema if it doesn't exist
                if (!componentsSchemas.ContainsKey(projectExtensionSchemaName))
                {
                    componentsSchemas.Add(projectExtensionSchemaName, filteredExtensionObject);
                }
            }

            // Handle redirected properties - add them to referenced schemas
            HandleRedirectedProperties(
                propertiesToRedirect,
                extensionProperties,
                componentsSchemas,
                projectName
            );
        }
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
        string extensionSchemaName = $"{componentSchemaName}Extension";
        string projectExtensionSchemaName = $"{projectName}_{componentSchemaName}Extension";

        // Add _ext to main schema if not already there
        if (locationForExt["_ext"] == null)
        {
            locationForExt.Add(
                "_ext",
                JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{extensionSchemaName}\" }}")
            );
        }

        // Create or get extension schema
        if (!componentsSchemas.ContainsKey(extensionSchemaName))
        {
            componentsSchemas.Add(
                extensionSchemaName,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            );
        }

        JsonObject extensionSchema =
            componentsSchemas[extensionSchemaName]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Extension schema '{extensionSchemaName}' is not an object."
            );

        JsonObject mainExtensionProperties =
            extensionSchema["properties"]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Extension schema '{extensionSchemaName}' missing 'properties'."
            );

        // Add reference to the specific project schema
        if (!mainExtensionProperties.ContainsKey(projectName))
        {
            mainExtensionProperties.Add(
                projectName,
                JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{projectExtensionSchemaName}\" }}")
            );
        }

        // Add the specific project schema with the direct extension content
        if (!componentsSchemas.ContainsKey(projectExtensionSchemaName))
        {
            componentsSchemas.Add(projectExtensionSchemaName, extObjectAsObject.DeepClone());
        }
    }

    /// <summary>
    /// Result type for extension schema creation to avoid magic strings
    /// </summary>
    private sealed record ExtensionSchemaResult(JsonObject Schema, string? ReferencedSchemaName = null);

    /// <summary>
    /// Represents a property redirection mapping for extension processing
    /// </summary>
    private sealed record PropertyRedirection(string PropertyName, string ReferencedSchemaName);

    /// <summary>
    /// Handles redirected properties by adding them to their referenced schemas
    /// </summary>
    private void HandleRedirectedProperties(
        List<PropertyRedirection> propertiesToRedirect,
        JsonObject extensionProperties,
        JsonObject componentsSchemas,
        string projectName
    )
    {
        foreach (PropertyRedirection redirection in propertiesToRedirect)
        {
            JsonNode? extensionPropertyValue = extensionProperties[redirection.PropertyName];
            if (extensionPropertyValue == null)
            {
                continue;
            }

            ProcessRedirectedProperty(
                redirection.PropertyName,
                redirection.ReferencedSchemaName,
                extensionPropertyValue,
                componentsSchemas,
                projectName
            );
        }
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
        JsonObject? referencedSchema = componentsSchemas[referencedSchemaName]?.AsObject();
        if (referencedSchema == null)
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
        string referencedExtensionSchemaName = $"{referencedSchemaName}Extension";
        string projectReferencedExtensionSchemaName = $"{projectName}_{referencedSchemaName}Extension";

        SetupReferencedExtensionSchema(
            referencedSchemaProperties,
            componentsSchemas,
            referencedExtensionSchemaName,
            projectReferencedExtensionSchemaName,
            projectName
        );

        // Create the project-specific referenced extension schema
        CreateProjectSpecificExtensionSchema(
            componentsSchemas,
            projectReferencedExtensionSchemaName,
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

        // Process each extension project
        foreach (JsonNode extensionApiSchemaRootNode in apiSchemas.ExtensionApiSchemaRootNodes)
        {
            ProjectSchema extensionProjectSchema = new(extensionApiSchemaRootNode["projectSchema"]!, _logger);
            IEnumerable<JsonNode> extensionResourceSchemaNodes =
                extensionProjectSchema.GetAllResourceSchemaNodes();

            foreach (JsonNode resourceSchemaNode in extensionResourceSchemaNodes)
            {
                ResourceSchema resourceSchema = new(resourceSchemaNode);

                // Skip if this resource doesn't have OpenAPI fragments
                if (resourceSchema.OpenApiFragments == null)
                {
                    continue;
                }

                JsonNode fragmentsNode = resourceSchema.OpenApiFragments;

                // Skip if this resource doesn't have fragments for this document type
                if (fragmentsNode[documentTypeKey] == null)
                {
                    continue;
                }

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
                if (resourceSchema.IsResourceExtension && fragment["exts"] is JsonObject exts)
                {
                    InsertExts(
                        exts,
                        openApiSpecification,
                        extensionProjectSchema.ProjectName.Value.ToLower()
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
