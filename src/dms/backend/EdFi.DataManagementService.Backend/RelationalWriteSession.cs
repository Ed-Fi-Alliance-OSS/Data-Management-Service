// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using EdFi.DataManagementService.Core.External.Diagnostics;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteSessionFactory
{
    Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default);
}

public interface IRelationalWriteSession : IAsyncDisposable
{
    DbConnection Connection { get; }

    DbTransaction Transaction { get; }

    /// <summary>
    /// Creates a provider-specific <see cref="DbCommand"/> bound to this session's connection
    /// and transaction. This is the single command-creation hook for the session; decorators
    /// that record or fail writes intercept here. Command executors produced by
    /// <see cref="CreateCommandExecutor"/> route through this method so a decorator observes
    /// every read and write issued in-session.
    /// </summary>
    DbCommand CreateCommand(RelationalCommand command);

    /// <summary>
    /// Returns an <see cref="IRelationalCommandExecutor"/> scoped to this session. The default
    /// implementation builds an executor that delegates command creation back to
    /// <see cref="CreateCommand(RelationalCommand)"/>, so decorators only need to override
    /// <c>CreateCommand</c> to intercept every in-session command (reads and writes).
    /// Test stubs may override this to inject a fake executor directly.
    /// </summary>
    IRelationalCommandExecutor CreateCommandExecutor() => SessionRelationalCommandExecutor.ForSession(this);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

internal sealed class RelationalWriteSession : IRelationalWriteSession
{
    private RelationalWriteSessionState _state = RelationalWriteSessionState.Pending;
    private bool _disposed;

    // DMS-1236 instrumentation: session span (creation to dispose) approximates the
    // transaction-open window; while it is open, Npgsql command spans are attributed
    // to Db.Command.InTxn so the idle-in-transaction gap can be derived.
    private readonly long _createdTimestamp = Stopwatch.GetTimestamp();
    private readonly RequestTiming? _timing = RequestTimingContext.Current;

    public RelationalWriteSession(DbConnection connection, DbTransaction transaction)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _timing?.EnterDbSession();
    }

    public DbConnection Connection { get; }

    public DbTransaction Transaction { get; }

    public DbCommand CreateCommand(RelationalCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SessionRelationalCommandFactory.CreateCommand(Connection, Transaction, command);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == RelationalWriteSessionState.Committed)
        {
            return;
        }

        if (_state == RelationalWriteSessionState.RolledBack)
        {
            throw new InvalidOperationException(
                "Relational write session cannot commit after it has already rolled back."
            );
        }

        long commitStart = Stopwatch.GetTimestamp();
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _timing?.Record(RequestTimingRegistry.DbPhases.Commit, commitStart);
        _state = RelationalWriteSessionState.Committed;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state == RelationalWriteSessionState.RolledBack)
        {
            return;
        }

        if (_state == RelationalWriteSessionState.Committed)
        {
            throw new InvalidOperationException(
                "Relational write session cannot roll back after it has already committed."
            );
        }

        long rollbackStart = Stopwatch.GetTimestamp();
        await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _timing?.Record(RequestTimingRegistry.DbPhases.Rollback, rollbackStart);
        _state = RelationalWriteSessionState.RolledBack;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);

        if (_timing is not null)
        {
            _timing.Record(RequestTimingRegistry.DbPhases.Session, _createdTimestamp);
            _timing.ExitDbSession();
        }
    }

    private enum RelationalWriteSessionState
    {
        Pending,
        Committed,
        RolledBack,
    }
}
