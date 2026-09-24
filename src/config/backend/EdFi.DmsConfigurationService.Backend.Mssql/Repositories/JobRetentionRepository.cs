// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>
/// SQL Server retention of finished jobs (spec D-17): one bounded <c>DELETE TOP</c> batch per call, of
/// <c>Completed</c> or <c>Error</c> jobs whose <c>FinishedAt</c> is at or before database now minus the retention
/// window. The cutoff is database time, active jobs are excluded by status whatever their <c>FinishedAt</c>, and
/// rows another transaction holds are skipped (<c>READPAST</c>), so the statement never waits for a lock and the
/// time it reads when it starts is current. <see cref="JobRetentionTimings.BatchTimeout"/> bounds the batch, and
/// every wait runs through a <see cref="JobDatabaseSession"/>.
/// </summary>
public sealed class JobRetentionRepository(IOptions<DatabaseOptions> databaseOptions)
    : IJobRetentionRepository
{
    private const string DeleteBatchSql = """
        DELETE TOP (@BatchSize) FROM dmscs.Job WITH (READPAST, ROWLOCK)
        WHERE Status IN (N'Completed', N'Error')
          AND FinishedAt <= DATEADD(second, -@RetentionSeconds, SYSUTCDATETIME());
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
            new SqlConnection(databaseOptions.Value.DatabaseConnection),
            SessionHooks
        );
        try
        {
            await MssqlJobSession.OpenAsync(session, budget, cancellationToken);
            int deleted = await session.RunAsync(
                "RetentionBatch",
                token =>
                    session.Connection.ExecuteAsync(
                        MssqlJobSession.Command(
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
                MssqlJobDiagnostics.From(exception, "DeleteFinishedOlderThan")
            );
        }
        finally
        {
            // An autocommitted statement leaves no transaction open and changes no session setting.
            await session.EndAsync(budget, null);
        }
    }
}
