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

    DbCommand CreateCommand(RelationalCommand command);

    IRelationalCommandExecutor CommandExecutor { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

internal sealed class RelationalWriteSession(DbConnection connection, DbTransaction transaction)
    : IRelationalWriteSession
{
    private RelationalWriteSessionState _state = RelationalWriteSessionState.Pending;
    private bool _disposed;
    private IRelationalCommandExecutor? _commandExecutor;

    public DbConnection Connection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public DbTransaction Transaction { get; } =
        transaction ?? throw new ArgumentNullException(nameof(transaction));

    public IRelationalCommandExecutor CommandExecutor
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _commandExecutor ??= new SessionRelationalCommandExecutor(Connection, Transaction);
        }
    }

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
