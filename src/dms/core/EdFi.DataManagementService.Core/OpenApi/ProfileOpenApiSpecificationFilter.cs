// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Filters an OpenAPI specification based on profile rules.
/// Creates profile-specific schemas for readable and writable content types.
/// </summary>
public class ProfileOpenApiSpecificationFilter(ILogger logger)
{
    /// <summary>
    /// Creates a profile-filtered OpenAPI specification from a base specification.
    /// </summary>
    /// <param name="baseSpecification">The base OpenAPI specification to filter</param>
    /// <param name="profileDefinition">The profile definition containing filter rules</param>
    /// <returns>A new filtered OpenAPI specification</returns>
    public JsonNode CreateProfileSpecification(
        JsonNode baseSpecification,
        ProfileDefinition profileDefinition
    )
    {
        // Deep clone the specification to avoid modifying the cached base spec
        JsonNode specification = JsonNode.Parse(baseSpecification.ToJsonString())!;

        // Update info.title with profile name
        UpdateInfoTitle(specification, profileDefinition.ProfileName);

        // Create separate _readable and _writable schemas based on profile rules
        CreateProfileSchemas(specification, profileDefinition);

        // Filter paths to only include resources covered by the profile
        // and update $ref to use _readable or _writable schemas
        FilterPaths(specification, profileDefinition);

        // Remove unused tags
        RemoveUnusedTags(specification);

        return specification;
    }

    /// <summary>
    /// Updates the info.title to include the profile name.
    /// </summary>
    private static void UpdateInfoTitle(JsonNode specification, string profileName)
    {
        if (specification["info"] is JsonObject info)
        {
            string? existingTitle = info["title"]?.GetValue<string>();
            info["title"] = $"{profileName} Resources";

            if (!string.IsNullOrEmpty(existingTitle))
            {
                info["description"] = $"Profile-filtered API for {profileName}. Based on: {existingTitle}";
            }
        }
    }

    /// <summary>
    /// Creates separate _readable and _writable schemas for resources covered by the profile.
    /// </summary>
    private void CreateProfileSchemas(JsonNode specification, ProfileDefinition profileDefinition)
    {
        if (
            specification["components"] is not JsonObject components
            || components["schemas"] is not JsonObject schemas
        )
        {
            return;
        }

        // Build a lookup of resource profiles by resource name (case-insensitive)
        var resourceProfilesByName = profileDefinition.Resources.ToDictionary(
            r => r.ResourceName.ToLowerInvariant(),
            r => r
        );

        // Track schemas to create
        var schemasToCreate = new List<(string baseName, JsonObject baseSchema, ResourceProfile profile)>();

        foreach ((string schemaName, JsonNode? schemaValue) in schemas)
        {
            if (schemaValue is not JsonObject schemaObject)
            {
                continue;
            }

            // Extract resource name from schema name (e.g., "EdFi_Student" -> "Student")
            string? resourceName = ExtractResourceNameFromSchemaName(schemaName);
            if (resourceName is null)
            {
                continue;
            }

            // Check if this resource is covered by the profile
            if (
                resourceProfilesByName.TryGetValue(
                    resourceName.ToLowerInvariant(),
                    out ResourceProfile? resourceProfile
                )
            )
            {
                schemasToCreate.Add((schemaName, schemaObject, resourceProfile));
            }
        }

        // Create _readable and _writable schemas for each covered resource
        foreach (var (baseName, baseSchema, profile) in schemasToCreate)
        {
            // Create _readable schema if profile has ReadContentType
            if (profile.ReadContentType is not null)
            {
                string readableName = $"{baseName}_readable";
                JsonObject readableSchema = JsonNode.Parse(baseSchema.ToJsonString())!.AsObject();
                FilterSchemaForReadable(readableSchema, profile.ReadContentType);
                UpdateNestedSchemaRefs(readableSchema, "_readable");
                schemas[readableName] = readableSchema;
                logger.LogDebug("Created readable schema '{SchemaName}' for profile", readableName);
            }

            // Create _writable schema if profile has WriteContentType
            if (profile.WriteContentType is not null)
            {
                string writableName = $"{baseName}_writable";
                JsonObject writableSchema = JsonNode.Parse(baseSchema.ToJsonString())!.AsObject();
                FilterSchemaForWritable(writableSchema, profile.WriteContentType);
                UpdateNestedSchemaRefs(writableSchema, "_writable");
                schemas[writableName] = writableSchema;
                logger.LogDebug("Created writable schema '{SchemaName}' for profile", writableName);
            }
        }
    }

