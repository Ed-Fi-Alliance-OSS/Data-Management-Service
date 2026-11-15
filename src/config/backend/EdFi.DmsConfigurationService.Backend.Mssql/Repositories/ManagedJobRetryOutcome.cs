// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>Projects managed outcomes only inside the lease repository's job-locked transaction.</summary>
internal static class ManagedJobRetryOutcome
{
    private const string MatchingAggregate = """
        (TenantId = @TenantId OR (TenantId IS NULL AND @TenantId IS NULL))
          AND ((@IsCreate = 1 AND CreateJobId COLLATE Latin1_General_100_BIN2 = @JobId
                AND DATALENGTH(CreateJobId) = DATALENGTH(@JobId) AND DeleteJobId IS NULL
                AND Status IN (N'PendingCreate', N'CreateInProgress', N'CreateFailed'))
            OR (@IsCreate = 0 AND DeleteJobId COLLATE Latin1_General_100_BIN2 = @JobId
                AND DATALENGTH(DeleteJobId) = DATALENGTH(@JobId)
                AND Status IN (N'PendingDelete', N'DeleteInProgress', N'DeleteFailed')))
        """;

    private const string MatchingExhaustedAggregate = """
        (m.TenantId = j.TenantId OR (m.TenantId IS NULL AND j.TenantId IS NULL))
          AND ((j.IsCreate = 1 AND m.CreateJobId COLLATE Latin1_General_100_BIN2 = j.JobId
                AND DATALENGTH(m.CreateJobId) = DATALENGTH(j.JobId) AND m.DeleteJobId IS NULL
                AND m.Status IN (N'PendingCreate', N'CreateInProgress', N'CreateFailed'))
            OR (j.IsCreate = 0 AND m.DeleteJobId COLLATE Latin1_General_100_BIN2 = j.JobId
                AND DATALENGTH(m.DeleteJobId) = DATALENGTH(j.JobId)
                AND m.Status IN (N'PendingDelete', N'DeleteInProgress', N'DeleteFailed')))
        """;

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

    private sealed record ExhaustedAggregate(int Id, string JobId, int LastAttempt);

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
        // Resolve under the tenant gate without retaining update locks on rejected scan rows.
        int? id = await session.RunAsync(
            "FindRetryAggregate",
            token =>
                session.Connection.QuerySingleOrDefaultAsync<int?>(
                    MssqlJobSession.Command(
                        session,
                        $"SELECT Id FROM dmscs.DataStoreManage WHERE {MatchingAggregate};",
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
        if (id is null)
        {
            return null;
        }
        var aggregate = await session.RunAsync(
            "LockRetryAggregate",
            token =>
                session.Connection.QuerySingleOrDefaultAsync<Aggregate>(
                    MssqlJobSession.Command(
                        session,
                        $"SELECT Id, LastAttempt FROM dmscs.DataStoreManage WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id AND {MatchingAggregate};",
                        new
                        {
                            Id = id.Value,
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
                    MssqlJobSession.Command(
                        session,
                        """
                        UPDATE dmscs.DataStoreManage
                        SET Status = @Status, LastAttempt = @Attempt,
                            LastFailureCategory = @Category, LastFailureProvisioningCode = @ProvisioningCode,
                            LastFailureJobCode = @JobCode,
                            UpdatedAt = @Now, LastModifiedDate = @Now, LastRefreshed = @Now
                        WHERE Id = @Id;
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

    internal static async Task WriteExhaustedAsync(
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
            return;
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
        // Keep persisted job IDs parameterized and the batch under the SQL Server parameter limit.
        DynamicParameters jobParameters = new();
        string jobRows = string.Join(
            ",",
            managedJobs.Values.Select(
                (job, index) =>
                {
                    string ordinal = index.ToString(CultureInfo.InvariantCulture);
                    jobParameters.Add("Job" + ordinal, job.JobId, DbType.String, size: -1);
                    string tenant = job.TenantId?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
                    string isCreate = job.JobType == ManagedJobTypes.Create ? "1" : "0";
                    return $"(@Job{ordinal}, {tenant}, {isCreate})";
                }
            )
        );
        var aggregates = (
            await session.RunAsync(
                "LockRetryAggregate",
                token =>
                    session.Connection.QueryAsync<ExhaustedAggregate>(
                        MssqlJobSession.Command(
                            session,
                            $"""
                            DECLARE @Candidates TABLE (Id int, JobId nvarchar(150), TenantId bigint, IsCreate bit);
                            INSERT INTO @Candidates (Id, JobId, TenantId, IsCreate)
                            SELECT m.Id, j.JobId, j.TenantId, j.IsCreate
                            FROM (VALUES {jobRows}) j(JobId, TenantId, IsCreate)
                            JOIN dmscs.DataStoreManage m ON {MatchingExhaustedAggregate};
                            -- Discover without update locks, then seek only candidate primary keys and recheck
                            -- identity/state under the aggregate locks. Rejected scan rows retain no update locks.
                            SELECT m.Id, j.JobId, m.LastAttempt
                            FROM @Candidates j
                            INNER LOOP JOIN dmscs.DataStoreManage m WITH (UPDLOCK, ROWLOCK, FORCESEEK (PK_DataStoreManage (Id)))
                                ON m.Id = j.Id
                            WHERE {MatchingExhaustedAggregate}
                            ORDER BY m.Id
                            OPTION (FORCE ORDER);
                            """,
                            jobParameters,
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
                    || managedJobs[aggregate.JobId].AttemptCount < aggregate.LastAttempt
            )
        )
        {
            throw new InvalidOperationException("The managed job attempt is inconsistent.");
        }
        if (aggregates.Length == 0)
        {
            return;
        }
        var failure = ManagedDataStoreFailure.FromJob(JobErrorCode.AttemptsExhausted);
        DynamicParameters targetParameters = new(
            new { Category = failure.Category.ToString(), failure.JobCode }
        );
        string targetRows = string.Join(
            ",",
            aggregates.Select(
                (aggregate, index) =>
                {
                    var job = managedJobs[aggregate.JobId];
                    string ordinal = index.ToString(CultureInfo.InvariantCulture);
                    targetParameters.Add("Now" + ordinal, job.DatabaseUtcNow, DbType.DateTime2);
                    string id = aggregate.Id.ToString(CultureInfo.InvariantCulture);
                    string attempt = job.AttemptCount.ToString(CultureInfo.InvariantCulture);
                    string status = job.JobType == ManagedJobTypes.Create ? "CreateError" : "DeleteError";
                    return $"({id}, N'{status}', {attempt}, @Now{ordinal})";
                }
            )
        );
        int updated = await session.RunAsync(
            "WriteRetryAggregate",
            token =>
                session.Connection.ExecuteAsync(
                    MssqlJobSession.Command(
                        session,
                        $"""
                        UPDATE m
                        SET Status = t.Status, LastAttempt = t.Attempt,
                            LastFailureCategory = @Category, LastFailureProvisioningCode = NULL,
                            LastFailureJobCode = @JobCode,
                            UpdatedAt = t.Now, LastModifiedDate = t.Now, LastRefreshed = t.Now
                        FROM (VALUES {targetRows}) t(Id, Status, Attempt, Now)
                        JOIN dmscs.DataStoreManage m ON m.Id = t.Id;
                        """,
                        targetParameters,
                        budget,
                        token
                    )
                ),
            budget,
            CancellationToken.None
        );
        if (updated != aggregates.Length)
        {
            throw new InvalidOperationException("The managed job outcomes were not saved.");
        }
    }
}
