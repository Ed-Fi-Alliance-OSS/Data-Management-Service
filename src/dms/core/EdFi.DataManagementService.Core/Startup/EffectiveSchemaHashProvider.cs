// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Computes a deterministic SHA-256 hash of the effective API schema.
/// Uses canonical JSON serialization to ensure identical hashes for
/// semantically equivalent schemas regardless of property ordering or formatting.
/// </summary>
public class EffectiveSchemaHashProvider(ILogger<EffectiveSchemaHashProvider> logger)
    : IEffectiveSchemaHashProvider
{
    private readonly ILogger<EffectiveSchemaHashProvider> _logger = logger;

    /// <inheritdoc />
    public string ComputeHash(ApiSchemaDocumentNodes nodes)
    {
        _logger.LogDebug(
            "Computing effective schema hash for core schema and {ExtensionCount} extension(s)",
            nodes.ExtensionApiSchemaRootNodes.Length
        );

        // Build a wrapper structure that combines core and extensions
        // Extensions are already sorted by projectEndpointName by ApiSchemaInputNormalizer
        var combinedSchema = new JsonObject
        {
            ["coreSchema"] = nodes.CoreApiSchemaRootNode.DeepClone(),
            ["extensionSchemas"] = new JsonArray(
                nodes.ExtensionApiSchemaRootNodes.Select(n => n.DeepClone()).ToArray()
            ),
        };

        // Serialize to canonical form
        byte[] canonicalBytes = CanonicalJsonSerializer.SerializeToUtf8Bytes(combinedSchema);

        _logger.LogDebug("Canonical schema size: {ByteCount} bytes", canonicalBytes.Length);

        // Compute SHA-256 hash
        byte[] hashBytes = SHA256.HashData(canonicalBytes);

        // Convert to lowercase hex string
        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        _logger.LogDebug("Computed effective schema hash: {Hash}", hashHex);

        return hashHex;
    }
}
