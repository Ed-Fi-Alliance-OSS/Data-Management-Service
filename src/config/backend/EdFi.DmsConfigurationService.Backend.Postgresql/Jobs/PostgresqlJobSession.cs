// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;

/// <summary>
/// PostgreSQL transaction control for a <see cref="JobDatabaseSession"/> (spec D-4, D-5, A3), on Npgsql's native
/// transaction API.
/// </summary>
/// <remarks>
/// Npgsql's begin, commit, and rollback are asynchronous and take a token, but after the token fires Npgsql can
/// still wait for its cancellation request, and disposing an open transaction rolls back without any token. So
/// every call runs through the session, which never lets the caller wait past the deadline, and an open
/// transaction is rolled back explicitly, within what is left of the deadline, rather than by disposal.
/// </remarks>
internal static class PostgresqlJobSession
{
    internal static Task OpenAsync(
        JobDatabaseSession session,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) => session.RunAsync("Open", token => session.Connection.OpenAsync(token), deadline, cancellationToken);

    internal static async Task BeginAsync(
        JobDatabaseSession session,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.Transaction = await session.RunAsync(
            "Begin",
            async token => await ((NpgsqlConnection)session.Connection).BeginTransactionAsync(token),
            deadline,
            cancellationToken
        );

    internal static Task CommitAsync(
        JobDatabaseSession session,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.RunAsync(
            "Commit",
            token => session.Transaction!.CommitAsync(token),
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

    /// <summary>A statement in the session's transaction, bounded by the time left on the deadline.</summary>
    internal static CommandDefinition Command(
        JobDatabaseSession session,
        string sql,
        object? parameters,
        JobDeadline deadline,
        CancellationToken token
    ) => new(sql, parameters, session.Transaction, deadline.CommandTimeoutSeconds, cancellationToken: token);

    /// <summary>The session cleanup: an explicit rollback of the open transaction.</summary>
    internal static Func<CancellationToken, Task> Rollback(JobDatabaseSession session) =>
        token =>
            session.Transaction is { } transaction ? transaction.RollbackAsync(token) : Task.CompletedTask;
}
