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

/// <summary>Projects a retry only inside the lease repository's transaction, after its fresh guarded write.</summary>
internal static class ManagedJobRetryOutcome
{
    internal sealed record Job(string JobId, long? TenantId, string JobType, int AttemptCount);

    internal sealed record Target(int Id, int Attempt, string Status);

    private sealed record Aggregate(int Id, int LastAttempt);

    internal static async Task<Target?> LockAsync(
        JobDatabaseSession session,
        Job job,
        JobDeadline budget,
        CancellationToken cancellationToken
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
            throw new InvalidOperationException("The managed retry attempt is inconsistent.");
        }
        return new(aggregate.Id, job.AttemptCount, isCreate ? "CreateFailed" : "DeleteFailed");
    }

    internal static async Task WriteAsync(
        JobDatabaseSession session,
        Target target,
        DateTime databaseUtcNow,
        JobDeadline budget,
        CancellationToken cancellationToken
    )
    {
        var failure = ManagedDataStoreFailure.RetryableJobFailure();
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
            throw new InvalidOperationException("The managed retry outcome was not saved.");
        }
    }
}
