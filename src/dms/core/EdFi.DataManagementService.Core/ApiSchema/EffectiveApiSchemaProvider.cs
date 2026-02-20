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
    private const string JsonSchemaForInsertProperties = "jsonSchemaForInsert.properties";

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
                JsonSchemaForInsertProperties,
                "equalityConstraints",
                "arrayUniquenessConstraints",
            ];

            int extensionCount = apiSchemaNodes.ExtensionApiSchemaRootNodes.Length;
            _logger.LogInformation(
                "Merging {ExtensionCount} extension schema(s) into core schema",
                extensionCount
            );

            var coreResourceByName = BuildCoreResourceByName(coreResources);

            foreach (JsonNode extension in apiSchemaNodes.ExtensionApiSchemaRootNodes)
            {
                List<JsonNode> extensionResources = extension
                    .SelectRequiredNodeFromPath("$.projectSchema.resourceSchemas", _logger)
                    .SelectNodesFromPropertyValues();

                foreach (var nodeKey in nodeKeys)
                {
                    CopyResourceExtensionNodeToCore(extensionResources, coreResourceByName, nodeKey);
                }

                ApplyCommonExtensionOverrides(extensionResources, coreResourceByName);
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

    /// <summary>
    /// Builds a lookup of core resources by name, detecting and throwing on duplicates.
    /// </summary>
    private static Dictionary<string, JsonNode> BuildCoreResourceByName(List<JsonNode> coreResources)
    {
        Dictionary<string, JsonNode> coreResourceByName = [];
        foreach (var coreResource in coreResources)
        {
            var name = coreResource.GetRequiredNode("resourceName").GetValue<string>();
            if (!coreResourceByName.TryAdd(name, coreResource))
            {
                throw new InvalidOperationException(
                    $"Resource '{name}' appears more than once in the core schema. This should not happen since resource names are JSON object keys."
                );
            }
        }

        return coreResourceByName;
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
        Dictionary<string, JsonNode> coreResourceByName,
        string nodeKey
    )
    {
        foreach (
            JsonNode extensionResource in extensionResources.Where(extensionResource =>
                extensionResource.GetRequiredNode("isResourceExtension").GetValue<bool>()
            )
        )
        {
            var resourceName = extensionResource.GetRequiredNode("resourceName").GetValue<string>();
            var coreResource = coreResourceByName[resourceName];

            var sourceExtensionNode = GetNodeByPath(extensionResource, nodeKey);
            var targetCoreNode = GetNodeByPath(coreResource, nodeKey);
            var nodeValueKind = targetCoreNode.GetValueKind();

            switch (nodeValueKind)
            {
                case JsonValueKind.Object:
                    bool hasCommonExtensionOverrides =
                        extensionResource["commonExtensionOverrides"]?.AsArray() is { Count: > 0 };
                    MergeExtensionObjectIntoCore(
                        sourceExtensionNode,
                        targetCoreNode,
                        hasCommonExtensionOverrides,
                        resourceName,
                        nodeKey
                    );
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
    private void MergeExtensionObjectIntoCore(
        JsonNode sourceExtensionNode,
        JsonNode targetCoreNode,
        bool hasCommonExtensionOverrides,
        string resourceName,
        string nodeKey
    )
    {
        var targetObject = targetCoreNode.AsObject();
        foreach (KeyValuePair<string, JsonNode?> sourceObject in sourceExtensionNode.AsObject())
        {
            // If _ext exists in the target, merge its properties from the source.
            if (
                string.Equals(sourceObject.Key, "_ext", StringComparison.InvariantCultureIgnoreCase)
                && targetObject["_ext"] is JsonObject existingExt
                && sourceObject.Value?.DeepClone() is JsonObject newExt
            )
            {
                MergeExtFragment(existingExt, newExt);
            }
            else if (!targetObject.ContainsKey(sourceObject.Key))
            {
                targetObject.Add(new(sourceObject.Key, sourceObject.Value?.DeepClone()));
            }
            else if (hasCommonExtensionOverrides && nodeKey == JsonSchemaForInsertProperties)
            {
                // Duplicate key is expected — it will be handled by ApplyCommonExtensionOverrides
                _logger.LogDebug(
                    "Skipping duplicate key '{Key}' in '{NodeKey}' for resource '{ResourceName}' during extension merge — will be handled by common extension overrides",
                    LoggingSanitizer.SanitizeForLogging(sourceObject.Key),
                    LoggingSanitizer.SanitizeForLogging(nodeKey),
                    LoggingSanitizer.SanitizeForLogging(resourceName)
                );
            }
            else
            {
                throw new InvalidOperationException(
                    $"Duplicate key '{sourceObject.Key}' in '{nodeKey}' for resource '{resourceName}' found during extension merge"
                );
            }
        }
    }

    /// <summary>
    /// Merges an _ext schema fragment into an existing _ext object, combining properties,
    /// required arrays, and other schema keywords.
    /// </summary>
    private static void MergeExtFragment(JsonObject existingExt, JsonObject newExt)
    {
        // Merge properties
        if (newExt["properties"] is JsonObject newProps)
        {
            if (existingExt["properties"] is JsonObject existingProps)
            {
                foreach (var item in newProps)
                {
                    existingProps[item.Key] = item.Value?.DeepClone();
                }
            }
            else
            {
                existingExt["properties"] = newProps.DeepClone();
            }
        }

        // Merge required arrays
        if (newExt["required"] is JsonArray newRequired)
        {
            if (existingExt["required"] is JsonArray existingRequired)
            {
                foreach (var req in newRequired)
                {
                    existingRequired.Add(req?.DeepClone());
                }
            }
            else
            {
                existingExt["required"] = newRequired.DeepClone();
            }
        }

        // Merge additionalProperties (new value wins if present)
        if (newExt.ContainsKey("additionalProperties"))
        {
            existingExt["additionalProperties"] = newExt["additionalProperties"]?.DeepClone();
        }
    }

    /// <summary>
    /// Merges extension arrayUniquenessConstraints into core, combining nestedConstraints
    /// when the top-level paths match rather than creating duplicate entries.
    /// </summary>
    private static void MergeArrayUniquenessConstraints(JsonArray source, JsonArray target)
    {
        // Build an index of existing target constraints by their paths for O(1) lookup
        var targetIndex = new Dictionary<string, JsonNode>();
        foreach (var targetItem in target)
        {
            if (targetItem?["paths"]?.AsArray() is JsonArray paths)
            {
                var key = PathArrayToKey(paths);
                targetIndex.TryAdd(key, targetItem);
            }
        }

        foreach (var sourceItem in source)
        {
            if (sourceItem is null)
            {
                continue;
            }

            JsonNode? matchingTarget = null;
            if (sourceItem["paths"]?.AsArray() is JsonArray sourcePaths)
            {
                var key = PathArrayToKey(sourcePaths);
                targetIndex.TryGetValue(key, out matchingTarget);
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
    /// Creates a stable dictionary key from a JSON array of string paths
    /// by joining the individual string values with a null separator.
    /// </summary>
    private static string PathArrayToKey(JsonArray paths)
    {
        return string.Join('\0', paths.Select(p => p?.GetValue<string>() ?? string.Empty));
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
        Dictionary<string, JsonNode> coreResourceByName
    )
    {
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
                throw new InvalidOperationException(
                    $"Common extension overrides present for resource '{resourceName}' but no matching core resource found"
                );
            }

            var coreJsonSchemaForInsert = coreResource["jsonSchemaForInsert"];
            if (coreJsonSchemaForInsert is null)
            {
                throw new InvalidOperationException(
                    $"Common extension overrides present for resource '{resourceName}' but core resource has no jsonSchemaForInsert"
                );
            }

            foreach (JsonNode? overrideEntry in overrides)
            {
                if (overrideEntry is null)
                {
                    throw new InvalidOperationException(
                        $"Null override entry found in commonExtensionOverrides for resource '{resourceName}'"
                    );
                }

                var insertionLocations = overrideEntry["insertionLocations"]?.AsArray();
                var schemaFragment = overrideEntry["schemaFragment"];

                if (insertionLocations is null || schemaFragment is null)
                {
                    throw new InvalidOperationException(
                        $"Common extension override for resource '{resourceName}' is missing required insertionLocations or schemaFragment"
                    );
                }

                if (schemaFragment is not JsonObject)
                {
                    throw new InvalidOperationException(
                        $"Common extension override schemaFragment for resource '{resourceName}' must be a JSON object, but was {schemaFragment.GetValueKind()}"
                    );
                }

                foreach (JsonNode? location in insertionLocations)
                {
                    var jsonPath = location?.GetValue<string>();
                    if (string.IsNullOrEmpty(jsonPath))
                    {
                        throw new InvalidOperationException(
                            $"Empty insertion location in commonExtensionOverrides for resource '{resourceName}'"
                        );
                    }

                    // Navigate the JSONPath (e.g., "$.properties.addresses.items")
                    // to find the target node in the core jsonSchemaForInsert
                    var targetNode = coreJsonSchemaForInsert.SelectNodeFromPath(jsonPath, _logger);
                    if (targetNode is null)
                    {
                        throw new InvalidOperationException(
                            $"Common extension override could not be applied: path '{jsonPath}' not found in core jsonSchemaForInsert for resource '{resourceName}'"
                        );
                    }

                    // Insert or merge the _ext property at the target location
                    var targetProperties = targetNode["properties"]?.AsObject();
                    if (targetProperties is null)
                    {
                        throw new InvalidOperationException(
                            $"Target node at '{jsonPath}' for resource '{resourceName}' has no 'properties' object"
                        );
                    }

                    if (
                        targetProperties["_ext"] is JsonObject existingExt
                        && schemaFragment.DeepClone() is JsonObject newFragment
                    )
                    {
                        MergeExtFragment(existingExt, newFragment);
                    }
                    else
                    {
                        targetProperties["_ext"] = schemaFragment.DeepClone();
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
}
