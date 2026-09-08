// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Compiles a mapping set at runtime from the effective schema for a specific SQL dialect.
/// Implementations are dialect-specific (one per <see cref="SqlDialect"/>), parameterized
/// by <see cref="ISqlDialectRules"/>.
/// </summary>
public interface IRuntimeMappingSetCompiler
{
    /// <summary>
    /// Gets the dialect this compiler targets.
    /// </summary>
    SqlDialect Dialect { get; }

    /// <summary>
    /// Gets the current mapping set key for this compiler's dialect, derived from
    /// the authoritative effective schema set.
    /// </summary>
    MappingSetKey GetCurrentKey();

    /// <summary>
    /// Compiles a mapping set for the given selection key.
    /// </summary>
    /// <param name="expectedKey">
    /// The expected selection key. The compiler validates that the current effective schema
    /// resolves to this key and throws <see cref="InvalidOperationException"/> on mismatch.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compiled mapping set.</returns>
    Task<MappingSet> CompileAsync(MappingSetKey expectedKey, CancellationToken cancellationToken);
}
