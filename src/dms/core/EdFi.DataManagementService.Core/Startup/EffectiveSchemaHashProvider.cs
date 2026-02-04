// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Metadata extracted from a projectSchema for effective schema hash computation.
/// </summary>
internal readonly record struct ProjectHashMetadata(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject,
    string ProjectHash
);

/// <summary>
/// Computes a deterministic SHA-256 hash of the effective API schema using a manifest-based approach.
///
/// The algorithm:
/// 1. Extract apiSchemaFormatVersion from the core schema
/// 2. For each project (core + extensions), compute a per-project SHA-256 hash of the canonical JSON
/// 3. Sort all projects by projectEndpointName using ordinal comparison
/// 4. Build a manifest string containing version info and per-project hashes
/// 5. Compute the final hash of the manifest string
///
/// This ensures the hash changes when:
/// - Any non-OpenAPI schema content changes
/// - The RelationalMappingVersion constant changes
/// - The hash algorithm version changes
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

        // Step 1: Extract apiSchemaFormatVersion from core schema
        var apiSchemaFormatVersion = GetApiSchemaVersion(nodes.CoreApiSchemaRootNode);

        // Step 2: Build project metadata list with per-project hashes
        var projects = new List<ProjectHashMetadata>(1 + nodes.ExtensionApiSchemaRootNodes.Length);

        // Add core project
        projects.Add(ExtractProjectMetadata(nodes.CoreApiSchemaRootNode));

        // Add extension projects (already sorted by ApiSchemaInputNormalizer, but we sort again for safety)
        foreach (var extension in nodes.ExtensionApiSchemaRootNodes)
        {
            projects.Add(ExtractProjectMetadata(extension));
        }

        // Step 3: Sort all projects by projectEndpointName using ordinal comparison
        projects.Sort(
            (a, b) => string.Compare(a.ProjectEndpointName, b.ProjectEndpointName, StringComparison.Ordinal)
        );

        // Step 4: Build manifest string
        var manifest = BuildManifestString(apiSchemaFormatVersion, projects);

        _logger.LogDebug("Manifest string length: {Length} characters", manifest.Length);

        // Step 5: Compute final hash of the manifest
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(manifest));
        string hashHex = Convert.ToHexStringLower(hashBytes);

        _logger.LogDebug("Computed effective schema hash: {Hash}", hashHex);

        return hashHex;
    }

    /// <summary>
    /// Extracts project metadata and computes the per-project hash.
    /// </summary>
    private static ProjectHashMetadata ExtractProjectMetadata(JsonNode schemaNode)
    {
        var projectSchema =
            schemaNode["projectSchema"]
            ?? throw new InvalidOperationException("Schema node missing 'projectSchema' property");

        // Extract metadata fields
        var projectEndpointName =
            projectSchema["projectEndpointName"]?.GetValue<string>()
            ?? throw new InvalidOperationException("projectSchema missing 'projectEndpointName'");

        var projectName =
            projectSchema["projectName"]?.GetValue<string>()
            ?? throw new InvalidOperationException("projectSchema missing 'projectName'");

        var projectVersion =
            projectSchema["projectVersion"]?.GetValue<string>()
            ?? throw new InvalidOperationException("projectSchema missing 'projectVersion'");

        var isExtensionProject = projectSchema["isExtensionProject"]?.GetValue<bool>() ?? false;

        // Compute per-project hash using canonical JSON serialization
        // Note: OpenAPI payloads have already been stripped by ApiSchemaInputNormalizer
        byte[] canonicalBytes = CanonicalJsonSerializer.SerializeToUtf8Bytes(projectSchema);
        byte[] projectHashBytes = SHA256.HashData(canonicalBytes);
        var projectHash = Convert.ToHexStringLower(projectHashBytes);

        return new ProjectHashMetadata(
            projectEndpointName,
            projectName,
            projectVersion,
            isExtensionProject,
            projectHash
        );
    }

    /// <summary>
    /// Builds the manifest string that will be hashed to produce the final EffectiveSchemaHash.
    /// </summary>
    private static string BuildManifestString(
        string apiSchemaFormatVersion,
        List<ProjectHashMetadata> projects
    )
    {
        // Header (~80 chars) + per project (~140 chars each)
        var estimatedSize = 80 + (projects.Count * 140);
        var sb = new StringBuilder(estimatedSize);

        // Line 1: Hash algorithm version header
        sb.Append(SchemaHashConstants.HashVersion);
        sb.Append('\n');

        // Line 2: Relational mapping version
        sb.Append("relationalMappingVersion=");
        sb.Append(SchemaHashConstants.RelationalMappingVersion);
        sb.Append('\n');

        // Line 3: API schema format version
        sb.Append("apiSchemaFormatVersion=");
        sb.Append(apiSchemaFormatVersion);

        // Lines 4+: Per-project entries (pipe-delimited)
        foreach (var project in projects)
        {
            sb.Append('\n');
            sb.Append(project.ProjectEndpointName);
            sb.Append('|');
            sb.Append(project.ProjectName);
            sb.Append('|');
            sb.Append(project.ProjectVersion);
            sb.Append('|');
            sb.Append(project.IsExtensionProject ? "true" : "false");
            sb.Append('|');
            sb.Append(project.ProjectHash);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the apiSchemaVersion from a schema root node.
    /// </summary>
    private static string GetApiSchemaVersion(JsonNode node) =>
        node["apiSchemaVersion"]?.GetValue<string>() ?? string.Empty;
}
