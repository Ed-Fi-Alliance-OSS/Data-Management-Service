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
public class OpenApiDocument(ILogger _logger)
{
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

            // Get the component.schema location for _ext insert
            JsonObject locationForExt =
                openApiCoreResources
                    .SelectNodeFromPath($"$.components.schemas.{componentSchemaName}.properties", _logger)
                    ?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment expects Core to have '$.components.schemas.{componentSchemaName}.properties'. Extension fragment validation failed?"
                );

            string extensionSchemaName = $"{componentSchemaName}Extension";

            // Add _ext if is not already there
            if (locationForExt["_ext"] == null)
            {
                locationForExt.Add(
                    "_ext",
                    JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{extensionSchemaName}\" }}")
                );
            }

            JsonObject componentsSchemas =
                openApiCoreResources.SelectNodeFromPath("$.components.schemas", _logger)?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI core resources missing 'components.schemas'."
                );

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

            JsonObject extensionProperties =
                extensionSchema["properties"]?.AsObject()
                ?? throw new InvalidOperationException(
                    $"Extension schema '{extensionSchemaName}' missing 'properties'."
                );

            // Name of the specific project schema
            string projectExtensionSchemaName = $"{projectName}_{componentSchemaName}Extension";

            // Add reference to the specific project schema
            if (!extensionProperties.ContainsKey(projectName))
            {
                extensionProperties.Add(
                    projectName,
                    JsonNode.Parse($"{{ \"$ref\": \"#/components/schemas/{projectExtensionSchemaName}\" }}")
                );
            }

            // Add the specific project schema if it doesn't exist
            if (!componentsSchemas.ContainsKey(projectExtensionSchemaName))
            {
                componentsSchemas.Add(projectExtensionSchemaName, extObject.DeepClone());
            }
        }
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

        return openApiSpecification;
    }
}