    /// <summary>
    /// Filters paths to only include resources covered by the profile.
    /// Updates Accept/Content-Type headers for profile-specific content types.
    /// </summary>
    private void FilterPaths(JsonNode specification, ProfileDefinition profileDefinition)
    {
        if (specification["paths"] is not JsonObject paths)
        {
            return;
        }

        // Build a lookup of resource profiles by lowercase resource name
        var resourceProfilesByName = profileDefinition.Resources.ToDictionary(
            r => r.ResourceName.ToLowerInvariant(),
            r => r
        );

        var pathsToRemove = new List<string>();

        foreach ((string pathKey, JsonNode? pathValue) in paths)
        {
            if (pathValue is not JsonObject pathObject)
            {
                continue;
            }

            // Extract resource name from path (e.g., "/ed-fi/students" -> "student")
            string? resourceName = ExtractResourceNameFromPath(pathKey);
            if (resourceName is null)
            {
                continue;
            }

            // Check if this resource is covered by the profile
            if (
                !resourceProfilesByName.TryGetValue(
                    resourceName.ToLowerInvariant(),
                    out ResourceProfile? resourceProfile
                )
            )
            {
                pathsToRemove.Add(pathKey);
                continue;
            }

            // Update operations based on profile content types
            UpdatePathOperations(pathObject, profileDefinition.ProfileName, resourceProfile, resourceName);
        }

        // Remove paths for resources not in the profile
        foreach (string pathToRemove in pathsToRemove)
        {
            paths.Remove(pathToRemove);
            logger.LogDebug(
                "Removed path '{Path}' from profile OpenAPI spec - resource not covered by profile",
                pathToRemove
            );
        }
    }

    /// <summary>
    /// Extracts the singular resource name from an OpenAPI path.
    /// </summary>
    private static string? ExtractResourceNameFromPath(string path)
    {
        // Path format: /ed-fi/students or /ed-fi/students/{id}
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        // Get the resource segment (usually the second one after namespace)
        string resourceSegment = segments[1];

        // Convert plural to singular (basic conversion)
        return ConvertPluralToSingular(resourceSegment);
    }

