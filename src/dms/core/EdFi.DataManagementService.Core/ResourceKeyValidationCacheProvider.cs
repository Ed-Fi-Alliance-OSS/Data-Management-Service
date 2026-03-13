// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides cached access to resource key validation results, keyed by connection string.
/// Thread-safe and process-lifetime scoped (singleton).
///
/// Both success and failure validation results are cached permanently for the life
/// of the process. This is intentional: if the database resource keys don't match
/// the expected seed, the database must be reprovisioned and DMS restarted.
/// Transient exceptions (network errors, timeouts) are evicted so the next
/// request retries.
/// </summary>
internal sealed class ResourceKeyValidationCacheProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ResourceKeyValidationResult>>> _cache = new();

    /// <summary>
    /// Returns the cached validation result for the given connection string, or
    /// computes it using the provided factory on first access. Concurrent first
    /// calls for the same connection string result in exactly one validation,
    /// even when that validation returns a failure result.
    /// </summary>
    /// <param name="connectionString">The database connection string used as cache key.</param>
    /// <param name="factory">
    /// An async factory that performs the actual validation. Called at most once
    /// per connection string (unless evicted due to transient failure).
    /// </param>
    /// <returns>The cached or freshly computed validation result.</returns>
    public async Task<ResourceKeyValidationResult> GetOrValidateAsync(
        string connectionString,
        Func<Task<ResourceKeyValidationResult>> factory
    )
    {
        var lazy = _cache.GetOrAdd(
            connectionString,
            static (_, state) => new Lazy<Task<ResourceKeyValidationResult>>(() => state()),
            factory
        );

        try
        {
            return await lazy.Value;
        }
        catch
        {
            // Evict faulted task so next request retries (transient errors)
            _cache.TryRemove(new(connectionString, lazy));
            throw;
        }
    }
}
