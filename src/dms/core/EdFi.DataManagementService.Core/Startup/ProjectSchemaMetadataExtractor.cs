// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Metadata extracted from a projectSchema node including the per-project content hash.
/// </summary>
public readonly record struct ProjectSchemaMetadata(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject,
    string ProjectHash,
    JsonObject ProjectSchema
);

/// <summary>
/// Extracts project metadata and computes the per-project SHA-256 content hash
/// from a schema root node. Shared by <see cref="EffectiveSchemaHashProvider"/>
/// and the CLI <c>EffectiveSchemaSetBuilder</c>.
/// </summary>
public static class ProjectSchemaMetadataExtractor
{
    /// <summary>
    /// Extracts project metadata fields and computes the per-project content hash.
    /// The returned <see cref="ProjectSchemaMetadata.ProjectSchema"/> is the original
    /// <see cref="JsonObject"/> (not cloned); callers that need a detached copy should
    /// deep-clone it themselves.
    /// </summary>
    public static ProjectSchemaMetadata Extract(JsonNode schemaNode)
    {
        var projectSchema =
            schemaNode["projectSchema"] as JsonObject
            ?? throw new InvalidOperationException("Schema node missing 'projectSchema' property");

        var projectEndpointName =
            projectSchema["projectEndpointName"]?.GetValue<string>()
            ?? throw new InvalidOperationException("projectSchema missing 'projectEndpointName'");

        var projectName =
            projectSchema["projectName"]?.GetValue<string>()
            ?? throw new InvalidOperationException("projectSchema missing 'projectName'");

        var projectVersion =
            projectSchema["projectVersion"]?.GetValue<string>()
            ?? throw new InvalidOperationException("projectSchema missing 'projectVersion'");

        var isExtensionProject =
            projectSchema["isExtensionProject"]?.GetValue<bool>()
            ?? throw new InvalidOperationException("projectSchema missing 'isExtensionProject'");

        // Compute per-project hash using canonical JSON serialization.
        // Note: OpenAPI payloads have already been stripped by ApiSchemaInputNormalizer.
        byte[] canonicalBytes = CanonicalJsonSerializer.SerializeToUtf8Bytes(projectSchema);
        byte[] projectHashBytes = SHA256.HashData(canonicalBytes);
        var projectHash = Convert.ToHexStringLower(projectHashBytes);

        return new ProjectSchemaMetadata(
            projectEndpointName,
            projectName,
            projectVersion,
            isExtensionProject,
            projectHash,
            projectSchema
        );
    }
}
