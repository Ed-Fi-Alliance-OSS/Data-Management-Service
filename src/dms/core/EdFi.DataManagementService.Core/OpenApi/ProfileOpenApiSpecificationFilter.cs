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
    /// Server-generated metadata fields that are always included in readable schemas
    /// and always excluded from writable schemas.
    /// </summary>
    private static readonly HashSet<string> _serverGeneratedFields =
    [
        "id",
        "link",
        "_etag",
        "_lastModifiedDate",
    ];

    /// <summary>
    /// Property name suffixes that identify natural key and reference fields.
    /// These are always included in both readable and writable schemas.
    /// </summary>
    private static readonly string[] _identityFieldSuffixes = ["Reference", "UniqueId"];

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
        JsonNode specification = baseSpecification.DeepClone();

        // Update info.title with profile name
        UpdateInfoTitle(specification, profileDefinition.ProfileName);

        // Step 1: Filter paths to only include resources covered by the profile and remove disallowed operations
        FilterPaths(specification, profileDefinition);

        // Step 2: Remove unused component parameters now that paths are filtered
        RemoveUnusedParameters(specification);

        // Step 3: Remove schemas not referenced by filtered paths (keep transitive refs)
        RemoveUnusedSchemas(specification);

        // Step 4: Create _readable/_writable schemas and rewrite operations (avoids double suffixes)
        CreateProfileSchemasAndRewriteOperations(specification, profileDefinition);

        // Step 5: Remove base schemas that now have suffixed versions
        RemoveBaseSchemasWithSuffixedVersions(specification);

        // Step 6: Final cleanup - remove any schemas orphaned after profile schema creation
        RemoveUnusedSchemas(specification);

        // Step 7: Remove unused tags
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
    /// Creates _readable/_writable schemas for profile resources and rewrites operations.
    /// Ensures suffixes are only added once to avoid double-suffixing.
    /// </summary>
    private void CreateProfileSchemasAndRewriteOperations(
        JsonNode specification,
        ProfileDefinition profileDefinition
    )
    {
        if (
            specification["components"] is not JsonObject components
            || components["schemas"] is not JsonObject schemas
        )
        {
            return;
        }

        if (specification["paths"] is not JsonObject paths)
        {
            return;
        }

        // Track which suffixed schemas have been created to avoid duplicates
        var createdSuffixedSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Build resource profile lookup
        var resourceProfilesByName = profileDefinition.Resources.ToDictionary(
            r => r.ResourceName.ToLowerInvariant(),
            r => r
        );

        // Process each path and its operations
        foreach ((string pathKey, JsonNode? pathValue) in paths)
        {
            if (pathValue is not JsonObject pathObject)
            {
                continue;
            }

            string? resourceName = ExtractResourceNameFromPath(pathKey);
            if (resourceName is null)
            {
                continue;
            }

            if (
                !resourceProfilesByName.TryGetValue(
                    resourceName.ToLowerInvariant(),
                    out ResourceProfile? resourceProfile
                )
            )
            {
                continue;
            }

            // Process GET operations (readable)
            if (pathObject["get"] is JsonObject getOperation && resourceProfile.ReadContentType is not null)
            {
                ProcessOperationSchemas(
                    schemas,
                    getOperation,
                    "_readable",
                    createdSuffixedSchemas,
                    resourceProfile.ReadContentType,
                    isReadable: true
                );
                UpdateOperationContentType(
                    getOperation,
                    profileDefinition.ProfileName,
                    resourceName,
                    "readable",
                    isRequest: false
                );
            }

            // Process POST operations (writable)
            if (
                pathObject["post"] is JsonObject postOperation
                && resourceProfile.WriteContentType is not null
            )
            {
                ProcessOperationSchemas(
                    schemas,
                    postOperation,
                    "_writable",
                    createdSuffixedSchemas,
                    resourceProfile.WriteContentType,
                    isReadable: false
                );
                UpdateOperationContentType(
                    postOperation,
                    profileDefinition.ProfileName,
                    resourceName,
                    "writable",
                    isRequest: true
                );
            }

            // Process PUT operations (writable)
            if (pathObject["put"] is JsonObject putOperation && resourceProfile.WriteContentType is not null)
            {
                ProcessOperationSchemas(
                    schemas,
                    putOperation,
                    "_writable",
                    createdSuffixedSchemas,
                    resourceProfile.WriteContentType,
                    isReadable: false
                );
                UpdateOperationContentType(
                    putOperation,
                    profileDefinition.ProfileName,
                    resourceName,
                    "writable",
                    isRequest: true
                );
            }
        }
    }

    /// <summary>
    /// Processes an operation's schema references, creating suffixed versions.
    /// </summary>
    private void ProcessOperationSchemas(
        JsonObject schemas,
        JsonObject operation,
        string suffix,
        HashSet<string> createdSuffixedSchemas,
        ContentTypeDefinition contentType,
        bool isReadable
    )
    {
        // Collect all schema refs from the operation
        var schemaRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSchemaRefs(operation, schemaRefs);

        // Create suffixed schemas for each referenced schema
        foreach (string baseSchemaName in schemaRefs)
        {
            CreateSuffixedSchema(
                schemas,
                baseSchemaName,
                suffix,
                createdSuffixedSchemas,
                contentType,
                isReadable,
                isRootResource: true
            );
        }
    }

    /// <summary>
    /// Collects all schema $ref names from a JSON node.
    /// </summary>
    private static void CollectSchemaRefs(JsonNode node, HashSet<string> schemaRefs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string propertyName, JsonNode? child) in obj)
                {
                    if (child is null)
                    {
                        continue;
                    }

                    if (propertyName.Equals("$ref", StringComparison.OrdinalIgnoreCase))
                    {
                        string? refValue = child.GetValue<string?>();
                        if (refValue is null)
                        {
                            continue;
                        }

                        const string SchemaPrefix = "#/components/schemas/";
                        if (refValue.StartsWith(SchemaPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            schemaRefs.Add(refValue[SchemaPrefix.Length..]);
                        }
                        continue;
                    }

                    CollectSchemaRefs(child, schemaRefs);
                }
                break;

            case JsonArray array:
                foreach (JsonNode item in array.Where(item => item is not null)!)
                {
                    CollectSchemaRefs(item, schemaRefs);
                }
                break;
        }
    }

    /// <summary>
    /// Creates a suffixed schema version, avoiding double suffixes.
    /// </summary>
    private void CreateSuffixedSchema(
        JsonObject schemas,
        string baseSchemaName,
        string suffix,
        HashSet<string> createdSuffixedSchemas,
        ContentTypeDefinition? contentType,
        bool isReadable,
        bool isRootResource
    )
    {
        // Avoid double suffixes - if already has a profile suffix, skip
        if (HasProfileSuffix(baseSchemaName))
        {
            return;
        }

        string targetName = $"{baseSchemaName}{suffix}";

        // Skip if already created
        if (!createdSuffixedSchemas.Add(targetName))
        {
            return;
        }

        // Get the base schema
        if (
            !schemas.TryGetPropertyValue(baseSchemaName, out JsonNode? baseSchemaNode)
            || baseSchemaNode is not JsonObject baseSchemaObj
        )
        {
            return;
        }

        // Clone the schema
        JsonObject clone = baseSchemaObj.DeepClone().AsObject();

        // Apply property filtering only to root resource schemas (not referenced schemas)
        if (isRootResource && contentType is not null)
        {
            if (isReadable)
            {
                FilterSchemaForReadable(clone, contentType);
            }
            else
            {
                FilterSchemaForWritable(clone, contentType);
            }
        }

        // Collect nested schema refs before updating them
        var nestedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSchemaRefs(clone, nestedRefs);

        // Update $refs in the clone to use suffixed versions (avoids double suffixes)
        UpdateSchemaRefsInPlace(clone, suffix);

        // Add the suffixed schema
        schemas[targetName] = clone;
        logger.LogDebug("Created {Suffix} schema '{SchemaName}' for profile", suffix, targetName);

        // Recursively create suffixed versions for nested refs
        foreach (string nestedRef in nestedRefs)
        {
            CreateSuffixedSchema(
                schemas,
                nestedRef,
                suffix,
                createdSuffixedSchemas,
                contentType: null, // Don't apply property filtering to nested schemas
                isReadable,
                isRootResource: false
            );
        }
    }

    /// <summary>
    /// Checks if a schema name already has a profile suffix.
    /// </summary>
    private static bool HasProfileSuffix(string schemaName)
    {
        return schemaName.EndsWith("_readable", StringComparison.OrdinalIgnoreCase)
            || schemaName.EndsWith("_writable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Updates $ref values in place, avoiding double suffixes.
    /// </summary>
    private static void UpdateSchemaRefsInPlace(JsonNode node, string suffix)
    {
        switch (node)
        {
            case JsonObject obj:
                if (
                    obj["$ref"] is JsonValue refValue
                    && refValue.GetValueKind() == System.Text.Json.JsonValueKind.String
                )
                {
                    string refString = refValue.GetValue<string>();
                    const string SchemaPrefix = "#/components/schemas/";

                    if (refString.StartsWith(SchemaPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string schemaName = refString[SchemaPrefix.Length..];

                        // Only add suffix if not already suffixed
                        if (!HasProfileSuffix(schemaName))
                        {
                            obj["$ref"] = $"{refString}{suffix}";
                        }
                    }
                }

                foreach (JsonNode? childValue in obj.Select(kvp => kvp.Value).Where(v => v is not null))
                {
                    UpdateSchemaRefsInPlace(childValue!, suffix);
                }
                break;

            case JsonArray arr:
                foreach (JsonNode item in arr.Where(item => item is not null)!)
                {
                    UpdateSchemaRefsInPlace(item, suffix);
                }
                break;
        }
    }

    /// <summary>
    /// Removes base schemas that have suffixed versions (cleanup after profile schema creation).
    /// </summary>
    private static void RemoveBaseSchemasWithSuffixedVersions(JsonNode specification)
    {
        if (
            specification["components"] is not JsonObject components
            || components["schemas"] is not JsonObject schemas
        )
        {
            return;
        }

        // Find all base schema names that have suffixed versions
        var schemasToRemove = new List<string>();
        var suffixedSchemas = schemas
            .Where(kvp => HasProfileSuffix(kvp.Key))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach ((string schemaName, JsonNode? _) in schemas)
        {
            // Skip suffixed schemas
            if (HasProfileSuffix(schemaName))
            {
                continue;
            }

            // Check if suffixed version exists
            string readableName = $"{schemaName}_readable";
            string writableName = $"{schemaName}_writable";

            if (suffixedSchemas.Contains(readableName) || suffixedSchemas.Contains(writableName))
            {
                schemasToRemove.Add(schemaName);
            }
        }

        foreach (string schemaName in schemasToRemove)
        {
            schemas.Remove(schemaName);
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

            // Remove disallowed operations based on profile content types without rewriting schemas yet
            RemoveDisallowedOperations(pathObject, resourceProfile, resourceName);
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
    /// Removes operations that are not allowed by the profile (read/write) without rewriting content types.
    /// </summary>
    private void RemoveDisallowedOperations(
        JsonObject pathObject,
        ResourceProfile resourceProfile,
        string resourceName
    )
    {
        if (pathObject["get"] is not null && resourceProfile.ReadContentType is null)
        {
            pathObject.Remove("get");
            logger.LogDebug(
                "Removed GET operation for '{Resource}' - profile has no readable content type",
                resourceName
            );
        }

        if (pathObject["post"] is not null && resourceProfile.WriteContentType is null)
        {
            pathObject.Remove("post");
            logger.LogDebug(
                "Removed POST operation for '{Resource}' - profile has no writable content type",
                resourceName
            );
        }

        if (pathObject["put"] is not null && resourceProfile.WriteContentType is null)
        {
            pathObject.Remove("put");
            logger.LogDebug(
                "Removed PUT operation for '{Resource}' - profile has no writable content type",
                resourceName
            );
        }

        if (pathObject["delete"] is not null && resourceProfile.WriteContentType is null)
        {
            pathObject.Remove("delete");
            logger.LogDebug(
                "Removed DELETE operation for '{Resource}' - profile has no writable content type",
                resourceName
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
                JsonNode clonedContent = jsonContent.DeepClone();
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
                    JsonNode clonedContent = jsonContent.DeepClone();
                    UpdateSchemaRef(clonedContent, schemaSuffix);

                    responseContent[profileContentType] = clonedContent;
                    responseContent.Remove("application/json");
                }
            }
        }
    }

    /// <summary>
    /// Updates $ref values in a JSON node to append a suffix (e.g., _readable or _writable).
    /// Avoids double suffixes by checking if already suffixed.
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
                    string schemaName = refString["#/components/schemas/".Length..];
                    // Only add suffix if not already suffixed
                    if (!HasProfileSuffix(schemaName))
                    {
                        obj["$ref"] = $"{refString}{suffix}";
                    }
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
    /// Removes schemas that are not referenced by any remaining paths or dependent schemas.
    /// </summary>
    private static void RemoveUnusedSchemas(JsonNode specification)
    {
        if (specification["components"] is not JsonObject components)
        {
            return;
        }

        if (components["schemas"] is not JsonObject schemas)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void EnqueueSchema(string name)
        {
            if (
                keep.Add(name)
                && schemas.TryGetPropertyValue(name, out JsonNode? schemaNode)
                && schemaNode is not null
            )
            {
                ExploreNode(schemaNode);
            }
        }

        void ExploreNode(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach ((string propertyName, JsonNode? child) in obj)
                    {
                        if (child is null)
                        {
                            continue;
                        }

                        if (propertyName.Equals("$ref", StringComparison.OrdinalIgnoreCase))
                        {
                            string? refValue = child.GetValue<string?>();
                            if (refValue is null)
                            {
                                continue;
                            }

                            const string SchemaPrefix = "#/components/schemas/";
                            const string ResponsePrefix = "#/components/responses/";
                            const string ParameterPrefix = "#/components/parameters/";
                            const string RequestBodyPrefix = "#/components/requestBodies/";

                            if (refValue.StartsWith(SchemaPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                EnqueueSchema(refValue[SchemaPrefix.Length..]);
                                continue;
                            }

                            if (
                                refValue.StartsWith(ResponsePrefix, StringComparison.OrdinalIgnoreCase)
                                && components["responses"] is JsonObject responses
                                && responses.TryGetPropertyValue(
                                    refValue[ResponsePrefix.Length..],
                                    out JsonNode? response
                                )
                                && response is not null
                            )
                            {
                                ExploreNode(response);
                                continue;
                            }

                            if (
                                refValue.StartsWith(ParameterPrefix, StringComparison.OrdinalIgnoreCase)
                                && components["parameters"] is JsonObject parameters
                                && parameters.TryGetPropertyValue(
                                    refValue[ParameterPrefix.Length..],
                                    out JsonNode? parameter
                                )
                                && parameter is not null
                            )
                            {
                                ExploreNode(parameter);
                                continue;
                            }

                            if (
                                refValue.StartsWith(RequestBodyPrefix, StringComparison.OrdinalIgnoreCase)
                                && components["requestBodies"] is JsonObject requestBodies
                                && requestBodies.TryGetPropertyValue(
                                    refValue[RequestBodyPrefix.Length..],
                                    out JsonNode? requestBody
                                )
                                && requestBody is not null
                            )
                            {
                                ExploreNode(requestBody);
                            }

                            continue;
                        }

                        ExploreNode(child);
                    }
                    break;

                case JsonArray array:
                    foreach (JsonNode item in array.Where(item => item is not null)!)
                    {
                        ExploreNode(item);
                    }
                    break;
            }
        }

        // Seed references from paths
        if (specification["paths"] is JsonObject paths)
        {
            ExploreNode(paths);
        }

        // Traverse schema graph to include transitive dependencies (handled during enqueue)

        // Remove unreferenced schemas
        var schemasToRemove = schemas.Where(kvp => !keep.Contains(kvp.Key)).Select(kvp => kvp.Key).ToList();

        foreach (string schemaName in schemasToRemove)
        {
            schemas.Remove(schemaName);
        }
    }

    /// <summary>
    /// Removes component parameters that are not referenced by any filtered paths.
    /// </summary>
    private static void RemoveUnusedParameters(JsonNode specification)
    {
        if (specification["components"] is not JsonObject components)
        {
            return;
        }

        if (components["parameters"] is not JsonObject parameters)
        {
            return;
        }

        var usedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ExploreParameters(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach ((string propertyName, JsonNode? child) in obj)
                    {
                        if (child is null)
                        {
                            continue;
                        }

                        if (propertyName.Equals("$ref", StringComparison.OrdinalIgnoreCase))
                        {
                            string? refValue = child.GetValue<string?>();
                            if (refValue is null)
                            {
                                continue;
                            }

                            const string ParameterPrefix = "#/components/parameters/";
                            if (refValue.StartsWith(ParameterPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                usedParameters.Add(refValue[ParameterPrefix.Length..]);
                                continue;
                            }
                        }

                        ExploreParameters(child);
                    }
                    break;

                case JsonArray array:
                    foreach (JsonNode item in array.Where(item => item is not null)!)
                    {
                        ExploreParameters(item);
                    }
                    break;
            }
        }

        if (specification["paths"] is JsonObject paths)
        {
            ExploreParameters(paths);
        }

        var parametersToRemove = parameters
            .Where(kvp => !usedParameters.Contains(kvp.Key))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (string parameterName in parametersToRemove)
        {
            parameters.Remove(parameterName);
        }
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

        // Determine which properties to remove based on MemberSelection mode
        var propertiesToRemove = new List<string>();

        foreach ((string propertyName, JsonNode? _) in properties)
        {
            // Writable schemas: Always exclude server-generated fields
            if (_serverGeneratedFields.Contains(propertyName))
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
        var objectNames = contentType.Objects.Select(obj => obj.Name);
        if (contentType.MemberSelection == MemberSelection.ExcludeOnly)
        {
            excludedProperties.UnionWith(objectNames);
        }
        else
        {
            allowedProperties.UnionWith(objectNames);
        }

        // Add collection rules
        var collectionNames = contentType.Collections.Select(coll => coll.Name);
        if (contentType.MemberSelection == MemberSelection.ExcludeOnly)
        {
            excludedProperties.UnionWith(collectionNames);
        }
        else
        {
            allowedProperties.UnionWith(collectionNames);
        }

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
        return _serverGeneratedFields.Contains(propertyName)
            || Array.Exists(
                _identityFieldSuffixes,
                suffix => propertyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    /// Determines if a property is an identity property for writable schemas.
    /// Writable schemas MUST include natural identity fields but NOT server-generated fields.
    /// </summary>
    private static bool IsWritableIdentityProperty(string propertyName)
    {
        // Natural identity fields (UniqueId) and references are kept in writable
        // Server-generated fields are excluded elsewhere via _serverGeneratedFields
        return Array.Exists(
            _identityFieldSuffixes,
            suffix => propertyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        );
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
