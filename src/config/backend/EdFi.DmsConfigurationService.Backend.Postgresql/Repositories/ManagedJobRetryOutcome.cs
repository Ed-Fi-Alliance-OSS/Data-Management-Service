// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
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

    internal static async Task<Target?> LockAsync(
        JobDatabaseSession session,
        Job job,
        JobDeadline budget,
        CancellationToken cancellationToken,
        bool terminal = false,
        bool tenantGateHeld = false
    )
    {
        // Ordinary jobs also run on older schemas; never touch the managed catalog for them.
        if (job.JobType is not (ManagedJobTypes.Create or ManagedJobTypes.Delete))
        {
            return null;
        }
        bool isCreate = job.JobType == ManagedJobTypes.Create;
        if (!tenantGateHeld)
        {
            await session.RunAsync(
                "RetryTenantGate",
                token => DataStoreCatalog.AcquireTenantGateAsync(session.Transaction!, job.TenantId, token),
                budget,
                cancellationToken
            );
        }
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
        JobErrorCode? terminalError = null
    )
    {
        // Consumer codes remain on Job; retained aggregate evidence accepts only the bounded infrastructure taxonomy.
        var failure = terminalError is null
            ? ManagedDataStoreFailure.RetryableJobFailure()
            : ManagedDataStoreFailure.FromJob(
                JobErrorCode.Infrastructure.FirstOrDefault(known => known.Code == terminalError.Code)
                    ?? JobErrorCode.HandlerFailed
            );
        DynamicParameters parameters = new(
            new
            {
                target.Id,
                target.Status,
                target.Attempt,
                Category = failure.Category.ToString(),
                failure.JobCode,
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
                            "LastFailureCategory" = @Category, "LastFailureProvisioningCode" = NULL,
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

    internal static async Task WriteExhaustedAsync(
        JobDatabaseSession session,
        IEnumerable<ExhaustedJob> jobs,
        JobDeadline budget
    )
    {
        // Every job row is already locked. Take tenant gates in a stable order across overlapping batches,
        // and only once per tenant, before locking that tenant's matching aggregates.
        foreach (
            var tenant in jobs.Where(job => job.JobType is ManagedJobTypes.Create or ManagedJobTypes.Delete)
                .GroupBy(job => job.TenantId)
                .OrderBy(group => group.Key)
        )
        {
            await session.RunAsync(
                "RetryTenantGate",
                token => DataStoreCatalog.AcquireTenantGateAsync(session.Transaction!, tenant.Key, token),
                budget,
                CancellationToken.None
            );
            foreach (var job in tenant.OrderBy(job => job.JobId, StringComparer.Ordinal))
            {
                var target = await LockAsync(
                    session,
                    new(job.JobId, job.TenantId, job.JobType, job.AttemptCount),
                    budget,
                    CancellationToken.None,
                    terminal: true,
                    tenantGateHeld: true
                );
                if (target is not null)
                {
                    await WriteAsync(
                        session,
                        target,
                        job.DatabaseUtcNow,
                        budget,
                        CancellationToken.None,
                        JobErrorCode.AttemptsExhausted
                    );
                }
            }
        }
    }
}
