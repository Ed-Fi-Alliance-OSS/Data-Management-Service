// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// How long a cached validation verdict lives, and what happens to it when the request that read it
/// finds the database unusable.
/// </summary>
internal enum ValidationCachePolicyClass
{
    /// <summary>
    /// A parent's own database. Its verdicts are cached for the process lifetime, positive and
    /// negative alike, because a primary that is unprovisioned or provisioned for a different schema
    /// requires an operator to repair the database and restart the service.
    /// </summary>
    Primary,

    /// <summary>
    /// A read replica or snapshot. Its verdicts expire, and a request that finds the database unusable
    /// drops the verdict immediately, because a derivative can be rebuilt or repointed underneath a
    /// running service without anything telling DMS.
    /// </summary>
    Derivative,
}

/// <summary>
/// What a cached validation verdict is about: which database, and under which policy.
/// </summary>
/// <param name="PolicyClass">Which lifetime and invalidation rules apply.</param>
/// <param name="ConfiguredConnectionString">
/// The connection string as configured, never a provider-realized form. Using the configured text
/// means cache identity requires no provider parsing, so a value that no provider could open still has
/// a stable identity and fails where provider errors belong - at acquisition.
/// </param>
/// <remarks>
/// Including the policy class in the key is what keeps a primary and a derivative apart when their
/// configured text is byte-identical, which is exactly the case where one entry would otherwise be
/// given two different lifetimes.
/// </remarks>
internal readonly record struct ValidationCacheKey(
    ValidationCachePolicyClass PolicyClass,
    string ConfiguredConnectionString
)
{
    /// <summary>
    /// The key for the database a request is being served from. The mapping from target kind to policy
    /// class lives here so the several call sites cannot disagree about it.
    /// </summary>
    public static ValidationCacheKey For(EffectiveDataStoreTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new ValidationCacheKey(
            target.Kind == EffectiveTargetKind.Primary
                ? ValidationCachePolicyClass.Primary
                : ValidationCachePolicyClass.Derivative,
            target.ConnectionString
        );
    }
}

/// <summary>
/// Removes exactly the cache entry a request observed, and only if it is still the current entry, so a
/// late verdict cannot delete a newer entry another request has already populated.
/// </summary>
internal interface IValidationCacheEntryToken
{
    void Invalidate();
}

/// <summary>
/// The token for a policy class whose verdicts are kept regardless of what the reading request
/// concluded.
/// </summary>
internal sealed class NoOpValidationCacheEntryToken : IValidationCacheEntryToken
{
    public static readonly NoOpValidationCacheEntryToken Instance = new();

    private NoOpValidationCacheEntryToken() { }

    public void Invalidate()
    {
        // Deliberately nothing. A primary verdict survives the request that read it: an unprovisioned
        // or wrong-schema primary needs an operator and a restart, not a re-probe on the next request.
    }
}

/// <summary>
/// A cached value together with the means to drop exactly the entry it came from.
/// </summary>
/// <remarks>
/// Handed back synchronously, before the value task is awaited, so a caller holds the token even when
/// awaiting the value throws. Returning the token only after a successful await would leave the fault
/// paths - the ones most likely to need invalidation - with nothing to invalidate.
/// </remarks>
internal sealed record ValidationCacheRead<T>(Task<T> Value, IValidationCacheEntryToken Token);

