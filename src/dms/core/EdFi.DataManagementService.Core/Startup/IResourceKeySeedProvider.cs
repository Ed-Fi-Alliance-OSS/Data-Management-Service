// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Represents a seed entry for the dms.ResourceKey table.
/// </summary>
/// <param name="ResourceKeyId">The sequential identifier assigned during seed generation (1..N).</param>
/// <param name="ProjectName">The project name (e.g., "Ed-Fi").</param>
/// <param name="ResourceName">The resource name (e.g., "Student").</param>
/// <param name="ResourceVersion">The resource version from the owning project (e.g., "5.0.0").</param>
/// <param name="IsAbstract">Whether this is an abstract resource.</param>
public record ResourceKeySeed(
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    string ResourceVersion,
    bool IsAbstract
);

/// <summary>
/// Provides deterministic resource key seeds derived from the API schema.
/// These seeds are used to populate and validate the dms.ResourceKey table.
/// </summary>
public interface IResourceKeySeedProvider
{
    /// <summary>
    /// Gets the list of resource key seeds derived from the API schema.
    /// The list is deterministically ordered by (ProjectName, ResourceName) using ordinal comparison,
    /// with ResourceKeyId assigned sequentially from 1..N.
    /// </summary>
    /// <param name="nodes">The API schema nodes to derive seeds from.</param>
    /// <returns>An ordered list of resource key seeds.</returns>
    IReadOnlyList<ResourceKeySeed> GetSeeds(ApiSchemaDocumentNodes nodes);

    /// <summary>
    /// Computes a SHA-256 hash of the resource key seeds for fast validation.
    /// The hash is computed over a canonical UTF-8 manifest with version header and one line per seed,
    /// where each line includes ResourceKeyId, ProjectName, ResourceName, and ResourceVersion.
    /// </summary>
    /// <param name="seeds">The seeds to hash.</param>
    /// <returns>The raw SHA-256 hash bytes (32 bytes).</returns>
    byte[] ComputeSeedHash(IReadOnlyList<ResourceKeySeed> seeds);
}
