// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Validates that the database's dms.ResourceKey contents match the expected
/// resource key seed for the effective schema.
/// </summary>
public interface IResourceKeyValidator
{
    /// <summary>
    /// Validates the database resource keys against the expected seed.
    /// Fast path: compares ResourceKeyCount and ResourceKeySeedHash from the fingerprint.
    /// Slow path: on mismatch, reads actual rows and produces a diff report.
    /// </summary>
    /// <param name="dbFingerprint">The database fingerprint read from dms.EffectiveSchema.</param>
    /// <param name="expectedResourceKeyCount">The expected number of resource keys.</param>
    /// <param name="expectedResourceKeySeedHash">The expected SHA-256 hash of the resource key seed list.</param>
    /// <param name="expectedResourceKeysInIdOrder">The expected resource key rows ordered by ResourceKeyId.</param>
    /// <param name="connectionString">The database connection string for slow-path row reads.</param>
    /// <returns>Success if keys match; Failure with a diff report otherwise.</returns>
    Task<ResourceKeyValidationResult> ValidateAsync(
        DatabaseFingerprint dbFingerprint,
        short expectedResourceKeyCount,
        ImmutableArray<byte> expectedResourceKeySeedHash,
        IReadOnlyList<ResourceKeyRow> expectedResourceKeysInIdOrder,
        string connectionString
    );
}
