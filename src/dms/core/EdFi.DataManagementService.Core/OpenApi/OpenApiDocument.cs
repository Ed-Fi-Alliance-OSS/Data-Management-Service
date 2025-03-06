// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

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
    private void InsertExts(JsonObject extList, List<JsonNode> openApiSpecificationList)
    {
        foreach (JsonNode openApiSpecification in openApiSpecificationList)
        {
            foreach ((string componentSchemaName, JsonNode? extObject) in extList)
            {
                if (extObject == null)
                {
                    throw new InvalidOperationException(
                        $"OpenAPI extension fragment has empty exts schema name '{componentSchemaName}'. Extension fragment validation failed?"
                    );
                }

                // Get the component.schema location for _ext insert
                JsonObject locationForExt =
                    openApiSpecification
                        .SelectNodeFromPath($"$.components.schemas.{componentSchemaName}.properties", _logger)
                        ?.AsObject()
                    ?? throw new InvalidOperationException(
                        $"OpenAPI extension fragment expects Core to have '$.components.schemas.EdFi_{componentSchemaName}.properties'. Extension fragment validation failed?"
                    );

                // If _ext has already been added by another extension, we don't support a second one
                if (locationForExt["_ext"] != null)
                {
                    throw new InvalidOperationException(
                        $"OpenAPI extension fragment tried to add a second _ext to '$.components.schemas.EdFi_{componentSchemaName}.properties', which is not supported. Extension fragment validation failed?"
                    );
                }

                locationForExt.Add("_ext", extObject.DeepClone());
            }
        }
    }

    /// <summary>
    /// Inserts new endpoint paths from extension OpenAPI fragments into the paths section of the corresponding
    /// core OpenAPI endpoint.
    /// </summary>
    private void InsertNewPaths(JsonObject newPaths, List<JsonNode> openApiSpecificationList)
    {
        foreach (JsonNode openApiSpecification in openApiSpecificationList)
        {
            foreach ((string pathName, JsonNode? pathObject) in newPaths)
            {
                if (pathObject == null)
                {
                    throw new InvalidOperationException(
                        $"OpenAPI extension fragment has empty newPaths path name '{pathName}'. Extension fragment validation failed?"
                    );
                }

                JsonObject locationForPaths = openApiSpecification
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
    }

    /// <summary>
    /// Inserts new schema objects from extension OpenAPI fragments into the components.schemas section of the
    /// core OpenAPI specification.
    /// </summary>
    private void InsertNewSchemas(JsonObject newSchemas, List<JsonNode> openApiSpecificationList)
    {
        foreach (JsonNode openApiSpecification in openApiSpecificationList)
        {
            foreach ((string schemaName, JsonNode? schemaObject) in newSchemas)
            {
                if (schemaObject == null)
                {
                    throw new InvalidOperationException(
                        $"OpenAPI extension fragment has empty newSchemas path name '{schemaName}'. Extension fragment validation failed?"
                    );
                }

                JsonObject locationForSchemas = openApiSpecification
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
    }

    /// <summary>
    /// Inserts new global tag objects from extension OpenAPI fragments into the tags section of the
    /// core OpenAPI specification.
    /// </summary>
    private void InsertNewTags(JsonArray newTagObjects, List<JsonNode> openApiSpecificationList)
    {
        foreach (JsonNode openApiSpecification in openApiSpecificationList)
        {
            // This is where the extension tags will be added
            JsonArray globalTags = openApiSpecification
                .SelectRequiredNodeFromPath("$.tags", _logger)
                .AsArray();

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
                    throw new InvalidOperationException(
                        $"OpenAPI extension fragment tried to add a second tag named '{tagObjectName}', which is not supported. Extension fragment validation failed?"
                    );
                }

                globalTags.Add(newTagObject.DeepClone());
            }
        }
    }

    /// <summary>
    /// Finds the openApiCoreResources and openApiCoreDescriptors in an Core ApiSchema document.
    /// </summary>
    public List<JsonNode> FindCoreOpenApiSpecification(JsonNode coreApiSchemaRootNode)
    {
        string[] paths = new string[]
        {
            "$.projectSchema.openApiCoreResources",
            "$.projectSchema.openApiCoreDescriptors",
        };

        List<JsonNode> selectedNodes = new List<JsonNode>();

        // Iterate over the paths and select the nodes
        foreach (var path in paths)
        {
            JsonNode node = coreApiSchemaRootNode.SelectRequiredNodeFromPath(path, _logger);
            if (node != null)
            {
                selectedNodes.Add(node.DeepClone());
            }
        }

        return selectedNodes;
    }

    /// <summary>
    /// Finds the openApiExtensionDescriptors and openApiExtensionResources in extension ApiSchemaDocument.
    /// </summary>
    public List<JsonNode> FindOpenApiExtensionFragments(JsonNode extensionApiSchemaRootNode)
    {
        string[] paths = new string[]
        {
            "$.projectSchema.openApiExtensionResourceFragments",
            "$.projectSchema.openApiExtensionDescriptorFragments",
        };

        List<JsonNode> selectedNodes = new List<JsonNode>();

        // Iterate over the paths and select the nodes
        foreach (var path in paths)
        {
            JsonNode node = extensionApiSchemaRootNode.SelectRequiredNodeFromPath(path, _logger);
            if (node != null)
            {
                selectedNodes.Add(node.DeepClone());
            }
        }

        return selectedNodes;
    }

    /// <summary>
    /// Creates an OpenAPI specification derived from the given core and extension ApiSchemas
    /// </summary>
    public JsonNode CreateDocument(ApiSchemaNodes apiSchemas)
    {
        // Get the core OpenAPI spec as a copy since we are going to modify it
        List<JsonNode> openApiSpecification = FindCoreOpenApiSpecification(apiSchemas.CoreApiSchemaRootNode);

        // Get each extension OpenAPI fragment to insert into core OpenAPI spec
        foreach (JsonNode extensionApiSchemaRootNode in apiSchemas.ExtensionApiSchemaRootNodes)
        {
            List<JsonNode> openApiExtensionFragments = FindOpenApiExtensionFragments(extensionApiSchemaRootNode);

            foreach (JsonNode openApiExtensionFragment in openApiExtensionFragments)
            {
                InsertExts(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.exts", _logger).AsObject(),
                    openApiSpecification
                );

                InsertNewPaths(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.newPaths", _logger).AsObject(),
                    openApiSpecification
                );

                InsertNewSchemas(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.newSchemas", _logger).AsObject(),
                    openApiSpecification
                );

                InsertNewTags(
                    openApiExtensionFragment.SelectRequiredNodeFromPath("$.newTags", _logger).AsArray(),
                    openApiSpecification
                );
            }
        }

        return MergeJsonNodesIntoArray(openApiSpecification);
    }

    private static JsonNode MergeJsonNodesIntoArray(List<JsonNode> openApiSpecification)
    {
        JsonArray mergedArray = new JsonArray();

        foreach (var node in openApiSpecification)
        {
            mergedArray.Add(node.DeepClone());
        }

        return mergedArray;
    }
}
