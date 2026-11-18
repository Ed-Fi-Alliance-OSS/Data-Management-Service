// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [TestCase("tenant"), TestCase("job"), TestCase("id"), Category("DataStoreManageCreateHandler")]
    public async Task It_rejects_a_different_create_partition_or_operation_without_writes(string mismatch)
    {
        long tenantId = await Tenant("create-handler-tenant");
        var seed = await SeedPublication(tenantId);
        var execution = mismatch == "job" ? seed.Execution with { JobId = "another-job" } : seed.Execution;
        var repository = PublicationRepository(mismatch == "tenant" ? null : tenantId);
        string before = await DeleteAcceptanceSnapshot();
        var action = () =>
            repository.StartCreateAsync(
                mismatch == "id" ? seed.Command.Id + 10000 : seed.Command.Id,
                execution,
                default
            );
        await action.Should().ThrowAsync<JobPermanentException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageCreateHandler")]
    public async Task It_rejects_created_replay_if_the_durable_catalog_link_changed()
    {
        var seed = await SeedPublication();
        int id = await PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStore" SET "Name" = 'changed' WHERE "Id" = @Id;""",
            new { Id = id }
        );
        string before = await DeleteAcceptanceSnapshot();
        var action = () => PublicationRepository().StartCreateAsync(seed.Command.Id, seed.Execution, default);
        await action.Should().ThrowAsync<JobPermanentException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    private async Task<PublicationSeed> SeedCreateHandler()
    {
        var seed = await SeedPublication(provider: "mssql");
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStoreManage" SET "Status" = 'PendingCreate', "LastAttempt" = 0,
                "PinnedContractVersion" = NULL, "PinnedProvider" = NULL, "PinnedPackageId" = NULL,
                "PinnedPackageVersion" = NULL, "PinnedPackageSha256" = NULL, "PinnedArtifactSha256" = NULL,
                "PinnedTemplateKind" = NULL, "PinnedDataStandardVersion" = NULL, "PinnedEffectiveSchemaHash" = NULL,
                "PinnedInventorySha256" = NULL, "PinnedApiSchemaFormatVersion" = NULL, "PinnedRelationalMappingVersion" = NULL
            WHERE "Id" = @Id;
            """,
            new { seed.Command.Id }
        );
        return seed;
    }

    private static ManagedDataStoreRuntimeConnectionBuilder CreateHandlerRuntime()
    {
        var secrets = Options.Create(new SecretsOptions());
        return new(
            Options.Create(
                new ManagedDataStoreRuntimeOptions
                {
                    RuntimeConnectionString =
                        "Server=tcp:db.example,1433;User ID=runtime;Password=test-only;Database=ignored",
                }
            ),
            new MssqlManagedRuntimeConnectionProvider(),
            new SecretValueCache(secrets),
            secrets,
            TimeProvider.System
        );
    }

    private async Task<JobExecutionContext> ReclaimCreateHandler(PublicationSeed seed)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = DATEADD(second, -1, SYSUTCDATETIME()), "NextAttemptAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "Id" = @Id;""",
            new { seed.Claim.Id }
        );
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "handler-recovery",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(seed.Claim.JobId);
        return new(
            claim.JobId,
            seed.Execution.TenantContext,
            claim.AttemptCount,
            5,
            new MssqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(claim, new())
        );
    }

    [TestCase(false), TestCase(true), Category("DataStoreManageCreateHandler")]
    public async Task It_runs_create_handler_and_recovers_publication_without_repinning_or_duplicates(
        bool rollbackPublication
    )
    {
        var seed = await SeedCreateHandler();
        var repository = PublicationRepository();
        var provisioner = A.Fake<IDmsDataStoreProvisioner>();
        int resolutions = 0;
        int physicalCalls = 0;
        A.CallTo(() => provisioner.ResolveArtifactAsync(A<DmsDatabaseTemplate>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                resolutions++;
                return Task.FromResult<DmsArtifactResolution>(
                    new DmsArtifactResolution.Resolved(seed.Command.PinnedArtifact, DmsCleanupReport.Clean)
                );
            });
        A.CallTo(() =>
                provisioner.CreateAsync(
                    A<DmsDataStoreCreateRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreCreateRequest request,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    // A separate connection sees the committed full pin before physical admission.
                    await using var connection = await Open();
                    var stored = await connection.QuerySingleAsync<ManagedDataStoreRow>(
                        """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                        new { seed.Command.Id }
                    );
                    stored
                        .Restore(ProtectedDatabaseNames.Create("cms", []))
                        .PinnedArtifact.Should()
                        .Be(seed.Command.PinnedArtifact);
                    request.PinnedArtifact.Should().Be(seed.Command.PinnedArtifact);
                    request.ExpectedSourceIdentity.Should().Be(seed.Command.Identity.ExpectedSourceIdentity);
                    await admit(endpoint, token);
                    physicalCalls++;
                    return physicalCalls == 1
                        ? (DmsDataStoreCreateResult)
                            new DmsDataStoreCreateResult.Created(
                                request.PinnedArtifact,
                                DmsCleanupReport.Clean
                            )
                        : new DmsDataStoreCreateResult.Reconciled(
                            request.PinnedArtifact,
                            DmsCleanupReport.Clean
                        );
                }
            );
        var handler = new CreateManagedDataStoreHandler(repository, provisioner, CreateHandlerRuntime());
        var execution = seed.Execution;
        if (rollbackPublication)
        {
            int fences = 0;
            var failed = execution with
            {
                Fence = new PublicationFenceDecorator(
                    execution.Fence,
                    (_, _) =>
                    {
                        if (++fences == 4)
                        {
                            throw new IOException("test publication rollback");
                        }
                        return Task.CompletedTask;
                    }
                ),
            };
            var action = () => handler.ExecuteAsync(failed, new(seed.Command.Id), default);
            await action.Should().ThrowAsync<IOException>();
            await using var connection = await Open();
            (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(0);
            (await DeleteRow(connection, seed.Command.Id))["Status"].Should().Be("CreateInProgress");
            execution = await ReclaimCreateHandler(seed);
        }
        await handler.ExecuteAsync(execution, new(seed.Command.Id), default);
        await using var completed = await Open();
        (await DeleteRow(completed, seed.Command.Id))["Status"].Should().Be("Created");
        (await completed.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
            .Should()
            .Be(1);
        string snapshot = await DeleteAcceptanceSnapshot();
        await handler.ExecuteAsync(execution, new(seed.Command.Id), default);
        (await DeleteAcceptanceSnapshot()).Should().Be(snapshot);
        resolutions.Should().Be(1);
        physicalCalls.Should().Be(rollbackPublication ? 2 : 1);
        A.CallTo(() => provisioner.CreateAsync(A<DmsDataStoreCreateRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase(false), TestCase(true), Category("DataStoreManageCreateHandler")]
    public async Task It_rolls_back_create_start_or_pin_when_the_fence_rejects_commit(bool pin)
    {
        var seed = await SeedCreateHandler();
        var repository = PublicationRepository();
        ManagedDataStoreCreateOperation? operation = null;
        if (pin)
        {
            operation = await repository.StartCreateAsync(seed.Command.Id, seed.Execution, default);
        }
        string before = await DeleteAcceptanceSnapshot();
        var execution = seed.Execution with
        {
            Fence = new PublicationFenceDecorator(
                seed.Execution.Fence,
                (_, _) => throw new JobLeaseLostException()
            ),
        };
        Func<Task> action = pin
            ? async () =>
                _ = await repository.PinCreateAsync(
                    operation!,
                    seed.Command.PinnedArtifact,
                    execution,
                    default
                )
            : async () => _ = await repository.StartCreateAsync(seed.Command.Id, execution, default);
        await action.Should().ThrowAsync<JobLeaseLostException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [TestCase("identity"), TestCase("endpoint"), TestCase("pin"), Category("DataStoreManageCreateHandler")]
    public async Task It_rejects_changed_persisted_create_evidence_at_physical_admission(string change)
    {
        var seed = await SeedPublication(provider: "mssql");
        var repository = PublicationRepository();
        var operation = await repository.StartCreateAsync(seed.Command.Id, seed.Execution, default);
        await using var connection = await Open();
        string sql = change switch
        {
            "identity" =>
                """UPDATE dmscs."DataStoreManage" SET "ExpectedSourceIdentity" = @Identity WHERE "Id" = @Id;""",
            "endpoint" =>
                """UPDATE dmscs."DataStoreManage" SET "EndpointDescriptor" = @Endpoint WHERE "Id" = @Id;""",
            _ =>
                """UPDATE dmscs."DataStoreManage" SET "PinnedPackageVersion" = 'changed' WHERE "Id" = @Id;""",
        };
        await connection.ExecuteAsync(
            sql,
            new
            {
                operation.Id,
                Identity = Guid.NewGuid(),
                Endpoint = (operation.Identity.Endpoint with { Host = "changed.example" }).CanonicalEncoding,
            }
        );
        string before = await DeleteAcceptanceSnapshot();
        var action = () =>
            repository.AdmitCreateAsync(operation, operation.Identity.Endpoint, seed.Execution, default);
        await action.Should().ThrowAsync<JobPermanentException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageCreateHandler")]
    public async Task It_completes_a_published_create_job_after_delete_acceptance_without_changes()
    {
        var seed = await SeedPublication(provider: "mssql");
        await PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
        await using var services = AcceptanceServices();
        // The provisioner is ready for the seeded record's own target provider.
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), readiness: ReadyFor("mssql"))
                .AcceptDeleteAsync(seed.Command.Id, default)
        )
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>();
        string before = await DeleteAcceptanceSnapshot();
        // DELETE admits only Created, so the create's publication committed; its job completes without
        // physical work and without touching the newer delete operation.
        (await PublicationRepository().StartCreateAsync(seed.Command.Id, seed.Execution, default))
            .AlreadyCreated.Should()
            .BeTrue();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageCreateHandler")]
    public async Task It_reuses_the_pin_on_a_new_attempt_and_refuses_the_old_lease()
    {
        var seed = await SeedPublication(provider: "mssql");
        var repository = PublicationRepository();
        var operation = await repository.StartCreateAsync(seed.Command.Id, seed.Execution, default);
        var failure = ManagedDataStoreFailure.FromProvisioner(
            DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.LockTimeout,
                DmsProvisioningDiagnostic.ForOperation("Create")
            )
        );
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).FailManaged(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                failure,
                1,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();
        var execution = await ReclaimCreateHandler(seed);
        var resumed = await repository.StartCreateAsync(seed.Command.Id, execution, default);
        resumed.Should().Be(operation);
        var stale = () =>
            repository.AdmitCreateAsync(operation, operation.Identity.Endpoint, seed.Execution, default);
        await stale.Should().ThrowAsync<JobLeaseLostException>();
        await repository.AdmitCreateAsync(resumed, resumed.Identity.Endpoint, execution, default);
        var changedPin = () =>
            repository.PinCreateAsync(
                resumed,
                seed.Command.PinnedArtifact with
                {
                    PackageVersion = "changed",
                },
                execution,
                default
            );
        await changedPin.Should().ThrowAsync<JobPermanentException>();
    }

    [
        TestCase(DmsProvisioningFailureCode.LockTimeout),
        TestCase(DmsProvisioningFailureCode.TargetUnowned),
        TestCase(DmsProvisioningFailureCode.StartupValidationFailed),
        Category("DataStoreManageCreateHandler")
    ]
    public async Task It_commits_typed_job_and_lifecycle_failure_together_and_rolls_back_injected_failure(
        DmsProvisioningFailureCode code
    )
    {
        var seed = await SeedPublication(provider: "mssql");
        var failure = ManagedDataStoreFailure.FromProvisioner(
            DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation("Create"))
        );
        int? backoff = failure.Category == DmsProvisioningFailureCategory.Transient ? 1 : null;
        var leases = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            BeforeOwnershipCommit = _ => throw new IOException("test outcome rollback"),
        };
        string before = await DeleteAcceptanceSnapshot();
        var result = await leases.FailManaged(
            seed.Claim.Id,
            seed.Claim.LeaseOwner,
            seed.Claim.FencingToken,
            failure,
            backoff,
            default
        );
        result.Should().NotBeOfType<JobWriteResult.Success>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
        leases = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        (
            await leases.FailManaged(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                failure,
                backoff,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();
        await using var connection = await Open();
        var row = await DeleteRow(connection, seed.Command.Id);
        row["Status"].Should().Be(backoff is null ? "CreateError" : "CreateFailed");
        row["LastFailureProvisioningCode"].Should().Be(code.ToString());
        row["LastFailureCategory"].Should().Be(failure.Category.ToString());
        row["LastFailureJobCode"].Should().BeNull();
        (
            await connection.ExecuteScalarAsync<string>(
                """SELECT "Status" FROM dmscs."Job" WHERE "Id" = @Id;""",
                new { seed.Claim.Id }
            )
        )
            .Should()
            .Be(backoff is null ? "Error" : "Pending");
    }
}