/// <summary>
/// The caching machinery both validation providers share: one entry per key, produced at most once,
/// expiring only for derivatives, and removable only by whoever observed the exact entry.
/// </summary>
/// <param name="timeProvider">Supplies the clock, so expiry is testable without waiting.</param>
/// <param name="derivativeExpiration">
/// How long a derivative entry lives. Primary entries never expire, so this does not apply to them.
/// </param>
/// <param name="shouldRetainFault">
/// Whether a faulted production is kept rather than evicted. Everything else about the two providers'
/// caching is identical; this is the only place their failure semantics differ, which is why it is a
/// parameter rather than two copies of the machinery.
/// </param>
internal sealed class ValidationEntryCache<TValue>(
    TimeProvider timeProvider,
    TimeSpan derivativeExpiration,
    Func<ValidationCacheKey, Exception, bool> shouldRetainFault
)
{
    private readonly ConcurrentDictionary<ValidationCacheKey, CacheEntry> _cache = new();

    // A field rather than a captured primary-constructor parameter, because the nested entry
    // class reaches it through its owner and cannot see the outer primary constructor.
    private readonly Func<ValidationCacheKey, Exception, bool> _shouldRetainFault = shouldRetainFault;

    /// <summary>
    /// The entry currently cached for <paramref name="key" />, producing it if there is none, plus the
    /// token that removes that exact entry.
    /// </summary>
    /// <remarks>
    /// Synchronous by design. It returns the task rather than awaiting it, so concurrent first readers
    /// share one production and every caller - including one whose await throws - holds a token.
    /// </remarks>
    public ValidationCacheRead<TValue> Read(ValidationCacheKey key, Func<Task<TValue>> produce)
    {
        ArgumentNullException.ThrowIfNull(produce);

        while (true)
        {
            CacheEntry entry = _cache.GetOrAdd(
                key,
                static (entryKey, state) =>
                    new CacheEntry(state.Cache, entryKey, state.Produce, state.CreatedAt),
                (Cache: this, Produce: produce, CreatedAt: timeProvider.GetUtcNow())
            );

            if (!IsExpired(key, entry))
            {
                return new ValidationCacheRead<TValue>(entry.Value.Value, TokenFor(key, entry));
            }

            // Exact-entry removal, so a reader that observed an expired entry cannot remove the
            // replacement another reader has already installed. Then loop, because the winner of that
            // race is whichever reader next reaches GetOrAdd.
            _cache.TryRemove(new KeyValuePair<ValidationCacheKey, CacheEntry>(key, entry));
        }
    }

    private bool IsExpired(ValidationCacheKey key, CacheEntry entry) =>
        key.PolicyClass == ValidationCachePolicyClass.Derivative
        && timeProvider.GetUtcNow() - entry.CreatedAt >= derivativeExpiration;

    private IValidationCacheEntryToken TokenFor(ValidationCacheKey key, CacheEntry entry) =>
        key.PolicyClass == ValidationCachePolicyClass.Primary
            ? NoOpValidationCacheEntryToken.Instance
            : new ExactEntryToken(_cache, key, entry);

    /// <summary>
    /// Removes one specific entry, and only while it is still the current one for its key.
    /// </summary>
    private sealed class ExactEntryToken(
        ConcurrentDictionary<ValidationCacheKey, CacheEntry> cache,
        ValidationCacheKey key,
        CacheEntry entry
    ) : IValidationCacheEntryToken
    {
        public void Invalidate() =>
            cache.TryRemove(new KeyValuePair<ValidationCacheKey, CacheEntry>(key, entry));
    }

    private sealed class CacheEntry
    {
        private readonly ValidationEntryCache<TValue> _owner;
        private readonly ValidationCacheKey _key;
        private readonly Func<Task<TValue>> _produce;

        public CacheEntry(
            ValidationEntryCache<TValue> owner,
            ValidationCacheKey key,
            Func<Task<TValue>> produce,
            DateTimeOffset createdAt
        )
        {
            _owner = owner;
            _key = key;
            _produce = produce;
            CreatedAt = createdAt;

            // A constructor body may reference `this`, so the lazy binds to this entry with no
            // placeholder local and no null-forgiving operator. GetOrAdd stores the entry and returns
            // it before anything reads Value, so the factory cannot run before construction completes.
            Value = new Lazy<Task<TValue>>(ProduceAndEvictAsync);
        }

        public Lazy<Task<TValue>> Value { get; }

        public DateTimeOffset CreatedAt { get; }

        private async Task<TValue> ProduceAndEvictAsync()
        {
            try
            {
                // The producer is invoked inside this async method, so a synchronous throw faults the
                // returned task rather than escaping Lazy.Value. That keeps the throw out of the
                // Lazy's own permanently cached exception and routes it through the eviction below.
                return await _produce().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (!_owner._shouldRetainFault(_key, exception))
                {
                    // Exact-entry removal: this entry, and only while it is still current. A late
                    // fault from an entry that has already been superseded removes nothing.
                    _owner._cache.TryRemove(new KeyValuePair<ValidationCacheKey, CacheEntry>(_key, this));
                }

                // Rethrown unchanged, so the awaiting caller sees the original exception and stack.
                throw;
            }
        }
    }
}
