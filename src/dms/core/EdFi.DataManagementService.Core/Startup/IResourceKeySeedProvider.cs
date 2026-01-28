// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Represents a seed entry for the dms.ResourceKey table.
/// </summary>
/// <param name="QualifiedResourceName">The fully qualified resource name (e.g., "ed-fi.student").</param>
/// <param name="IsAbstract">Whether this is an abstract resource.</param>
public record ResourceKeySeed(string QualifiedResourceName, bool IsAbstract);

/// <summary>
/// Provides deterministic resource key seeds derived from the API schema.
/// These seeds are used to populate and validate the dms.ResourceKey table.
/// </summary>
public interface IResourceKeySeedProvider
{
    /// <summary>
    /// Gets the list of resource key seeds derived from the API schema.
    /// The list is deterministically ordered for consistent hashing.
    /// </summary>
    /// <param name="nodes">The API schema nodes to derive seeds from.</param>
    /// <returns>An ordered list of resource key seeds.</returns>
    IReadOnlyList<ResourceKeySeed> GetSeeds(ApiSchemaDocumentNodes nodes);

    /// <summary>
    /// Computes a hash of the resource key seeds for fast validation.
    /// </summary>
    /// <param name="seeds">The seeds to hash.</param>
    /// <returns>A hexadecimal string representation of the seed hash.</returns>
    string ComputeSeedHash(IReadOnlyList<ResourceKeySeed> seeds);
}