    /// <summary>
    /// Basic plural to singular conversion for resource names.
    /// </summary>
    private static string ConvertPluralToSingular(string plural)
    {
        // Handle common cases
        if (plural.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
        {
            return plural[..^3] + "y";
        }
        if (
            plural.EndsWith("ses", StringComparison.OrdinalIgnoreCase)
            || plural.EndsWith("xes", StringComparison.OrdinalIgnoreCase)
            || plural.EndsWith("ches", StringComparison.OrdinalIgnoreCase)
            || plural.EndsWith("shes", StringComparison.OrdinalIgnoreCase)
        )
        {
            return plural[..^2];
        }
        if (plural.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return plural[..^1];
        }
        return plural;
    }

    /// <summary>
    /// Updates path operations with profile-specific content types and schema references.
    /// </summary>
    private void UpdatePathOperations(
        JsonObject pathObject,
        string profileName,
        ResourceProfile resourceProfile,
        string resourceName
    )
    {
        // Update GET operations (use readable content type and _readable schema)
        if (pathObject["get"] is JsonObject getOperation && resourceProfile.ReadContentType is not null)
        {
            UpdateOperationContentType(getOperation, profileName, resourceName, "readable", isRequest: false);
        }
        else if (pathObject["get"] is not null && resourceProfile.ReadContentType is null)
        {
            // Remove GET if profile doesn't have read content type
            pathObject.Remove("get");
            logger.LogDebug(
                "Removed GET operation for '{Resource}' - profile has no readable content type",
                resourceName
            );
        }

        // Update POST operations (use writable content type and _writable schema)
        if (pathObject["post"] is JsonObject postOperation && resourceProfile.WriteContentType is not null)
        {
            UpdateOperationContentType(postOperation, profileName, resourceName, "writable", isRequest: true);
        }
        else if (pathObject["post"] is not null && resourceProfile.WriteContentType is null)
        {
            // Remove POST if profile doesn't have write content type
            pathObject.Remove("post");
            logger.LogDebug(
                "Removed POST operation for '{Resource}' - profile has no writable content type",
                resourceName
            );
        }

        // Update PUT operations (use writable content type and _writable schema)
        if (pathObject["put"] is JsonObject putOperation && resourceProfile.WriteContentType is not null)
        {
            UpdateOperationContentType(putOperation, profileName, resourceName, "writable", isRequest: true);
        }
        else if (pathObject["put"] is not null && resourceProfile.WriteContentType is null)
        {
            // Remove PUT if profile doesn't have write content type
            pathObject.Remove("put");
            logger.LogDebug(
                "Removed PUT operation for '{Resource}' - profile has no writable content type",
                resourceName
            );
        }

        // DELETE operations are not affected by profiles
    }

    /// <summary>
    /// Updates an operation's content type and schema references.
    /// </summary>
    private static void UpdateOperationContentType(
        JsonObject operation,
        string profileName,
        string resourceName,
        string usageType,
        bool isRequest
    )
    {
        string profileContentType =
            $"application/vnd.ed-fi.{resourceName.ToLowerInvariant()}.{profileName.ToLowerInvariant()}.{usageType}+json";
        string schemaSuffix = $"_{usageType}";

        if (isRequest)
        {
            // Update requestBody content type and schema reference
            if (
                operation["requestBody"] is JsonObject requestBody
                && requestBody["content"] is JsonObject requestContent
                && requestContent["application/json"] is JsonNode jsonContent
            )
            {
                // Clone and update schema $ref
                JsonNode clonedContent = JsonNode.Parse(jsonContent.ToJsonString())!;
                UpdateSchemaRef(clonedContent, schemaSuffix);

                requestContent[profileContentType] = clonedContent;
                requestContent.Remove("application/json");
            }
        }
        else
        {
            // Update responses content type and schema references (for Accept header)
            if (operation["responses"] is not JsonObject responses)
            {
                return;
            }

            foreach ((string _, JsonNode? responseValue) in responses)
            {
                if (
                    responseValue is JsonObject response
                    && response["content"] is JsonObject responseContent
                    && responseContent["application/json"] is JsonNode jsonContent
                )
                {
                    // Clone and update schema $ref
                    JsonNode clonedContent = JsonNode.Parse(jsonContent.ToJsonString())!;
                    UpdateSchemaRef(clonedContent, schemaSuffix);

                    responseContent[profileContentType] = clonedContent;
                    responseContent.Remove("application/json");
                }
            }
        }
    }

    /// <summary>
    /// Updates $ref values in a JSON node to append a suffix (e.g., _readable or _writable).
    /// </summary>
    private static void UpdateSchemaRef(JsonNode node, string suffix)
    {
        if (node is JsonObject obj)
        {
            // Check if this object has a $ref
            if (
                obj["$ref"] is JsonValue refValue
                && refValue.GetValueKind() == System.Text.Json.JsonValueKind.String
            )
            {
                string refString = refValue.GetValue<string>();
                // Update ref from "#/components/schemas/EdFi_Student" to "#/components/schemas/EdFi_Student_readable"
                if (refString.StartsWith("#/components/schemas/"))
                {
                    obj["$ref"] = $"{refString}{suffix}";
                }
            }

            // Check schema property which may contain $ref
            if (obj["schema"] is JsonObject schemaObj)
            {
                UpdateSchemaRef(schemaObj, suffix);
            }

            // Check items for array schemas
            if (obj["items"] is JsonObject itemsObj)
            {
                UpdateSchemaRef(itemsObj, suffix);
            }

            // Recurse into properties
            foreach (JsonNode? childValue in obj.Select(kvp => kvp.Value).Where(v => v is not null))
            {
                UpdateSchemaRef(childValue!, suffix);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode item in arr.Where(item => item is not null)!)
            {
                UpdateSchemaRef(item, suffix);
            }
        }
    }

    /// <summary>
    /// Extracts the resource name from a schema name.
    /// </summary>
    private static string? ExtractResourceNameFromSchemaName(string schemaName)
    {
        // Schema names are typically like "EdFi_Student" or "TPDM_Candidate"
        int underscoreIndex = schemaName.IndexOf('_');
        if (underscoreIndex > 0 && underscoreIndex < schemaName.Length - 1)
        {
            return schemaName[(underscoreIndex + 1)..];
        }
        return null;
    }

    /// <summary>
    /// Filters schema properties for readable (GET response) schemas.
    /// Per Ed-Fi rules:
    /// - MUST include _etag, _lastModifiedDate
    /// - MUST include server-generated id
    /// - MAY include renamed or flattened fields
    /// - References MUST use *_readable variants
    /// </summary>
    private void FilterSchemaForReadable(JsonObject schemaObject, ContentTypeDefinition contentType)
    {
        if (schemaObject["properties"] is not JsonObject properties)
        {
            return;
        }

        // Collect property rules from the content type
        var allowedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPropertyRules(contentType, allowedProperties, excludedProperties);

        // Determine which properties to remove based on MemberSelection mode
        var propertiesToRemove = new List<string>();

        foreach ((string propertyName, JsonNode? _) in properties)
        {
            // Readable schemas: Always keep metadata and identifying properties
            if (IsReadableRequiredProperty(propertyName))
            {
                continue;
            }

            bool shouldRemove = contentType.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !allowedProperties.Contains(propertyName),
                MemberSelection.ExcludeOnly => excludedProperties.Contains(propertyName),
                MemberSelection.IncludeAll => false,
                _ => false,
            };

            if (shouldRemove)
            {
                propertiesToRemove.Add(propertyName);
            }
        }

        // Remove filtered properties
        foreach (string propertyName in propertiesToRemove)
        {
            properties.Remove(propertyName);
            logger.LogDebug("Removed property '{Property}' from readable schema", propertyName);
        }

        // Update required array to remove filtered properties
        UpdateRequiredArray(schemaObject, propertiesToRemove);
    }

