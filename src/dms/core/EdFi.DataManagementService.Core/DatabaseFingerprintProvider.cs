// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides cached access to database fingerprints, keyed by connection string.
/// Thread-safe and process-lifetime scoped (singleton).
///
/// Positive and negative results are cached permanently for the life of the
/// process. This intentionally avoids request-time re-probing for databases
/// that were unprovisioned on first use.
/// Permanent validation failures are also cached so malformed databases fail
/// fast per connection string. Other faulted tasks are evicted immediately so
/// transient errors retry on next request.
///
/// NOTE: This cache/eviction pattern is intentionally duplicated in
/// <see cref="ResourceKeyValidationCacheProvider"/> because the two caches
/// store different value types and have different deterministic-failure
/// semantics. If the retry policy needs to change, update both classes
/// and the design doc (new-startup-flow.md §Failure Modes).
/// </summary>
internal sealed class DatabaseFingerprintProvider(IDatabaseFingerprintReader fingerprintReader)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DatabaseFingerprint?>>> _cache = new();

    /// <summary>
    /// Returns the cached fingerprint for the given connection string, or reads it
    /// from the database on first access. Concurrent first calls for the same
    /// connection string result in exactly one database read, even when that read
    /// returns <c>null</c>.
    /// </summary>
    public async Task<DatabaseFingerprint?> GetFingerprintAsync(string connectionString)
    {
        var lazy = _cache.GetOrAdd(
            connectionString,
            static (key, state) =>
                new Lazy<Task<DatabaseFingerprint?>>(() => state.ReadFingerprintAsync(key)),
            fingerprintReader
        );

        try
        {
            return await lazy.Value;
        }
        catch (DatabaseFingerprintValidationException)
        {
            throw;
        }
        catch
        {
            // Evict faulted task so next request retries
            _cache.TryRemove(new(connectionString, lazy));
            throw;
        }
    }
}
