// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that prepares and provides the API schema documents for pipeline processing.
/// Merges core API schemas with extension schemas, resolving extension data into the core
/// resources to create a unified schema representation that supports both standard and
/// extended Ed-Fi data models.
/// </summary>
internal class ProvideApiSchemaMiddleware(
    IApiSchemaProvider apiSchemaProvider,
    ILogger logger,
    ICompiledSchemaCache compiledSchemaCache
) : IPipelineStep
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
                    "equalityConstraints",
                    "arrayUniquenessConstraints",
                ];

                foreach (JsonNode extension in apiSchemaNodes.ExtensionApiSchemaRootNodes)
                {
                    List<JsonNode> extensionResources = extension
                        .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", logger)
                        .SelectNodesFromPropertyValues();

                    // Handle jsonSchemaForInsert. properties separately with new logic
                    MergeJsonSchemaForInsertProperties(extensionResources, coreResources, logger);

                    // Iterates over a list of node keys and calls
                    // CopyResourceExtensionNodeToCore for each key, transferring
                    // data from extensionResources to coreResources
                    foreach (var nodeKey in nodeKeys)
                    {
                        CopyResourceExtensionNodeToCore(extensionResources, coreResources, nodeKey);
                    }
                }

                // Ensure ALL resources have required array at ROOT level of jsonSchemaForInsert
                foreach (var coreResource in coreResources)
                {
                    try
                    {
                        var jsonSchemaForInsert = coreResource.GetRequiredNode("jsonSchemaForInsert");

                        if (jsonSchemaForInsert is JsonObject schemaObj && !schemaObj.ContainsKey("required"))
                        {
                            schemaObj["required"] = new JsonArray();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error ensuring required array exists for resource");
                    }
                }

                var documents = new ApiSchemaDocuments(
                    apiSchemaNodes with
                    {
                        CoreApiSchemaRootNode = coreApiSchema,
                    },
                    logger
                );

                compiledSchemaCache.Prime(documents, apiSchemaProvider.ReloadId);

                return documents;
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

        var (documents, version) = _apiSchemaDocuments.GetValueAndVersion();
        requestInfo.ApiSchemaDocuments = documents;
        requestInfo.ApiSchemaReloadId = version;
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
    /// Merges extension schema properties into core schema for jsonSchemaForInsert.properties.
    /// Handles both top-level _ext properties and nested _ext within array item schemas.
    /// </summary>
    private static void MergeJsonSchemaForInsertProperties(
        List<JsonNode> extensionResources,
        List<JsonNode> coreResources,
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
            var resourceName = extensionResource.GetRequiredNode("resourceName").GetValue<string>();

            if (!coreResourceByName.TryGetValue(resourceName, out var coreResource))
            {
                logger.LogWarning("Core resource not found for extension: {ResourceName}", resourceName);
                continue;
            }

            try
            {
                var extensionJsonSchemaForInsert = GetNodeByPath(extensionResource, "jsonSchemaForInsert");
                var coreJsonSchemaForInsert = GetNodeByPath(coreResource, "jsonSchemaForInsert");

                if (
                    extensionJsonSchemaForInsert is not JsonObject extSchemaObj
                    || coreJsonSchemaForInsert is not JsonObject coreSchemaObj
                )
                {
                    continue;
                }

                // Ensure core schema has required array
                if (!coreSchemaObj.ContainsKey("required"))
                {
                    coreSchemaObj["required"] = new JsonArray();
                }

                var extensionProperties = extSchemaObj["properties"] as JsonObject;
                var coreProperties = coreSchemaObj["properties"] as JsonObject;

                if (extensionProperties == null || coreProperties == null)
                {
                    continue;
                }

                // Extract _ext from extension properties
                if (extensionProperties["_ext"]?["properties"] is not JsonObject extRootObj)
                {
                    continue;
                }

                foreach (var extensionNamespace in extRootObj)
                {
                    var extensionName = extensionNamespace.Key; // e.g., "sample"
                    var extensionContent = extensionNamespace.Value as JsonObject;

                    if (extensionContent?["properties"] is not JsonObject extensionProps)
                    {
                        continue;
                    }

                    // Process each property in the extension
                    foreach (var extProp in extensionProps)
                    {
                        var propertyName = extProp.Key; // e.g., "addresses", "authors"
                        var extPropertyValue = extProp.Value as JsonObject;

                        if (extPropertyValue == null)
                        {
                            continue;
                        }

                        // Check if this property exists in core schema
                        if (coreProperties.ContainsKey(propertyName))
                        {
                            // Property exists in core - merge _ext into array items if applicable
                            var coreProperty = coreProperties[propertyName] as JsonObject;
                            if (coreProperty != null)
                            {
                                MergeExtensionIntoArrayItems(coreProperty, extPropertyValue, extensionName);
                            }
                        }
                        else
                        {
                            // Property doesn't exist in core - add to root _ext
                            EnsureRootExtStructure(coreProperties, extensionName);

                            if (
                                coreProperties["_ext"]?["properties"]?[extensionName]?["properties"]
                                is JsonObject targetExtProps
                            )
                            {
                                targetExtProps[propertyName] = extPropertyValue.DeepClone();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Error merging jsonSchemaForInsert. properties for resource: {ResourceName}",
                    resourceName
                );
            }
        }
    }

    /// <summary>
    /// Merges extension properties into array item schemas by adding _ext structure.
    /// This handles cases like addresses where core has an array and extension adds _ext to items.
    /// </summary>
    private static void MergeExtensionIntoArrayItems(
        JsonObject coreProperty,
        JsonObject extensionProperty,
        string extensionName
    )
    {
        // Check if both are arrays
        if (
            coreProperty["type"]?.GetValue<string>() != "array"
            || extensionProperty["type"]?.GetValue<string>() != "array"
        )
        {
            return;
        }

        var coreItems = coreProperty["items"] as JsonObject;
        var extItems = extensionProperty["items"] as JsonObject;

        if (
            coreItems?["properties"] is not JsonObject coreItemProps
            || extItems?["properties"] is not JsonObject extItemProps
        )
        {
            return;
        }

        // Extract _ext from extension items
        if (extItemProps["_ext"]?["properties"]?[extensionName] is not JsonObject extContent)
        {
            return;
        }

        // Ensure _ext structure exists in core items
        EnsureItemExtStructure(coreItemProps, extensionName);

        // Merge the extension properties
        if (coreItemProps["_ext"]?["properties"]?[extensionName]?["properties"] is not JsonObject targetProps)
        {
            return;
        }

        if (extContent["properties"] is JsonObject sourceProps)
        {
            foreach (var prop in sourceProps)
            {
                targetProps[prop.Key] = prop.Value?.DeepClone();
            }
        }

        // Merge required fields if present
        if (
            extContent["required"] is JsonArray sourceRequired
            && coreItemProps["_ext"]?["properties"]?[extensionName] is JsonObject extObj
        )
        {
            if (extObj["required"] is JsonArray targetRequired)
            {
                foreach (var req in sourceRequired)
                {
                    targetRequired.Add(req?.DeepClone());
                }
            }
            else
            {
                extObj["required"] = sourceRequired.DeepClone();
            }
        }
    }

    /// <summary>
    /// Ensures the _ext structure exists at the root properties level.
    /// Used when adding extension-only properties like authors, becameParent, etc.
    /// </summary>
    private static void EnsureRootExtStructure(JsonObject properties, string extensionName)
    {
        if (properties["_ext"] is not JsonObject extObj)
        {
            extObj = new JsonObject
            {
                ["additionalProperties"] = true,
                ["description"] = "optional extension collection",
                ["properties"] = new JsonObject(),
                ["type"] = "object",
            };
            properties["_ext"] = extObj;
        }

        if (extObj["properties"] is not JsonObject extPropsObj)
        {
            extPropsObj = new JsonObject();
            extObj["properties"] = extPropsObj;
        }

        if (extPropsObj[extensionName] is not JsonObject)
        {
            extPropsObj[extensionName] = new JsonObject
            {
                ["additionalProperties"] = true,
                ["description"] = $"{extensionName} extension properties collection",
                ["properties"] = new JsonObject(),
                ["type"] = "object",
            };
        }
    }

    /// <summary>
    /// Ensures the _ext structure exists within array item properties.
    /// Used when merging extension properties into existing core array items like addresses.
    /// </summary>
    private static void EnsureItemExtStructure(JsonObject itemProperties, string extensionName)
    {
        if (itemProperties["_ext"] is not JsonObject extObj)
        {
            extObj = new JsonObject
            {
                ["additionalProperties"] = false,
                ["description"] = "Extension properties",
                ["properties"] = new JsonObject(),
                ["type"] = "object",
            };
            itemProperties["_ext"] = extObj;
        }

        if (extObj["properties"] is not JsonObject extPropsObj)
        {
            extPropsObj = new JsonObject();
            extObj["properties"] = extPropsObj;
        }

        if (extPropsObj[extensionName] is not JsonObject)
        {
            extPropsObj[extensionName] = new JsonObject
            {
                ["additionalProperties"] = false,
                ["description"] = $"{extensionName} extension properties",
                ["properties"] = new JsonObject(),
                ["type"] = "object",
            };
        }
    }

    /// <summary>
    /// Merges extension resource data into core resources by copying specific nodes identified
    /// by the nodeKey.
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
                    MergeExtensionObjectIntoCore(sourceExtensionNode, targetCoreNode);
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
    private static void MergeExtensionObjectIntoCore(JsonNode sourceExtensionNode, JsonNode targetCoreNode)
    {
        var targetObject = targetCoreNode.AsObject();
        foreach (KeyValuePair<string, JsonNode?> sourceObject in sourceExtensionNode.AsObject())
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
            else if (!targetObject.ContainsKey(sourceObject.Key))
            {
                targetObject.Add(new(sourceObject.Key, sourceObject.Value?.DeepClone()));
            }
        }
    }
}
