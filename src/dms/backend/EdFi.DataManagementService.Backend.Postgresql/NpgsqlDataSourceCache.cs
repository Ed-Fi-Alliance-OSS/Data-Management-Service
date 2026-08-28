// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Singleton cache of NpgsqlDataSource instances, keyed by the configured connection string, whose
/// lifetime follows what the Configuration Service currently says is configured.
/// </summary>
/// <remarks>
/// PostgreSQL realization is the identity function - a configured string is its own pool key - so this
/// cache needs no realization map and its cleanup disposes a specific data-source object. That is why
/// it needs no clearing tombstone: if a new acquisition publishes a fresh entry for the same string
/// while an old disposal is outstanding, the disposal targets the old object and the new entry owns a
/// different one, so the two cannot interfere.
///
/// The public surface is leased-only. There is no way to obtain a data source without also obtaining
/// the lease that keeps it alive, which is what makes "no untracked or immortal data source"
/// structural rather than a convention callers have to remember.
/// </remarks>
public sealed class NpgsqlDataSourceCache : IDataStoreOwnershipReconciler, IDisposable
{
    private readonly ILogger<NpgsqlDataSourceCache> _logger;
    private readonly INpgsqlDataSourceLifetime _lifetime;

    public NpgsqlDataSourceCache(ILogger<NpgsqlDataSourceCache> logger)
        : this(logger, NpgsqlDataSourceLifetime.Instance) { }

    internal NpgsqlDataSourceCache(ILogger<NpgsqlDataSourceCache> logger, INpgsqlDataSourceLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Guards the configured owner set, the ownership version, the ready entries, their lease counts,
    /// and their retirement state - together, because a decision about any one of them that did not
    /// see the others could retire an entry a concurrent acquisition is about to lease, or lease one a
    /// concurrent reconciliation has already decided to dispose.
    /// </summary>
    /// <remarks>
    /// No data source is ever built, opened, or disposed while this is held. Every one of those is
    /// provider work that can block on a network or throw, and doing it here would make one slow
    /// database stall every other database's acquisitions.
    /// </remarks>
    private readonly object _stateLock = new();

    /// <summary>
    /// Whether the calling thread currently holds the state lock. The correctness argument for this
    /// cache rests on no provider work running under that lock, and this is what lets a substituted
    /// lifetime assert it from inside a build, an open, or a disposal rather than leaving it a comment.
    /// </summary>
    internal bool IsStateLockHeldByCurrentThread => Monitor.IsEntered(_stateLock);

    private readonly Dictionary<string, ReadyEntry> _entries = new(StringComparer.Ordinal);
    private HashSet<string> _configuredOwners = new(StringComparer.Ordinal);
    private long _ownershipVersion;
    private bool _disposed;

    /// <summary>
    /// A published data source. Every entry in the dictionary is fully built: there is no reservation
    /// and no is-building flag, so a lease can never name an entry whose data source is still null.
    /// </summary>
    private sealed class ReadyEntry(NpgsqlDataSource dataSource)
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;

        public bool IsRetired { get; set; }

        public int Leases { get; set; }
    }

    /// <summary>
    /// Leases the data source for a configured connection string, building one if none is published.
    /// </summary>
    /// <remarks>
    /// The build happens outside the lock and touches no shared state, so a provider-invalid string
    /// throws here with nothing to repair and the next caller retries from scratch. Losing a build
    /// race costs the loser its candidate, which is disposed, rather than costing the winner its
    /// entry.
    /// </remarks>
    public NpgsqlDataSourceLease AcquireLease(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(NpgsqlDataSourceCache));

