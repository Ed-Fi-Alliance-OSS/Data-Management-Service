// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>Test seams for <see cref="JobDatabaseSession"/>. Production code passes none.</summary>
public sealed class JobDatabaseSessionHooks
{
    /// <summary>
    /// Runs inside an operation's pending task before the operation itself, with the operation's name and token.
    /// A test uses it to hold an operation pending after cancellation, or to make it fail.
    /// </summary>
    public Func<string, CancellationToken, Task>? BeforeOperation { get; init; }

    /// <summary>
    /// Receives the cleanup task when a session is handed over. The task completes, after the connection is
    /// released, with the exception the pending operation ended with, or null.
    /// </summary>
    public Action<Task<Exception?>>? HandedOver { get; init; }
}

/// <summary>
/// One job database connection and its transaction, with the waiting rule of spec A3: the caller waits for a
/// database operation no longer than the time left on its deadline, whether or not the provider has finished.
/// </summary>
/// <remarks>
/// <para>
/// A provider can keep an operation running after its token fires: SqlClient waits for the server to acknowledge
/// the cancellation, Npgsql waits for its cancellation request, and neither bounds the rollback that disposing an
/// open transaction issues. So <see cref="RunAsync{T}"/> stops waiting when the deadline or the caller's token
/// ends first, and hands the session over: a cleanup task becomes the only owner of the pending operation, the
/// transaction, and the connection. It waits for the pending operation, observes how it ended, and only then
/// disposes the transaction and the connection, which rolls back an open transaction. No other command runs on
/// the connection after the hand-over, and the connection returns to the pool only once nothing uses it.
/// </para>
/// <para>
/// An operation abandoned this way has an unknown outcome, which the caller classifies as it would any failure at
/// that point: <see cref="RunAsync{T}"/> rethrows the <see cref="TimeoutException"/> or the cancellation that
/// ended the wait.
/// </para>
/// </remarks>
public sealed class JobDatabaseSession(DbConnection connection, JobDatabaseSessionHooks? hooks = null)
{
    public DbConnection Connection { get; } = connection;

    /// <summary>The current transaction, set by the caller once its begin has completed.</summary>
    public DbTransaction? Transaction { get; set; }

    /// <summary>A cleanup task owns the session; the caller must not use it again.</summary>
    public bool HandedOver { get; private set; }

    /// <summary>
    /// Runs <paramref name="operation"/> with a token cancelled at the deadline, waiting no longer than the time
    /// left on it. When the deadline or <paramref name="cancellationToken"/> ends the wait first, the session is
    /// handed over and the exception that ended the wait is rethrown.
    /// </summary>
    public async Task<T> RunAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> operation,
        JobDeadline deadline,
        CancellationToken cancellationToken
    )
    {
        if (HandedOver)
        {
            throw new InvalidOperationException("The job database session was handed over to cleanup.");
        }

        CancellationTokenSource token = deadline.CreateTokenSource(cancellationToken);
        Task<T> pending = Start(name, operation, token.Token);
        try
        {
            T result = await pending.WaitAsync(deadline.Remaining, cancellationToken);
            token.Dispose();
            return result;
        }
        catch (Exception exception)
            when (exception is TimeoutException or OperationCanceledException && !pending.IsCompleted)
        {
            // The provider has not finished: make sure it is asked to cancel, and give it to cleanup.
            await token.CancelAsync();
            HandOver(pending, token);
            throw;
        }
        catch (Exception)
        {
            token.Dispose();
            throw;
        }
    }

    public Task RunAsync(
        string name,
        Func<CancellationToken, Task> operation,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        RunAsync(
            name,
            async token =>
            {
                await operation(token);
                return true;
            },
            deadline,
            cancellationToken
        );

    /// <summary>
    /// Ends the session within the time left on <paramref name="deadline"/> and releases the connection.
    /// <paramref name="endSession"/> rolls back an open transaction and resets any session setting; null means
    /// nothing is open, and the connection is released without a round trip. When <paramref name="endSession"/>
    /// cannot finish in time, or fails, the session is handed over, and closing the connection rolls back.
    /// </summary>
    public async Task EndAsync(JobDeadline deadline, Func<CancellationToken, Task>? endSession)
    {
        if (HandedOver)
        {
            return;
        }

        if (endSession is null || Connection.State != ConnectionState.Open)
        {
            await DisposeQuietlyAsync();
            return;
        }

        EndOutcome outcome = deadline.Expired
            ? EndOutcome.Failed
            : await TryEndSessionAsync(deadline, endSession);
        if (outcome == EndOutcome.Ended)
        {
            await DisposeQuietlyAsync();
        }
        else if (outcome == EndOutcome.Failed)
        {
            // The session could not be ended here; releasing the connection in cleanup rolls it back.
            HandOver(Task.CompletedTask, null);
        }
    }

    private enum EndOutcome
    {
        Ended,
        Failed,
        HandedOver,
    }

    private async Task<EndOutcome> TryEndSessionAsync(
        JobDeadline deadline,
        Func<CancellationToken, Task> endSession
    )
    {
        try
        {
            await RunAsync("EndSession", endSession, deadline, CancellationToken.None);
            return EndOutcome.Ended;
        }
        catch (Exception)
        {
            // Cleanup already owns the session when the wait for the statement ran out.
            return HandedOver ? EndOutcome.HandedOver : EndOutcome.Failed;
        }
    }

    private Task<T> Start<T>(
        string name,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken token
    ) =>
        hooks?.BeforeOperation is { } before
            ? RunAfterAsync(before, name, operation, token)
            : operation(token);

    private static async Task<T> RunAfterAsync<T>(
        Func<string, CancellationToken, Task> before,
        string name,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken token
    )
    {
        await before(name, token);
        return await operation(token);
    }

    private void HandOver(Task pending, CancellationTokenSource? token)
    {
        HandedOver = true;
        Task<Exception?> cleanup = Task.Run(() => ReleaseAfterAsync(pending, token));
        hooks?.HandedOver?.Invoke(cleanup);
    }

    /// <summary>Waits for the pending operation, observes how it ended, then releases the session.</summary>
    private async Task<Exception?> ReleaseAfterAsync(Task pending, CancellationTokenSource? token)
    {
        Exception? outcome = null;
        try
        {
            await pending;
        }
        catch (Exception exception)
        {
            outcome = exception;
        }

        token?.Dispose();
        await DisposeQuietlyAsync();
        return outcome;
    }

    /// <summary>Disposes the transaction and the connection; a provider error here changes no outcome.</summary>
    private async Task DisposeQuietlyAsync()
    {
        try
        {
            if (Transaction is not null)
            {
                await Transaction.DisposeAsync();
            }
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            // Closing the connection below ends the transaction.
        }

        try
        {
            await Connection.DisposeAsync();
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            // The connection is unusable, and the pool discards it.
        }
    }

    private static bool IsProviderFailure(Exception exception) =>
        exception
            is DbException
                or InvalidOperationException
                or ObjectDisposedException
                or TimeoutException
                or OperationCanceledException
                or IOException;
}
