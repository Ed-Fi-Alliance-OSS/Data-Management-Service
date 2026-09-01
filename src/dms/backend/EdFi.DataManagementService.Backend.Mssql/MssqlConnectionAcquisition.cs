// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// The single SQL Server connection-acquisition boundary. Every seam that opens a SQL Server
/// connection for a request goes through it, so all of them share one pool identity for a given
/// target: the fingerprint reader and the resource-key row reader are the first two reads of any
/// request, and a derivative that acquired a different effective string there would occupy a second
/// pool without the forced pool-blocking policy, on exactly the reads that fail when a derivative is
/// mid-provisioning.
/// </summary>
public interface IMssqlConnectionAcquisition
{
    /// <summary>
    /// Takes a lease on the pool identity for <paramref name="target" />, realizing the effective
    /// connection string in the process.
    /// </summary>
    /// <remarks>
    /// Asynchronous because retirement clears one exact pool, and a new lease on an identity whose
    /// clear is outstanding has to wait for that clear to finish - outside any lock - rather than be
    /// granted against a pool that is about to be discarded.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown by the provider when a derivative connection string cannot be parsed. This is the
    /// acquisition boundary, and the exception is deliberately not translated here.
    /// </exception>
    Task<MssqlConnectionLease> AcquireLeaseAsync(
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// One configured target as the Configuration Service states it: a kind plus the connection string
/// exactly as configured. Realization is a pure function of this pair, which is why a recorded
/// mapping never goes stale.
/// </summary>
internal readonly record struct ConfiguredTargetKey(
    EffectiveTargetKind Kind,
    string ConfiguredConnectionString
);

/// <inheritdoc />
/// <remarks>
/// SQL Server owns no data-source object, so unlike PostgreSQL there is nothing here to dispose. The
/// only lifecycle action is clearing one exact SqlClient pool, and that action is identity-scoped
/// rather than object-scoped: a clear issued for a string discards whatever pool that string currently
/// names, including one a new owner has just started using. That asymmetry is why this class needs a
/// clearing tombstone and the PostgreSQL cache does not.
/// </remarks>
public sealed class MssqlConnectionAcquisition : IMssqlConnectionAcquisition, IDataStoreOwnershipReconciler
{
    private readonly Func<string, DbConnection> _createConnection;
    private readonly ISqlServerPoolClearing _poolClearing;
    private readonly ILogger<MssqlConnectionAcquisition> _logger;

    /// <summary>
    /// Guards the configured owner keys, the realization memo, every pool's lease/retirement/clearing
    /// state, the ownership version, and the clear-generation counter - together, because a decision
    /// about any one of them that did not see the others could clear a pool a concurrent acquisition
    /// is about to lease, or lease one whose clear is already under way.
    /// </summary>
    /// <remarks>
    /// Nothing realizes, parses, opens, clears, waits, or signals a completion while this is held.
    /// Every one of those is provider or external work that can block or throw, and doing it here would
    /// make one slow database stall every other database's acquisitions.
    /// </remarks>
    private readonly object _stateLock = new();

    /// <summary>The latest snapshot's configured targets. Ownership is expressed only in these terms.</summary>
    private HashSet<ConfiguredTargetKey> _configuredOwners = [];

    /// <summary>
    /// What each configured target realizes to. A memo, not an ownership record: realization is a pure
    /// function of the key, so a recorded mapping never goes stale and is not deleted when ownership
    /// changes. Keeping it is what makes reactivation exact without re-parsing anything.
    /// </summary>
    private readonly Dictionary<ConfiguredTargetKey, string> _realized = [];

    /// <summary>Per pool identity - that is, per realized effective connection string.</summary>
    private readonly Dictionary<string, PoolState> _pools = new(StringComparer.Ordinal);

    private long _ownershipVersion;
    private long _clearGeneration;
    private long _realizationCount;
    private long _tombstoneWaitCount;

    public MssqlConnectionAcquisition(
        ISqlServerPoolClearing poolClearing,
        ILogger<MssqlConnectionAcquisition> logger,
        Func<string, DbConnection>? createConnection = null
    )
    {
        ArgumentNullException.ThrowIfNull(poolClearing);
        ArgumentNullException.ThrowIfNull(logger);

        _poolClearing = poolClearing;
        _logger = logger;
        _createConnection =
            createConnection ?? (effectiveConnectionString => new SqlConnection(effectiveConnectionString));
    }

    /// <summary>
    /// Whether the calling thread currently holds the state lock. The correctness argument for this
    /// class rests on no provider or external work running under that lock, and this is what lets a
    /// substituted seam assert it from the inside rather than leaving it a comment.
    /// </summary>
    internal bool IsStateLockHeldByCurrentThread => Monitor.IsEntered(_stateLock);

    /// <summary>
    /// How many times a connection string has been provider-realized. Configuration load, ownership
    /// publication, and target selection must all leave this at zero: parsing belongs only inside
    /// acquisition, so that a present-but-provider-invalid derivative stays selectable and fails there.
    /// </summary>
    internal long RealizationCount => Interlocked.Read(ref _realizationCount);

    /// <summary>
    /// How many acquisitions have observed an outstanding clear and gone on to wait for it. Whether a
    /// caller took no lease and waited is otherwise invisible from outside, and a test that inferred it
    /// from elapsed time would be asserting on the scheduler rather than on the exclusion.
    /// </summary>
    internal long TombstoneWaitCount => Interlocked.Read(ref _tombstoneWaitCount);

    /// <summary>What a pool identity currently owes: outstanding leases, ownership, and any clear in flight.</summary>
    private sealed class PoolState
    {
        public int Leases { get; set; }

        public bool IsRetired { get; set; }

        /// <summary>
        /// Set while a clear for this identity is outstanding. It is a tombstone rather than a removal
        /// precisely so a concurrent acquisition can observe it and wait instead of taking a lease on a
        /// pool that is about to be discarded.
        /// </summary>
        public ClearingState? Clearing { get; set; }
    }

    /// <summary>
    /// One clear attempt. The generation is what makes completion exact: a later clear of the same
    /// identity is a different generation, so a late completion cannot release the wrong waiters or
    /// remove a state the next clear owns.
    /// </summary>
    private sealed record ClearingState(long Generation, TaskCompletionSource Completion);

    /// <inheritdoc />
    public async Task<MssqlConnectionLease> AcquireLeaseAsync(
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        ConfiguredTargetKey key = new(target.Kind, target.ConnectionString);

        // Outside the lock. This is where SqlClient parses, and where a provider-invalid derivative
        // throws - inside the acquisition boundary, with no shared state touched, so a failure needs no
        // repair and records neither a memo entry nor a pool.
        string effective = RealizeEffectiveConnectionString(target);
        Interlocked.Increment(ref _realizationCount);

        while (true)
        {
            TaskCompletionSource? outstandingClear = null;

            lock (_stateLock)
            {
                if (_pools.TryGetValue(effective, out PoolState? existing) && existing.Clearing is not null)
                {
                    // A clear is in flight for exactly this identity. Take no lease and open nothing:
                    // the clear would discard the pool this lease was about to be granted against.
                    outstandingClear = existing.Clearing.Completion;
                    Interlocked.Increment(ref _tombstoneWaitCount);
                }
                else
                {
                    return LeaseLocked(key, effective);
                }
            }

            // Outside the lock, and cancellable. Each iteration waits on a distinct clear generation
            // whose completion is signalled from a finally, so the loop can neither spin nor strand.
            await outstandingClear.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two-directional: retirement is set <em>from</em> current ownership for every pool rather than
    /// only switched on, so an owner removed and re-added before its last lease ends is reactivated on
    /// the same pass that would otherwise have retired its identity - with no clear, no re-realization,
    /// and no acquisition needed, because its memo entry still exists.
    /// </remarks>
    public void Reconcile(DataStoreOwnershipSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<(string EffectiveConnectionString, long Generation)> toClear = [];

        lock (_stateLock)
        {
            if (snapshot.Version <= _ownershipVersion)
            {
                // Not an error: the publication lock makes an out-of-order delivery unreachable, and
                // ignoring one costs nothing because the next publication carries the full set again.
                return;
            }

            // Computed first, into locals. Enumerating the snapshot is the only thing here that can
            // throw, and nothing may be mutated before it succeeds: advancing the version first would
            // make a retry carrying that same version look stale, leaving the old owner set live for
            // good. String set operations only - nothing here parses a connection string, so a value no
            // provider could open participates in ownership like any other.
            HashSet<ConfiguredTargetKey> newConfiguredOwners = snapshot
                .Owners.Select(owner => new ConfiguredTargetKey(owner.Kind, owner.ConfiguredConnectionString))
                .ToHashSet();

            HashSet<string> ownedEffective = OwnedEffectiveLocked(newConfiguredOwners);

            // From here down it is assignments and dictionary operations, none of which can throw.
            _ownershipVersion = snapshot.Version;
            _configuredOwners = newConfiguredOwners;

            foreach ((string effective, PoolState state) in _pools)
            {
                state.IsRetired = !ownedEffective.Contains(effective);
            }

            foreach ((string effective, PoolState state) in _pools)
            {
                if (BeginClearingLocked(state) is { } generation)
                {
                    toClear.Add((effective, generation));
                }
            }
        }

        foreach ((string effective, long generation) in toClear)
        {
            ClearAndComplete(effective, generation);
        }
    }

    /// <summary>
    /// Releases one lease, starting a clear if that was the last one and the identity is no longer
    /// owned. Called only by <see cref="MssqlConnectionLease" />, at most once per lease.
    /// </summary>
    internal void ReleaseLease(string effectiveConnectionString)
    {
        long? generation;

        lock (_stateLock)
        {
            if (!_pools.TryGetValue(effectiveConnectionString, out PoolState? state))
            {
                // Already gone: this lease outlived a clear that has since completed and removed the
                // state. There is nothing of this lease's left to drop.
                return;
            }

            state.Leases--;
            generation = BeginClearingLocked(state);
        }

        if (generation is { } started)
        {
            // The same clear-then-complete path reconciliation uses, so the tombstone protocol holds
            // regardless of which side started the clear.
            ClearAndComplete(effectiveConnectionString, started);
        }
    }

    /// <summary>
    /// The one place a SQL Server connection string is parsed. A primary passes through byte for byte,
    /// with no builder constructed at all, so primary behavior and any operator-supplied pool setting
    /// are untouched. A derivative is rebuilt with <see cref="PoolBlockingPeriod.NeverBlock" />,
    /// overriding whatever the operator supplied, so a failed login or timeout is not replayed from
    /// SqlClient's blocking period on the request that immediately follows. A provider-invalid
    /// derivative string throws here, inside acquisition, rather than at configuration load or
    /// target selection.
    /// </summary>
    internal static string RealizeEffectiveConnectionString(EffectiveDataStoreTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Kind == EffectiveTargetKind.Primary)
        {
            return target.ConnectionString;
        }

        SqlConnectionStringBuilder builder = new(target.ConnectionString)
        {
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// The realization identity of a configured target: the part of the key that
    /// <see cref="RealizeEffectiveConnectionString" /> actually distinguishes. A primary passes
    /// through byte for byte and every derivative kind gets the same rebuild, so two keys with equal
    /// identities are guaranteed to realize to the same effective string. That guarantee is what lets
    /// ownership reasoning cover a configured owner that was never itself acquired - it has no memo
    /// entry, but a realized sibling with the same identity proves where it would land - without
    /// parsing anything. Must be kept in agreement with that method.
    /// </summary>
    private static (string ConfiguredConnectionString, bool IsPrimary) RealizationIdentityOf(
        ConfiguredTargetKey key
    ) => (key.ConfiguredConnectionString, key.Kind == EffectiveTargetKind.Primary);

    /// <summary>
    /// Memoizes the realization, counts the lease, and settles retirement. A currently configured key
    /// reactivates its identity; a key that is not configured leaves retirement to the union of every
    /// other owner that realizes to the same string, so a stale request neither resurrects ownership
    /// nor retires an identity another owner still holds.
    /// </summary>
    private MssqlConnectionLease LeaseLocked(ConfiguredTargetKey key, string effectiveConnectionString)
    {
        _realized[key] = effectiveConnectionString;

        if (!_pools.TryGetValue(effectiveConnectionString, out PoolState? state))
        {
            state = new PoolState();
            _pools[effectiveConnectionString] = state;
        }

        state.IsRetired =
            !_configuredOwners.Contains(key)
            && !OwnedEffectiveLocked(_configuredOwners).Contains(effectiveConnectionString);

        state.Leases++;

        return new MssqlConnectionLease(effectiveConnectionString, _createConnection, this);
    }

    /// <summary>
    /// Every effective string some configured owner is guaranteed to realize to. Derived from the
    /// memo rather than stored, and matched by realization identity rather than by exact key, so
    /// several owners sharing one identity keep it owned until the last of them is gone - including
    /// an owner that was never itself acquired and so has no memo entry of its own - and no
    /// configured string is ever parsed to work it out.
    /// </summary>
    private HashSet<string> OwnedEffectiveLocked(HashSet<ConfiguredTargetKey> owners)
    {
        HashSet<(string ConfiguredConnectionString, bool IsPrimary)> ownerIdentities = owners
            .Select(RealizationIdentityOf)
            .ToHashSet();

        HashSet<string> owned = new(StringComparer.Ordinal);

        foreach ((ConfiguredTargetKey key, string effective) in _realized)
        {
            if (ownerIdentities.Contains(RealizationIdentityOf(key)))
            {
                owned.Add(effective);
            }
        }

        return owned;
    }

    /// <summary>
    /// Publishes a clearing tombstone for a retired, unleased identity that has none, and returns its
    /// generation. Returns null when there is nothing to clear. The pool state is deliberately left in
    /// place: the tombstone is what a concurrent acquisition observes and waits on.
    /// </summary>
    private long? BeginClearingLocked(PoolState state)
    {
        if (state.Leases != 0 || !state.IsRetired || state.Clearing is not null)
        {
            return null;
        }

        ClearingState clearing = new(
            ++_clearGeneration,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );

        state.Clearing = clearing;

        return clearing.Generation;
    }

    /// <summary>
    /// Clears one exact pool and then completes its generation - always, including when the clear
    /// throws. A failing clear leaves the pool as the driver has it, which is the leak-favouring
    /// direction, and the next acquisition proceeds against fresh state rather than waiting forever.
    /// </summary>
    private void ClearAndComplete(string effectiveConnectionString, long generation)
    {
        try
        {
            _poolClearing.ClearPool(effectiveConnectionString);
        }
        catch (Exception exception)
        {
            // Only the exception's type, never the exception itself and never its message, data, or
            // inner exceptions. Clearing constructs a SqlConnection from the effective string, and a
            // SqlClient failure there quotes the offending keyword back - which for this class is a
            // secret.
            // S6667 asks for the caught exception to be passed to the logger. That is the right
            // default and the wrong thing here, for the reason above: the exception carries the
            // untrusted value. Its type is logged instead, which is the part that helps an operator
            // without carrying anything the provider put in it.
#pragma warning disable S6667
            _logger.LogWarning(
                "Error clearing a SQL Server connection pool ({ExceptionType}); leaving it to the driver",
                exception.GetType().Name
            );
#pragma warning restore S6667
        }
        finally
        {
            CompleteClear(effectiveConnectionString, generation);
        }
    }

    /// <summary>
    /// Retires the tombstone this generation owns, removes that pool's state so a future acquisition
    /// starts fresh, prunes the memo entries that named it and are no longer configured, and then
    /// releases the waiters outside the lock.
    /// </summary>
    private void CompleteClear(string effectiveConnectionString, long generation)
    {
        TaskCompletionSource? completion = null;

        lock (_stateLock)
        {
            if (
                _pools.TryGetValue(effectiveConnectionString, out PoolState? state)
                && state.Clearing?.Generation == generation
            )
            {
                completion = state.Clearing.Completion;
                _pools.Remove(effectiveConnectionString);

                foreach (ConfiguredTargetKey key in _realized.Keys.ToArray())
                {
                    if (
                        string.Equals(_realized[key], effectiveConnectionString, StringComparison.Ordinal)
                        && !_configuredOwners.Contains(key)
                    )
                    {
                        _realized.Remove(key);
                    }
                }
            }
        }

        // Outside the lock: a continuation resuming here would otherwise run under it.
        completion?.SetResult();
    }
}

/// <summary>
/// An opened connection together with the lease on the pool identity it came from. Disposing it
/// disposes the connection first and releases the lease second, so nothing can retire that identity
/// while a connection drawn from it is still open. Disposal is idempotent.
/// </summary>
public sealed class MssqlLeasedConnection : IAsyncDisposable
{
    private readonly MssqlConnectionLease? _lease;
    private int _disposed;

    private MssqlLeasedConnection(DbConnection connection, MssqlConnectionLease? lease)
    {
        Connection = connection;
        _lease = lease;
    }

    public DbConnection Connection { get; }

    /// <summary>
    /// The claim this connection was drawn from, for a caller that takes ownership of the connection
    /// and must therefore also take ownership of the claim. Null only for the test seam below.
    /// </summary>
    public IAsyncDisposable? Lease => _lease;

    /// <summary>
    /// For a test seam that supplies its own already-open connection and therefore holds no lease.
    /// </summary>
    internal static MssqlLeasedConnection WithoutLease(DbConnection connection) => new(connection, null);

    /// <summary>
    /// Opens a connection for the target through the acquisition boundary. On failure the lease is
    /// released before the original exception propagates.
    /// </summary>
    internal static async Task<MssqlLeasedConnection> OpenAsync(
        IMssqlConnectionAcquisition acquisition,
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken
    )
    {
        MssqlConnectionLease lease = await acquisition
            .AcquireLeaseAsync(target, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            DbConnection connection = await lease.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new MssqlLeasedConnection(connection, lease);
        }
        catch
        {
            // Releasing the claim must not replace the open failure the caller needs to see.
            await DisposeWithoutMaskingAsync(lease).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // The claim is released even when disposing the connection throws, so a failure there
            // cannot strand the pool identity. Ordering still holds: the connection goes first.
            if (_lease is not null)
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Disposes during failure handling. A fault raised while cleaning up is swallowed on purpose: the
    /// exception the caller must see is the one that started the cleanup, and .NET has no way to attach
    /// a secondary exception to it without changing the type the caller catches.
    /// </summary>
    internal static async ValueTask DisposeWithoutMaskingAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Intentionally ignored; see the summary above.
        }
    }
}

/// <summary>
/// A held claim on one SQL Server pool identity, and the only way to construct or open a connection
/// against it. Disposal is idempotent, so a caller that disposes twice releases exactly once.
/// </summary>
public sealed class MssqlConnectionLease : IAsyncDisposable, IDisposable
{
    private readonly Func<string, DbConnection> _createConnection;
    private readonly MssqlConnectionAcquisition? _acquisition;
    private int _released;

    internal MssqlConnectionLease(
        string effectiveConnectionString,
        Func<string, DbConnection> createConnection,
        MssqlConnectionAcquisition? acquisition = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveConnectionString);
        ArgumentNullException.ThrowIfNull(createConnection);

        EffectiveConnectionString = effectiveConnectionString;
        _createConnection = createConnection;
        _acquisition = acquisition;
    }

    /// <summary>
    /// The realized string this lease is held against. It is the pool identity, so a primary and a
    /// derivative whose stored text is byte-identical are distinct here.
    /// </summary>
    public string EffectiveConnectionString { get; }

    /// <summary>True once the lease has been released, whether by sync or async disposal.</summary>
    internal bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>Constructs an unopened connection against this lease's pool identity.</summary>
    public DbConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(IsReleased, this);

        return _createConnection(EffectiveConnectionString);
    }

    /// <summary>
    /// Constructs and opens a connection. On failure or cancellation the partially constructed
    /// connection is disposed and the original exception propagates unchanged; the lease itself is
    /// left for the caller's own disposal, so an <c>await using</c> around the lease still releases
    /// exactly once.
    /// </summary>
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // A disposal fault must not replace the open failure or the cancellation.
            await MssqlLeasedConnection.DisposeWithoutMaskingAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => Release();

    /// <summary>
    /// Exactly once, however many times disposal is called: a double release would drop another
    /// caller's claim and could clear a pool still in use.
    /// </summary>
    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _acquisition?.ReleaseLease(EffectiveConnectionString);
    }
}