            if (_entries.TryGetValue(connectionString, out ReadyEntry? existing))
            {
                return LeaseLocked(connectionString, existing);
            }
        }

        // Built outside the lock. This is where Npgsql parses, and where a provider-invalid string
        // throws - inside the acquisition boundary, with no shared state touched.
        NpgsqlDataSource candidate = Build(connectionString);
        NpgsqlDataSource? loser = null;

        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(NpgsqlDataSourceCache));

                if (_entries.TryGetValue(connectionString, out ReadyEntry? published))
                {
                    // Another builder won while this one was outside the lock. Lease the winner and
                    // discard this candidate rather than replacing a source others may already hold.
                    loser = candidate;
                    return LeaseLocked(connectionString, published);
                }

                // Re-checked against the owner set as it stands *now*, so a build that completed after
                // its owner was removed publishes as retired rather than as an immortal unowned entry.
                ReadyEntry entry = new(candidate)
                {
                    Leases = 1,
                    IsRetired = !_configuredOwners.Contains(connectionString),
                };

                _entries[connectionString] = entry;

                return new NpgsqlDataSourceLease(this, connectionString, entry.DataSource);
            }
        }
        finally
        {
            // Outside the lock, and only ever this call's own candidate.
            DisposeSafely(loser);
        }
    }

    /// <summary>
    /// Leases the data source and opens a connection from it, transferring both to the caller.
    /// </summary>
    /// <remarks>
    /// The convenience form for the ownership-transferring consumers. If the open fails or is
    /// cancelled the lease is released here, so a caller that never receives a connection never has to
    /// remember it acquired something.
    /// </remarks>
    public async Task<LeasedNpgsqlConnection> OpenLeasedConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        NpgsqlDataSourceLease lease = AcquireLease(connectionString);

        try
        {
            NpgsqlConnection connection = await _lifetime.OpenConnectionAsync(
                lease.DataSource,
                cancellationToken
            );
            return new LeasedNpgsqlConnection(connection, lease);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two-directional: retirement is set *from* current ownership for every entry rather than only
    /// switched on, so a key removed and re-added before its last lease ends is reactivated on the
    /// same pass that would otherwise have retired it, keeping its live data source.
    /// </remarks>
    public void Reconcile(DataStoreOwnershipSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<NpgsqlDataSource> toDispose = [];

        lock (_stateLock)
        {
            if (_disposed || snapshot.Version <= _ownershipVersion)
            {
                // Not an error: the publication lock makes an out-of-order delivery unreachable, and
                // ignoring one costs nothing because the next publication carries the full set again.
                return;
            }

            _ownershipVersion = snapshot.Version;

            // String set operations only. Nothing here parses a connection string, so a value no
            // provider could open participates in ownership like any other.
            // Every owner of every kind, because a string one kind stops claiming may still be
            // claimed by another - a primary and some other store's replica can name one database.
            _configuredOwners = snapshot
                .Owners.Select(owner => owner.ConfiguredConnectionString)
                .ToHashSet(StringComparer.Ordinal);

            foreach ((string key, ReadyEntry entry) in _entries)
            {
                entry.IsRetired = !_configuredOwners.Contains(key);
            }

            // Collected into a local and removed here, so the disposal below names objects nothing can
            // still reach through this cache.
            foreach (string key in _entries.Keys.ToArray())
            {
                ReadyEntry entry = _entries[key];

                if (entry.IsRetired && entry.Leases == 0)
                {
                    _entries.Remove(key);
                    toDispose.Add(entry.DataSource);
                }
            }
        }

        foreach (NpgsqlDataSource dataSource in toDispose)
        {
            DisposeSafely(dataSource);
        }
    }

    /// <summary>
    /// Process-end backstop. Snapshots and clears under the lock, then disposes outside it.
    /// </summary>
    public void Dispose()
    {
        List<NpgsqlDataSource> toDispose;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = [.. _entries.Values.Select(entry => entry.DataSource)];
            _entries.Clear();
        }

        _logger.LogInformation("Disposing {Count} cached NpgsqlDataSource instances", toDispose.Count);

        foreach (NpgsqlDataSource dataSource in toDispose)
        {
            DisposeSafely(dataSource);
        }
    }

    /// <summary>
    /// Releases one lease, disposing the data source if that was the last one and the entry is no
    /// longer configured. Called only by <see cref="NpgsqlDataSourceLease" />, at most once per lease.
    /// </summary>
    internal void ReleaseLease(string connectionString, NpgsqlDataSource dataSource)
    {
        NpgsqlDataSource? toDispose = null;

        lock (_stateLock)
        {
            if (!_entries.TryGetValue(connectionString, out ReadyEntry? entry))
            {
                // Already gone: the cache was disposed, or this lease outlived a removal that a later
                // acquisition has since replaced. Either way there is nothing of this lease's to drop.
                return;
            }

            // Only this lease's own entry. A newer entry for the same string owns a different object
            // and is not this lease's to decrement.
            if (!ReferenceEquals(entry.DataSource, dataSource))
            {
                return;
            }

            entry.Leases--;

            if (entry.Leases == 0 && entry.IsRetired)
            {
                _entries.Remove(connectionString);
                toDispose = entry.DataSource;
            }
        }

        DisposeSafely(toDispose);
    }

    /// <summary>
    /// Counts a lease against an already-published entry, reactivating it when the string it names is
    /// currently configured and leaving it retired when it is not - so a stale request for a key no
    /// longer owned does not resurrect ownership of it.
    /// </summary>
    private NpgsqlDataSourceLease LeaseLocked(string connectionString, ReadyEntry entry)
    {
        entry.Leases++;
        entry.IsRetired = !_configuredOwners.Contains(connectionString);

        return new NpgsqlDataSourceLease(this, connectionString, entry.DataSource);
    }

    private NpgsqlDataSource Build(string connectionString)
    {
        _logger.LogDebug(
            "Creating new NpgsqlDataSource for connection string hash: {Hash}",
            connectionString.GetHashCode(StringComparison.Ordinal)
        );

        return _lifetime.Build(connectionString);
    }

    /// <summary>
    /// Disposes one data source in its own fault boundary. A failure leaks that one source rather than
    /// leaving it half-disposed or stopping the disposal of the others, which is the safe direction:
    /// a leaked pool costs memory, a disposed one that is still in use costs a request.
    /// </summary>
    private void DisposeSafely(NpgsqlDataSource? dataSource)
    {
        if (dataSource is null)
        {
            return;
        }

        try
        {
            _lifetime.DisposeDataSource(dataSource);
        }
        catch (Exception exception)
        {
            // The exception is logged in full here, unlike the reconciler warning in Core: this one
            // comes from Npgsql rather than from arbitrary caller code. Nothing names the connection
            // string.
            _logger.LogWarning(exception, "Error disposing an NpgsqlDataSource; leaving it to the process");
        }
    }
}
