// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides cached access to database fingerprints, keyed by connection string.
/// Thread-safe and process-lifetime scoped (singleton).
///
/// Positive results (fingerprint found) are cached permanently.
/// Negative results (database not provisioned) are cached for a configurable TTL
/// to avoid hammering the database under load.
/// Faulted tasks are evicted immediately so transient errors retry on next request.
/// </summary>
internal sealed class DatabaseFingerprintProvider(
    IDatabaseFingerprintReader fingerprintReader,
    IOptions<AppSettings> appSettings,
    TimeProvider timeProvider
)
{
    private sealed record CacheEntry(DatabaseFingerprint? Fingerprint, DateTimeOffset? ExpiresAt);

    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _cache = new();

    /// <summary>
    /// Returns the cached fingerprint for the given connection string, or reads it
    /// from the database on first access. Concurrent first calls for the same
    /// connection string result in exactly one database read.
    /// </summary>
    public async Task<DatabaseFingerprint?> GetFingerprintAsync(string connectionString)
    {
        var ttl = TimeSpan.FromSeconds(appSettings.Value.DatabaseFingerprintNegativeCacheTtlSeconds);

        if (_cache.TryGetValue(connectionString, out var existing))
        {
            var entry = await existing.Value;
            if (entry.Fingerprint != null)
            {
                return entry.Fingerprint;
            }

            if (entry.ExpiresAt.HasValue && timeProvider.GetUtcNow() < entry.ExpiresAt.Value)
            {
                return null;
            }

            // Negative entry expired — evict and fall through to re-read
            _cache.TryRemove(connectionString, out _);
        }

        var lazy = _cache.GetOrAdd(
            connectionString,
            static (key, state) =>
                new Lazy<Task<CacheEntry>>(async () =>
                {
                    var result = await state.reader.ReadFingerprintAsync(key);
                    if (result != null)
                    {
                        return new CacheEntry(result, null);
                    }
                    return new CacheEntry(null, state.timeProvider.GetUtcNow() + state.ttl);
                }),
            (reader: fingerprintReader, timeProvider, ttl)
        );

        CacheEntry cacheEntry;
        try
        {
            cacheEntry = await lazy.Value;
        }
        catch
        {
            // Evict faulted task so next request retries
            _cache.TryRemove(connectionString, out _);
            throw;
        }

        return cacheEntry.Fingerprint;
    }
}
