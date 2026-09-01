// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheAdministrativeMutex
{
    RelationalProviderToken ProviderToken { get; }

    Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
        DocumentCacheTargetConnectionInput connectionInput,
        CancellationToken cancellationToken = default
    );
}

internal interface IDocumentCacheAdministrativeMutexLease : IAsyncDisposable
{
    RelationalProviderToken ProviderToken { get; }

    DbConnection Connection { get; }

    bool IsSessionOpen { get; }

    Task<IRelationalWriteSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default
    );
}

internal sealed class DocumentCacheAdministrativeMutexSessionLostException(
    RelationalProviderToken providerToken,
    Exception? innerException = null
)
    : InvalidOperationException(
        $"DocumentCache administrative mutex session for provider '{providerToken.Value}' is not open. The command must abort without reconnecting under presumed mutex ownership.",
        innerException
    );

internal static class DocumentCacheAdministrativeMutexGuards
{
    public static string RequireConnectionString(
        DocumentCacheTargetConnectionInput connectionInput,
        RelationalProviderToken providerToken
    )
    {
        ArgumentNullException.ThrowIfNull(connectionInput);
        ArgumentNullException.ThrowIfNull(providerToken);

        if (connectionInput.ProviderToken != providerToken)
        {
            throw new InvalidOperationException(
                $"DocumentCache administrative mutex provider '{providerToken.Value}' cannot acquire a mutex for connection input provider '{connectionInput.ProviderToken.Value}'."
            );
        }

        if (string.IsNullOrWhiteSpace(connectionInput.Value))
        {
            throw new InvalidOperationException(
                $"DocumentCache administrative mutex provider '{providerToken.Value}' requires a non-empty connection string."
            );
        }

        return connectionInput.Value;
    }
}

internal abstract class DocumentCacheAdministrativeMutexLease(
    RelationalProviderToken providerToken,
    DbConnection connection,
    IAsyncDisposable? ownedResource = null
) : IDocumentCacheAdministrativeMutexLease
{
    private bool _disposed;

    public RelationalProviderToken ProviderToken { get; } =
        providerToken ?? throw new ArgumentNullException(nameof(providerToken));

    public DbConnection Connection { get; } =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public bool IsSessionOpen => !_disposed && Connection.State == ConnectionState.Open;

    public async Task<IRelationalWriteSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Connection.State != ConnectionState.Open)
        {
            throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken);
        }

        try
        {
            await ValidateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DocumentCacheAdministrativeMutexSessionLostException)
        {
            await CloseLostConnectionAsync().ConfigureAwait(false);
            throw;
        }
        catch (DbException exception)
        {
            await CloseLostConnectionAsync().ConfigureAwait(false);
            throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken, exception);
        }
        catch (InvalidOperationException exception) when (Connection.State != ConnectionState.Open)
        {
            await CloseLostConnectionAsync().ConfigureAwait(false);
            throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken, exception);
        }

        DbTransaction transaction;
        try
        {
            transaction = await Connection
                .BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            await CloseLostConnectionAsync().ConfigureAwait(false);
            throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken, exception);
        }
        catch (InvalidOperationException exception) when (Connection.State != ConnectionState.Open)
        {
            await CloseLostConnectionAsync().ConfigureAwait(false);
            throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken, exception);
        }

        return new AdministrativeMutexRelationalWriteSession(Connection, transaction);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Connection.State == ConnectionState.Open)
            {
                await ReleaseAsync(Connection, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Whatever keeps this connection's source alive is released after the connection, and
                // released even when the connection's own disposal throws. A provider that owns
                // nothing beyond the connection supplies nothing here.
                if (ownedResource is not null)
                {
                    await ownedResource.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    protected abstract Task ReleaseAsync(DbConnection connection, CancellationToken cancellationToken);

    protected virtual Task ValidateSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CloseLostConnectionAsync()
    {
        try
        {
            await Connection.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // The session is already unusable. Preserve the session-loss classification.
        }
    }
}

internal sealed class AdministrativeMutexRelationalWriteSession(
    DbConnection connection,
    DbTransaction transaction
) : IRelationalWriteSession
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
                "Administrative mutex write session cannot commit after it has already rolled back."
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
                "Administrative mutex write session cannot roll back after it has already committed."
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
    }

    private enum RelationalWriteSessionState
    {
        Pending,
        Committed,
        RolledBack,
    }
}
