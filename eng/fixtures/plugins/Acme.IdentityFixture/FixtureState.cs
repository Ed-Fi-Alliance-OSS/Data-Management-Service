// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Acme.IdentityFixture;

/// <summary>
/// The fixture's in-memory state: the namespace index, the per-namespace person stores and the
/// async jobs. It is a singleton shared by concurrent requests, so every collection is concurrent
/// and a job guards its own counter. Nothing here survives a process restart.
/// </summary>
public sealed class FixtureState
{
    /// <summary>The reserved property that records the order persons were issued in.</summary>
    public const string IssuanceOrderProperty = "~IssuanceOrder";

    private readonly Lazy<Dictionary<FixtureContextKey, string>> _namespaceIndex;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonObject>> _persons = new(
        StringComparer.Ordinal
    );
    private readonly ConcurrentDictionary<string, FixtureJob> _jobs = new(StringComparer.Ordinal);
    private readonly Lazy<bool> _seeded;
    private long _issuances;

    public FixtureState(IOptions<IdentityFixtureOptions> options)
    {
        _seeded = new Lazy<bool>(() => Seed(options.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        _namespaceIndex = new Lazy<Dictionary<FixtureContextKey, string>>(
            () => BuildIndex(options.Value),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
    }

    /// <summary>The namespace the context selects, or null when no configured context is equivalent.</summary>
    public string? SelectNamespace(FixtureContextKey context) =>
        _namespaceIndex.Value.TryGetValue(context, out string? name) ? name : null;

    /// <summary>
    /// Stores the person under a new 32-character hyphen-free ASCII alphanumeric id, unique within the
    /// namespace regardless of case. The issuance order is kept under a reserved name, which no response
    /// ever carries, so the earliest issuance for a key can be told from a later duplicate.
    /// </summary>
    public string AddPerson(string namespaceName, JsonObject person)
    {
        ConcurrentDictionary<string, JsonObject> store = StoreFor(namespaceName);

        while (true)
        {
            string uniqueId = Guid.NewGuid().ToString("N");
            JsonObject stored = (JsonObject)person.DeepClone();
            stored[IssuanceOrderProperty] = Interlocked.Increment(ref _issuances);

            if (store.TryAdd(uniqueId, stored))
            {
                return uniqueId;
            }
        }
    }

    public JsonObject? FindPerson(string namespaceName, string uniqueId) =>
        StoreFor(namespaceName).TryGetValue(uniqueId, out JsonObject? person) ? person : null;

    public IReadOnlyList<KeyValuePair<string, JsonObject>> Persons(string namespaceName) =>
        [.. StoreFor(namespaceName)];

    /// <summary>
    /// Accepts a job. With no <paramref name="requestedToken"/> the token is a fresh GUID; with one, that
    /// exact string is the token, replacing any earlier job issued under it.
    /// </summary>
    public string AddJob(
        string namespaceName,
        FixtureJobOwner owner,
        JsonObject completePayload,
        string? requestedToken = null,
        string? resultsVariant = null
    )
    {
        if (requestedToken is not null)
        {
            _jobs[requestedToken] = new FixtureJob(namespaceName, owner, completePayload, resultsVariant);
            return requestedToken;
        }

        while (true)
        {
            string token = Guid.NewGuid().ToString("N");
            if (_jobs.TryAdd(token, new FixtureJob(namespaceName, owner, completePayload, resultsVariant)))
            {
                return token;
            }
        }
    }

    public FixtureJob? FindJob(string token) => _jobs.TryGetValue(token, out FixtureJob? job) ? job : null;

    public void RemoveJob(string token) => _jobs.TryRemove(token, out _);

    private ConcurrentDictionary<string, JsonObject> StoreFor(string namespaceName)
    {
        _ = _seeded.Value;
        return StoreForUnseeded(namespaceName);
    }

    private ConcurrentDictionary<string, JsonObject> StoreForUnseeded(string namespaceName) =>
        _persons.GetOrAdd(
            namespaceName,
            _ => new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
        );

    // Places the configured persons under their explicit ids, so a test can make two independent
    // namespaces hold the same id.
    private bool Seed(IdentityFixtureOptions options)
    {
        foreach (FixtureNamespaceOptions configured in options.Namespaces)
        {
            foreach (FixtureSeedPersonOptions seed in configured.SeedPersons)
            {
                JsonObject person = [];
                foreach ((string name, string value) in seed.Attributes)
                {
                    person[name] = value;
                }

                person[IssuanceOrderProperty] = Interlocked.Increment(ref _issuances);

                if (!StoreForUnseeded(configured.Name).TryAdd(seed.UniqueId, person))
                {
                    throw new InvalidOperationException(
                        "IdentityFixture:Namespaces seeds the same UniqueId twice in one namespace."
                    );
                }
            }
        }

        return true;
    }

    private static Dictionary<FixtureContextKey, string> BuildIndex(IdentityFixtureOptions options)
    {
        Dictionary<FixtureContextKey, string> index = [];

        foreach (FixtureNamespaceOptions configured in options.Namespaces)
        {
            foreach (FixtureContextOptions context in configured.Contexts)
            {
                FixtureContextKey key = new(context.Tenant, context.Qualifiers);

                if (index.TryGetValue(key, out string? existing) && existing != configured.Name)
                {
                    throw new InvalidOperationException(
                        "IdentityFixture:Namespaces maps one context to more than one namespace."
                    );
                }

                index[key] = configured.Name;
            }
        }

        return index;
    }
}

/// <summary>An accepted async job, bound to the context and client that issued it.</summary>
public sealed class FixtureJob(
    string namespaceName,
    FixtureJobOwner owner,
    JsonObject completePayload,
    string? resultsVariant
)
{
    private readonly object _sync = new();
    private int _polls;

    public string Namespace { get; } = namespaceName;

    public FixtureJobOwner Owner { get; } = owner;

    public JsonObject CompletePayload { get; } = completePayload;

    /// <summary>The variant every authorized poll answers instead of the normal outcome, or null.</summary>
    public string? ResultsVariant { get; } = resultsVariant;

    /// <summary>
    /// Counts one authorized poll and reports whether the job is still incomplete: the first
    /// <paramref name="pollsUntilComplete"/> polls are, and every poll after them is not.
    /// </summary>
    public bool RecordPollAndIsIncomplete(int pollsUntilComplete)
    {
        lock (_sync)
        {
            if (_polls < pollsUntilComplete)
            {
                _polls++;
                return true;
            }

            return false;
        }
    }
}
