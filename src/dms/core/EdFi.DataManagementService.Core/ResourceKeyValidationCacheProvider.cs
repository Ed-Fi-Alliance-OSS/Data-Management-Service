// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides cached access to resource key validation results, keyed by policy class and configured
/// connection string. Thread-safe and process-lifetime scoped (singleton).
///
/// A Primary result is cached permanently, success and failure alike: if the database resource keys do
/// not match the expected seed, the database must be reprovisioned and DMS restarted, so re-validating
/// per request would only repeat the same answer. A Derivative result expires at the configured
/// bounded interval, and a request that reads a failure drops it immediately through its token.
///
/// Every fault is evicted on both classes, so a transient error retries on the next request. Unlike
/// the fingerprint provider, this one retains no fault: it has no deterministic-failure exception of
/// its own, because a resource-key mismatch is returned as a result rather than thrown.
/// </summary>
/// <remarks>
/// The caching machinery lives in <see cref="ValidationEntryCache{TValue}" />, shared with
/// <see cref="DatabaseFingerprintProvider" />, so the exact-entry eviction and expiry rules cannot
/// drift apart between the two caches.
/// </remarks>
internal sealed class ResourceKeyValidationCacheProvider
{
    private readonly ValidationEntryCache<ResourceKeyValidationResult> _cache;

    public ResourceKeyValidationCacheProvider(TimeProvider timeProvider, CacheSettings cacheSettings)
    {
        ArgumentNullException.ThrowIfNull(cacheSettings);

        _cache = new ValidationEntryCache<ResourceKeyValidationResult>(
            timeProvider,
            DerivativeValidationCacheExpiration.Effective(
                cacheSettings.DerivativeValidationCacheExpirationSeconds,
                cacheSettings
            ),
            static (_, _) => false
        );
    }

    /// <summary>
    /// The cached validation result for the given key, computing it with <paramref name="factory" />
    /// on first access, plus the token that removes exactly the entry this caller observed.
    /// </summary>
    /// <param name="key">Which database, and under which policy class, is being validated.</param>
    /// <param name="factory">
    /// An async factory that performs the actual validation. Called at most once per key, unless the
    /// entry is evicted by a fault, expiry, or an invalidating caller.
    /// </param>
    /// <remarks>
    /// Synchronous, returning the task rather than awaiting it, so concurrent first calls share one
    /// validation - even when it returns a failure - and a caller whose await throws still holds a
    /// token. A synchronous throw from <paramref name="factory" /> becomes a faulted task and is
    /// evicted like any other fault, rather than escaping this call.
    /// </remarks>
    public ValidationCacheRead<ResourceKeyValidationResult> Read(
        ValidationCacheKey key,
        Func<Task<ResourceKeyValidationResult>> factory
    ) => _cache.Read(key, factory);
}
