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

        List<JsonNode> coreResources = coreApiSchema
            .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", _logger)
            .SelectNodesFromPropertyValues();

        foreach (JsonNode extension in apiSchemaNodes.ExtensionApiSchemaRootNodes)
        {
            List<JsonNode> extensionResources = extension
                .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", _logger)
                .SelectNodesFromPropertyValues();

            CopyResourceExtensionNodeToCore(extensionResources, coreResources, "dateTimeJsonPaths");
            CopyResourceExtensionNodeToCore(extensionResources, coreResources, "booleanJsonPaths");
            CopyResourceExtensionNodeToCore(extensionResources, coreResources, "numericJsonPaths");
            CopyResourceExtensionNodeToCore(extensionResources, coreResources, "documentPathsMapping");
            CopyResourceExtensionNodeToCore(
                extensionResources,
                coreResources,
                "jsonSchemaForInsert.properties"
            );
            CopyResourceExtensionNodeToCore(extensionResources, coreResources, "equalityConstraints");
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
        string nodeKey
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
                        targetObject.Add(new(sourceObject.Key, sourceObject.Value?.DeepClone()));
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
