// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>
/// SQL Server transaction control for a <see cref="JobDatabaseSession"/> (spec D-4, D-5, A3).
/// </summary>
/// <remarks>
/// SqlClient has no asynchronous begin, commit, or rollback: the inherited <c>DbConnection.BeginTransactionAsync</c>
/// and <c>DbTransaction.CommitAsync</c> check their token once and then make the synchronous call. Every job
/// transaction is therefore an API transaction whose synchronous begin runs on the thread pool, where the session
/// can stop waiting for it, and whose commit and rollback are T-SQL statements, which a command timeout and a token
/// can cancel. A T-SQL <c>COMMIT</c> or <c>ROLLBACK</c> issued on an API transaction ends it, and SqlClient then
/// treats that transaction as completed. API transactions matter for cleanup: closing a connection rolls back its
/// open API transaction, whereas a transaction begun in T-SQL survives the close and keeps its locks until the pool
/// next reuses the connection.
/// </remarks>
internal static class MssqlJobSession
{
    internal const string CommitTransaction = "COMMIT TRANSACTION;";

    internal const string EndSession = "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; SET LOCK_TIMEOUT -1;";

    internal static Task OpenAsync(
        JobDatabaseSession session,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) => session.RunAsync("Open", token => session.Connection.OpenAsync(token), deadline, cancellationToken);

    /// <summary>Begins the session's API transaction, bounded by the time left on <paramref name="deadline"/>.</summary>
    internal static async Task BeginAsync(
        JobDatabaseSession session,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.Transaction = await session.RunAsync(
            "Begin",
            _ =>
                Task.Run(
                    () => (DbTransaction)((SqlConnection)session.Connection).BeginTransaction(),
                    CancellationToken.None
                ),
            deadline,
            cancellationToken
        );

    /// <summary>Runs a statement in the session's transaction, bounded by the time left on the deadline.</summary>
    internal static Task<int> ExecuteAsync(
        JobDatabaseSession session,
        string name,
        string sql,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.RunAsync(
            name,
            token => session.Connection.ExecuteAsync(Command(session, sql, null, deadline, token)),
            deadline,
            cancellationToken
        );

    /// <summary>
    /// A statement in the session's transaction while it is active, bounded by the time left on the deadline. A
    /// transaction that a T-SQL <c>COMMIT</c> or <c>ROLLBACK</c> already ended has no connection and is not named.
    /// </summary>
    internal static CommandDefinition Command(
        JobDatabaseSession session,
        string sql,
        object? parameters,
        JobDeadline deadline,
        CancellationToken token
    ) =>
        new(
            sql,
            parameters,
            session.Transaction?.Connection is null ? null : session.Transaction,
            deadline.CommandTimeoutSeconds,
            cancellationToken: token
        );

    /// <summary>The session cleanup: rolls back an open transaction and resets <c>LOCK_TIMEOUT</c>.</summary>
    internal static Func<CancellationToken, Task> EndSessionWith(
        JobDatabaseSession session,
        JobDeadline deadline,
        string endSessionSql
    ) => token => session.Connection.ExecuteAsync(Command(session, endSessionSql, null, deadline, token));
}
