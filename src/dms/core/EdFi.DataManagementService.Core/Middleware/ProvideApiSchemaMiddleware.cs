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

        JsonNode coreResourceSchemas = FindResourceSchemas(ApiSchemaNodes.CoreApiSchemaRootNode).DeepClone();

        foreach (JsonNode extensionApiSchemaRootNode in ApiSchemaNodes.ExtensionApiSchemaRootNodes)
        {
            JsonNode extensionResourceSchemas = FindResourceSchemas(extensionApiSchemaRootNode);

            InsertTypeCoercionExts(extensionResourceSchemas, coreResourceSchemas, "dateTimeJsonPaths");
            InsertTypeCoercionExts(extensionResourceSchemas, coreResourceSchemas, "booleanJsonPaths");
            InsertTypeCoercionExts(extensionResourceSchemas, coreResourceSchemas, "numericJsonPaths");
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

    private void InsertTypeCoercionExts(JsonNode extensionResourceSchemas, JsonNode coreResourceSchemas, string jsonPathKey)
    {

        var validExtensionResourceSchemas = extensionResourceSchemas.AsObject()
            .Where(ext => ext.Value?["isResourceExtension"]?.GetValue<bool>() == true)
            .Where(ext => ext.Value?[jsonPathKey] is JsonArray { Count: > 0 })
            .ToList();

        foreach (KeyValuePair<string, JsonNode?> keyValueExtension in validExtensionResourceSchemas)
        {
            string extensionResourceName = keyValueExtension.Key;
            JsonNode? extSchema = keyValueExtension.Value;
            if (extSchema != null)
            {
                JsonNode? extensionJsonPaths = extSchema[jsonPathKey];
                if (extensionJsonPaths is JsonArray extensionJsonArray)
                {
                    foreach (KeyValuePair<string, JsonNode?> keyValueCore in coreResourceSchemas.AsObject())
                    {
                        string coreResourceName = keyValueCore.Key;
                        JsonNode? coreSchema = keyValueCore.Value;
                        if (
                            extensionResourceName.Equals(coreResourceName, StringComparison.OrdinalIgnoreCase)
                            && coreSchema != null
                        )
                        {
                            JsonArray coreJsonPaths = coreSchema[jsonPathKey] as JsonArray ?? new JsonArray();
                            bool pathsAdded = false;

                            foreach (var path in extensionJsonArray)
                            {
                                if (path != null)
                                {
                                    JsonNode clonedPath = path.DeepClone();

                                    if (!coreJsonPaths.Contains(clonedPath))
                                    {
                                        coreJsonPaths.Add(clonedPath);
                                        pathsAdded = true;
                                    }
                                }
                            }

                            if (pathsAdded && ApiSchemaNodes != null)
                            {
                                JsonNode clonedCoreJsonPaths = coreJsonPaths.DeepClone();

                                coreSchema[jsonPathKey] = clonedCoreJsonPaths;

                                JsonNode coreApiSchemaRootNode = ApiSchemaNodes.CoreApiSchemaRootNode;
                                JsonNode? resourceSchemasNode = coreApiSchemaRootNode["projectSchema"]?[
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
