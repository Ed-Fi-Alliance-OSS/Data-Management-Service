// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;

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
    /// Asynchronous because a later retirement path must be able to wait, outside any lock, for an
    /// outstanding clear of the same pool identity to finish before granting a new lease. Nothing waits
    /// today, so this completes synchronously; the shape is what keeps that from becoming a second
    /// migration across every acquisition seam.
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

/// <inheritdoc />
public sealed class MssqlConnectionAcquisition(Func<string, DbConnection>? createConnection = null)
    : IMssqlConnectionAcquisition
{
    private readonly Func<string, DbConnection> _createConnection =
        createConnection ?? (effectiveConnectionString => new SqlConnection(effectiveConnectionString));

    public Task<MssqlConnectionLease> AcquireLeaseAsync(
        EffectiveDataStoreTarget target,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            new MssqlConnectionLease(RealizeEffectiveConnectionString(target), _createConnection)
        );
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
    private int _released;

    internal MssqlConnectionLease(
        string effectiveConnectionString,
        Func<string, DbConnection> createConnection
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveConnectionString);
        ArgumentNullException.ThrowIfNull(createConnection);

        EffectiveConnectionString = effectiveConnectionString;
        _createConnection = createConnection;
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

    private void Release() => Interlocked.Exchange(ref _released, 1);
}
