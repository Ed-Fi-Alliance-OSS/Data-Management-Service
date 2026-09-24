// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>The time left on one operation's deadline, from its start and its budget.</summary>
internal readonly record struct OperationDeadline(long Started, TimeSpan Budget)
{
    public static OperationDeadline Start(TimeSpan budget) => new(Stopwatch.GetTimestamp(), budget);

    public TimeSpan Remaining
    {
        get
        {
            TimeSpan remaining = Budget - Stopwatch.GetElapsedTime(Started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// A command timeout derived from the time left, at least 1 s because 0 would mean no timeout. The token from
    /// <see cref="CreateTokenSource"/> still cancels the command at the exact deadline.
    /// </summary>
    public int CommandTimeoutSeconds => Math.Max(1, (int)Math.Ceiling(Remaining.TotalSeconds));

    /// <summary>A token source cancelled when the time left runs out, linked to <paramref name="cancellationToken"/>.</summary>
    public CancellationTokenSource CreateTokenSource(CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Remaining);
        return source;
    }
}

/// <summary>
/// Deadline-bounded transaction control for the job subsystem on SQL Server (spec D-4, D-5, A3).
/// </summary>
/// <remarks>
/// <para>
/// SqlClient has no asynchronous begin, commit, or rollback: the inherited <c>DbConnection.BeginTransactionAsync</c>
/// and <c>DbTransaction.CommitAsync</c> check their token once and then make the synchronous call, which no
/// deadline or token bounds. Every job transaction is therefore an API transaction whose synchronous begin runs
/// off the caller's thread under the deadline, and whose commit and rollback are T-SQL statements, which a
/// command timeout and a cancellation token do bound. A T-SQL <c>COMMIT</c> or <c>ROLLBACK</c> issued on an API
/// transaction ends it, and SqlClient then treats that transaction as completed.
/// </para>
/// <para>
/// Ending a session rolls back any open transaction and resets <c>LOCK_TIMEOUT</c>, within the time left on the
/// operation's deadline, never a fresh allowance. When that cannot finish, the connection is disposed in the
/// background instead: closing a connection rolls back its open API transaction, and the pool resets the session,
/// lock timeout included, before the connection is reused (step 2.6 probes), so the operation never waits for
/// it. A transaction begun in T-SQL would not be rolled back on close, only when the pool next reuses the
/// connection, and would hold its locks until then; that is why every job transaction is an API transaction.
/// </para>
/// </remarks>
internal static class MssqlJobSession
{
    internal const string CommitTransaction = "COMMIT TRANSACTION;";

    internal const string EndSession = "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; SET LOCK_TIMEOUT -1;";

    /// <summary>Runs <paramref name="sql"/> bounded by the time left on <paramref name="deadline"/>.</summary>
    internal static Task<int> ExecuteAsync(
        SqlConnection connection,
        string sql,
        DbTransaction? transaction,
        OperationDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                transaction: transaction,
                commandTimeout: deadline.CommandTimeoutSeconds,
                cancellationToken: cancellationToken
            )
        );

    /// <summary>
    /// Ends the session within the time left on <paramref name="deadline"/> and releases the connection. With
    /// <paramref name="sessionClean"/>, no transaction is open and no session setting was changed, so the
    /// connection is released without a round trip.
    /// </summary>
    internal static async Task EndAsync(
        SqlConnection connection,
        DbTransaction? transaction,
        OperationDeadline deadline,
        string endSessionSql,
        bool sessionClean = false
    )
    {
        if (sessionClean || connection.State != ConnectionState.Open)
        {
            if (transaction is not null)
            {
                // Ended by its commit or by the closed connection, so disposing it makes no round trip.
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            return;
        }

        if (deadline.Remaining > TimeSpan.Zero)
        {
            using CancellationTokenSource cleanup = deadline.CreateTokenSource(CancellationToken.None);
            try
            {
                // An API transaction that is still active must be named on the command; one that a T-SQL COMMIT
                // already ended has no connection and must not be.
                DbTransaction? active = transaction?.Connection is null ? null : transaction;
                await ExecuteAsync(connection, endSessionSql, active, deadline, cleanup.Token);
                if (transaction is not null)
                {
                    // Already ended by the statement above, so disposing it makes no round trip.
                    await transaction.DisposeAsync();
                }

                await connection.DisposeAsync();
                return;
            }
            catch (Exception exception)
                when (exception is SqlException or InvalidOperationException or OperationCanceledException)
            {
                // The session could not be ended within the deadline.
            }
        }

        ReleaseInBackground(connection);
    }

    /// <summary>
    /// Disposes a connection whose session could not be ended in time, without waiting for it: closing rolls
    /// back an open transaction, and the pool resets the session before reuse.
    /// </summary>
    internal static void ReleaseInBackground(SqlConnection connection) =>
        _ = Task.Run(() =>
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception exception) when (exception is SqlException or InvalidOperationException)
            {
                // The connection is already unusable, and the pool discards it.
            }
        });

    /// <summary>
    /// Begins an API transaction, which a fence needs for its consumer's work, bounded by the time left on
    /// <paramref name="deadline"/>. SqlClient's begin is synchronous, so it runs off the caller's thread; when the
    /// deadline or <paramref name="cancellationToken"/> ends the wait first, the connection is released only after
    /// the begin finishes, and <paramref name="handedOff"/> tells the caller it no longer owns the connection.
    /// </summary>
    internal static async Task<DbTransaction> BeginApiTransactionAsync(
        SqlConnection connection,
        OperationDeadline deadline,
        CancellationToken cancellationToken,
        Action handedOff
    )
    {
        Task<SqlTransaction> begin = Task.Run(() => connection.BeginTransaction(), CancellationToken.None);
        try
        {
            return await begin.WaitAsync(deadline.Remaining, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            handedOff();
            _ = begin.ContinueWith(
                _ => ReleaseInBackground(connection),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );
            throw;
        }
    }
}

/// <summary>
/// One connection and its current API transaction, for one bounded operation. Once a begin that outlived its
/// deadline owns the connection, the session no longer touches it.
/// </summary>
internal sealed class BoundedSession(SqlConnection connection)
{
    public SqlConnection Connection { get; } = connection;

    public DbTransaction? Transaction { get; private set; }

    public bool HandedOff { get; private set; }

    public async Task BeginAsync(OperationDeadline deadline, CancellationToken cancellationToken) =>
        Transaction = await MssqlJobSession.BeginApiTransactionAsync(
            Connection,
            deadline,
            cancellationToken,
            () => HandedOff = true
        );

    public Task<int> ExecuteAsync(
        string sql,
        OperationDeadline deadline,
        CancellationToken cancellationToken
    ) => MssqlJobSession.ExecuteAsync(Connection, sql, Transaction, deadline, cancellationToken);

    /// <summary>A statement in the current transaction, bounded by the time left on <paramref name="deadline"/>.</summary>
    public CommandDefinition Command(
        string sql,
        object? parameters,
        OperationDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        new(
            sql,
            parameters,
            Transaction,
            deadline.CommandTimeoutSeconds,
            cancellationToken: cancellationToken
        );

    public Task EndAsync(OperationDeadline deadline, string endSessionSql, bool sessionClean = false) =>
        HandedOff
            ? Task.CompletedTask
            : MssqlJobSession.EndAsync(Connection, Transaction, deadline, endSessionSql, sessionClean);
}
