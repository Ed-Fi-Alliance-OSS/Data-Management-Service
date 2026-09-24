// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>
/// PostgreSQL retention of finished jobs (spec D-17): one bounded <c>DELETE</c> batch per call, of
/// <c>Completed</c> or <c>Error</c> jobs whose <c>FinishedAt</c> is at or before database now minus the retention
/// window. The cutoff is database time, active jobs are excluded by status whatever their <c>FinishedAt</c>, and
/// rows another transaction holds are skipped. <see cref="JobRetentionTimings.BatchTimeout"/> bounds the batch, and
/// every wait runs through a <see cref="JobDatabaseSession"/>.
/// </summary>
public sealed class JobRetentionRepository(IOptions<DatabaseOptions> databaseOptions)
    : IJobRetentionRepository
{
    private const string DeleteBatchSql = """
        DELETE FROM "dmscs"."Job"
        WHERE "Id" IN (
            SELECT "Id"
            FROM "dmscs"."Job"
            WHERE "Status" IN ('Completed', 'Error')
              AND "FinishedAt" <= (now() AT TIME ZONE 'UTC') - @RetentionSeconds * interval '1 second'
            LIMIT @BatchSize
            FOR UPDATE SKIP LOCKED
        );
        """;

    /// <summary>Test seam: hooks into every session this repository opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    public async Task<JobRetentionResult> DeleteFinishedOlderThan(
        int retentionSeconds,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        JobDeadline budget = JobDeadline.Start(JobRetentionTimings.BatchTimeout);
        JobDatabaseSession session = new(
            new NpgsqlConnection(databaseOptions.Value.DatabaseConnection),
            SessionHooks
        );
        try
        {
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            int deleted = await session.RunAsync(
                "RetentionBatch",
                token =>
                    session.Connection.ExecuteAsync(
                        PostgresqlJobSession.Command(
                            session,
                            DeleteBatchSql,
                            new { RetentionSeconds = retentionSeconds, BatchSize = batchSize },
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );

            return new JobRetentionResult.Success(deleted);
        }
        catch (Exception exception)
        {
            return new JobRetentionResult.FailureUnknown(
                PostgresqlJobDiagnostics.From(exception, "DeleteFinishedOlderThan")
            );
        }
        finally
        {
            // An autocommitted statement leaves no transaction open.
            await session.EndAsync(budget, null);
        }
    }
}
