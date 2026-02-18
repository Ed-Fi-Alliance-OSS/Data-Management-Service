// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Utilities;
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

                ApplyCommonExtensionOverrides(extensionResources, coreResources);
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
    private static void CopyResourceExtensionNodeToCore(
        List<JsonNode> extensionResources,
        List<JsonNode> coreResources,
        string nodeKey
    )
    {
        Dictionary<string, JsonNode> coreResourceByName = [];
        foreach (var coreResource in coreResources)
        {
            var name = coreResource.GetRequiredNode("resourceName").GetValue<string>();
            coreResourceByName[name] = coreResource;
        }

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
                    if (nodeKey == "arrayUniquenessConstraints")
                    {
                        MergeArrayUniquenessConstraints(sourceExtensionNode.AsArray(), targetArray);
                    }
                    else
                    {
                        foreach (var sourceItem in sourceExtensionNode.AsArray())
                        {
                            targetArray.Add(sourceItem?.DeepClone());
                        }
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
            // Keys that already exist in core (e.g., "addresses") are common extension
            // entries handled by ApplyCommonExtensionOverrides — skip them here.
        }
    }

    /// <summary>
    /// Merges extension arrayUniquenessConstraints into core, combining nestedConstraints
    /// when the top-level paths match rather than creating duplicate entries.
    /// </summary>
    private static void MergeArrayUniquenessConstraints(JsonArray source, JsonArray target)
    {
        foreach (var sourceItem in source)
        {
            if (sourceItem is null)
            {
                continue;
            }

            var sourcePaths = sourceItem["paths"]?.ToJsonString();

            // Find a matching core constraint with the same top-level paths
            JsonNode? matchingTarget = null;
            if (sourcePaths is not null)
            {
                foreach (var targetItem in target)
                {
                    if (targetItem?["paths"]?.ToJsonString() == sourcePaths)
                    {
                        matchingTarget = targetItem;
                        break;
                    }
                }
            }

            if (matchingTarget is not null)
            {
                // Merge nestedConstraints from extension into the existing core constraint
                var sourceNested = sourceItem["nestedConstraints"]?.AsArray();
                if (sourceNested is not null)
                {
                    var targetNested = matchingTarget["nestedConstraints"]?.AsArray();
                    if (targetNested is null)
                    {
                        matchingTarget.AsObject()["nestedConstraints"] = sourceNested.DeepClone();
                    }
                    else
                    {
                        foreach (var nestedItem in sourceNested)
                        {
                            targetNested.Add(nestedItem?.DeepClone());
                        }
                    }
                }
            }
            else
            {
                target.Add(sourceItem.DeepClone());
            }
        }
    }

    /// <summary>
    /// Applies common extension overrides to insert _ext schema fragments into the core
    /// jsonSchemaForInsert at specified insertion locations.
    ///
    /// When an extension project extends a common type (e.g., Sample extends EdFi.Address
    /// used in Contact), the extension schema includes commonExtensionOverrides that specify:
    /// - insertionLocations: JSONPath(s) into the core jsonSchemaForInsert where _ext should be added
    ///   (e.g., $.properties.addresses.items targets the Address items within Contact)
    /// - schemaFragment: the _ext schema to insert (e.g., Address extension fields like complex, onBusRoute)
    /// </summary>
    private void ApplyCommonExtensionOverrides(
        List<JsonNode> extensionResources,
        List<JsonNode> coreResources
    )
    {
        Dictionary<string, JsonNode> coreResourceByName = [];
        foreach (var coreResource in coreResources)
        {
            var name = coreResource.GetRequiredNode("resourceName").GetValue<string>();
            coreResourceByName[name] = coreResource;
        }

        foreach (
            JsonNode extensionResource in extensionResources.Where(extensionResource =>
                extensionResource.GetRequiredNode("isResourceExtension").GetValue<bool>()
            )
        )
        {
            var overrides = extensionResource["commonExtensionOverrides"]?.AsArray();
            if (overrides is null || overrides.Count == 0)
            {
                continue;
            }

            var resourceName = extensionResource.GetRequiredNode("resourceName").GetValue<string>();
            if (!coreResourceByName.TryGetValue(resourceName, out var coreResource))
            {
                continue;
            }

            var coreJsonSchemaForInsert = coreResource["jsonSchemaForInsert"];
            if (coreJsonSchemaForInsert is null)
            {
                continue;
            }

            foreach (JsonNode? overrideEntry in overrides)
            {
                if (overrideEntry is null)
                {
                    continue;
                }

                var insertionLocations = overrideEntry["insertionLocations"]?.AsArray();
                var schemaFragment = overrideEntry["schemaFragment"];

                if (insertionLocations is null || schemaFragment is null)
                {
                    continue;
                }

                foreach (JsonNode? location in insertionLocations)
                {
                    var jsonPath = location?.GetValue<string>();
                    if (string.IsNullOrEmpty(jsonPath))
                    {
                        continue;
                    }

                    // Navigate the JSONPath (e.g., "$.properties.addresses.items")
                    // to find the target node in the core jsonSchemaForInsert
                    var targetNode = NavigateJsonPath(coreJsonSchemaForInsert, jsonPath);
                    if (targetNode is null)
                    {
                        _logger.LogDebug(
                            "Could not navigate to '{JsonPath}' in core jsonSchemaForInsert for resource '{ResourceName}'",
                            LoggingSanitizer.SanitizeForLogging(jsonPath),
                            LoggingSanitizer.SanitizeForLogging(resourceName)
                        );
                        continue;
                    }

                    // Insert or merge the _ext property at the target location
                    var targetProperties = targetNode["properties"]?.AsObject();
                    if (targetProperties is null)
                    {
                        continue;
                    }

                    if (targetProperties.ContainsKey("_ext"))
                    {
                        // Merge into existing _ext
                        var existingExtProps = targetProperties["_ext"]?["properties"]?.AsObject();
                        var newExtProps = schemaFragment.DeepClone()["properties"]?.AsObject();
                        if (existingExtProps is not null && newExtProps is not null)
                        {
                            foreach (var prop in newExtProps)
                            {
                                existingExtProps[prop.Key] = prop.Value?.DeepClone();
                            }
                        }
                    }
                    else
                    {
                        // Add _ext property
                        targetProperties.Add("_ext", schemaFragment.DeepClone());
                    }

                    _logger.LogDebug(
                        "Applied common extension override at '{JsonPath}' for resource '{ResourceName}'",
                        LoggingSanitizer.SanitizeForLogging(jsonPath),
                        LoggingSanitizer.SanitizeForLogging(resourceName)
                    );
                }
            }
        }
    }

    /// <summary>
    /// Navigates a simple JSONPath (e.g., "$.properties.addresses.items") starting from the given node.
    /// Supports only dot-notation property access (no wildcards, filters, or array indices).
    /// </summary>
    private static JsonNode? NavigateJsonPath(JsonNode root, string jsonPath)
    {
        // Strip leading "$." prefix
        var path = jsonPath.StartsWith("$.") ? jsonPath[2..] : jsonPath;

        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
