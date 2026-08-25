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

    /// <summary>
    /// Records that a provider database failure was raised on this session. The session uses it as one of
    /// two preconditions for tolerating a rollback of a transaction the server has already completed; it
    /// never alters, wraps, or suppresses the exception itself.
    /// </summary>
    /// <remarks>
    /// A default no-op keeps the many test doubles that wrap no real session compiling and behaving as
    /// before. A decorator over a real session must forward this, or the session it wraps never learns of
    /// the failure and loses the tolerance the production session would have had.
    /// </remarks>
    void ReportDatabaseFailure(DbException exception) { }
}

/// <summary>
/// Decides whether a transaction has already been completed by the server, so that a client-side rollback
/// could only throw.
/// </summary>
/// <remarks>
/// The probe receives every piece of evidence needed to exclude a connection-level fault, which presents
/// similarly to a server-side rollback from the client's side but must still surface.
/// </remarks>
internal interface IRelationalTransactionStateProbe
{
    bool IsAlreadyCompleted(DbConnection connection, DbTransaction transaction, DbException reportedFailure);
}

/// <summary>
/// The default probe, which never reports completion. Providers whose aborted transaction always accepts a
/// rollback — PostgreSQL — keep it, so their behavior is identical to having no probe at all.
/// </summary>
internal sealed class NeverCompletedTransactionStateProbe : IRelationalTransactionStateProbe
{
    public static readonly NeverCompletedTransactionStateProbe Instance = new();

    private NeverCompletedTransactionStateProbe() { }

    public bool IsAlreadyCompleted(
        DbConnection connection,
        DbTransaction transaction,
        DbException reportedFailure
    ) => false;
}

internal sealed class RelationalWriteSession(
    DbConnection connection,
    DbTransaction transaction,
    IRelationalTransactionStateProbe? transactionStateProbe = null
) : IRelationalWriteSession
{
    private readonly IRelationalTransactionStateProbe _transactionStateProbe =
        transactionStateProbe ?? NeverCompletedTransactionStateProbe.Instance;

    private RelationalWriteSessionState _state = RelationalWriteSessionState.Pending;
    private bool _disposed;
    private DbException? _reportedDatabaseFailure;

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

        // A pre-check, deliberately not a catch. When the server has already rolled this transaction back
        // and detached it, the client-side rollback can only throw, and throwing here would replace a
        // mapped database failure with an unrelated one. Because nothing is caught, a cancellation, a
        // connection fault, and any unrelated invalid-operation failure still propagate by construction
        // rather than by an exclusion list. Both preconditions are required: a detached transaction with no
        // reported failure still takes the physical rollback and surfaces whatever it throws.
        if (
            _reportedDatabaseFailure is { } reportedFailure
            && _transactionStateProbe.IsAlreadyCompleted(Connection, Transaction, reportedFailure)
        )
        {
            _state = RelationalWriteSessionState.RolledBack;
            return;
        }

        await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _state = RelationalWriteSessionState.RolledBack;
    }

    public void ReportDatabaseFailure(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _reportedDatabaseFailure = exception;
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

internal sealed class CommandTimeoutRelationalWriteSession(
    IRelationalWriteSession innerSession,
    Func<TimeSpan> getRemainingCommandBudget
) : IRelationalWriteSession
{
    private readonly IRelationalWriteSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));
    private readonly Func<TimeSpan> _getRemainingCommandBudget =
        getRemainingCommandBudget ?? throw new ArgumentNullException(nameof(getRemainingCommandBudget));

    public DbConnection Connection => _innerSession.Connection;

    public DbTransaction Transaction => _innerSession.Transaction;

    public DbCommand CreateCommand(RelationalCommand command)
    {
        DbCommand dbCommand = _innerSession.CreateCommand(command);
        dbCommand.CommandTimeout = ToCommandTimeoutSeconds(_getRemainingCommandBudget());
        return dbCommand;
    }

    public IRelationalCommandExecutor CreateCommandExecutor() =>
        SessionRelationalCommandExecutor.ForSession(this);

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _innerSession.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _innerSession.RollbackAsync(cancellationToken);

    public void ReportDatabaseFailure(DbException exception) =>
        _innerSession.ReportDatabaseFailure(exception);

    public ValueTask DisposeAsync() => _innerSession.DisposeAsync();

    private static int ToCommandTimeoutSeconds(TimeSpan remainingCommandBudget)
    {
        if (remainingCommandBudget <= TimeSpan.Zero)
        {
            return 1;
        }

        double timeoutSeconds = Math.Ceiling(remainingCommandBudget.TotalSeconds);
        if (timeoutSeconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(1, Convert.ToInt32(timeoutSeconds));
    }
}
