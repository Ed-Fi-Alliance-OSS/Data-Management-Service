// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [Test, Category("DataStoreManagePublication")]
    public async Task It_compares_every_persisted_pin_field(
        [Values(
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
            "PinnedRelationalMappingVersion"
        )]
            string field
    )
    {
        var seed = await SeedPublication();
        await using var connection = await Open();
        // Field names come only from the fixed NUnit corpus. The valid command stays unchanged.
        object changed = field == "PinnedContractVersion" ? 2 : "changed";
        await connection.ExecuteAsync(
            $"UPDATE dmscs.\"DataStoreManage\" SET \"{field}\" = @Changed WHERE \"Id\" = @Id;",
            new { seed.Command.Id, Changed = changed }
        );
        string before = await DeleteAcceptanceSnapshot();
        var action = () => PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_recovers_committed_publication_with_a_new_real_claim_without_rewriting()
    {
        var seed = await SeedPublication();
        int id = await PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second' WHERE "JobId" = @JobId;""",
            new { seed.Claim.JobId }
        );
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "recovery-worker",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.AttemptCount.Should().BeGreaterThan(seed.Claim.AttemptCount);
        var execution = new JobExecutionContext(
            claim.JobId,
            seed.Execution.TenantContext,
            claim.AttemptCount,
            5,
            new PostgresqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(claim, new())
        );
        var encryption = A.Fake<IConnectionStringEncryptionService>();
        A.CallTo(() => encryption.Encrypt(A<string?>._))
            .Throws(new InvalidOperationException("must-not-encrypt-recovery"));
        string before = await DeleteAcceptanceSnapshot();
        (
            await PublicationRepository(encryption: encryption)
                .PublishCreateAsync(seed.Command, execution, default)
        )
            .Should()
            .Be(id);
        A.CallTo(() => encryption.Encrypt(A<string?>._)).MustNotHaveHappened();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    private const string PublicationRuntime =
        "Host=runtime.example;Database=dms_store_minimal;Username=runtime;Password=test-only-secret";
    private static readonly JobLeaseTimings PublicationTimings = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15)
    );

    private sealed record PublicationSeed(
        ManagedDataStorePublishCommand Command,
        JobExecutionContext Execution,
        ClaimedJob Claim
    );

    private IOptions<DatabaseOptions> PublicationOptions =>
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = _database.ConnectionString,
                EncryptionKey = "publication-test-only-key",
            }
        );

    private async Task<PublicationSeed> SeedPublication(
        long? tenantId = null,
        string name = "Store",
        string provider = "postgresql"
    )
    {
        using var services = AcceptanceServices(tenantId);
        var ready = new DmsDataStoreProvisionerReadiness();
        ready.Record(new DmsProvisionerReadiness.Ready(provider, DateTimeOffset.UtcNow));
        var identity = CreateCommand(name, provider);
        var accepted = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), tenantId, ready)
                .AcceptCreateAsync(identity, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>()
            .Subject;
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "publication-worker",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(accepted.JobId);
        DmsTrustedArtifactPin pin = new()
        {
            ContractVersion = 1,
            Provider = provider,
            PackageId = "EdFi.Dms.Minimal",
            PackageVersion = "1.0.0",
            PackageSha256 = new string('a', 64),
            ArtifactSha256 = new string('b', 64),
            TemplateKind = "Minimal",
            DataStandardVersion = "5.2",
            EffectiveSchemaHash = new string('c', 64),
            InventorySha256 = new string('d', 64),
            ApiSchemaFormatVersion = "1",
            RelationalMappingVersion = "v3",
        };
        DynamicParameters parameters = new(
            new
            {
                accepted.Id,
                Attempt = claim.AttemptCount,
                pin.ContractVersion,
                pin.Provider,
                pin.PackageId,
                pin.PackageVersion,
                pin.PackageSha256,
                pin.ArtifactSha256,
                pin.TemplateKind,
                pin.DataStandardVersion,
                pin.EffectiveSchemaHash,
                pin.InventorySha256,
                pin.ApiSchemaFormatVersion,
                pin.RelationalMappingVersion,
            }
        );
        parameters.Add(
            "Timestamp",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1234560),
            DbType.DateTime2
        );
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStoreManage" SET "Status" = 'CreateInProgress', "LastAttempt" = @Attempt,
                "CreatedAt" = @Timestamp, "UpdatedAt" = @Timestamp, "LastModifiedDate" = @Timestamp, "LastRefreshed" = @Timestamp,
                "PinnedContractVersion" = @ContractVersion, "PinnedProvider" = @Provider, "PinnedPackageId" = @PackageId,
                "PinnedPackageVersion" = @PackageVersion, "PinnedPackageSha256" = @PackageSha256,
                "PinnedArtifactSha256" = @ArtifactSha256, "PinnedTemplateKind" = @TemplateKind,
                "PinnedDataStandardVersion" = @DataStandardVersion, "PinnedEffectiveSchemaHash" = @EffectiveSchemaHash,
                "PinnedInventorySha256" = @InventorySha256, "PinnedApiSchemaFormatVersion" = @ApiSchemaFormatVersion,
                "PinnedRelationalMappingVersion" = @RelationalMappingVersion
            WHERE "Id" = @Id;
            """,
            parameters
        );
        var fence = new PostgresqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(
            claim,
            new()
        );
        return new(
            new(accepted.Id, identity, pin, PublicationRuntime),
            new(claim.JobId, CreateTenantContext(tenantId).Context, claim.AttemptCount, 5, fence),
            claim
        );
    }

    private DataStoreManageRepository PublicationRepository(
        long? tenantId = null,
        IConnectionStringEncryptionService? encryption = null,
        IAuditContext? audit = null
    ) =>
        ManagedRepository(
            A.Fake<IJobEnqueuer>(),
            tenantId,
            ordinaryRepository: Repository(
                tenantId,
                audit: audit,
                encryption: encryption ?? new ConnectionStringEncryptionService(PublicationOptions)
            )
        );

    private sealed class PublicationFenceDecorator(
        IJobFence inner,
        Func<DbTransaction, CancellationToken, Task> after
    ) : IJobFence
    {
        public Task ExecuteAsync(
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        ) =>
            inner.ExecuteAsync(
                async (transaction, token) =>
                {
                    await work(transaction, token);
                    await after(transaction, token);
                },
                cancellationToken
            );
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_publishes_encrypted_shared_catalog_state_and_recovers_without_rewriting(
        [Values(false, true)] bool multitenant,
        [Values("postgresql", "mssql")] string targetProvider
    )
    {
        long? tenant = multitenant ? await Tenant("publication-tenant") : null;
        var seed = await SeedPublication(tenant, "  Store  ", targetProvider);
        await using var connection = await Open();
        var before = await DeleteRow(connection, seed.Command.Id);
        var realEncryption = new ConnectionStringEncryptionService(PublicationOptions);
        var encryption = A.Fake<IConnectionStringEncryptionService>();
        byte[]? encrypted = null;
        A.CallTo(() => encryption.Encrypt(PublicationRuntime))
            .ReturnsLazily(() =>
            {
                encrypted = realEncryption.Encrypt(PublicationRuntime);
                return encrypted;
            });
        var repository = PublicationRepository(tenant, encryption);
        int id = await repository.PublishCreateAsync(seed.Command, seed.Execution, default);
        A.CallTo(() => encryption.Encrypt(PublicationRuntime)).MustHaveHappenedOnceExactly();
        var ordinary = await connection.QuerySingleAsync<(
            string Name,
            string DataStoreType,
            string Provider,
            byte[] ConnectionString,
            long? TenantId
        )>(
            """SELECT "Name", "DataStoreType", "Provider", "ConnectionString", "TenantId" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
            new { Id = id }
        );
        ordinary.Name.Should().Be("Store");
        ordinary.DataStoreType.Should().Be("Shared");
        ordinary.Provider.Should().Be(targetProvider == "mssql" ? "sqlserver" : "postgresql");
        ordinary.TenantId.Should().Be(tenant);
        ordinary.ConnectionString.SequenceEqual(encrypted!).Should().BeTrue();
        (realEncryption.Decrypt(ordinary.ConnectionString) == PublicationRuntime).Should().BeTrue();
        var after = await DeleteRow(connection, seed.Command.Id);
        after["Status"].Should().Be("Created");
        after["DataStoreId"].Should().Be(id);
        after["HistoricalDataStoreId"].Should().Be(id);
        after["HistoricalDataStoreName"].Should().Be("Store");
        ((DateTime)after["UpdatedAt"]).Should().BeAfter((DateTime)before["UpdatedAt"]);
        after["LastRefreshed"].Should().Be(after["UpdatedAt"]);
        after["LastModifiedDate"].Should().Be(after["UpdatedAt"]);
        foreach (
            string changed in new[]
            {
                "Status",
                "DataStoreId",
                "HistoricalDataStoreId",
                "HistoricalDataStoreName",
                "UpdatedAt",
                "LastModifiedDate",
                "LastRefreshed",
            }
        )
        {
            before.Remove(changed);
            after.Remove(changed);
        }
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        string published = await DeleteAcceptanceSnapshot();
        (
            await repository.PublishCreateAsync(
                seed.Command with
                {
                    RuntimeConnectionString = "rotated-runtime-secret",
                },
                seed.Execution,
                default
            )
        )
            .Should()
            .Be(id);
        (await DeleteAcceptanceSnapshot()).Should().Be(published);
        A.CallTo(() => encryption.Encrypt(A<string?>._)).MustHaveHappenedOnceExactly();
        seed.Command.ToString().Should().NotContain("test-only-secret");
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_rolls_back_when_encryption_or_audit_fails([Values(false, true)] bool failEncryption)
    {
        var seed = await SeedPublication();
        string before = await DeleteAcceptanceSnapshot();
        var encryption = A.Fake<IConnectionStringEncryptionService>();
        var audit = A.Fake<IAuditContext>();
        if (failEncryption)
        {
            A.CallTo(() => encryption.Encrypt(A<string?>._))
                .Throws(new InvalidOperationException("encryption-test"));
        }
        else
        {
            A.CallTo(() => encryption.Encrypt(A<string?>._)).Returns(new byte[] { 1, 2, 3 });
            A.CallTo(() => audit.GetCurrentUser()).Throws(new InvalidOperationException("audit-test"));
        }
        var action = () =>
            PublicationRepository(encryption: encryption, audit: audit)
                .PublishCreateAsync(seed.Command, seed.Execution, default);
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
        (await PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default))
            .Should()
            .BePositive();
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_rolls_back_both_writes_on_failure_or_cancellation_before_fence_commit(
        [Values(false, true)] bool cancel
    )
    {
        var seed = await SeedPublication();
        string before = await DeleteAcceptanceSnapshot();
        using CancellationTokenSource cancellation = new();
        bool observedBothWrites = false;
        var fence = new PublicationFenceDecorator(
            seed.Execution.Fence,
            async (transaction, token) =>
            {
                int count = await transaction.Connection!.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        """SELECT COUNT(*) FROM dmscs."DataStoreManage" m JOIN dmscs."DataStore" d ON m."DataStoreId" = d."Id" WHERE m."Status" = 'Created';""",
                        transaction: transaction,
                        cancellationToken: token
                    )
                );
                observedBothWrites = count == 1;
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("after-publication-test");
            }
        );
        var action = () =>
            PublicationRepository()
                .PublishCreateAsync(seed.Command, seed.Execution with { Fence = fence }, cancellation.Token);
        if (cancel)
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await action.Should().ThrowAsync<InvalidOperationException>();
        }
        observedBothWrites.Should().BeTrue();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_rejects_changed_identity_pin_job_and_attempt_without_writes(
        [Values(
            "name",
            "source",
            "target",
            "endpoint",
            "template",
            "pin",
            "pin-space",
            "job",
            "job-space",
            "attempt",
            "context-tenant",
            "repository-tenant",
            "ordinary-tenant"
        )]
            string change
    )
    {
        var seed = await SeedPublication();
        var command = seed.Command;
        var execution = seed.Execution;
        var repository = PublicationRepository();
        await using var persisted = await Open();
        switch (change)
        {
            case "name":
                await persisted.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Name" = 'store' WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                break;
            case "source":
                command = command with
                {
                    Identity = command.Identity with { ExpectedSourceIdentity = Guid.NewGuid() },
                };
                break;
            case "target":
                await persisted.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "TargetDatabaseName" = 'dms_other_minimal' WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                break;
            case "endpoint":
                command = command with
                {
                    Identity = command.Identity with
                    {
                        Endpoint = command.Identity.Endpoint with { Host = "other.example" },
                    },
                };
                break;
            case "template":
                await persisted.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Template" = 'Sample' WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                break;
            case "pin":
                command = command with
                {
                    PinnedArtifact = command.PinnedArtifact with { InventorySha256 = new string('e', 64) },
                };
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
            case "job":
                execution = execution with { JobId = "unrelated-job" };
                break;
            case "job-space":
                execution = execution with { JobId = execution.JobId + " " };
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
            case "ordinary-tenant":
                repository = ManagedRepository(
                    A.Fake<IJobEnqueuer>(),
                    ordinaryRepository: Repository(await Tenant("other"))
                );
                break;
        }
        string before = await DeleteAcceptanceSnapshot();
        var action = () => repository.PublishCreateAsync(command, execution, default);
        await action.Should().ThrowAsync<Exception>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_refuses_stale_real_fences_and_preserves_the_new_lease()
    {
        var seed = await SeedPublication();
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second' WHERE "JobId" = @JobId;""",
            new { seed.Claim.JobId }
        );
        var reclaimed = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "replacement-worker",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        reclaimed.FencingToken.Should().BeGreaterThan(seed.Claim.FencingToken);
        string before = await DeleteAcceptanceSnapshot();
        var action = () => PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
        await action.Should().ThrowAsync<JobLeaseLostException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_preserves_later_lifecycle_states_and_newer_jobs(
        [Values("PendingDelete", "DeleteInProgress", "Deleted", "newer-job")] string state
    )
    {
        var seed = await SeedPublication();
        var repository = PublicationRepository();
        await repository.PublishCreateAsync(seed.Command, seed.Execution, default);
        await using var connection = await Open();
        if (state == "newer-job")
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs."DataStoreManage" SET "CreateJobId" = 'newer-create-job' WHERE "Id" = @Id;""",
                new { seed.Command.Id }
            );
        }
        else
        {
            await connection.ExecuteAsync(
                """
                UPDATE dmscs."DataStoreManage" SET "Status" = @Status, "DeleteJobId" = 'delete-job',
                    "DataStoreId" = CASE WHEN @Status = 'Deleted' THEN NULL ELSE "DataStoreId" END
                WHERE "Id" = @Id;
                """,
                new { seed.Command.Id, Status = state }
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        var action = () => repository.PublishCreateAsync(seed.Command, seed.Execution, default);
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_verifies_the_actual_published_link(
        [Values("name", "type", "provider", "history", "other-row", "other-tenant")] string corruption
    )
    {
        var seed = await SeedPublication();
        var repository = PublicationRepository();
        int id = await repository.PublishCreateAsync(seed.Command, seed.Execution, default);
        await using var connection = await Open();
        switch (corruption)
        {
            case "name":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStore" SET "Name" = 'Other' WHERE "Id" = @Id;""",
                    new { Id = id }
                );
                break;
            case "type":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStore" SET "DataStoreType" = 'Production' WHERE "Id" = @Id;""",
                    new { Id = id }
                );
                break;
            case "provider":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStore" SET "Provider" = 'other' WHERE "Id" = @Id;""",
                    new { Id = id }
                );
                break;
            case "history":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "HistoricalDataStoreId" = @Other WHERE "Id" = @Id;""",
                    new { seed.Command.Id, Other = id + 1 }
                );
                break;
            case "other-row":
                int other = (await Repository().InsertDataStore(Insert("Unrelated")))
                    .Should()
                    .BeOfType<DataStoreInsertResult.Success>()
                    .Subject.Id;
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "DataStoreId" = @Other WHERE "Id" = @Id;""",
                    new { seed.Command.Id, Other = other }
                );
                break;
            case "other-tenant":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStore" SET "TenantId" = @TenantId WHERE "Id" = @Id;""",
                    new { Id = id, TenantId = await Tenant("other") }
                );
                break;
        }
        string before = await DeleteAcceptanceSnapshot();
        var action = () => repository.PublishCreateAsync(seed.Command, seed.Execution, default);
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_rechecks_the_canonical_ordinary_namespace(
        [Values("store", " STORE ", "café")] string ordinaryName
    )
    {
        var seed = await SeedPublication(name: ordinaryName == "café" ? "cafe" : "Store");
        await using var connection = await Open();
        // Simulate an out-of-band row: the public ordinary writer correctly rejects an active reservation.
        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs."DataStore" ("Name", "DataStoreType", "Provider") VALUES (@Name, 'Shared', 'postgresql');
            """,
            new { Name = ordinaryName }
        );
        var repository = PublicationRepository();
        if (ordinaryName == "café")
        {
            // Both display spellings must remain discoverable by their canonical keys.
            (await repository.PublishCreateAsync(seed.Command, seed.Execution, default))
                .Should()
                .BePositive();
            (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(2);
            foreach (string lookup in new[] { "cafe", "café" })
            {
                var found = (await Repository().QueryDataStore(new() { Name = lookup }))
                    .Should()
                    .BeOfType<DataStoreQueryResult.Success>()
                    .Subject.DataStoreResponses;
                found.Should().ContainSingle().Which.Name.Should().Be(lookup);
            }
        }
        else
        {
            string before = await DeleteAcceptanceSnapshot();
            var action = () => repository.PublishCreateAsync(seed.Command, seed.Execution, default);
            await action.Should().ThrowAsync<InvalidOperationException>();
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_keeps_the_reservation_while_publication_defeats_an_ordinary_insert_or_rename(
        [Values("insert", "rename")] string operation,
        [Values(false, true)] bool multitenant
    )
    {
        long? tenant = multitenant ? await Tenant("publication-tenant") : null;
        int existing = (await Repository(tenant).InsertDataStore(Insert("Existing")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        var seed = await SeedPublication(tenant);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var fence = new PublicationFenceDecorator(
            seed.Execution.Fence,
            async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            }
        );
        var publication = PublicationRepository(tenant)
            .PublishCreateAsync(seed.Command, seed.Execution with { Fence = fence }, default);
        Task<object>? ordinary = null;
        bool waited = false;
        string applicationName = "publication-contender-" + Guid.NewGuid().ToString("N");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var repository = Repository(tenant, applicationName);
            ordinary = Mutate();
            async Task<object> Mutate() =>
                operation == "insert"
                    ? await repository.InsertDataStore(Insert("store"))
                    : await repository.UpdateDataStore(Rename(existing, "store"));
            waited = await ObserveGateWait(applicationName, ordinary);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await publication;
            }
            finally
            {
                if (ordinary is not null)
                {
                    await ordinary;
                }
            }
        }
        waited.Should().BeTrue("the contender must wait on the publication's actual transaction");
        (await publication).Should().BePositive();
        (await ordinary!)
            .Should()
            .BeOfType(
                operation == "insert"
                    ? typeof(DataStoreInsertResult.FailureDuplicateName)
                    : typeof(DataStoreUpdateResult.FailureDuplicateName)
            );
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_rolls_back_the_ordinary_insert_and_aggregate_when_either_link_write_boundary_fails(
        [Values(false, true)] bool afterLink
    )
    {
        var seed = await SeedPublication();
        string before = await DeleteAcceptanceSnapshot();
        await using var connection = await Open();
        string timing = afterLink ? "AFTER" : "BEFORE";
        await connection.ExecuteAsync(
            $"""

            CREATE FUNCTION dmscs.reject_publication() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'publication-write-test'; END $$;
            CREATE TRIGGER reject_publication {timing} UPDATE ON dmscs."DataStoreManage"
            FOR EACH ROW EXECUTE FUNCTION dmscs.reject_publication();
            """
        );
        try
        {
            var action = () =>
                PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
            await action.Should().ThrowAsync<Npgsql.PostgresException>();
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                DROP TRIGGER reject_publication ON dmscs."DataStoreManage"; DROP FUNCTION dmscs.reject_publication();
                """
            );
        }
        (await PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default))
            .Should()
            .BePositive();
    }

    [Test, Category("DataStoreManagePublication")]
    public async Task It_propagates_cancellation_before_any_write()
    {
        var seed = await SeedPublication();
        string before = await DeleteAcceptanceSnapshot();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        var action = () =>
            PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }
}
