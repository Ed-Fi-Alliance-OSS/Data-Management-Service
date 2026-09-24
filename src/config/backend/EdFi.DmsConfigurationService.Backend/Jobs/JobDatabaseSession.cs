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
    /// Receives the cleanup task when a session is handed over. The task completes after the connection is
    /// released, with how the pending operation and its cancellation ended.
    /// </summary>
    public Action<Task<JobDatabaseSessionCleanup>>? HandedOver { get; init; }
}

/// <summary>
/// How a handed-over session's cleanup ended: the exception the pending operation ended with, and the exception
/// the operation's cancellation callbacks threw, each null when there was none.
/// </summary>
public sealed record JobDatabaseSessionCleanup(Exception? Operation, Exception? Cancellation);

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
/// Each operation gets a token of its own, which only that cleanup cancels, after the hand-over. The provider's
/// cancellation callbacks therefore never run on the caller's path, where a slow callback would extend the wait
/// and a throwing one could prevent the hand-over; nor on a timer thread, where a timer-driven cancellation would
/// rethrow a callback's exception unhandled. Cleanup waits for both the cancellation and the operation, and
/// observes both, before it releases anything. The provider's command timeout still bounds each statement on the
/// server.
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

        // The operation's own token: never linked or timed, so its callbacks run only in cleanup.
        CancellationTokenSource operationCancellation = new();
        Task<T> pending = InvokeAsync(name, operation, operationCancellation.Token);
        try
        {
            T result = await pending.WaitAsync(deadline.Remaining, cancellationToken);
            operationCancellation.Dispose();
            return result;
        }
        catch (Exception exception)
            when (exception is TimeoutException or OperationCanceledException && !pending.IsCompleted)
        {
            // The provider has not finished. Ownership moves to cleanup first; cleanup then asks the provider
            // to cancel, off this path.
            HandOver(pending, operationCancellation);
            throw;
        }
        catch (Exception)
        {
            operationCancellation.Dispose();
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

    /// <summary>Runs the operation as a task, so even a synchronous failure is a completed task.</summary>
    private async Task<T> InvokeAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken token
    )
    {
        if (hooks?.BeforeOperation is { } before)
        {
            await before(name, token);
        }

        return await operation(token);
    }

    private void HandOver(Task pending, CancellationTokenSource? operationCancellation)
    {
        HandedOver = true;
        Task<JobDatabaseSessionCleanup> cleanup = Task.Run(() =>
            ReleaseAfterAsync(pending, operationCancellation)
        );
        hooks?.HandedOver?.Invoke(cleanup);
    }

    /// <summary>
    /// Asks the pending operation to cancel, waits for both the cancellation callbacks and the operation, observes
    /// how each ended, and only then releases the session.
    /// </summary>
    private async Task<JobDatabaseSessionCleanup> ReleaseAfterAsync(
        Task pending,
        CancellationTokenSource? operationCancellation
    )
    {
        Task cancellation = operationCancellation?.CancelAsync() ?? Task.CompletedTask;
        Exception? cancellationFailure = await ObserveAsync(cancellation);
        Exception? operationFailure = await ObserveAsync(pending);

        operationCancellation?.Dispose();
        await DisposeQuietlyAsync();
        return new JobDatabaseSessionCleanup(operationFailure, cancellationFailure);
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
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