    /// <summary>
    /// Filters schema properties for writable (POST/PUT request) schemas.
    /// Per Ed-Fi rules:
    /// - MUST include natural identity fields (x-Ed-Fi-isIdentity = true)
    /// - MUST NOT include server-generated surrogate id
    /// - MUST exclude _etag, _lastModifiedDate, computed fields
    /// - References MUST use *_writable variants
    /// </summary>
    private void FilterSchemaForWritable(JsonObject schemaObject, ContentTypeDefinition contentType)
    {
        if (schemaObject["properties"] is not JsonObject properties)
        {
            return;
        }

        // Collect property rules from the content type
        var allowedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPropertyRules(contentType, allowedProperties, excludedProperties);

        // Writable schemas MUST exclude these server-generated/metadata fields
        var writableExcludedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "_etag",
            "_lastModifiedDate",
            "link",
        };

        // Determine which properties to remove based on MemberSelection mode
        var propertiesToRemove = new List<string>();

        foreach ((string propertyName, JsonNode? _) in properties)
        {
            // Writable schemas: Always exclude server-generated fields
            if (writableExcludedFields.Contains(propertyName))
            {
                propertiesToRemove.Add(propertyName);
                continue;
            }

            // Keep identity fields (UniqueId, References)
            if (IsWritableIdentityProperty(propertyName))
            {
                continue;
            }

            bool shouldRemove = contentType.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !allowedProperties.Contains(propertyName),
                MemberSelection.ExcludeOnly => excludedProperties.Contains(propertyName),
                MemberSelection.IncludeAll => false,
                _ => false,
            };

