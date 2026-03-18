// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Provides mapping sets for runtime plan execution, coordinating pack loading,
/// runtime compilation fallback, and in-process caching.
/// </summary>
public interface IMappingSetProvider
{
    /// <summary>
    /// Gets or creates a compiled mapping set for the given selection key.
    /// In steady state this is a cache hit. On first access for a key, the provider
    /// attempts pack loading (if enabled) then runtime compilation fallback (if allowed).
    /// </summary>
    /// <param name="key">The mapping set selection key (hash + dialect + mapping version).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compiled mapping set.</returns>
    /// <exception cref="MappingSetUnavailableException">
    /// Thrown when no mapping set can be provided for the key (pack missing/invalid and
    /// runtime compilation not allowed or failed).
    /// </exception>
    Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a mapping set cannot be provided for a selection key.
/// </summary>
public sealed class MappingSetUnavailableException : Exception
{
    public MappingSetUnavailableException(string message)
        : base(message) { }

    public MappingSetUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
