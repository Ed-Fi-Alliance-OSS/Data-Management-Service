// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>Projects managed outcomes only inside the lease repository's job-locked transaction.</summary>
internal static class ManagedJobRetryOutcome
{
    internal sealed record Job(string JobId, long? TenantId, string JobType, int AttemptCount);

    internal sealed record ExhaustedJob(
        string JobId,
        long? TenantId,
        string JobType,
        int AttemptCount,
        DateTime DatabaseUtcNow
    );

    internal sealed record Target(int Id, int Attempt, string Status);

    private sealed record Aggregate(int Id, int LastAttempt);

    private sealed record ExhaustedAggregate(int Id, string JobId, int LastAttempt, string Status);

    internal static async Task<Target?> LockAsync(
        JobDatabaseSession session,
        Job job,
        JobDeadline budget,
        CancellationToken cancellationToken,
        Boolean terminal = false
    )
    {
        // Ordinary jobs also run on older schemas; never touch the managed catalog for them.
        if (job.JobType is not (ManagedJobTypes.Create or ManagedJobTypes.Delete))
        {
            return null;
        }
        bool isCreate = job.JobType == ManagedJobTypes.Create;
        await session.RunAsync(
            "RetryTenantGate",
            token => DataStoreCatalog.AcquireTenantGateAsync(session.Transaction!, job.TenantId, token),
            budget,
            cancellationToken
        );
        var aggregate = await session.RunAsync(
            "LockRetryAggregate",
            token =>
                session.Connection.QuerySingleOrDefaultAsync<Aggregate>(
                    PostgresqlJobSession.Command(
                        session,
                        """
                        SELECT "Id", "LastAttempt" FROM dmscs."DataStoreManage"
                        WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                          AND ((@IsCreate AND "CreateJobId" = @JobId COLLATE "C" AND "DeleteJobId" IS NULL
                                AND "Status" IN ('PendingCreate', 'CreateInProgress', 'CreateFailed'))
                            OR (NOT @IsCreate AND "DeleteJobId" = @JobId COLLATE "C"
                                AND "Status" IN ('PendingDelete', 'DeleteInProgress', 'DeleteFailed')))
                        FOR UPDATE;
                        """,
                        new
                        {
                            job.TenantId,
                            job.JobId,
                            IsCreate = isCreate,
                        },
                        budget,
                        token
                    )
                ),
            budget,
            cancellationToken
        );
        if (aggregate is null)
        {
            return null;
        }
        if (job.AttemptCount <= 0 || job.AttemptCount < aggregate.LastAttempt)
        {
            throw new InvalidOperationException("The managed job attempt is inconsistent.");
        }
        return new(
            aggregate.Id,
            job.AttemptCount,
            (isCreate ? "Create" : "Delete") + (terminal ? "Error" : "Failed")
        );
    }

    internal static async Task WriteAsync(
        JobDatabaseSession session,
        Target target,
        DateTime databaseUtcNow,
        JobDeadline budget,
        CancellationToken cancellationToken,
        JobErrorCode? terminalError = null,
        ManagedDataStoreFailure? managedFailure = null
    )
    {
        // Retain only bounded infrastructure/provisioner taxonomy values, never provider messages.
        var failure =
            managedFailure
            ?? (
                terminalError is null
                    ? ManagedDataStoreFailure.RetryableJobFailure()
                    : ManagedDataStoreFailure.FromJob(
                        JobErrorCode.Infrastructure.FirstOrDefault(known => known.Code == terminalError.Code)
                            ?? JobErrorCode.HandlerFailed
                    )
            );
        DynamicParameters parameters = new(
            new
            {
                target.Id,
                target.Status,
                target.Attempt,
                Category = failure.Category.ToString(),
                failure.JobCode,
                ProvisioningCode = failure.ProvisioningCode?.ToString(),
            }
        );
        parameters.Add("Now", databaseUtcNow, DbType.DateTime2);
        int updated = await session.RunAsync(
            "WriteRetryAggregate",
            token =>
                session.Connection.ExecuteAsync(
                    PostgresqlJobSession.Command(
                        session,
                        """
                        UPDATE dmscs."DataStoreManage"
                        SET "Status" = @Status, "LastAttempt" = @Attempt,
                            "LastFailureCategory" = @Category, "LastFailureProvisioningCode" = @ProvisioningCode,
                            "LastFailureJobCode" = @JobCode,
                            "UpdatedAt" = @Now, "LastModifiedDate" = @Now, "LastRefreshed" = @Now
                        WHERE "Id" = @Id;
                        """,
                        parameters,
                        budget,
                        token
                    )
                ),
            budget,
            cancellationToken
        );
        if (updated != 1)
        {
            throw new InvalidOperationException("The managed job outcome was not saved.");
        }
    }

