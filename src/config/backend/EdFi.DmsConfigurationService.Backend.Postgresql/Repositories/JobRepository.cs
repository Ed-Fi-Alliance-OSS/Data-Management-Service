// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>
/// PostgreSQL enqueue and status read for <c>dmscs.Job</c> (spec D-14, D-16a, D-18). A failure is returned
/// as <c>FailureUnknown</c> with a <see cref="JobFailureDiagnostic"/>; nothing here logs an exception
/// message or a payload value. Cancellation requested by the caller's token is not a failure and is
/// rethrown.
/// </summary>
public sealed class JobRepository(
    IOptions<DatabaseOptions> databaseOptions,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IJobRepository
{
    internal const string EnqueueOperation = "EnqueueJob";
    internal const string GetStatusOperation = "GetJobStatus";

    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId =>
        TenantContext is TenantContext.Multitenant multitenant ? multitenant.TenantId : null;

    public async Task<JobEnqueueResult> EnqueueJob(
        JobEnqueueCommand command,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        // CreatedAt and NextAttemptAt come from one time sample, so a new job is eligible from the moment it
        // was created (A1). now() is the transaction start time, the same value as the CreatedAt default.
        const string Sql = """
            INSERT INTO "dmscs"."Job" (
                "JobId", "TenantId", "JobType", "PayloadVersion", "Payload", "Status", "CreatedAt",
                "NextAttemptAt", "CreatedBy")
            SELECT @JobId, @TenantId, @JobType, @PayloadVersion, @Payload, @Status, sample."Now",
                sample."Now", @CreatedBy
            FROM (SELECT (now() AT TIME ZONE 'UTC') AS "Now") AS sample;
            """;

        string jobId = Guid.NewGuid().ToString("N");

        try
        {
            CommandDefinition insert = new(
                Sql,
                new
                {
                    JobId = jobId,
                    TenantId,
                    command.JobType,
                    command.PayloadVersion,
                    Payload = command.PayloadJson,
                    Status = JobStatuses.Pending,
                    CreatedBy = auditContext.GetCurrentUser(),
                },
                transaction,
                cancellationToken: cancellationToken
            );

            if (transaction is null)
            {
                await using NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(insert);
            }
            else
            {
                // The caller owns the transaction, so the job commits or rolls back with it. A completed
                // transaction must fail rather than let the insert run outside it: Npgsql keeps Connection set
                // after a commit or rollback and runs a command on that transaction in autocommit, but reading
                // IsolationLevel throws once the transaction has completed or been disposed.
                _ = transaction.IsolationLevel;
                DbConnection connection =
                    transaction.Connection
                    ?? throw new InvalidOperationException("The supplied transaction has no connection.");
                await connection.ExecuteAsync(insert);
            }

            return new JobEnqueueResult.Success(jobId);
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken))
        {
            return new JobEnqueueResult.FailureUnknown(
                PostgresqlJobDiagnostics.From(exception, EnqueueOperation)
            );
        }
    }

    public async Task<JobStatusQueryResult> GetJobStatus(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            // An exact, parameterized match (D-14), scoped to the current tenant.
            string sql = $"""
                SELECT "JobId", "Status", "CreatedAt", "FinishedAt", "ErrorMessage"
                FROM "dmscs"."Job"
                WHERE "JobId" = @JobId AND {TenantContext.TenantWhereClause()};
                """;

            await using NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);
            JobStatusRow? row = await connection.QuerySingleOrDefaultAsync<JobStatusRow>(
                new CommandDefinition(
                    sql,
                    new { JobId = jobId, TenantId },
                    cancellationToken: cancellationToken
                )
            );

            return row is null
                ? new JobStatusQueryResult.FailureNotFound()
                : new JobStatusQueryResult.Success(ToResponse(row));
        }
        catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken))
        {
            return new JobStatusQueryResult.FailureUnknown(
                PostgresqlJobDiagnostics.From(exception, GetStatusOperation)
            );
        }
    }

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    // The columns hold UTC in TIMESTAMP (without time zone), which Npgsql reads as DateTimeKind.Unspecified.
    private static JobStatusResponse ToResponse(JobStatusRow row) =>
        new()
        {
            JobId = row.JobId,
            Status = row.Status,
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            FinishedAt = row.FinishedAt is { } finishedAt
                ? DateTime.SpecifyKind(finishedAt, DateTimeKind.Utc)
                : null,
            ErrorMessage = row.ErrorMessage,
        };

    private sealed record JobStatusRow(
        string JobId,
        string Status,
        DateTime CreatedAt,
        DateTime? FinishedAt,
        string? ErrorMessage
    );
}
