// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    public sealed class AcceptanceOnlyDeleteHandler : IJobHandler<DeleteManagedDataStorePayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            DeleteManagedDataStorePayload payload,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Acceptance-only handler must never execute");
    }

    // Test-only publication data. Fenced publication is implemented in the next reviewed step.
    private async Task<(int Id, int OrdinaryId)> SeedDeleteAcceptance(long? tenantId = null)
    {
        int ordinaryId = (await Repository(tenantId).InsertDataStore(Insert("Published")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var connection = await Open();
        int id = await Reserve(connection, "Published", tenantId, "Created", ordinaryId);
        DynamicParameters parameters = new(
            new
            {
                Id = id,
                OrdinaryId = ordinaryId,
                Hash = new string('a', 64),
            }
        );
        // Explicit datetime2 avoids SqlClient's legacy datetime rounding of these preservation sentinels.
        parameters.Add(
            "Timestamp",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1234560),
            DbType.DateTime2
        );
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStoreManage" SET "HistoricalDataStoreId" = @OrdinaryId,
                "HistoricalDataStoreName" = 'Published', "LastAttempt" = 3,
                "CreatedAt" = @Timestamp, "UpdatedAt" = @Timestamp, "LastRefreshed" = @Timestamp,
                "LastModifiedDate" = @Timestamp, "PinnedContractVersion" = 1, "PinnedProvider" = 'postgresql',
                "PinnedPackageId" = 'EdFi.Dms.Minimal', "PinnedPackageVersion" = '1.0.0',
                "PinnedPackageSha256" = @Hash, "PinnedArtifactSha256" = @Hash, "PinnedTemplateKind" = 'Minimal',
                "PinnedDataStandardVersion" = '5.2', "PinnedEffectiveSchemaHash" = @Hash,
                "PinnedInventorySha256" = @Hash, "PinnedApiSchemaFormatVersion" = '1', "PinnedRelationalMappingVersion" = 'v3'
            WHERE "Id" = @Id;
            INSERT INTO dmscs."DataStoreContext" ("DataStoreId", "ContextKey", "ContextValue") VALUES (@OrdinaryId, 'schoolYear', '2026');
            """,
            parameters
        );
        return (id, ordinaryId);
    }

    private static async Task<Dictionary<string, object>> DeleteRow(DbConnection connection, int id) =>
        (
            (IDictionary<string, object>)
                await connection.QuerySingleAsync(
                    """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                    new { Id = id }
                )
        ).ToDictionary();

    private async Task<string> DeleteAcceptanceSnapshot()
    {
        await using var connection = await Open();
        List<object> tables = [];
        foreach (
            string table in new[]
            {
                "DataStoreManage",
                "Job",
                "DataStore",
                "DataStoreContext",
                "DataStoreDerivative",
                "ApiClientDataStore",
                "ApiClient",
                "Application",
                "Vendor",
            }
        )
        {
            string orderBy = table switch
            {
                "Job" => "\"JobId\"",
                "ApiClientDataStore" => "\"ApiClientId\", \"DataStoreId\"",
                _ => "\"Id\"",
            };
            tables.Add(
                (await connection.QueryAsync($"SELECT * FROM dmscs.\"{table}\" ORDER BY {orderBy};"))
                    .Select(row => (IDictionary<string, object>)row)
                    .ToArray()
            );
        }
        return JsonSerializer.Serialize(tables);
    }

    private static async Task AssertAcceptedDelete(
        DbConnection connection,
        int id,
        long? tenantId,
        string jobId,
        Dictionary<string, object> before
    )
    {
        var after = await DeleteRow(connection, id);
        after["Status"].Should().Be("PendingDelete");
        after["DeleteJobId"].Should().Be(jobId);
        after["LastAttempt"].Should().Be(0);
        foreach (
            string column in new[]
            {
                "LastFailureCategory",
                "LastFailureProvisioningCode",
                "LastFailureJobCode",
            }
        )
        {
            after[column].Should().BeNull();
        }
        ((DateTime)after["UpdatedAt"]).Should().BeAfter((DateTime)before["UpdatedAt"]);
        after["LastModifiedDate"].Should().Be(after["UpdatedAt"]);
        foreach (
            string changed in new[]
            {
                "Status",
                "DeleteJobId",
                "LastAttempt",
                "LastFailureCategory",
                "LastFailureProvisioningCode",
                "LastFailureJobCode",
                "UpdatedAt",
                "LastModifiedDate",
            }
        )
        {
            after.Remove(changed);
            before.Remove(changed);
        }
        after
            .Should()
            .BeEquivalentTo(
                before,
                "identity, full pin, target, current link, history and refresh time survive acceptance"
            );
        var job =
            (IDictionary<string, object>)await connection.QuerySingleAsync("""SELECT * FROM dmscs."Job";""");
        job["JobId"].Should().Be(jobId);
        job["Status"].Should().Be("Pending");
        job["TenantId"].Should().Be(tenantId);
        job["JobType"].Should().Be(ManagedJobTypes.Delete);
        job["PayloadVersion"].Should().Be(ManagedJobTypes.PayloadVersion);
        JobPayloadSerializer
            .Deserialize<DeleteManagedDataStorePayload>((string)job["Payload"])
            .Should()
            .BeOfType<JobPayloadReadResult<DeleteManagedDataStorePayload>.Success>()
            .Subject.Payload.Should()
            .Be(new DeleteManagedDataStorePayload(id));
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_accepts_delete_with_one_validated_job_and_preserves_catalog_and_reservations(
        [Values(false, true)] bool multitenant
    )
    {
        long? tenant = multitenant ? await Tenant("selected") : null;
        var seed = await SeedDeleteAcceptance(tenant);
        using var services = AcceptanceServices(tenant);
        await using var connection = await Open();
        var before = await DeleteRow(connection, seed.Id);
        string ordinaryBefore = await OrdinarySnapshot(connection, seed.OrdinaryId);
        DateTime earliest = await connection.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        var result = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), tenant)
                .AcceptDeleteAsync(seed.Id, default)
        )
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>()
            .Subject;
        DateTime latest = await connection.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        var after = await DeleteRow(connection, seed.Id);
        ((DateTime)after["UpdatedAt"]).Should().BeOnOrAfter(earliest).And.BeOnOrBefore(latest);
        await AssertAcceptedDelete(connection, seed.Id, tenant, result.JobId, before);
        (await OrdinarySnapshot(connection, seed.OrdinaryId)).Should().Be(ordinaryBefore);
        AssertManaged(await Repository(tenant).UpdateDataStore(Rename(seed.OrdinaryId, "Changed")), seed.Id);
        AssertManaged(await Repository(tenant).DeleteDataStore(seed.OrdinaryId), seed.Id);
        (await Repository(tenant).InsertDataStore(Insert("published")))
            .Should()
            .BeOfType<DataStoreInsertResult.FailureDuplicateName>();
        await using var transaction = await connection.BeginTransactionAsync();
        (
            await DataStoreCatalog.HasTargetReservationAsync(
                transaction,
                tenant,
                new("postgresql", "tcp", "db.example.test", 5432, null, "postgres"),
                Target((string)after["TargetDatabaseName"]),
                default
            )
        )
            .Should()
            .BeTrue();
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_preserves_exact_state_and_prioritizes_lifecycle_before_readiness(
        [Values(false, true)] bool multitenant,
        [Values(
            "PendingCreate",
            "CreateInProgress",
            "CreateFailed",
            "CreateError",
            "Created",
            "PendingDelete",
            "DeleteInProgress",
            "DeleteFailed",
            "DeleteError",
            "Deleted"
        )]
            string status,
        [Values("Ready", "StartupPending", "NotReady", "Disabled")] string readiness
    )
    {
        long? tenant = multitenant ? await Tenant("selected") : null;
        var seed = await SeedDeleteAcceptance(tenant);
        await using var connection = await Open();
        if (status == "Created" && readiness == "Ready")
        {
            using var services = AcceptanceServices(tenant);
            var rowBefore = await DeleteRow(connection, seed.Id);
            string ordinaryBefore = await OrdinarySnapshot(connection, seed.OrdinaryId);
            var accepted = (
                await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), tenant)
                    .AcceptDeleteAsync(seed.Id, default)
            )
                .Should()
                .BeOfType<DataStoreManageDeleteResult.Success>()
                .Subject;
            await AssertAcceptedDelete(connection, seed.Id, tenant, accepted.JobId, rowBefore);
            (await OrdinarySnapshot(connection, seed.OrdinaryId)).Should().Be(ordinaryBefore);
            return;
        }
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStoreManage" SET "Status" = @Status WHERE "Id" = @Id;""",
            new { Status = status, seed.Id }
        );
        string before = await DeleteAcceptanceSnapshot();
        var enqueuer = new EnqueuerDecorator(
            (_, _, _) => throw new AssertionException("Refused delete must not enqueue.")
        );
        var result = await ManagedRepository(enqueuer, tenant, CreateReadiness(readiness))
            .AcceptDeleteAsync(seed.Id, default);
        if (status == "Deleted")
        {
            result.Should().BeOfType<DataStoreManageDeleteResult.NotFound>();
        }
        else if (status == "Created")
        {
            result.Should().BeOfType<DataStoreManageDeleteResult.ProvisioningUnavailable>();
        }
        else
        {
            result
                .Should()
                .BeOfType<DataStoreManageDeleteResult.InvalidState>()
                .Subject.Status.Should()
                .Be(Enum.Parse<ManagedDataStoreStatus>(status));
        }
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_returns_not_found_for_absent_and_other_tenant_before_readiness(
        [Values(false, true)] bool multitenant,
        [Values("Ready", "StartupPending", "NotReady", "Disabled")] string readiness
    )
    {
        long selected = await Tenant("selected");
        long? tenant = multitenant ? selected : null;
        long? other = multitenant ? null : selected;
        var seed = await SeedDeleteAcceptance(other);
        string before = await DeleteAcceptanceSnapshot();
        var enqueuer = new EnqueuerDecorator(
            (_, _, _) => throw new AssertionException("Missing delete must not enqueue.")
        );
        var repository = ManagedRepository(enqueuer, tenant, CreateReadiness(readiness));
        (await repository.AcceptDeleteAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.NotFound>();
        (await repository.AcceptDeleteAsync(int.MaxValue, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.NotFound>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_refuses_missing_or_cross_tenant_current_link_without_mutation(
        [Values(false, true)] bool multitenant,
        [Values(false, true)] bool missing
    )
    {
        long selected = await Tenant("selected");
        long? tenant = multitenant ? selected : null;
        var seed = await SeedDeleteAcceptance(tenant);
        await using var connection = await Open();
        if (missing)
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL WHERE "Id" = @Id;""",
                new { seed.Id }
            );
        }
        else
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs."DataStore" SET "TenantId" = @TenantId WHERE "Id" = @Id;""",
                new { TenantId = multitenant ? (long?)null : selected, Id = seed.OrdinaryId }
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        using var services = AcceptanceServices(tenant);
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), tenant)
                .AcceptDeleteAsync(seed.Id, default)
        )
            .Should()
            .BeOfType<DataStoreManageDeleteResult.FailureUnknown>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [TestCase("type"), TestCase("version"), TestCase("malformed"), TestCase("invalid")]
    [Category("DataStoreManageDeleteAcceptance")]
    public async Task It_rolls_back_delete_when_real_envelope_validation_fails(string failure)
    {
        var seed = await SeedDeleteAcceptance();
        string before = await DeleteAcceptanceSnapshot();
        using var services = AcceptanceServices();
        var real = services.GetRequiredService<IJobEnqueuer>();
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                (
                    await transaction.Connection!.QuerySingleAsync<string>(
                        """SELECT "Status" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                        new { seed.Id },
                        transaction
                    )
                )
                    .Should()
                    .Be("PendingDelete");
                return await real.EnqueueAsync(
                    failure switch
                    {
                        "type" => command with { JobType = "Unknown.Delete" },
                        "version" => command with { PayloadVersion = 2 },
                        "malformed" => command with { PayloadJson = "{" },
                        _ => command with { PayloadJson = "{\"manageId\":0}" },
                    },
                    transaction,
                    token
                );
            }
        );
        var result = (await ManagedRepository(decorator).AcceptDeleteAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.EnqueueFailed>()
            .Subject;
        result
            .Failure.Should()
            .BeOfType(
                failure switch
                {
                    "type" => typeof(JobEnqueueResult.FailureUnsupportedType),
                    "version" => typeof(JobEnqueueResult.FailureUnsupportedVersion),
                    _ => typeof(JobEnqueueResult.FailurePayloadInvalid),
                }
            );
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
        (await ManagedRepository(real).AcceptDeleteAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>();
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_rolls_back_delete_on_exception_or_cancellation_before_or_after_enqueue(
        [Values(false, true)] bool afterEnqueue,
        [Values(false, true)] bool cancel
    )
    {
        var seed = await SeedDeleteAcceptance();
        string before = await DeleteAcceptanceSnapshot();
        using var services = AcceptanceServices();
        var real = services.GetRequiredService<IJobEnqueuer>();
        using CancellationTokenSource cancellation = new();
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                (
                    await transaction.Connection!.QuerySingleAsync<string>(
                        """SELECT "Status" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                        new { seed.Id },
                        transaction
                    )
                )
                    .Should()
                    .Be("PendingDelete");
                if (afterEnqueue)
                {
                    (await real.EnqueueAsync(command, transaction, token))
                        .Should()
                        .BeOfType<JobEnqueueResult.Success>();
                }
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("delete-sensitive-sentinel");
            }
        );
        var action = () => ManagedRepository(decorator).AcceptDeleteAsync(seed.Id, cancellation.Token);
        if (cancel)
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            (await action())
                .Should()
                .BeOfType<DataStoreManageDeleteResult.FailureUnknown>()
                .Subject.Diagnostic.ToString()
                .Should()
                .NotContain("delete-sensitive-sentinel");
        }
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
        (await ManagedRepository(real).AcceptDeleteAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>();
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_rolls_back_delete_for_sql_errors_at_each_write(
        [Values("transition", "job", "link")] string point
    )
    {
        var seed = await SeedDeleteAcceptance();
        string before = await DeleteAcceptanceSnapshot();
        using var services = AcceptanceServices();
        await using var connection = await Open();
        string table = point == "job" ? "Job" : "DataStoreManage";
        string condition = point switch
        {
            "job" => "1 = 1",
            "link" => "UPDATE(DeleteJobId)",
            _ => "UPDATE(Status)",
        };
        await connection.ExecuteAsync(
            $"""
            CREATE TRIGGER dmscs.reject_delete_acceptance ON dmscs."{table}" AFTER {(
                point == "job" ? "INSERT" : "UPDATE"
            )}
            AS BEGIN IF {condition} THROW 51000, 'delete-sql-sensitive-sentinel', 1; END;
            """
        );
        try
        {
            var result = await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                .AcceptDeleteAsync(seed.Id, default);
            JobFailureDiagnostic diagnostic =
                point == "job"
                    ? result
                        .Should()
                        .BeOfType<DataStoreManageDeleteResult.EnqueueFailed>()
                        .Subject.Failure.Should()
                        .BeOfType<JobEnqueueResult.FailureUnknown>()
                        .Subject.Diagnostic
                    : result
                        .Should()
                        .BeOfType<DataStoreManageDeleteResult.FailureUnknown>()
                        .Subject.Diagnostic;
            diagnostic.ProviderErrorCode.Should().NotBeNull();
            diagnostic.ToString().Should().NotContain("delete-sql-sensitive-sentinel");
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        finally
        {
            await connection.ExecuteAsync("DROP TRIGGER dmscs.reject_delete_acceptance;");
        }
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                .AcceptDeleteAsync(seed.Id, default)
        )
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>();
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_requires_exactly_one_conditional_transition_and_job_link(
        [Values(false, true)] bool failLink
    )
    {
        var seed = await SeedDeleteAcceptance();
        using var services = AcceptanceServices();
        await using var connection = await Open();
        if (!failLink)
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs."DataStoreManage" SET "DeleteJobId" = 'existing-operation' WHERE "Id" = @Id;""",
                new { seed.Id }
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        var real = services.GetRequiredService<IJobEnqueuer>();
        bool enqueued = false;
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                enqueued = true;
                var result = await real.EnqueueAsync(command, transaction, token);
                await transaction.Connection!.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Status" = 'Created' WHERE "Id" = @Id;""",
                    new { seed.Id },
                    transaction
                );
                return result;
            }
        );
        (await ManagedRepository(decorator).AcceptDeleteAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.FailureUnknown>();
        enqueued.Should().Be(failLink);
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_serializes_competing_deletes_after_commit_or_rollback(
        [Values(false, true)] bool multitenant,
        [Values(false, true)] bool commit
    )
    {
        long? tenant = multitenant ? await Tenant("selected") : null;
        var seed = await SeedDeleteAcceptance(tenant);
        using var services = AcceptanceServices(tenant);
        await using var connection = await Open();
        var before = await DeleteRow(connection, seed.Id);
        string ordinary = await OrdinarySnapshot(connection, seed.OrdinaryId);
        var real = services.GetRequiredService<IJobEnqueuer>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                var result = await real.EnqueueAsync(command, transaction, token);
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                if (!commit)
                {
                    throw new InvalidOperationException("rollback-first-delete");
                }
                return result;
            }
        );
        var first = ManagedRepository(decorator, tenant).AcceptDeleteAsync(seed.Id, default);
        Task<DataStoreManageDeleteResult>? second = null;
        bool waited = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string applicationName = "delete-competing-" + Guid.NewGuid().ToString("N");
            second = ManagedRepository(real, tenant, applicationName: applicationName)
                .AcceptDeleteAsync(seed.Id, default);
            waited = await ObserveGateWait(applicationName, second);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await first;
            }
            finally
            {
                if (second is not null)
                {
                    await second;
                }
            }
        }
        waited.Should().BeTrue();
        string winningJob;
        if (commit)
        {
            winningJob = (await first).Should().BeOfType<DataStoreManageDeleteResult.Success>().Subject.JobId;
            (await second!)
                .Should()
                .BeOfType<DataStoreManageDeleteResult.InvalidState>()
                .Subject.Status.Should()
                .Be(ManagedDataStoreStatus.PendingDelete);
        }
        else
        {
            (await first).Should().BeOfType<DataStoreManageDeleteResult.FailureUnknown>();
            winningJob = (await second!)
                .Should()
                .BeOfType<DataStoreManageDeleteResult.Success>()
                .Subject.JobId;
        }
        await AssertAcceptedDelete(connection, seed.Id, tenant, winningJob, before);
        (await OrdinarySnapshot(connection, seed.OrdinaryId)).Should().Be(ordinary);
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_samples_acceptance_time_after_observing_a_predecessor_commit()
    {
        var seed = await SeedDeleteAcceptance();
        using var services = AcceptanceServices();
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        string applicationName = "delete-timestamp-" + Guid.NewGuid().ToString("N");
        var pending = ManagedRepository(
                services.GetRequiredService<IJobEnqueuer>(),
                applicationName: applicationName
            )
            .AcceptDeleteAsync(seed.Id, default);
        bool waited = false;
        DateTime predecessor = default;
        try
        {
            waited = await ObserveGateWait(applicationName, pending);
            predecessor = await holder.ExecuteScalarAsync<DateTime>(
                "SELECT SYSUTCDATETIME();",
                transaction: transaction
            );
            DynamicParameters parameters = new(new { seed.Id });
            parameters.Add("Timestamp", predecessor, DbType.DateTime2);
            await holder.ExecuteAsync(
                """UPDATE dmscs."DataStoreManage" SET "UpdatedAt" = @Timestamp, "LastModifiedDate" = @Timestamp WHERE "Id" = @Id;""",
                parameters,
                transaction
            );
        }
        finally
        {
            await transaction.CommitAsync();
            await pending;
        }
        waited.Should().BeTrue();
        (await pending).Should().BeOfType<DataStoreManageDeleteResult.Success>();
        ((DateTime)(await DeleteRow(holder, seed.Id))["UpdatedAt"]).Should().BeAfter(predecessor);
    }

    [Test, Category("DataStoreManageDeleteAcceptance")]
    public async Task It_honors_gate_then_aggregate_then_ordinary_locks_and_cancellation(
        [Values("gate", "aggregate", "ordinary")] string blockedAt
    )
    {
        var seed = await SeedDeleteAcceptance();
        string before = await DeleteAcceptanceSnapshot();
        using var services = AcceptanceServices();
        await using var holder = await Open();
        await using var transaction = await holder.BeginTransactionAsync();
        if (blockedAt == "gate")
        {
            await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        }
        else
        {
            string table = blockedAt == "aggregate" ? "DataStoreManage" : "DataStore";
            await holder.QuerySingleAsync<int>(
                $"SELECT \"Id\" FROM dmscs.\"{table}\" WITH (UPDLOCK, HOLDLOCK) WHERE \"Id\" = @Id;",
                new { Id = blockedAt == "aggregate" ? seed.Id : seed.OrdinaryId },
                transaction
            );
        }
        using CancellationTokenSource cancellation = new();
        string applicationName = "delete-cancel-" + Guid.NewGuid().ToString("N");
        var pending = ManagedRepository(
                services.GetRequiredService<IJobEnqueuer>(),
                applicationName: applicationName
            )
            .AcceptDeleteAsync(seed.Id, cancellation.Token);
        bool waited = false;
        try
        {
            waited =
                blockedAt == "gate"
                    ? await ObserveGateWait(applicationName, pending)
                    : await ObserveOrdinaryRowWait(applicationName, pending);
            if (blockedAt == "aggregate")
            {
                // While acceptance waits on the aggregate, it must not already own the ordinary row.
                await using var probe = await Open();
                await using var probeTransaction = await probe.BeginTransactionAsync();
                using CancellationTokenSource probeDeadline = new(TimeSpan.FromSeconds(3));
                (
                    await probe.QuerySingleAsync<int>(
                        new CommandDefinition(
                            """SELECT "Id" FROM dmscs."DataStore" WITH (UPDLOCK, HOLDLOCK) WHERE "Id" = @Id;""",
                            new { Id = seed.OrdinaryId },
                            probeTransaction,
                            commandTimeout: 3,
                            cancellationToken: probeDeadline.Token
                        )
                    )
                )
                    .Should()
                    .Be(seed.OrdinaryId);
            }
            if (blockedAt != "gate")
            {
                // A later catalog writer must wait behind acceptance while it waits for the row.
                string contenderName = "delete-order-" + Guid.NewGuid().ToString("N");
                await using var contender = await Open(contenderName);
                await using var contenderTransaction = await contender.BeginTransactionAsync();
                using CancellationTokenSource contenderCancellation = new();
                var gate = DataStoreCatalog.AcquireTenantGateAsync(
                    contenderTransaction,
                    null,
                    contenderCancellation.Token
                );
                try
                {
                    (await ObserveGateWait(contenderName, gate)).Should().BeTrue();
                }
                finally
                {
                    await contenderCancellation.CancelAsync();
                    Func<Task> gateAction = () => gate;
                    await gateAction.Should().ThrowAsync<OperationCanceledException>();
                }
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            Func<Task> action = async () => await pending;
            try
            {
                await action.Should().ThrowAsync<OperationCanceledException>();
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }
        waited.Should().BeTrue();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }
}