            if (shouldRemove)
            {
                propertiesToRemove.Add(propertyName);
            }
        }

        // Remove filtered properties
        foreach (string propertyName in propertiesToRemove)
        {
            properties.Remove(propertyName);
            logger.LogDebug("Removed property '{Property}' from writable schema", propertyName);
        }

        // Update required array to remove filtered properties
        UpdateRequiredArray(schemaObject, propertiesToRemove);
    }

    /// <summary>
    /// Updates nested $ref values within a schema to use the appropriate suffix.
    /// This ensures collections and nested objects reference the correct schema variant.
    /// </summary>
    private static void UpdateNestedSchemaRefs(JsonObject schemaObject, string suffix)
    {
        if (schemaObject["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach ((string _, JsonNode? propertyValue) in properties)
        {
            if (propertyValue is JsonObject propObj)
            {
                // Handle direct $ref
                if (
                    propObj["$ref"] is JsonValue refValue
                    && refValue.GetValueKind() == System.Text.Json.JsonValueKind.String
                )
                {
                    string refString = refValue.GetValue<string>();
                    if (refString.StartsWith("#/components/schemas/"))
                    {
                        propObj["$ref"] = $"{refString}{suffix}";
                    }
                }

                // Handle array items with $ref
                if (
                    propObj["items"] is JsonObject itemsObj
                    && itemsObj["$ref"] is JsonValue itemsRef
                    && itemsRef.GetValueKind() == System.Text.Json.JsonValueKind.String
                )
                {
                    string refString = itemsRef.GetValue<string>();
                    if (refString.StartsWith("#/components/schemas/"))
                    {
                        itemsObj["$ref"] = $"{refString}{suffix}";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Collects property names from content type definition rules.
    /// </summary>
    private static void CollectPropertyRules(
        ContentTypeDefinition contentType,
        HashSet<string> allowedProperties,
        HashSet<string> excludedProperties
    )
    {
        // Add top-level properties
        var propertyNames = contentType.Properties.Select(prop => prop.Name);
        if (contentType.MemberSelection == MemberSelection.ExcludeOnly)
        {
            excludedProperties.UnionWith(propertyNames);
        }
        else
        {
            allowedProperties.UnionWith(propertyNames);
        }

        // Add object rules
        allowedProperties.UnionWith(contentType.Objects.Select(obj => obj.Name));

        // Add collection rules
        allowedProperties.UnionWith(contentType.Collections.Select(coll => coll.Name));

        // Add extension rules (extensions are typically under _ext)
        if (contentType.Extensions.Count > 0)
        {
            allowedProperties.Add("_ext");
        }
    }

    /// <summary>
    /// Determines if a property is required in readable schemas.
    /// Readable schemas MUST include metadata fields and server-generated identifiers.
    /// </summary>
    private static bool IsReadableRequiredProperty(string propertyName)
    {
        return propertyName.Equals("id", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("link", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("_etag", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("_lastModifiedDate", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("Reference", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("UniqueId", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines if a property is an identity property for writable schemas.
    /// Writable schemas MUST include natural identity fields but NOT server-generated fields.
    /// </summary>
    private static bool IsWritableIdentityProperty(string propertyName)
    {
        // Natural identity fields (UniqueId) and references are kept in writable
        // Server-generated fields (id, _etag, _lastModifiedDate, link) are excluded elsewhere
        return propertyName.EndsWith("Reference", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("UniqueId", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Updates the required array to remove properties that have been filtered out.
    /// </summary>
    private static void UpdateRequiredArray(JsonObject schemaObject, List<string> removedProperties)
    {
        if (schemaObject["required"] is not JsonArray requiredArray || removedProperties.Count == 0)
        {
            return;
        }

        var removedSet = removedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indicesToRemove = new List<int>();

        for (int i = 0; i < requiredArray.Count; i++)
        {
            string? requiredProp = requiredArray[i]?.GetValue<string>();
            if (requiredProp is not null && removedSet.Contains(requiredProp))
            {
                indicesToRemove.Add(i);
            }
        }

        // Remove in reverse order to maintain correct indices
        for (int i = indicesToRemove.Count - 1; i >= 0; i--)
        {
            requiredArray.RemoveAt(indicesToRemove[i]);
        }
    }

    /// <summary>
    /// Removes tags that are not referenced by any remaining paths.
    /// </summary>
    private static void RemoveUnusedTags(JsonNode specification)
    {
        if (specification["tags"] is not JsonArray tags || specification["paths"] is not JsonObject paths)
        {
            return;
        }

        // Collect all tag names used in paths
        var usedTagNames = new HashSet<string>();

        foreach ((string _, JsonNode? pathValue) in paths)
        {
            if (pathValue is not JsonObject pathObject)
            {
                continue;
            }

            foreach ((string _, JsonNode? operationValue) in pathObject)
            {
                if (operationValue is JsonObject operation && operation["tags"] is JsonArray operationTags)
                {
                    foreach (JsonNode? tagNode in operationTags)
                    {
                        string? tagName = tagNode?.GetValue<string>();
                        if (tagName is not null)
                        {
                            usedTagNames.Add(tagName);
                        }
                    }
                }
            }
        }

        // Remove tags that are not used
        var indicesToRemove = new List<int>();
        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i] is JsonObject tagObject)
            {
                string? tagName = tagObject["name"]?.GetValue<string>();
                if (tagName is not null && !usedTagNames.Contains(tagName))
                {
                    indicesToRemove.Add(i);
                }
            }
        }

        // Remove in reverse order
        for (int i = indicesToRemove.Count - 1; i >= 0; i--)
        {
            tags.RemoveAt(indicesToRemove[i]);
        }
    }
}
