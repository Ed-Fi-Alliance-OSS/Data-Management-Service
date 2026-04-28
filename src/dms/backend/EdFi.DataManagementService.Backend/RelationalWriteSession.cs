// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

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
    /// Test stubs may override this to inject a fake executor directly.
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

internal sealed class RelationalWriteSession(DbConnection connection, DbTransaction transaction)
    : IRelationalWriteSession
{
    private RelationalWriteSessionState _state = RelationalWriteSessionState.Pending;
    private bool _disposed;

    public DbConnection Connection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public DbTransaction Transaction { get; } =
        transaction ?? throw new ArgumentNullException(nameof(transaction));

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

        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
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
    }

    private enum RelationalWriteSessionState
    {
        Pending,
        Committed,
        RolledBack,
    }
}
