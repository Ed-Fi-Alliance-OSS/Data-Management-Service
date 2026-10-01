// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// The host-owned cache of resolved secret values, keyed by tenant and secret name. It is a
/// singleton with no scoped dependency: the caller passes the tenant, the same way the resolver
/// receives it.
///
/// A value expires <see cref="SecretsOptions.CacheExpirationSeconds"/> after it was fetched, and
/// reading it does not extend that, so the setting is how long a rotation takes to reach this host.
/// Concurrent misses on one key share one fetch and its outcome, a failed fetch caches nothing, and
/// a key never waits behind another one.
///
/// An expired value is never served. It is released when its key is next read, or when any value is
/// next stored, whichever comes first, so plain text can outlive its expiration until the host next
/// touches the cache; nothing sweeps it on a timer. What is held is bounded by the number of distinct
/// secret names read.
///
/// Nothing tells the cache that a tenant was added or removed. A new tenant has no entries to be
/// stale, and a removed tenant's entries can only be reached with its own name, so they are never
/// read again and are released by the next store after they expire.
/// </summary>
public sealed class SecretValueCache(IOptions<SecretsOptions> options, TimeProvider? timeProvider = null)
{
    private readonly record struct Key(string? Tenant, string Name);

    /// <summary>
    /// A class rather than a record, so removing an expired entry compares by reference and can
    /// never remove one stored since.
    /// </summary>
    private sealed class Entry(string value, DateTimeOffset expiresAt)
    {
        public string Value { get; } = value;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }

    private readonly TimeSpan _expiration = TimeSpan.FromSeconds(options.Value.CacheExpirationSeconds);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Key, Entry> _values = new();
    private readonly ConcurrentDictionary<Key, Lazy<Task<string>>> _flights = new();

    /// <summary>
    /// The number of values held, expired or not. Exposed so tests can see that expired entries are
    /// released rather than kept for the life of the host.
    /// </summary>
    internal int Count => _values.Count;

    /// <summary>
    /// Returns the cached value for the tenant and name, or awaits <paramref name="fetch"/> for it.
    /// A fetch already in flight for the same key is joined rather than repeated. Only a non-empty
    /// value is cached; whether an empty one is acceptable is the caller's decision.
    /// </summary>
    public async Task<string> GetOrFetchAsync(string? tenant, string name, Func<Task<string>> fetch)
    {
        Key key = new(tenant, name);

        if (TryGetFresh(key, out string? cached))
        {
            return cached;
        }

        Lazy<Task<string>>? flight = null;
        flight = new Lazy<Task<string>>(() => FetchAsync(key, fetch, flight!));

        return await _flights.GetOrAdd(key, flight).Value;
    }

    private async Task<string> FetchAsync(Key key, Func<Task<string>> fetch, Lazy<Task<string>> flight)
    {
        try
        {
            // A flight that finished between the caller's miss and this one starting left a value
            // the caller should take instead of asking again.
            if (TryGetFresh(key, out string? cached))
            {
                return cached;
            }

            string value = await fetch();

            if (_expiration > TimeSpan.Zero && !string.IsNullOrEmpty(value))
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                RemoveExpired(now);
                _values[key] = new Entry(value, now + _expiration);
            }

            return value;
        }
        finally
        {
            // Only this flight is removed, never one that has since replaced it.
            _flights.TryRemove(new KeyValuePair<Key, Lazy<Task<string>>>(key, flight));
        }
    }

    /// <summary>
    /// Returns the cached value for the tenant and name without fetching, for a caller that has
    /// decided not to ask the resolver.
    /// </summary>
    public bool TryGetFresh(
        string? tenant,
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value
    ) => TryGetFresh(new Key(tenant, name), out value);

    private bool TryGetFresh(Key key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        value = null;

        if (!_values.TryGetValue(key, out Entry? entry))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow() >= entry.ExpiresAt)
        {
            // Removes only this expired entry, never a fresh one stored since.
            _values.TryRemove(new KeyValuePair<Key, Entry>(key, entry));
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <summary>
    /// Drops every expired entry. It runs only when a value is stored, which happens at most once
    /// per key per expiration window, so the sweep costs nothing on the read path. An expired entry
    /// read before then is dropped by the read itself.
    /// </summary>
    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (KeyValuePair<Key, Entry> pair in _values)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                // Removes only the expired entry, never a fresh one stored since.
                _values.TryRemove(pair);
            }
        }
    }
}