    /// <summary>
    /// Projects exhaustion onto each selected job's unfinished aggregate, and completes instead each selected
    /// job whose exact CMS success already committed. Returns the number of jobs completed.
    /// </summary>
    internal static async Task<int> WriteExhaustedAsync(
        JobDatabaseSession session,
        IEnumerable<ExhaustedJob> jobs,
        JobDeadline budget
    )
    {
        var managedJobs = jobs.Where(job => job.JobType is ManagedJobTypes.Create or ManagedJobTypes.Delete)
            .ToDictionary(job => job.JobId, StringComparer.Ordinal);
        // Ordinary jobs also run on older schemas; do not query the managed catalog when none were selected.
        if (managedJobs.Count == 0)
        {
            return 0;
        }
        // Every selected job is already locked. Acquire all tenant gates in stable order in a bounded
        // number of round trips, then lock and project the matching aggregates as sets under the same deadline.
        await session.RunAsync(
            "RetryTenantGate",
            token =>
                DataStoreCatalog.AcquireTenantGatesAsync(
                    session.Transaction!,
                    managedJobs.Values.Select(job => job.TenantId),
                    token
                ),
            budget,
            CancellationToken.None
        );
        var aggregates = (
            await session.RunAsync(
                "LockRetryAggregate",
                token =>
                    session.Connection.QueryAsync<ExhaustedAggregate>(
                        PostgresqlJobSession.Command(
                            session,
                            """
                            WITH jobs AS (
                                SELECT * FROM jsonb_to_recordset(CAST(@Jobs AS jsonb))
                                    AS j("JobId" text, "TenantId" bigint, "IsCreate" boolean)
                            )
                            -- A create's success is its publication (Created with links); DELETE admits only
                            -- Created, so every later delete state of the same create also proves it. A
                            -- delete's success is its finalized tombstone.
                            SELECT m."Id", j."JobId", m."LastAttempt", m."Status"
                            FROM jobs j JOIN dmscs."DataStoreManage" m
                              ON (m."TenantId" = j."TenantId" OR (m."TenantId" IS NULL AND j."TenantId" IS NULL))
                             AND ((j."IsCreate" AND m."CreateJobId" = j."JobId" COLLATE "C"
                                   AND ((m."DeleteJobId" IS NULL
                                         AND m."Status" IN ('PendingCreate', 'CreateInProgress', 'CreateFailed'))
                                     OR (m."HistoricalDataStoreId" IS NOT NULL
                                         AND ((m."DeleteJobId" IS NULL AND m."Status" = 'Created'
                                               AND m."DataStoreId" IS NOT NULL)
                                           OR (m."DeleteJobId" IS NOT NULL
                                               AND m."Status" IN ('PendingDelete', 'DeleteInProgress', 'DeleteFailed',
                                                   'DeleteError', 'Deleted'))))))
                               OR (NOT j."IsCreate" AND m."DeleteJobId" = j."JobId" COLLATE "C"
                                   AND (m."Status" IN ('PendingDelete', 'DeleteInProgress', 'DeleteFailed')
                                     OR (m."Status" = 'Deleted' AND m."DeletedAt" IS NOT NULL
                                         AND m."DataStoreId" IS NULL))))
                            ORDER BY m."Id"
                            FOR UPDATE OF m;
                            """,
                            new
                            {
                                Jobs = JsonSerializer.Serialize(
                                    managedJobs.Values.Select(job => new
                                    {
                                        job.JobId,
                                        job.TenantId,
                                        IsCreate = job.JobType == ManagedJobTypes.Create,
                                    })
                                ),
                            },
                            budget,
                            token
                        )
                    ),
                budget,
                CancellationToken.None
            )
        ).ToArray();
        if (
            aggregates
                .GroupBy(aggregate => aggregate.JobId, StringComparer.Ordinal)
                .Any(group => group.Count() != 1)
        )
        {
            throw new InvalidOperationException("The managed job has multiple matching aggregates.");
        }
        if (
            Array.Exists(
                aggregates,
                aggregate =>
                    managedJobs[aggregate.JobId].AttemptCount <= 0
                    || (
                        IsOwnPhase(managedJobs[aggregate.JobId], aggregate.Status)
                        && managedJobs[aggregate.JobId].AttemptCount < aggregate.LastAttempt
                    )
            )
        )
        {
            throw new InvalidOperationException("The managed job attempt is inconsistent.");
        }
        var settled = aggregates
            .Where(aggregate => IsCommittedSuccess(managedJobs[aggregate.JobId], aggregate.Status))
            .Select(aggregate => aggregate.JobId)
            .ToArray();
        var unfinished = aggregates
            .Where(aggregate => !IsCommittedSuccess(managedJobs[aggregate.JobId], aggregate.Status))
            .ToArray();
        if (settled.Length > 0)
        {
            // Same transaction as the exhaustion UPDATE: no Error is ever committed for these jobs. The
            // aggregate is left exactly as its successful operation, or a newer delete, committed it.
            int completed = await session.RunAsync(
                "SettleCommittedJobs",
                token =>
                    session.Connection.ExecuteAsync(
                        PostgresqlJobSession.Command(
                            session,
                            """
                            UPDATE dmscs."Job" SET "Status" = 'Completed', "ErrorMessage" = NULL
                            WHERE "JobId" COLLATE "C" = ANY(@JobIds) AND "Status" = 'Error';
                            """,
                            new { JobIds = settled },
                            budget,
                            token
                        )
                    ),
                budget,
                CancellationToken.None
            );
            if (completed != settled.Length)
            {
                throw new InvalidOperationException("The committed managed jobs were not completed.");
            }
        }
        if (unfinished.Length == 0)
        {
            return settled.Length;
        }
        var targets = unfinished.Select(aggregate =>
        {
            var job = managedJobs[aggregate.JobId];
            return new
            {
                aggregate.Id,
                Status = job.JobType == ManagedJobTypes.Create ? "CreateError" : "DeleteError",
                Attempt = job.AttemptCount,
                Now = job.DatabaseUtcNow,
            };
        });
        var failure = ManagedDataStoreFailure.FromJob(JobErrorCode.AttemptsExhausted);
        int updated = await session.RunAsync(
            "WriteRetryAggregate",
            token =>
                session.Connection.ExecuteAsync(
                    PostgresqlJobSession.Command(
                        session,
                        """
                        UPDATE dmscs."DataStoreManage" m
                        SET "Status" = t."Status", "LastAttempt" = t."Attempt",
                            "LastFailureCategory" = @Category, "LastFailureProvisioningCode" = NULL,
                            "LastFailureJobCode" = @JobCode,
                            "UpdatedAt" = t."Now", "LastModifiedDate" = t."Now", "LastRefreshed" = t."Now"
                        FROM jsonb_to_recordset(CAST(@Targets AS jsonb))
                            AS t("Id" int, "Status" text, "Attempt" int, "Now" timestamp)
                        WHERE m."Id" = t."Id";
                        """,
                        new
                        {
                            Targets = JsonSerializer.Serialize(targets),
                            Category = failure.Category.ToString(),
                            failure.JobCode,
                        },
                        budget,
                        token
                    )
                ),
            budget,
            CancellationToken.None
        );
        if (updated != unfinished.Length)
        {
            throw new InvalidOperationException("The managed job outcomes were not saved.");
        }
        return settled.Length;
    }

    private static bool IsCommittedSuccess(ExhaustedJob job, string status) =>
        job.JobType == ManagedJobTypes.Create
            ? status is not ("PendingCreate" or "CreateInProgress" or "CreateFailed")
            : status == "Deleted";

    // A create's attempt history is replaced when a delete is accepted.
    private static bool IsOwnPhase(ExhaustedJob job, string status) =>
        job.JobType != ManagedJobTypes.Create
        || status is "PendingCreate" or "CreateInProgress" or "CreateFailed" or "Created";
}
