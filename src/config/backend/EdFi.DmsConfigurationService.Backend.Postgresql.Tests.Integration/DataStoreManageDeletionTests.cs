// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private sealed record DeletionSeed(
        ManagedDataStoreDeleteCommand Command,
        JobExecutionContext Execution,
        ClaimedJob Claim
    );

    private async Task<DeletionSeed> SeedDeletion(long? tenantId = null, string provider = "postgresql")
    {
        var published = await SeedPublication(tenantId, provider: provider);
        int ordinaryId = await PublicationRepository(tenantId)
            .PublishCreateAsync(published.Command, published.Execution, default);
        var leases = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        (
            await leases.Complete(
                published.Claim.Id,
                published.Claim.LeaseOwner,
                published.Claim.FencingToken,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();
        using var services = AcceptanceServices(tenantId);
        var accepted = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), tenantId)
                .AcceptDeleteAsync(published.Command.Id, default)
        )
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>()
            .Subject;
        var claim = (await leases.ClaimNext("deletion-worker", 300, 5, default))
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(accepted.JobId);
        await using var connection = await Open();
        // The handler's attempt transition is outside this persistence step; seed only that prerequisite.
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStoreManage" SET "Status" = 'DeleteInProgress', "LastAttempt" = @Attempt WHERE "Id" = @Id;""",
            new { published.Command.Id, Attempt = claim.AttemptCount }
        );
        return new(
            new(
                published.Command.Id,
                published.Command.Identity,
                published.Command.PinnedArtifact,
                ordinaryId,
                published.Claim.JobId
            ),
            new(
                claim.JobId,
                published.Execution.TenantContext,
                claim.AttemptCount,
                5,
                new PostgresqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(claim, new())
            ),
            claim
        );
    }

    private static async Task<int> SeedDeletionClient(DbConnection connection)
    {
        await connection.ExecuteAsync(
            """INSERT INTO dmscs."Vendor" ("Company") VALUES ('Deletion vendor');"""
        );
        int vendorId = await connection.ExecuteScalarAsync<int>(
            """SELECT "Id" FROM dmscs."Vendor" WHERE "Company" = 'Deletion vendor';"""
        );
        await connection.ExecuteAsync(
            """INSERT INTO dmscs."Application" ("ApplicationName", "VendorId", "ClaimSetName") VALUES ('Deletion app', @VendorId, 'test');""",
            new { VendorId = vendorId }
        );
        int applicationId = await connection.ExecuteScalarAsync<int>(
            """SELECT "Id" FROM dmscs."Application" WHERE "VendorId" = @VendorId;""",
            new { VendorId = vendorId }
        );
        await connection.ExecuteAsync(
            """INSERT INTO dmscs."ApiClient" ("ApplicationId", "Name", "ClientId", "ClientUuid") VALUES (@ApplicationId, 'Deletion client', @ClientId, @ClientUuid);""",
            new
            {
                ApplicationId = applicationId,
                ClientId = Guid.NewGuid().ToString(),
                ClientUuid = Guid.NewGuid(),
            }
        );
        return await connection.ExecuteScalarAsync<int>(
            """SELECT "Id" FROM dmscs."ApiClient" WHERE "ApplicationId" = @ApplicationId;""",
            new { ApplicationId = applicationId }
        );
    }

    private static Task InsertDeletionDependent(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        int dataStoreId,
        int clientId
    ) =>
        connection.ExecuteAsync(
            table switch
            {
                "ApiClientDataStore" =>
                    """INSERT INTO dmscs."ApiClientDataStore" ("ApiClientId", "DataStoreId") VALUES (@ClientId, @DataStoreId);""",
                "DataStoreContext" =>
                    """INSERT INTO dmscs."DataStoreContext" ("DataStoreId", "ContextKey", "ContextValue") VALUES (@DataStoreId, 'schoolYear', '2026');""",
                "DataStoreDerivative" =>
                    """INSERT INTO dmscs."DataStoreDerivative" ("DataStoreId", "DerivativeType") VALUES (@DataStoreId, 'ReadReplica');""",
                _ => throw new ArgumentException("Unknown test dependent.", nameof(table)),
            },
            new { DataStoreId = dataStoreId, ClientId = clientId },
            transaction
        );

    private static async Task AssertDeletionDependents(
        DbConnection connection,
        int id,
        int expected,
        DbTransaction? transaction = null
    )
    {
        foreach (string table in new[] { "ApiClientDataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            (
                await connection.ExecuteScalarAsync<int>(
                    $"SELECT COUNT(*) FROM dmscs.\"{table}\" WHERE \"DataStoreId\" = @Id;",
                    new { Id = id },
                    transaction
                )
            )
                .Should()
                .Be(expected, table);
        }
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_qualifies_every_migrated_foreign_key_referencing_the_ordinary_data_store()
    {
        await using var connection = await Open();
        var actual = await connection.QueryAsync<(string Table, string Column, string Action)>(
            """
            SELECT child.relname, column_info.attname, CASE fk.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'a' THEN 'NO ACTION' ELSE fk.confdeltype::text END
            FROM pg_constraint fk JOIN pg_class child ON child.oid = fk.conrelid
            JOIN pg_attribute column_info ON column_info.attrelid = child.oid AND column_info.attnum = ANY(fk.conkey)
            WHERE fk.contype = 'f' AND fk.confrelid = 'dmscs."DataStore"'::regclass;
            """
        );
        actual
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    ("ApiClientDataStore", "DataStoreId", "CASCADE"),
                    ("DataStoreContext", "DataStoreId", "CASCADE"),
                    ("DataStoreDerivative", "DataStoreId", "CASCADE"),
                    ("DataStoreManage", "DataStoreId", "NO ACTION"),
                }
            );
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_removes_only_the_recorded_catalog_and_dependents_and_retains_management_history(
        [Values(false, true)] bool multitenant,
        [Values("postgresql", "mssql")] string provider
    )
    {
        long? tenant = multitenant ? await Tenant("deletion") : null;
        long otherTenant = await Tenant("other");
        var seed = await SeedDeletion(tenant, provider);
        int otherId = (await Repository(otherTenant).InsertDataStore(Insert("Store")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await using var connection = await Open();
        int client = await SeedDeletionClient(connection);
        foreach (string table in new[] { "ApiClientDataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            await InsertDeletionDependent(
                connection,
                null,
                table,
                seed.Command.HistoricalDataStoreId,
                client
            );
            await InsertDeletionDependent(connection, null, table, otherId, client);
        }
        var before = await DeleteRow(connection, seed.Command.Id);
        await PublicationRepository(tenant).CompleteDeleteAsync(seed.Command, seed.Execution, default);
        var after = await DeleteRow(connection, seed.Command.Id);
        after["Status"].Should().Be("Deleted");
        after["DataStoreId"].Should().BeNull();
        ((DateTime)after["DeletedAt"]).Should().BeAfter((DateTime)before["UpdatedAt"]);
        foreach (string column in new[] { "UpdatedAt", "LastModifiedDate", "LastRefreshed" })
        {
            after[column].Should().Be(after["DeletedAt"]);
        }
        foreach (
            string column in new[]
            {
                "Status",
                "DataStoreId",
                "DeletedAt",
                "UpdatedAt",
                "LastModifiedDate",
                "LastRefreshed",
            }
        )
        {
            before.Remove(column);
            after.Remove(column);
        }
        after
            .Should()
            .BeEquivalentTo(
                before,
                "all historical identity, full pin, jobs, attempt and audit fields survive"
            );
        await AssertDeletionDependents(connection, seed.Command.HistoricalDataStoreId, 0);
        await AssertDeletionDependents(connection, otherId, 1);
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = seed.Command.HistoricalDataStoreId }
            )
        )
            .Should()
            .Be(0);
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs."ApiClient" WHERE "Id" = @Id;""",
                new { Id = client }
            )
        )
            .Should()
            .Be(1);
        (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Application";"""))
            .Should()
            .Be(1);
        var response = (await PublicationRepository(tenant).GetByIdAsync(seed.Command.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>()
            .Subject.DataStore;
        response.DataStoreId.Should().Be(seed.Command.HistoricalDataStoreId);
        response.DataStoreName.Should().Be(seed.Command.Identity.Name);
        response.Status.Should().Be("Deleted");
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_reconciles_absence_using_only_recorded_history_and_replays_with_a_new_real_claim(
        [Values(false, true)] bool initiallyAbsent
    )
    {
        var seed = await SeedDeletion();
        await using var connection = await Open();
        if (initiallyAbsent)
        {
            await connection.ExecuteAsync(
                """
                UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL WHERE "Id" = @Id;
                DELETE FROM dmscs."DataStore" WHERE "Id" = @HistoricalDataStoreId;
                INSERT INTO dmscs."DataStore" ("Name", "DataStoreType", "Provider") VALUES ('Store', 'Shared', 'postgresql');
                """,
                new { seed.Command.Id, seed.Command.HistoricalDataStoreId }
            );
        }
        await PublicationRepository().CompleteDeleteAsync(seed.Command, seed.Execution, default);
        if (!initiallyAbsent)
        {
            (await Repository().InsertDataStore(Insert("Store")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>();
        }
        int replacement = await connection.ExecuteScalarAsync<int>(
            """SELECT "Id" FROM dmscs."DataStore" WHERE "Name" = 'Store';"""
        );
        replacement.Should().NotBe(seed.Command.HistoricalDataStoreId);
        // Another aggregate can own the reused name/target; replay must not inspect or change it.
        await Reserve(
            connection,
            "Store",
            status: "Created",
            dataStoreId: replacement,
            targetName: seed.Command.Identity.TargetName.Value
        );
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second' WHERE "JobId" = @JobId;""",
            new { seed.Claim.JobId }
        );
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "deletion-recovery",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(seed.Claim.JobId);
        claim.AttemptCount.Should().BeGreaterThan(seed.Claim.AttemptCount);
        var execution = seed.Execution with
        {
            Attempt = claim.AttemptCount,
            Fence = new PostgresqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(
                claim,
                new()
            ),
        };
        string before = await DeleteAcceptanceSnapshot();
        await PublicationRepository().CompleteDeleteAsync(seed.Command, execution, default);
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
        var action = () =>
            PublicationRepository()
                .CompleteDeleteAsync(
                    seed.Command with
                    {
                        CreateJobId = "different-create",
                    },
                    execution,
                    default
                );
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_refuses_changed_deletion_identity_without_writes(
        [Values(
            "Name",
            "Template",
            "TargetDatabaseName",
            "EndpointDescriptor",
            "CreateJobId",
            "DeleteJobId",
            "PinnedContractVersion",
            "PinnedProvider",
            "PinnedPackageId",
            "PinnedPackageVersion",
            "PinnedPackageSha256",
            "PinnedArtifactSha256",
            "PinnedTemplateKind",
            "PinnedDataStandardVersion",
            "PinnedEffectiveSchemaHash",
            "PinnedInventorySha256",
            "PinnedApiSchemaFormatVersion",
            "PinnedRelationalMappingVersion",
            "HistoricalDataStoreName",
            "HistoricalDataStoreId",
            "source",
            "job-space",
            "pin-space",
            "attempt",
            "context-tenant",
            "repository-tenant",
            "ordinary-repository-tenant",
            "PendingDelete",
            "DeleteFailed",
            "Created",
            "link-null",
            "ordinary-name",
            "ordinary-type",
            "ordinary-provider",
            "ordinary-tenant",
            "other-aggregate"
        )]
            string change
    )
    {
        var seed = await SeedDeletion();
        var command = seed.Command;
        var execution = seed.Execution;
        var repository = PublicationRepository();
        await using var connection = await Open();
        switch (change)
        {
            case "EndpointDescriptor":
                command = command with
                {
                    Identity = command.Identity with
                    {
                        Endpoint = command.Identity.Endpoint with { Host = "other.example" },
                    },
                };
                break;
            case "source":
                command = command with
                {
                    Identity = command.Identity with { ExpectedSourceIdentity = Guid.NewGuid() },
                };
                break;
            case "job-space":
                execution = execution with { JobId = execution.JobId + " " };
                break;
            case "pin-space":
                command = command with
                {
                    PinnedArtifact = command.PinnedArtifact with
                    {
                        PackageVersion = command.PinnedArtifact.PackageVersion + " ",
                    },
                };
                break;
            case "attempt":
                execution = execution with { Attempt = execution.Attempt + 1 };
                break;
            case "context-tenant":
                execution = execution with
                {
                    TenantContext = CreateTenantContext(await Tenant("other")).Context,
                };
                break;
            case "repository-tenant":
                repository = PublicationRepository(await Tenant("other"));
                break;
            case "ordinary-repository-tenant":
                repository = ManagedRepository(
                    FakeItEasy.A.Fake<IJobEnqueuer>(),
                    ordinaryRepository: Repository(await Tenant("other"))
                );
                break;
            case "PendingDelete":
            case "DeleteFailed":
            case "Created":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Status" = @Status WHERE "Id" = @Id;""",
                    new { command.Id, Status = change }
                );
                break;
            case "link-null":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                break;
            case "ordinary-name":
            case "ordinary-type":
            case "ordinary-provider":
            case "ordinary-tenant":
                string column = change switch
                {
                    "ordinary-name" => "Name",
                    "ordinary-type" => "DataStoreType",
                    "ordinary-provider" => "Provider",
                    _ => "TenantId",
                };
                object value = change switch
                {
                    "ordinary-tenant" => await Tenant("other"),
                    "ordinary-type" => "Production",
                    _ => "Changed",
                };
                await connection.ExecuteAsync(
                    $"UPDATE dmscs.\"DataStore\" SET \"{column}\" = @Value WHERE \"Id\" = @Id;",
                    new { Id = command.HistoricalDataStoreId, Value = value }
                );
                break;
            case "other-aggregate":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                await Reserve(
                    connection,
                    "Other",
                    status: "Created",
                    dataStoreId: command.HistoricalDataStoreId
                );
                break;
            default:
                object changed = change switch
                {
                    "PinnedContractVersion" => 2,
                    "HistoricalDataStoreId" => command.HistoricalDataStoreId + 10000,
                    "Template" => "Sample",
                    _ => "changed",
                };
                await connection.ExecuteAsync(
                    $"UPDATE dmscs.\"DataStoreManage\" SET \"{change}\" = @Changed WHERE \"Id\" = @Id;",
                    new { command.Id, Changed = changed }
                );
                break;
        }
        string before = await DeleteAcceptanceSnapshot();
        var action = () => repository.CompleteDeleteAsync(command, execution, default);
        await action.Should().ThrowAsync<Exception>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_rolls_back_unlink_cascades_and_tombstone_when_the_fence_cannot_commit(
        [Values(false, true)] bool cancel
    )
    {
        var seed = await SeedDeletion();
        await using var connection = await Open();
        int client = await SeedDeletionClient(connection);
        foreach (string table in new[] { "ApiClientDataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            await InsertDeletionDependent(
                connection,
                null,
                table,
                seed.Command.HistoricalDataStoreId,
                client
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        using CancellationTokenSource cancellation = new();
        bool observed = false;
        var fence = new PublicationFenceDecorator(
            seed.Execution.Fence,
            async (transaction, token) =>
            {
                (
                    await transaction.Connection!.ExecuteScalarAsync<string>(
                        new CommandDefinition(
                            """SELECT "Status" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                            new { seed.Command.Id },
                            transaction,
                            cancellationToken: token
                        )
                    )
                )
                    .Should()
                    .Be("Deleted");
                await AssertDeletionDependents(
                    transaction.Connection!,
                    seed.Command.HistoricalDataStoreId,
                    0,
                    transaction
                );
                observed = true;
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("before-deletion-commit");
            }
        );
        var action = () =>
            PublicationRepository()
                .CompleteDeleteAsync(seed.Command, seed.Execution with { Fence = fence }, cancellation.Token);
        if (cancel)
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await action.Should().ThrowAsync<InvalidOperationException>();
        }
        observed.Should().BeTrue();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_refuses_stale_deletion_fences_and_pre_cancelled_calls(
        [Values(false, true)] bool cancelled
    )
    {
        var seed = await SeedDeletion();
        using CancellationTokenSource cancellation = new();
        if (cancelled)
        {
            await cancellation.CancelAsync();
        }
        else
        {
            await using var connection = await Open();
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second' WHERE "JobId" = @JobId;""",
                new { seed.Claim.JobId }
            );
            var claim = (
                await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                    "replacement",
                    300,
                    5,
                    default
                )
            )
                .Should()
                .BeOfType<JobClaimResult.Claimed>()
                .Subject.Job;
            claim.FencingToken.Should().BeGreaterThan(seed.Claim.FencingToken);
        }
        string before = await DeleteAcceptanceSnapshot();
        var action = () =>
            PublicationRepository().CompleteDeleteAsync(seed.Command, seed.Execution, cancellation.Token);
        if (cancelled)
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await action.Should().ThrowAsync<JobLeaseLostException>();
        }
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_rolls_back_each_deletion_write_boundary(
        [Values("unlink", "ordinary", "tombstone")] string boundary
    )
    {
        var seed = await SeedDeletion();
        await using var connection = await Open();
        int client = await SeedDeletionClient(connection);
        foreach (string table in new[] { "ApiClientDataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            await InsertDeletionDependent(
                connection,
                null,
                table,
                seed.Command.HistoricalDataStoreId,
                client
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        string tableName = boundary == "ordinary" ? "DataStore" : "DataStoreManage";
        string condition = boundary switch
        {
            "unlink" => "WHEN (OLD.\"DataStoreId\" IS NOT NULL AND NEW.\"DataStoreId\" IS NULL)",
            "tombstone" => "WHEN (NEW.\"Status\" = 'Deleted')",
            _ => "",
        };
        string operation = boundary == "ordinary" ? "DELETE" : "UPDATE";
        await connection.ExecuteAsync(
            $"""
            CREATE FUNCTION dmscs.reject_deletion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'deletion-write-test'; END $$;
            CREATE TRIGGER reject_deletion AFTER {operation} ON dmscs."{tableName}"
            FOR EACH ROW {condition} EXECUTE FUNCTION dmscs.reject_deletion();
            """
        );
        try
        {
            var action = () =>
                PublicationRepository().CompleteDeleteAsync(seed.Command, seed.Execution, default);
            await action.Should().ThrowAsync<Npgsql.PostgresException>();
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        finally
        {
            await connection.ExecuteAsync(
                $"""DROP TRIGGER reject_deletion ON dmscs."{tableName}"; DROP FUNCTION dmscs.reject_deletion();"""
            );
        }
        await PublicationRepository().CompleteDeleteAsync(seed.Command, seed.Execution, default);
        await AssertDeletionDependents(connection, seed.Command.HistoricalDataStoreId, 0);
    }

    private sealed class BeforeDeletionFence(
        IJobFence inner,
        Func<DbTransaction, CancellationToken, Task> before
    ) : IJobFence
    {
        public Task ExecuteAsync(
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        ) =>
            inner.ExecuteAsync(
                async (transaction, token) =>
                {
                    await before(transaction, token);
                    await work(transaction, token);
                },
                cancellationToken
            );
    }

    private async Task<bool> ObserveDeletionLockWait(int sessionId, Task contender)
    {
        await using var observer = await Open();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        try
        {
            while (!contender.IsCompleted)
            {
                if (
                    await observer.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @SessionId AND NOT granted AND locktype IN ('transactionid', 'tuple'));",
                            new { SessionId = sessionId },
                            cancellationToken: deadline.Token
                        )
                    )
                )
                {
                    return true;
                }
                await Task.Yield();
            }
            return false;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    [Test, Category("DataStoreManageDeletion")]
    public async Task It_serializes_each_dependent_insert_with_deletion_in_both_orders(
        [Values("ApiClientDataStore", "DataStoreContext", "DataStoreDerivative")] string table,
        [Values(false, true)] bool insertFirst,
        [Values(false, true)] bool rollbackFirst
    )
    {
        var seed = await SeedDeletion();
        await using var contender = await Open();
        int client = await SeedDeletionClient(contender);
        if (insertFirst)
        {
            await using var insertion = await contender.BeginTransactionAsync();
            await InsertDeletionDependent(
                contender,
                insertion,
                table,
                seed.Command.HistoricalDataStoreId,
                client
            );
            TaskCompletionSource<int> session = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var fence = new BeforeDeletionFence(
                seed.Execution.Fence,
                async (transaction, token) =>
                {
                    session.TrySetResult(
                        await transaction.Connection!.ExecuteScalarAsync<int>(
                            new CommandDefinition(
                                "SELECT pg_backend_pid();",
                                transaction: transaction,
                                cancellationToken: token
                            )
                        )
                    );
                }
            );
            var deletion = PublicationRepository()
                .CompleteDeleteAsync(seed.Command, seed.Execution with { Fence = fence }, default);
            bool observed = false;
            try
            {
                int sessionId = await session.Task.WaitAsync(TimeSpan.FromSeconds(5));
                observed = await ObserveDeletionLockWait(sessionId, deletion);
            }
            finally
            {
                if (rollbackFirst)
                {
                    await insertion.RollbackAsync();
                }
                else
                {
                    await insertion.CommitAsync();
                }
                await deletion;
            }
            observed
                .Should()
                .BeTrue("the actual parent FK lock must serialize deletion after the uncommitted insert");
            await AssertDeletionDependents(contender, seed.Command.HistoricalDataStoreId, 0);
        }
        else
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var fence = new PublicationFenceDecorator(
                seed.Execution.Fence,
                async (_, token) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    if (rollbackFirst)
                    {
                        throw new InvalidOperationException("deletion-contender-rollback");
                    }
                }
            );
            var deletion = PublicationRepository()
                .CompleteDeleteAsync(seed.Command, seed.Execution with { Fence = fence }, default);
            Task? insertion = null;
            bool observed = false;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                int sessionId = await contender.ExecuteScalarAsync<int>("SELECT pg_backend_pid();");
                insertion = InsertDeletionDependent(
                    contender,
                    null,
                    table,
                    seed.Command.HistoricalDataStoreId,
                    client
                );
                observed = await ObserveDeletionLockWait(sessionId, insertion);
            }
            finally
            {
                release.TrySetResult();
                if (rollbackFirst)
                {
                    Func<Task> action = () => deletion;
                    await action
                        .Should()
                        .ThrowAsync<InvalidOperationException>()
                        .WithMessage("deletion-contender-rollback");
                    if (insertion is not null)
                    {
                        await insertion;
                    }
                }
                else
                {
                    await deletion;
                    if (insertion is not null)
                    {
                        Func<Task> action = () => insertion;
                        (await action.Should().ThrowAsync<Npgsql.PostgresException>())
                            .Which.SqlState.Should()
                            .Be("23503");
                    }
                }
            }
            observed
                .Should()
                .BeTrue("the dependent must wait on the real deletion transaction before its FK outcome");
            int count = await contender.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM dmscs.\"{table}\" WHERE \"DataStoreId\" = @Id;",
                new { Id = seed.Command.HistoricalDataStoreId }
            );
            count.Should().Be(rollbackFirst ? 1 : 0);
            var row = await DeleteRow(contender, seed.Command.Id);
            row["Status"].Should().Be(rollbackFirst ? "DeleteInProgress" : "Deleted");
            row["DataStoreId"].Should().Be(rollbackFirst ? seed.Command.HistoricalDataStoreId : null);
        }
    }
}
