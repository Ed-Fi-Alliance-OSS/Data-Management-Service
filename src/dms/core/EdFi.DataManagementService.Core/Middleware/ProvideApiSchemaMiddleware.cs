// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ProvideApiSchemaMiddleware(IApiSchemaProvider _apiSchemaProvider, ILogger _logger)
    : IPipelineStep
{
    private readonly Lazy<ApiSchemaNodes> _lazyApiSchemaNodes = new(() =>
    {
        return _apiSchemaProvider.GetApiSchemaNodes();
    });

    public ApiSchemaNodes ApiSchemaNodes => _lazyApiSchemaNodes.Value;

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideApiSchemaMiddleware- {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        var coreResourceSchemas = FindResourceSchemas(ApiSchemaNodes.CoreApiSchemaRootNode)
            .DeepClone()
            .AsObject();

        foreach (JsonNode extensionApiSchemaRootNode in ApiSchemaNodes.ExtensionApiSchemaRootNodes)
        {
            var extensionResourceSchemas = FindResourceSchemas(extensionApiSchemaRootNode).AsObject();

            InsertJsonPathsExts(extensionResourceSchemas, coreResourceSchemas, "dateTimeJsonPaths");
            InsertJsonPathsExts(extensionResourceSchemas, coreResourceSchemas, "booleanJsonPaths");
            InsertJsonPathsExts(extensionResourceSchemas, coreResourceSchemas, "numericJsonPaths");
        }

        context.ApiSchemaDocuments = new ApiSchemaDocuments(ApiSchemaNodes, _logger);
        await next();
    }

    public JsonNode FindResourceSchemas(JsonNode extensionApiSchemaRootNode)
    {
        return extensionApiSchemaRootNode.SelectRequiredNodeFromPath(
            "$.projectSchema.resourceSchemas",
            _logger
        );
    }

    private void InsertJsonPathsExts(JsonObject extList, JsonObject coreResourceSchemas, string jsonPathKey)
    {
        var validExtensionResourceSchemas = extList
            .Where(ext => ext.Value?["isResourceExtension"]?.GetValue<bool>() == true)
            .Where(ext => ext.Value?[jsonPathKey] is JsonArray { Count: > 0 })
            .ToList();

        foreach (var (extensionResourceName, extSchema) in validExtensionResourceSchemas)
        {
            if (extSchema != null)
            {
                var extensionJsonPaths = extSchema[jsonPathKey];
                if (extensionJsonPaths is JsonArray extensionJsonArray)
                {
                    foreach (var (coreResourceName, coreSchema) in coreResourceSchemas)
                    {
                        if (
                            extensionResourceName.Equals(coreResourceName, StringComparison.OrdinalIgnoreCase)
                            && coreSchema != null
                        )
                        {
                            var coreJsonPaths = coreSchema[jsonPathKey] as JsonArray ?? new JsonArray();
                            bool pathsAdded = false;

                            foreach (var path in extensionJsonArray)
                            {
                                if (path != null)
                                {
                                    var clonedPath = path.DeepClone();

                                    if (!coreJsonPaths.Contains(clonedPath))
                                    {
                                        coreJsonPaths.Add(clonedPath);
                                        pathsAdded = true;
                                    }
                                }
                            }

                            if (pathsAdded && ApiSchemaNodes != null)
                            {
                                var clonedCoreJsonPaths = coreJsonPaths.DeepClone();

                                coreSchema[jsonPathKey] = clonedCoreJsonPaths;

                                var coreApiSchemaRootNode = ApiSchemaNodes.CoreApiSchemaRootNode;
                                var resourceSchemasNode = coreApiSchemaRootNode["projectSchema"]?[
                                    "resourceSchemas"
                                ];

                                if (resourceSchemasNode != null)
                                {
                                    resourceSchemasNode[coreResourceName] = coreSchema.DeepClone();
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
