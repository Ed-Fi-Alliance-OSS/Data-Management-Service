// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// A claim on one cached NpgsqlDataSource. While it is held, the data source is not disposed even if
/// its configuration is removed; the disposal happens when the last lease is released.
/// </summary>
/// <remarks>
/// This is the only way to obtain a data source from the cache, which is what keeps a caller from
/// holding one the cache no longer knows about.
/// </remarks>
public sealed class NpgsqlDataSourceLease : IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSourceCache _cache;
    private readonly string _connectionString;
    private int _released;

    internal NpgsqlDataSourceLease(
        NpgsqlDataSourceCache cache,
        string connectionString,
        NpgsqlDataSource dataSource
    )
    {
        _cache = cache;
        _connectionString = connectionString;
        DataSource = dataSource;
    }

    /// <summary>The leased data source. Never null, and valid until this lease is released.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>Whether this lease has already been released.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>Constructs an unopened connection against this lease's data source.</summary>
    /// <remarks>
    /// For the callers whose shared support owns the open and therefore needs a connection factory
    /// rather than an open connection. Refusing once the lease is released is what stops a connection
    /// being created against a data source this lease no longer keeps alive.
    /// </remarks>
    public NpgsqlConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(IsReleased, this);

        return DataSource.CreateConnection();
    }

    public void Dispose() => Release();

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Exactly once, however many times disposal is called: a double release would decrement another
    /// caller's lease and could dispose a data source still in use.
    /// </summary>
    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _cache.ReleaseLease(_connectionString, DataSource);
    }
}

/// <summary>
/// A connection together with the lease that keeps its data source alive, so a caller can hand both to
/// something that owns a connection and nothing has to remember to release the lease separately.
/// </summary>
public sealed class LeasedNpgsqlConnection : IAsyncDisposable
{
    private readonly NpgsqlDataSourceLease _lease;
    private int _disposed;

    internal LeasedNpgsqlConnection(NpgsqlConnection connection, NpgsqlDataSourceLease lease)
    {
        Connection = connection;
        _lease = lease;
    }

    public NpgsqlConnection Connection { get; }

    /// <summary>
    /// The lease alone, for the few owners that already dispose the connection themselves and need
    /// only to release the claim afterwards. Internal, because handing it out publicly would let a
    /// caller release the claim while still holding the connection.
    /// </summary>
    internal NpgsqlDataSourceLease Lease => _lease;

    /// <summary>
    /// Disposes something that must be released before its connection - a transaction - and then the
    /// leased connection itself, so the claim is given back even when that first disposal throws. The
    /// first exception is the one that propagates; a later cleanup failure does not replace it.
    /// </summary>
    /// <remarks>
    /// Every owner that holds a transaction over a leased connection needs this exact ordering, and
    /// getting it wrong strands a lease for the life of the process rather than failing visibly, so it
    /// lives here once rather than being rewritten at each owner.
    /// </remarks>
    internal static async ValueTask DisposeOwnedAsync(
        IAsyncDisposable precedingResource,
        IAsyncDisposable? owned
    )
    {
        try
        {
            await precedingResource.DisposeAsync();
        }
        catch
        {
            // Failure handling: the exception that started the cleanup is the one the caller must
            // see, and .NET has no way to attach a secondary fault to it without changing the type
            // the caller catches - so a fault from the owned disposal here is swallowed, matching
            // the SQL Server backend's DisposeWithoutMaskingAsync.
            if (owned is not null)
            {
                try
                {
                    await owned.DisposeAsync();
                }
                catch (Exception)
                {
                    // Intentionally ignored; see above.
                }
            }

            throw;
        }

        if (owned is not null)
        {
            await owned.DisposeAsync();
        }
    }

    /// <summary>
    /// Disposes the connection, then releases the lease - in that order, because releasing first could
    /// dispose the data source out from under a connection still being returned to its pool.
    /// </summary>
    /// <remarks>
    /// The lease is released even when the connection's own disposal throws, and the connection's
    /// exception is the one that propagates.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Connection.DisposeAsync();
        }
        finally
        {
            await _lease.DisposeAsync();
        }
    }
}
