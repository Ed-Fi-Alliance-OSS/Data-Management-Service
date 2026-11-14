// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>Projects a retry only inside the lease repository's transaction, after its fresh guarded write.</summary>
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
                    MssqlJobSession.Command(
                        session,
                        """
                        UPDATE dmscs.DataStoreManage
                        SET Status = @Status, LastAttempt = @Attempt,
                            LastFailureCategory = @Category, LastFailureProvisioningCode = NULL,
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
            throw new InvalidOperationException("The managed retry outcome was not saved.");
        }
    }
}
