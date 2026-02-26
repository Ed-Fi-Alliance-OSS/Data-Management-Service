// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Computes a deterministic hash of the effective API schema.
/// This hash is used to verify that database instances are provisioned
/// for the same schema version as the running DMS instance.
/// </summary>
public interface IEffectiveSchemaHashProvider
{
    /// <summary>
    /// Computes a deterministic hash of the provided API schema nodes.
    /// The hash should be stable across runs given the same logical schema content.
    /// </summary>
    /// <param name="nodes">The normalized API schema nodes to hash.</param>
    /// <returns>A hexadecimal string representation of the schema hash.</returns>
    string ComputeHash(ApiSchemaDocumentNodes nodes);

    /// <summary>
    /// Computes a deterministic hash from pre-extracted project metadata.
    /// Use this overload when metadata has already been extracted to avoid
    /// redundant <see cref="ProjectSchemaMetadataExtractor.Extract"/> calls.
    /// </summary>
    /// <param name="apiSchemaFormatVersion">The API schema format version from the core schema.</param>
    /// <param name="sortedProjects">Project metadata entries already sorted by projectEndpointName (ordinal).</param>
    /// <returns>A hexadecimal string representation of the schema hash.</returns>
    string ComputeHash(string apiSchemaFormatVersion, IReadOnlyList<ProjectSchemaMetadata> sortedProjects);
}
