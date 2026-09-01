// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides cached access to database fingerprints, keyed by policy class and configured connection
/// string. Thread-safe and process-lifetime scoped (singleton).
///
/// A Primary verdict is cached permanently, positive and negative alike, which intentionally avoids
/// request-time re-probing of a database that was unprovisioned on first use: repairing one requires
/// an operator and a service restart either way. A malformed-fingerprint failure is likewise retained,
/// so a malformed primary fails fast rather than re-reading on every request. Every other Primary
/// fault is evicted immediately, so a transient error retries on the next request.
///
/// A Derivative verdict expires at the configured bounded interval, and every Derivative fault is
/// evicted, because a replica or snapshot can be rebuilt or repointed underneath a running service
/// without anything telling DMS. A reading request that finds one unusable drops the verdict through
/// the token it was handed.
/// </summary>
/// <remarks>
/// The caching machinery itself lives in <see cref="ValidationEntryCache{TValue}" />, shared with
/// <see cref="ResourceKeyValidationCacheProvider" />. The two differ only in which faults they retain,
/// which is the argument passed below; sharing the machinery is what keeps the exact-entry eviction
/// rules from drifting apart between them.
/// </remarks>
internal sealed class DatabaseFingerprintProvider
{
    private readonly IDatabaseFingerprintReader _fingerprintReader;
    private readonly ValidationEntryCache<DatabaseFingerprint?> _cache;

    public DatabaseFingerprintProvider(
        IDatabaseFingerprintReader fingerprintReader,
        TimeProvider timeProvider,
        CacheSettings cacheSettings
    )
    {
        ArgumentNullException.ThrowIfNull(cacheSettings);

        _fingerprintReader = fingerprintReader;
        _cache = new ValidationEntryCache<DatabaseFingerprint?>(
            timeProvider,
            DerivativeValidationCacheExpiration.Effective(
                cacheSettings.DerivativeValidationCacheExpirationSeconds,
                cacheSettings
            ),
            ShouldRetainFault
        );
    }

    /// <summary>
    /// The cached fingerprint for the given target, reading it from the database on first access, plus
    /// the token that removes exactly the entry this caller observed.
    /// </summary>
    /// <remarks>
    /// Synchronous, returning the task rather than awaiting it, so concurrent first calls for the same
    /// key result in exactly one database read - even when that read returns <c>null</c> - and so a
    /// caller whose await throws still holds a token.
    /// </remarks>
    public ValidationCacheRead<DatabaseFingerprint?> ReadFingerprint(
        ValidationCacheKey key,
        EffectiveDataStoreTarget target
    ) => _cache.Read(key, () => _fingerprintReader.ReadFingerprintAsync(target));

    /// <summary>
    /// The one intentionally retained fault: a Primary malformed-fingerprint verdict stays cached for
    /// the process lifetime, exactly as it did before policy classes existed. Every other fault on
    /// either class is evicted, so the next request retries.
    /// </summary>
    private static bool ShouldRetainFault(ValidationCacheKey key, Exception exception) =>
        key.PolicyClass == ValidationCachePolicyClass.Primary
        && exception is DatabaseFingerprintValidationException;
}
