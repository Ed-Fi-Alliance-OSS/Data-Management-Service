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

internal class ProvideApiSchemaMiddleware(IApiSchemaProvider _apiSchemaProvider, ILogger _logger)
    : IPipelineStep
{
    private readonly Lazy<ApiSchemaDocuments> _apiSchemaDocuments = new(() =>
    {
        var apiSchemaNodes = _apiSchemaProvider.GetApiSchemaNodes();

        // Clone to not mutate the original schema
        var coreApiSchema = apiSchemaNodes.CoreApiSchemaRootNode.DeepClone();

        // [DMS-597] Workaround for DMS-630 Non-part-of-identity Namespace fields should also be securityElements
        var educationContentsNamespaceSecurityElementsNode = coreApiSchema["projectSchema"]
            ?["resourceSchemas"]
            ?["educationContents"]
            ?["securityElements"]
            ?["Namespace"];
        educationContentsNamespaceSecurityElementsNode?.ReplaceWith(new JsonArray("$.namespace"));

        List<JsonNode> coreResources = coreApiSchema
            .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", _logger)
            .SelectNodesFromPropertyValues();

        string[] nodeKeys =
        [
            "dateTimeJsonPaths",
            "booleanJsonPaths",
            "numericJsonPaths",
            "documentPathsMapping",
            "jsonSchemaForInsert.properties",
            "equalityConstraints",
        ];

        foreach (JsonNode extension in apiSchemaNodes.ExtensionApiSchemaRootNodes)
        {
            List<JsonNode> extensionResources = extension
                .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", _logger)
                .SelectNodesFromPropertyValues();

            // Iterates over a list of node keys and calls
            // CopyResourceExtensionNodeToCore for each key, transferring
            // data from extensionResources to coreResources
            foreach (var nodeKey in nodeKeys)
            {
                CopyResourceExtensionNodeToCore(extensionResources, coreResources, nodeKey, _logger);
            }
        }

        return new ApiSchemaDocuments(apiSchemaNodes with { CoreApiSchemaRootNode = coreApiSchema }, _logger);
    });

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideApiSchemaMiddleware- {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        context.ApiSchemaDocuments = _apiSchemaDocuments.Value;
        await next();
    }

    /// <summary>
    /// Copies the <see cref="JsonNode"/> present at the <paramref name="path"/> from
    /// <paramref name="extensionResources"/> into <paramref name="resources"/>.
    /// Note that <paramref name="path"/> gets path  in the resources.
    /// </summary>
    private static JsonNode GetNodeByPath(JsonNode resources, string path)
    {
        foreach (var key in path.Split('.'))
        {
            resources = resources.GetRequiredNode(key);
        }
        return resources;
    }

    /// <summary>
    /// Copies the <see cref="JsonNode"/> present at the <paramref name="nodeKey"/> from
    /// <paramref name="extensionResources"/> into <paramref name="coreResources"/>.
    /// Note that <paramref name="coreResources"/> gets mutated in the process.
    /// </summary>
    private static void CopyResourceExtensionNodeToCore(
        List<JsonNode> extensionResources,
        List<JsonNode> coreResources,
        string nodeKey,
        ILogger _logger
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
                    var targetObject = targetCoreNode.AsObject();
                    foreach (var sourceObject in sourceExtensionNode.AsObject())
                    {
                        // DMS-591 Ticket to fix duplicate key for Sample Extension in Common extension EdFi.Address in SampleMetaEd
                        // Remove this condition once DMS-591 is fixed
                        if (
                            targetObject.ContainsKey(sourceObject.Key)
                            && !string.Equals(
                                sourceObject.Key,
                                "_ext",
                                StringComparison.InvariantCultureIgnoreCase
                            )
                        )
                        {
                            _logger.LogWarning(
                                "Duplicate Key exists for Sample Extension related with Common extension EdFi.Address. Key:{Key}",
                                sourceObject.Key
                            );
                        }
                        else
                        {
                            // If _ext exists in the target, merge its properties from the source.
                            // Otherwise, add _ext with its properties.
                            if (
                                string.Equals(
                                    sourceObject.Key,
                                    "_ext",
                                    StringComparison.InvariantCultureIgnoreCase
                                )
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
}
