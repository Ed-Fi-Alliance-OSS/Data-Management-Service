// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that prepares and provides the API schema documents for pipeline processing.
/// Merges core API schemas with extension schemas, resolving extension data into the core
/// resources to create a unified schema representation that supports both standard and
/// extended Ed-Fi data models.
/// </summary>
internal class ProvideApiSchemaMiddleware(IApiSchemaProvider apiSchemaProvider, ILogger logger)
    : IPipelineStep
{
    // Lazy-loaded merged schema documents that refresh when schema is reloaded
    private readonly VersionedLazy<ApiSchemaDocuments> _apiSchemaDocuments =
        new VersionedLazy<ApiSchemaDocuments>(
            () =>
            {
                var apiSchemaNodes = apiSchemaProvider.GetApiSchemaNodes();

                // Clone to not mutate the original schema
                var coreApiSchema = apiSchemaNodes.CoreApiSchemaRootNode.DeepClone();

                List<JsonNode> coreResources = coreApiSchema
                    .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", logger)
                    .SelectNodesFromPropertyValues();

                string[] nodeKeys =
                [
                    "dateTimeJsonPaths",
                    "booleanJsonPaths",
                    "numericJsonPaths",
                    "documentPathsMapping",
                    "jsonSchemaForInsert.properties",
                    "equalityConstraints",
                    "arrayUniquenessConstraints",
                ];

                foreach (JsonNode extension in apiSchemaNodes.ExtensionApiSchemaRootNodes)
                {
                    List<JsonNode> extensionResources = extension
                        .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", logger)
                        .SelectNodesFromPropertyValues();

                    // Iterates over a list of node keys and calls
                    // CopyResourceExtensionNodeToCore for each key, transferring
                    // data from extensionResources to coreResources
                    foreach (var nodeKey in nodeKeys)
                    {
                        CopyResourceExtensionNodeToCore(extensionResources, coreResources, nodeKey, logger);
                    }
                }

                return new ApiSchemaDocuments(
                    apiSchemaNodes with
                    {
                        CoreApiSchemaRootNode = coreApiSchema,
                    },
                    logger
                );
            },
            () => apiSchemaProvider.ReloadId
        );

    /// <summary>
    /// Provides the merged API schema documents to the requestInfo.
    /// This makes the unified schema available to all subsequent
    /// pipeline steps for request processing and validation.
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ProvideApiSchemaMiddleware- {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.ApiSchemaDocuments = _apiSchemaDocuments.Value;
        await next();
    }

    private static JsonNode GetNodeByPath(JsonNode resources, string path)
    {
        foreach (var key in path.Split('.'))
        {
            resources = resources.GetRequiredNode(key);
        }
        return resources;
    }

    /// <summary>
    /// Merges extension resource data into core resources by copying specific nodes identified
    /// by the nodeKey.
    /// </summary>
    private static void CopyResourceExtensionNodeToCore(
        List<JsonNode> extensionResources,
        List<JsonNode> coreResources,
        string nodeKey,
        ILogger logger
    )
    {
        Dictionary<string, JsonNode> coreResourceByName = coreResources.ToDictionary(coreResource =>
            coreResource.GetRequiredNode("resourceName").GetValue<string>()
        );

        foreach (
            JsonNode extensionResource in extensionResources.Where(extensionResource =>
                extensionResource.GetRequiredNode("isResourceExtension").GetValue<bool>()
            )
        )
        {
            var coreResource = coreResourceByName[
                extensionResource.GetRequiredNode("resourceName").GetValue<string>()
            ];

            var sourceExtensionNode = GetNodeByPath(extensionResource, nodeKey);

            var targetCoreNode = GetNodeByPath(coreResource, nodeKey);

            var nodeValueKind = targetCoreNode.GetValueKind();

            switch (nodeValueKind)
            {
                case JsonValueKind.Object:
                    MergeExtensionObjectIntoCore(logger, sourceExtensionNode, targetCoreNode);
                    break;
                case JsonValueKind.Array:
                    var targetArray = targetCoreNode.AsArray();
                    foreach (var sourceItem in sourceExtensionNode.AsArray())
                    {
                        targetArray.Add(sourceItem?.DeepClone());
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The value type '{nodeValueKind}' is not supported."
                    );
            }
        }
    }

    /// <summary>
    /// Merges JSON object properties from an extension schema into a core schema object.
    /// </summary>
    private static void MergeExtensionObjectIntoCore(
        ILogger logger,
        JsonNode sourceExtensionNode,
        JsonNode targetCoreNode
    )
    {
        var targetObject = targetCoreNode.AsObject();
        foreach (KeyValuePair<string, JsonNode?> sourceObject in sourceExtensionNode.AsObject())
        {
            // DMS-591 Ticket to fix duplicate key for Sample Extension in Common extension EdFi.Address in SampleMetaEd
            // Remove this condition once DMS-591 is fixed
            if (
                targetObject.ContainsKey(sourceObject.Key)
                && !string.Equals(sourceObject.Key, "_ext", StringComparison.InvariantCultureIgnoreCase)
            )
            {
                logger.LogWarning(
                    "Duplicate Key exists for Sample Extension related with Common extension EdFi.Address. Key:{Key}",
                    sourceObject.Key
                );
            }
            else
            {
                // If _ext exists in the target, merge its properties from the source.
                // Otherwise, add _ext with its properties.
                if (
                    string.Equals(sourceObject.Key, "_ext", StringComparison.InvariantCultureIgnoreCase)
                    && targetObject["_ext"]?["properties"] is JsonObject existingProps
                    && sourceObject.Value?.DeepClone()?["properties"] is JsonObject newProps
                )
                {
                    foreach (var item in newProps)
                    {
                        existingProps[item.Key] = item.Value?.DeepClone();
                    }
                }
                else
                {
                    targetObject.Add(new(sourceObject.Key, sourceObject.Value?.DeepClone()));
                }
            }
        }
    }
}
