// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private async Task AtCompatibilityLevel(int compatibilityLevel, Func<Task> action)
    {
        // This fixture owns an isolated database and is non-parallelizable.
        await using var connection = await Open();
        int original = await connection.ExecuteScalarAsync<int>(
            "SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID();"
        );
        int? allowBuiltInTvfs = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT CONVERT(int, value) FROM sys.database_scoped_configurations WHERE name = 'ALLOW_BUILTIN_TVF_IN_ALL_COMPAT_LEVELS';"
        );
        try
        {
            if (allowBuiltInTvfs == 1)
            {
                await connection.ExecuteAsync(
                    "ALTER DATABASE SCOPED CONFIGURATION SET ALLOW_BUILTIN_TVF_IN_ALL_COMPAT_LEVELS = OFF;"
                );
            }
            await connection.ExecuteAsync(
                $"ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = {compatibilityLevel.ToString(CultureInfo.InvariantCulture)};"
            );
            Func<Task> openJson = () =>
                connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM OPENJSON(N'[]');");
            (await openJson.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(208);
            await action();
        }
        finally
        {
            await connection.ExecuteAsync(
                $"ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = {original.ToString(CultureInfo.InvariantCulture)};"
            );
            if (allowBuiltInTvfs == 1)
            {
                await connection.ExecuteAsync(
                    "ALTER DATABASE SCOPED CONFIGURATION SET ALLOW_BUILTIN_TVF_IN_ALL_COMPAT_LEVELS = ON;"
                );
            }
        }
    }

    [Test, Category("DataStoreCompatibility")]
    public async Task It_preserves_ordinary_writes_without_managed_services_at_older_compatibility(
        [Values(100, 120)] int compatibilityLevel,
        [Values("insert", "update", "delete")] string operation
    )
    {
        // Ordinary repositories do not require any managed service registration or enabled worker.
        var repository = Repository();
        int id = (await repository.InsertDataStore(Insert("Existing")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await AtCompatibilityLevel(
            compatibilityLevel,
            async () =>
            {
                if (operation == "insert")
                {
                    (await repository.InsertDataStore(Insert("Inserted")))
                        .Should()
                        .BeOfType<DataStoreInsertResult.Success>();
                }
                else if (operation == "update")
                {
                    (await repository.UpdateDataStore(Rename(id, "Renamed")))
                        .Should()
                        .BeOfType<DataStoreUpdateResult.Success>();
                }
                else
                {
                    (await repository.DeleteDataStore(id)).Should().BeOfType<DataStoreDeleteResult.Success>();
                }
            }
        );
    }

    [Test, Category("DataStoreCompatibility")]
    public Task It_exhausts_full_delayed_batches_at_older_compatibility(
        [Values(100, 120)] int compatibilityLevel,
        [Values(false, true)] bool cancelBetweenBatches
    ) =>
        AtCompatibilityLevel(
            compatibilityLevel,
            () =>
                It_commits_full_managed_exhaust_batches_with_latency_and_honors_cancellation_between_batches(
                    cancelBetweenBatches,
                    1000
                )
        );

    [Test, Category("DataStoreCompatibility")]
    public async Task It_preserves_arbitrary_job_identifiers_and_exact_timestamps_at_older_compatibility(
        [Values(100, 120)] int compatibilityLevel,
        [Values(false, true)] bool delete
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        // XML-forbidden control characters, SQL/JSON punctuation, Unicode, and significant trailing space
        // are all valid persisted nvarchar data. Fill the full schema width without truncation.
        string jobId = "\0\u0001\u000b<>&'\"\\雪😀".PadRight(149, 'x') + " ";
        await connection.ExecuteAsync(
            $"""
            UPDATE dmscs.Job SET JobId = @JobId WHERE Id = @Id;
            UPDATE dmscs.DataStoreManage SET {(
                delete ? "DeleteJobId" : "CreateJobId"
            )} = @JobId WHERE Id = @AggregateId;
            """,
            new
            {
                JobId = jobId,
                seed.Claim.Id,
                AggregateId = seed.Id,
            }
        );
        await ExpireTerminalClaim(connection, seed.Claim);
        await AtCompatibilityLevel(
            compatibilityLevel,
            async () =>
            {
                (
                    await new JobLeaseRepository(PublicationOptions, PublicationTimings).Exhaust(
                        1,
                        JobErrorCode.AttemptsExhausted,
                        default
                    )
                )
                    .Should()
                    .BeOfType<JobExhaustResult.Success>()
                    .Which.ExhaustedCount.Should()
                    .Be(1);
                var aggregate = await DeleteRow(connection, seed.Id);
                aggregate["Status"].Should().Be(delete ? "DeleteError" : "CreateError");
                aggregate["LastFailureJobCode"].Should().Be("AttemptsExhausted");
                aggregate[delete ? "DeleteJobId" : "CreateJobId"].Should().Be(jobId);
                var job = await connection.QuerySingleAsync<RetryJobRow>(
                    "SELECT Status, LeaseOwner, LeaseExpiresAt, FinishedAt, NextAttemptAt, LastModifiedAt FROM dmscs.Job WHERE Id = @Id;",
                    new { seed.Claim.Id }
                );
                job.Status.Should().Be("Error");
                job.FinishedAt.Should().Be(job.LastModifiedAt);
                aggregate["UpdatedAt"].Should().Be(job.LastModifiedAt);
                aggregate["LastModifiedDate"].Should().Be(job.LastModifiedAt);
                aggregate["LastRefreshed"].Should().Be(job.LastModifiedAt);
            }
        );
    }
}
