// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides the effective (merged) API schema documents built at startup.
/// This service builds the merged schema view once during initialization and
/// caches it for the lifetime of the application.
/// </summary>
internal class EffectiveApiSchemaProvider : IEffectiveApiSchemaProvider
{
    private readonly ILogger _logger;
    private readonly ICompiledSchemaCache _compiledSchemaCache;
    private readonly object _initLock = new();

    private ApiSchemaDocuments? _documents;
    private Guid _schemaId;
    private volatile bool _isInitialized;

    public EffectiveApiSchemaProvider(
        ILogger<EffectiveApiSchemaProvider> logger,
        ICompiledSchemaCache compiledSchemaCache
    )
    {
        _logger = logger;
        _compiledSchemaCache = compiledSchemaCache;
    }

    /// <inheritdoc />
    public ApiSchemaDocuments Documents
    {
        get
        {
            EnsureInitialized();
            return _documents!;
        }
    }

    /// <inheritdoc />
    public Guid SchemaId
    {
        get
        {
            EnsureInitialized();
            return _schemaId;
        }
    }

    /// <inheritdoc />
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Initializes the effective schema from the provided schema nodes.
    /// This should be called once during application startup.
    /// </summary>
    /// <param name="apiSchemaNodes">The raw schema nodes from the schema provider.</param>
    /// <exception cref="InvalidOperationException">Thrown if already initialized.</exception>
    public void Initialize(ApiSchemaDocumentNodes apiSchemaNodes)
    {
        lock (_initLock)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException(
                    "EffectiveApiSchemaProvider has already been initialized."
                );
            }

            _logger.LogInformation("Building effective API schema from core and extension schemas");

            // Clone to not mutate the original schema
            var coreApiSchema = apiSchemaNodes.CoreApiSchemaRootNode.DeepClone();

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
                "arrayUniquenessConstraints",
            ];

            int extensionCount = apiSchemaNodes.ExtensionApiSchemaRootNodes.Length;
            _logger.LogInformation(
                "Merging {ExtensionCount} extension schema(s) into core schema",
                extensionCount
            );

            foreach (JsonNode extension in apiSchemaNodes.ExtensionApiSchemaRootNodes)
            {
                List<JsonNode> extensionResources = extension
                    .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", _logger)
                    .SelectNodesFromPropertyValues();

                foreach (var nodeKey in nodeKeys)
                {
                    CopyResourceExtensionNodeToCore(extensionResources, coreResources, nodeKey);
                }
            }

            _documents = new ApiSchemaDocuments(
                apiSchemaNodes with
                {
                    CoreApiSchemaRootNode = coreApiSchema,
                },
                _logger
            );

            _schemaId = Guid.NewGuid();

            _logger.LogInformation("Priming compiled schema cache");
            _compiledSchemaCache.Prime(_documents, _schemaId);

            _isInitialized = true;

            _logger.LogInformation(
                "Effective API schema built successfully. SchemaId: {SchemaId}",
                _schemaId
            );
        }
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                "EffectiveApiSchemaProvider has not been initialized. Ensure the startup orchestrator has run."
            );
        }
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
    private void CopyResourceExtensionNodeToCore(
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
    private void MergeExtensionObjectIntoCore(JsonNode sourceExtensionNode, JsonNode targetCoreNode)
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
