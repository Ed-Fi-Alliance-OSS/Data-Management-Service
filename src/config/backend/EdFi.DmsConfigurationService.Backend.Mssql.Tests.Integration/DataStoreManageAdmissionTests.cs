// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    // This is deliberately test composition, not a future handler. The physical primitive knows nothing
    // about CMS lifecycle state; the caller supplies this short transaction through the real claimed fence.
    private static Task AdmitManagedOperation(
        ManagedDataStorePublishCommand command,
        JobExecutionContext execution,
        bool delete,
        ManagedDataStoreEndpoint actual,
        CancellationToken cancellationToken
    ) =>
        execution.Fence.ExecuteAsync(
            async (transaction, token) =>
            {
                actual.Should().Be(command.Identity.Endpoint);
                long? tenantId = execution.TenantContext is TenantContext.Multitenant tenant
                    ? tenant.TenantId
                    : null;
                var row = await transaction.Connection!.QuerySingleOrDefaultAsync<AdmissionRow>(
                    new CommandDefinition(
                        """
                        SELECT "Status", "CreateJobId", "DeleteJobId", "ExpectedSourceIdentity", "EndpointDescriptor", "TargetDatabaseName", "LastAttempt"
                        FROM dmscs."DataStoreManage" WITH (UPDLOCK, HOLDLOCK)
                        WHERE "Id" = @Id AND ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL)) ;
                        """,
                        new { command.Id, TenantId = tenantId },
                        transaction,
                        cancellationToken: token
                    )
                );
                if (
                    row is null
                    || row.Status != (delete ? "DeleteInProgress" : "CreateInProgress")
                    || row.LastAttempt != execution.Attempt
                    || (delete ? row.DeleteJobId : row.CreateJobId) != execution.JobId
                    || (!delete && row.DeleteJobId is not null)
                    || row.ExpectedSourceIdentity != command.Identity.ExpectedSourceIdentity
                    || row.EndpointDescriptor != actual.CanonicalEncoding
                    || row.TargetDatabaseName != command.Identity.TargetName.Value
                )
                {
                    throw new InvalidOperationException("Managed admission identity or eligibility changed.");
                }
            },
            cancellationToken
        );

    private static async Task SettleAdmissionTask(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            // The test asserts the recorded task's result after all concurrent work has ended.
        }
    }

    public sealed class AdmissionRow
    {
        public string Status { get; set; } = "";
        public string CreateJobId { get; set; } = "";
        public string? DeleteJobId { get; set; }
        public Guid ExpectedSourceIdentity { get; set; }
        public string EndpointDescriptor { get; set; } = "";
        public string TargetDatabaseName { get; set; } = "";
        public int LastAttempt { get; set; }
    }

    private JobExecutionContext AdmissionExecution(ClaimedJob claim) =>
        new(
            claim.JobId,
            CreateTenantContext(claim.TenantId).Context,
            claim.AttemptCount,
            5,
            new MssqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(claim, new())
        );

    private async Task<ClaimedJob> ReclaimAdmission(ClaimedJob previous)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "JobId" = @JobId;""",
            new { previous.JobId }
        );
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "admission-successor",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(previous.JobId);
        claim.FencingToken.Should().BeGreaterThan(previous.FencingToken);
        // The successor's attempt transition is test composition until handlers exist. It must commit
        // through the newly claimed fence before physical admission or phase-2 publication/finalization.
        await AdmissionExecution(claim)
            .Fence.ExecuteAsync(
                async (transaction, token) =>
                {
                    int updated = await transaction.Connection!.ExecuteAsync(
                        new CommandDefinition(
                            """
                            UPDATE dmscs."DataStoreManage" SET "LastAttempt" = @Attempt
                            WHERE ("TenantId" = @TenantId OR ("TenantId" IS NULL AND @TenantId IS NULL))
                                AND "LastAttempt" = @PreviousAttempt AND "Status" = @Status
                                AND ((@Status = 'CreateInProgress' AND "CreateJobId" = @JobId AND "DeleteJobId" IS NULL)
                                    OR (@Status = 'DeleteInProgress' AND "DeleteJobId" = @JobId));
                            """,
                            new
                            {
                                claim.TenantId,
                                claim.JobId,
                                Attempt = claim.AttemptCount,
                                PreviousAttempt = previous.AttemptCount,
                                Status = claim.JobType == ManagedJobTypes.Delete
                                    ? "DeleteInProgress"
                                    : "CreateInProgress",
                            },
                            transaction,
                            cancellationToken: token
                        )
                    );
                    updated.Should().Be(1);
                },
                default
            );
        return claim;
    }

    private async Task CompleteAdmissionJob(ClaimedJob claim) =>
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).Complete(
                claim.Id,
                claim.LeaseOwner,
                claim.FencingToken,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();

    private async Task<DeletionSeed> AcceptAdmissionDelete(PublicationSeed created)
    {
        await CompleteAdmissionJob(created.Claim);
        using var services = AcceptanceServices();
        var accepted = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                .AcceptDeleteAsync(created.Command.Id, default)
        )
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>()
            .Subject;
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "admission-delete",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(accepted.JobId);
        await using var connection = await Open();
        int catalogId = await connection.ExecuteScalarAsync<int>(
            """SELECT "DataStoreId" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
            new { created.Command.Id }
        );
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStoreManage" SET "Status" = 'DeleteInProgress', "LastAttempt" = @Attempt WHERE "Id" = @Id;""",
            new { created.Command.Id, Attempt = claim.AttemptCount }
        );
        return new(
            new(
                created.Command.Id,
                created.Command.Identity,
                created.Command.PinnedArtifact,
                catalogId,
                created.Claim.JobId
            ),
            AdmissionExecution(claim),
            claim
        );
    }

    private static ManagedDataStoreEndpoint RequireAdmissionEndpoint()
    {
        RequireConfiguredOrSkip();
        if (!MssqlManagedEndpoint.TryParse(AdminConnectionString!, out var endpoint))
        {
            Assert.Ignore(
                "Managed physical admission requires a canonical TCP endpoint; named pipes/LocalDB do not qualify."
            );
            throw new InvalidOperationException();
        }
        RequireSharedStagingOrSkip();
        return endpoint;
    }

    private async Task<PublicationSeed> SeedPhysicalAdmission(
        Lane lane,
        ManagedDataStoreEndpoint endpoint,
        string? name = null
    )
    {
        var identity = CreateCommand(name ?? "Admission" + Guid.NewGuid().ToString("N")[..12], "mssql") with
        {
            Endpoint = endpoint,
            ExpectedSourceIdentity = lane.Request.ExpectedSourceIdentity,
        };
        return await SeedPublication(
            physical: new(1, identity, lane.Request.PinnedArtifact, PublicationRuntime)
        );
    }

    private static DmsDataStoreCreateRequest AdmissionRequest(PublicationSeed seed) =>
        new(
            seed.Command.Identity.Template,
            seed.Command.Identity.TargetName,
            seed.Command.Identity.ExpectedSourceIdentity,
            seed.Command.PinnedArtifact
        );

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_refuses_delayed_create_after_a_successor_creates_and_deletes_the_target()
    {
        var endpoint = RequireAdmissionEndpoint();
        LaneHooks hooks = new();
        await using Lane lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), hooks);
        var old = await SeedPhysicalAdmission(lane, endpoint);
        await using TestDatabase cleanup = new(old.Command.Identity.TargetName.Value);
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int locks = 0;
        hooks.Probe = async (point, _) =>
        {
            if (point == DmsProvisioningHookPoint.BeforeTargetLock && Interlocked.Increment(ref locks) == 1)
            {
                paused.SetResult();
                await resume.Task;
            }
        };
        Task<DmsDataStoreCreateResult> delayed = lane.Service.CreateAsync(
            AdmissionRequest(old),
            endpoint,
            (actual, token) => AdmitManagedOperation(old.Command, old.Execution, false, actual, token),
            default
        );
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var claim = await ReclaimAdmission(old.Claim);
            var successor = old with { Claim = claim, Execution = AdmissionExecution(claim) };
            (
                await lane.Service.CreateAsync(
                    AdmissionRequest(successor),
                    endpoint,
                    (actual, token) =>
                        AdmitManagedOperation(successor.Command, successor.Execution, false, actual, token),
                    default
                )
            )
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>();
            await PublicationRepository().PublishCreateAsync(successor.Command, successor.Execution, default);
            var deletion = await AcceptAdmissionDelete(successor);
            (
                await lane.Service.DeleteAsync(
                    new(
                        successor.Command.Identity.TargetName,
                        successor.Command.Identity.ExpectedSourceIdentity
                    ),
                    endpoint,
                    (actual, token) =>
                        AdmitManagedOperation(successor.Command, deletion.Execution, true, actual, token),
                    default
                )
            )
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Dropped>();
            await PublicationRepository().CompleteDeleteAsync(deletion.Command, deletion.Execution, default);
            await CompleteAdmissionJob(deletion.Claim);
        }
        finally
        {
            resume.TrySetResult();
            await SettleAdmissionTask(delayed);
        }
        Func<Task> oldAction = () => delayed;
        await oldAction.Should().ThrowAsync<JobLeaseLostException>();
        (await Engine().InspectAsync(old.Command.Identity.TargetName, default))
            .Should()
            .BeOfType<DmsLiveTargetState.Absent>();
        await using var connection = await Open();
        (await DeleteRow(connection, old.Command.Id))["Status"].Should().Be("Deleted");
        lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)).Should().Be(1);
        lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(1);
        lane.OperationEntries.Should().BeEmpty();
    }

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_keeps_the_physical_lock_after_admission_when_the_job_is_reclaimed()
    {
        var endpoint = RequireAdmissionEndpoint();
        LaneHooks hooks = new();
        await using Lane lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), hooks);
        var old = await SeedPhysicalAdmission(lane, endpoint);
        await using TestDatabase cleanup = new(old.Command.Identity.TargetName.Value);
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DmsOperationId> successorLock = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int admissions = 0;
        hooks.Probe = async (point, operation) =>
        {
            if (point == DmsProvisioningHookPoint.BeforeTargetLock && admitted.Task.IsCompleted)
            {
                successorLock.TrySetResult(operation);
            }
            if (
                point == DmsProvisioningHookPoint.AfterAdmission
                && Interlocked.Increment(ref admissions) == 1
            )
            {
                admitted.SetResult();
                await resume.Task;
            }
        };
        var first = lane.Service.CreateAsync(
            AdmissionRequest(old),
            endpoint,
            (actual, token) => AdmitManagedOperation(old.Command, old.Execution, false, actual, token),
            default
        );
        Task<DmsDataStoreCreateResult>? second = null;
        PublicationSeed successor = old;
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var claim = await ReclaimAdmission(old.Claim);
            successor = old with { Claim = claim, Execution = AdmissionExecution(claim) };
            second = lane.Service.CreateAsync(
                AdmissionRequest(successor),
                endpoint,
                (actual, token) =>
                    AdmitManagedOperation(successor.Command, successor.Execution, false, actual, token),
                default
            );
            DmsOperationId waiting = await successorLock.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (
                await WaitUntilAsync(
                    async () =>
                        await ScalarOnServerAsync<int>(
                            "SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.program_name = @app AND r.wait_type LIKE 'LCK%';",
                            ("app", waiting.HolderApplicationName)
                        ) > 0,
                    TimeSpan.FromSeconds(3)
                )
            )
                .Should()
                .BeTrue("the successor must wait on the same physical target lock");
            second.IsCompleted.Should().BeFalse();
            admissions.Should().Be(1);
            // Keep the admitted holder paused until the real server lock budget expires. Physical
            // work need not finish within a contender's budget, so exercise the retry deterministically.
            var failure = (await second.WaitAsync(TimeSpan.FromSeconds(30)))
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject;
            await TestContext.Out.WriteLineAsync(
                $"SuccessorFailure: Code={failure.Detail.Code}; Category={failure.Detail.Category}; "
                    + $"Operation={failure.Detail.Diagnostic.Operation}; "
                    + $"CleanupHasResidue={failure.Cleanup.HasResidue}; ResidueCount={failure.Cleanup.Residue.Count}"
            );
            failure.Detail.Code.Should().Be(DmsProvisioningFailureCode.LockTimeout);
            failure.Detail.Category.Should().Be(DmsProvisioningFailureCategory.Transient);
            failure.Detail.Diagnostic.Operation.Should().Be("AcquireTargetLock");
            failure.Cleanup.HasResidue.Should().BeFalse();
            failure.Cleanup.Residue.Should().BeEmpty();
            first.IsCompleted.Should().BeFalse();
            admissions.Should().Be(1);
            lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)).Should().Be(0);
        }
        finally
        {
            resume.TrySetResult();
            await SettleAdmissionTask(first);
            if (second is not null)
            {
                await SettleAdmissionTask(second);
            }
        }
        (await first).Should().BeOfType<DmsDataStoreCreateResult.Created>();
        Func<Task> stalePublish = () =>
            PublicationRepository().PublishCreateAsync(old.Command, old.Execution, default);
        await stalePublish.Should().ThrowAsync<JobLeaseLostException>();
        // Compose a later attempt with a new real lease and its persisted attempt transition. This
        // proves admission/publication fencing; runtime handler retry scheduling is a later step.
        var retryClaim = await ReclaimAdmission(successor.Claim);
        retryClaim.AttemptCount.Should().Be(successor.Claim.AttemptCount + 1);
        var retry = successor with { Claim = retryClaim, Execution = AdmissionExecution(retryClaim) };
        (
            await lane.Service.CreateAsync(
                AdmissionRequest(retry),
                endpoint,
                (actual, token) =>
                    AdmitManagedOperation(retry.Command, retry.Execution, false, actual, token),
                default
            )
        )
            .Should()
            .BeOfType<DmsDataStoreCreateResult.Reconciled>();
        int catalogId = await PublicationRepository()
            .PublishCreateAsync(retry.Command, retry.Execution, default);
        (await PublicationRepository().PublishCreateAsync(retry.Command, retry.Execution, default))
            .Should()
            .Be(catalogId);
        await using var connection = await Open();
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs."DataStore" WHERE "Name" = @Name AND "TenantId" IS NULL;""",
                new { retry.Command.Identity.Name }
            )
        )
            .Should()
            .Be(1);
        lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)).Should().Be(1);
        lane.Engine.CountOf(nameof(IDmsTargetEngine.AssignMarkerAsync)).Should().Be(1);
        admissions.Should().Be(2);
        lane.OperationEntries.Should().BeEmpty();
    }

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_refuses_delayed_delete_after_a_new_identity_reuses_the_name()
    {
        var endpoint = RequireAdmissionEndpoint();
        LaneHooks hooks = new();
        await using Lane lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), hooks);
        var created = await SeedPhysicalAdmission(lane, endpoint);
        await using TestDatabase cleanup = new(created.Command.Identity.TargetName.Value);
        (
            await lane.Service.CreateAsync(
                AdmissionRequest(created),
                endpoint,
                (actual, token) =>
                    AdmitManagedOperation(created.Command, created.Execution, false, actual, token),
                default
            )
        )
            .Should()
            .BeOfType<DmsDataStoreCreateResult.Created>();
        await PublicationRepository().PublishCreateAsync(created.Command, created.Execution, default);
        var old = await AcceptAdmissionDelete(created);
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int locks = 0;
        hooks.Probe = async (point, _) =>
        {
            if (point == DmsProvisioningHookPoint.BeforeTargetLock && Interlocked.Increment(ref locks) == 1)
            {
                paused.SetResult();
                await resume.Task;
            }
        };
        var deleteRequest = new DmsDataStoreDeleteRequest(
            created.Command.Identity.TargetName,
            created.Command.Identity.ExpectedSourceIdentity
        );
        var delayed = lane.Service.DeleteAsync(
            deleteRequest,
            endpoint,
            (actual, token) => AdmitManagedOperation(created.Command, old.Execution, true, actual, token),
            default
        );
        PublicationSeed replacement = created;
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var claim = await ReclaimAdmission(old.Claim);
            var execution = AdmissionExecution(claim);
            (
                await lane.Service.DeleteAsync(
                    deleteRequest,
                    endpoint,
                    (actual, token) => AdmitManagedOperation(created.Command, execution, true, actual, token),
                    default
                )
            )
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Dropped>();
            await PublicationRepository().CompleteDeleteAsync(old.Command, execution, default);
            await CompleteAdmissionJob(claim);
            replacement = await SeedPublication(
                physical: created.Command with
                {
                    Identity = created.Command.Identity with { ExpectedSourceIdentity = Guid.NewGuid() },
                }
            );
            (
                await lane.Service.CreateAsync(
                    AdmissionRequest(replacement),
                    endpoint,
                    (actual, token) =>
                        AdmitManagedOperation(
                            replacement.Command,
                            replacement.Execution,
                            false,
                            actual,
                            token
                        ),
                    default
                )
            )
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>();
            await PublicationRepository()
                .PublishCreateAsync(replacement.Command, replacement.Execution, default);
        }
        finally
        {
            resume.TrySetResult();
            await SettleAdmissionTask(delayed);
        }
        Func<Task> staleDelete = () => delayed;
        await staleDelete.Should().ThrowAsync<JobLeaseLostException>();
        lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(1);
        var present = (await Engine().InspectAsync(replacement.Command.Identity.TargetName, default))
            .Should()
            .BeOfType<DmsLiveTargetState.Present>()
            .Subject;
        var opened = (
            await Engine()
                .OpenSessionAsync(replacement.Command.Identity.TargetName, present.Instance, default)
        )
            .Should()
            .BeOfType<DmsSessionOpenResult.Opened>()
            .Subject.Session;
        await using (opened)
        {
            (await Engine().ReadIdentityAsync(opened, default))
                .Should()
                .Be(new DmsIdentityRead.Found(replacement.Command.Identity.ExpectedSourceIdentity));
        }
        lane.OperationEntries.Should().BeEmpty();
    }

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_checks_persisted_identity_under_a_real_fence_with_a_fake_physical_engine(
        [Values(
            "valid",
            "tenant",
            "managedId",
            "operation",
            "job",
            "source",
            "endpoint",
            "status",
            "lease",
            "timeout",
            "cancellation",
            "cms-outage"
        )]
            string mismatch
    )
    {
        var seed = await SeedDeletion();
        var command = new ManagedDataStorePublishCommand(
            seed.Command.Id,
            seed.Command.Identity,
            seed.Command.PinnedArtifact,
            PublicationRuntime
        );
        var execution = seed.Execution;
        await using var connection = await Open();
        switch (mismatch)
        {
            case "tenant":
                execution = execution with { TenantContext = new TenantContext.Multitenant(999, "wrong") };
                break;
            case "managedId":
                command = command with { Id = int.MaxValue };
                break;
            case "operation":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Status" = 'Created' WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                break;
            case "job":
                execution = execution with { JobId = "wrong" };
                break;
            case "source":
                command = command with
                {
                    Identity = command.Identity with { ExpectedSourceIdentity = Guid.NewGuid() },
                };
                break;
            case "endpoint":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "EndpointDescriptor" = @Endpoint WHERE "Id" = @Id;""",
                    new
                    {
                        command.Id,
                        Endpoint = (command.Identity.Endpoint with { Host = "other" }).CanonicalEncoding,
                    }
                );
                break;
            case "status":
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Status" = 'DeleteFailed' WHERE "Id" = @Id;""",
                    new { command.Id }
                );
                break;
            case "lease":
                await ReclaimAdmission(seed.Claim);
                break;
        }
        using CancellationTokenSource caller = new();
        Exception outage = new IOException("Injected CMS session-open failure");
        if (mismatch is "cancellation" or "cms-outage")
        {
            var factory = new MssqlJobFenceFactory(PublicationOptions, PublicationTimings)
            {
                SessionHooks = new JobDatabaseSessionHooks
                {
                    BeforeOperation = (operation, _) =>
                    {
                        if (mismatch == "cms-outage" && operation == "Open")
                        {
                            throw outage;
                        }
                        if (mismatch == "cancellation" && operation == "Work")
                        {
                            caller.Cancel();
                        }
                        return Task.CompletedTask;
                    },
                },
            };
            execution = execution with { Fence = factory.Create(seed.Claim, new()) };
        }
        string before = await DeleteAcceptanceSnapshot();
        await using var blocker = await Open();
        await using var blockingTransaction = await blocker.BeginTransactionAsync();
        if (mismatch == "timeout")
        {
            await blocker.ExecuteScalarAsync<long>(
                """SELECT "Id" FROM dmscs."Job" WITH (UPDLOCK, HOLDLOCK) WHERE "Id" = @Id;""",
                new { seed.Claim.Id },
                blockingTransaction
            );
        }
        var engine = A.Fake<IDmsTargetEngine>();
        A.CallTo(() => engine.Provider).Returns(command.Identity.Endpoint.Provider);
        var holder = A.Fake<IDmsTargetLockHolder>();
        var admin = A.Fake<IDmsAdminConnectionSource>();
        var readiness = A.Fake<IDmsDataStoreProvisionerReadiness>();
        A.CallTo(() => readiness.Current)
            .Returns(new DmsProvisionerReadiness.Ready("mssql", DateTimeOffset.UtcNow));
        A.CallTo(() => admin.ResolveAsync(A<CancellationToken>._))
            .Returns(new DmsAdminConnectionResolution.Resolved(new("fake CMS contract endpoint")));
        A.CallTo(() => engine.ParseManagedEndpoint(A<DmsAdminConnectionString>._))
            .Returns(command.Identity.Endpoint);
        A.CallTo(() =>
                engine.AcquireTargetLockAsync(
                    A<DmsTargetDatabaseName>._,
                    A<DmsOperationId>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new DmsTargetLockResult.Acquired(holder, command.Identity.Endpoint));
        A.CallTo(() => engine.ReadServerFactsAsync(A<CancellationToken>._))
            .Returns(new DmsServerFacts(18, null, null));
        A.CallTo(() => engine.InspectAsync(A<DmsDatabaseName>._, A<CancellationToken>._))
            .Returns(new DmsLiveTargetState.Absent());
        var service = new DmsDataStoreProvisioningService(
            readiness,
            engine,
            A.Fake<EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog.ITemplateArtifactCatalog>(),
            Options.Create(new DmsDataStoreProvisioningOptions()),
            ProtectedDatabaseNames.Create("cms", []),
            admin,
            TimeProvider.System,
            NullLogger<DmsDataStoreProvisioningService>.Instance
        );
        int admissions = 0;
        Exception? observed = null;
        Task<DmsDataStoreDeleteResult> Act() =>
            service.DeleteAsync(
                new(command.Identity.TargetName, command.Identity.ExpectedSourceIdentity),
                command.Identity.Endpoint,
                async (actual, token) =>
                {
                    admissions++;
                    try
                    {
                        await AdmitManagedOperation(command, execution, true, actual, token);
                    }
                    catch (Exception exception)
                    {
                        observed = exception;
                        throw;
                    }
                },
                caller.Token
            );
        if (mismatch == "valid")
        {
            (await Act()).Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();
        }
        else
        {
            Func<Task> action = () => Act();
            var thrown = (await action.Should().ThrowAsync<Exception>()).Which;
            thrown.Should().BeSameAs(observed);
            if (mismatch == "lease")
            {
                thrown.Should().BeOfType<JobLeaseLostException>();
            }
            else if (mismatch == "timeout")
            {
                thrown.Should().BeOfType<JobFenceUnavailableException>();
            }
            else if (mismatch == "cancellation")
            {
                thrown.Should().BeAssignableTo<OperationCanceledException>();
            }
            else if (mismatch == "cms-outage")
            {
                thrown.Should().BeSameAs(outage);
            }
            else
            {
                thrown.Should().BeOfType<InvalidOperationException>();
            }
            A.CallTo(() => engine.ReadServerFactsAsync(A<CancellationToken>._)).MustNotHaveHappened();
            A.CallTo(() => engine.InspectAsync(A<DmsDatabaseName>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
        await blockingTransaction.RollbackAsync();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
        admissions.Should().Be(1);
        A.CallTo(() => holder.DisposeAsync()).MustHaveHappenedOnceExactly();
    }

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_aborts_when_the_actual_holder_is_lost_during_admission_or_before_drop(
        [Values(false, true)] bool duringAdmission
    )
    {
        var endpoint = RequireAdmissionEndpoint();
        LaneHooks hooks = new();
        await using Lane lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), hooks);
        var seed = await SeedPhysicalAdmission(lane, endpoint);
        await using TestDatabase cleanup = new(seed.Command.Identity.TargetName.Value);
        DmsOperationId operation = default;
        hooks.Probe = (point, current) =>
        {
            if (point == DmsProvisioningHookPoint.BeforeTargetLock)
            {
                operation = current;
            }
            return Task.CompletedTask;
        };
        if (duringAdmission)
        {
            string before = await DeleteAcceptanceSnapshot();
            Exception? observed = null;
            Func<Task> action = () =>
                lane.Service.CreateAsync(
                    AdmissionRequest(seed),
                    endpoint,
                    async (actual, token) =>
                    {
                        await AdmitManagedOperation(seed.Command, seed.Execution, false, actual, token);
                        await KillAdmissionHolder(operation);
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token)
                                .WaitAsync(TimeSpan.FromSeconds(30));
                        }
                        catch (Exception exception)
                        {
                            observed = exception;
                            throw;
                        }
                    },
                    default
                );
            (await action.Should().ThrowAsync<OperationCanceledException>())
                .Which.Should()
                .BeSameAs(observed);
            lane.Engine.CountOf(nameof(IDmsTargetEngine.ReadServerFactsAsync)).Should().Be(0);
            lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)).Should().Be(0);
            (await Engine().InspectAsync(seed.Command.Identity.TargetName, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Absent>();
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        else
        {
            (
                await lane.Service.CreateAsync(
                    AdmissionRequest(seed),
                    endpoint,
                    (actual, token) =>
                        AdmitManagedOperation(seed.Command, seed.Execution, false, actual, token),
                    default
                )
            )
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>();
            await PublicationRepository().PublishCreateAsync(seed.Command, seed.Execution, default);
            var deletion = await AcceptAdmissionDelete(seed);
            var original = (await Engine().InspectAsync(seed.Command.Identity.TargetName, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;
            hooks.Probe = async (point, current) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeDrop)
                {
                    await KillAdmissionHolder(current);
                }
            };
            var result = await lane.Service.DeleteAsync(
                new(seed.Command.Identity.TargetName, seed.Command.Identity.ExpectedSourceIdentity),
                endpoint,
                (actual, token) =>
                    AdmitManagedOperation(seed.Command, deletion.Execution, true, actual, token),
                default
            );
            // Loss after the target was fenced leaves operator-inspection residue; a successor
            // cannot automatically unfence it. The separate transfer test loses the holder before fencing.
            result
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail.Category.Should()
                .Be(DmsProvisioningFailureCategory.OperatorInspectionRequired);
            var remaining = await Engine().InspectAsync(seed.Command.Identity.TargetName, default);
            DmsInstanceIdentity instance = remaining switch
            {
                DmsLiveTargetState.Present present => present.Instance,
                DmsLiveTargetState.Fenced fenced => fenced.Instance,
                _ => throw new AssertionException(
                    "The old worker must not drop the target after holder loss."
                ),
            };
            instance.IsSameStableInstance(original).Should().BeTrue();
            await using var connection = await Open();
            (await DeleteRow(connection, seed.Command.Id))["Status"].Should().Be("DeleteInProgress");
        }
        lane.OperationEntries.Should().BeEmpty();
    }

    private static async Task KillAdmissionHolder(DmsOperationId operation)
    {
        int session = await SessionIdNamedAsync(operation.HolderApplicationName);
        session.Should().NotBe(0);
        await KillAsync(session);
        (
            await WaitUntilAsync(
                async () => await SessionCountNamedAsync(operation.HolderApplicationName) == 0,
                TimeSpan.FromSeconds(30)
            )
        )
            .Should()
            .BeTrue();
    }

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_keeps_a_reclaimed_delete_waiting_until_holder_loss_then_preserves_reused_target()
    {
        var endpoint = RequireAdmissionEndpoint();
        LaneHooks hooks = new();
        await using Lane lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), hooks);
        var created = await SeedPhysicalAdmission(lane, endpoint);
        await using TestDatabase cleanup = new(created.Command.Identity.TargetName.Value);
        (
            await lane.Service.CreateAsync(
                AdmissionRequest(created),
                endpoint,
                (actual, token) =>
                    AdmitManagedOperation(created.Command, created.Execution, false, actual, token),
                default
            )
        )
            .Should()
            .BeOfType<DmsDataStoreCreateResult.Created>();
        await PublicationRepository().PublishCreateAsync(created.Command, created.Execution, default);
        var old = await AcceptAdmissionDelete(created);
        TaskCompletionSource<DmsOperationId> admitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<DmsOperationId> waitingForLock = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int admissions = 0;
        hooks.Probe = async (point, operation) =>
        {
            if (point == DmsProvisioningHookPoint.BeforeTargetLock && admitted.Task.IsCompleted)
            {
                waitingForLock.TrySetResult(operation);
            }
            if (
                point == DmsProvisioningHookPoint.AfterAdmission
                && Interlocked.Increment(ref admissions) == 1
            )
            {
                admitted.SetResult(operation);
                await resume.Task;
            }
        };
        DmsDataStoreDeleteRequest request = new(
            created.Command.Identity.TargetName,
            created.Command.Identity.ExpectedSourceIdentity
        );
        // Pause after admission but before fencing, so the successor can complete deletion once
        // the physical holder is gone. Loss at BeforeDrop is separately tested as inspection-required.
        var delayed = lane.Service.DeleteAsync(
            request,
            endpoint,
            (actual, token) => AdmitManagedOperation(created.Command, old.Execution, true, actual, token),
            default
        );
        Task<DmsDataStoreDeleteResult>? second = null;
        PublicationSeed replacement = created;
        DmsInstanceIdentity? replacementInstance = null;
        try
        {
            DmsOperationId oldOperation = await admitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var claim = await ReclaimAdmission(old.Claim);
            var execution = AdmissionExecution(claim);
            second = lane.Service.DeleteAsync(
                request,
                endpoint,
                (actual, token) => AdmitManagedOperation(created.Command, execution, true, actual, token),
                default
            );
            DmsOperationId waiting = await waitingForLock.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (
                await WaitUntilAsync(
                    async () =>
                        await ScalarOnServerAsync<int>(
                            "SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.program_name = @app AND r.wait_type LIKE 'LCK%';",
                            ("app", waiting.HolderApplicationName)
                        ) > 0,
                    TimeSpan.FromSeconds(3)
                )
            )
                .Should()
                .BeTrue();
            second.IsCompleted.Should().BeFalse();
            admissions.Should().Be(1);
            await KillAdmissionHolder(oldOperation);
            (await second).Should().BeOfType<DmsDataStoreDeleteResult.Dropped>();
            await PublicationRepository().CompleteDeleteAsync(old.Command, execution, default);
            await CompleteAdmissionJob(claim);
            replacement = await SeedPublication(
                physical: created.Command with
                {
                    Identity = created.Command.Identity with { ExpectedSourceIdentity = Guid.NewGuid() },
                }
            );
            (
                await lane.Service.CreateAsync(
                    AdmissionRequest(replacement),
                    endpoint,
                    (actual, token) =>
                        AdmitManagedOperation(
                            replacement.Command,
                            replacement.Execution,
                            false,
                            actual,
                            token
                        ),
                    default
                )
            )
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>();
            await PublicationRepository()
                .PublishCreateAsync(replacement.Command, replacement.Execution, default);
            replacementInstance = (await Engine().InspectAsync(request.TargetName, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;
        }
        finally
        {
            resume.TrySetResult();
            await SettleAdmissionTask(delayed);
            if (second is not null)
            {
                await SettleAdmissionTask(second);
            }
        }
        (await delayed).Should().BeOfType<DmsDataStoreDeleteResult.Failure>();
        Func<Task> staleFinalization = () =>
            PublicationRepository().CompleteDeleteAsync(old.Command, old.Execution, default);
        await staleFinalization.Should().ThrowAsync<JobLeaseLostException>();
        var present = (await Engine().InspectAsync(request.TargetName, default))
            .Should()
            .BeOfType<DmsLiveTargetState.Present>()
            .Subject;
        present.Instance.IsSameStableInstance(replacementInstance!).Should().BeTrue();
        var opened = (await Engine().OpenSessionAsync(request.TargetName, present.Instance, default))
            .Should()
            .BeOfType<DmsSessionOpenResult.Opened>()
            .Subject.Session;
        await using (opened)
        {
            (await Engine().ReadIdentityAsync(opened, default))
                .Should()
                .Be(new DmsIdentityRead.Found(replacement.Command.Identity.ExpectedSourceIdentity));
        }
        lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(1);
        lane.OperationEntries.Should().BeEmpty();
    }

    [Test, Category("DataStoreManageAdmission")]
    public async Task It_cannot_mark_an_external_replacement_after_losing_the_holder_with_a_marker_session_open()
    {
        var endpoint = RequireAdmissionEndpoint();
        LaneHooks hooks = new();
        await using Lane lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), hooks);
        var seed = await SeedPhysicalAdmission(lane, endpoint);
        var target = seed.Command.Identity.TargetName;
        await using TestDatabase cleanup = new(target.Value);
        TaskCompletionSource<DmsOperationId> atMarker = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.Probe = async (point, operation) =>
        {
            if (point == DmsProvisioningHookPoint.BeforeMarkerDispatch)
            {
                atMarker.SetResult(operation);
                await resume.Task;
            }
        };
        var delayed = lane.Service.CreateAsync(
            AdmissionRequest(seed),
            endpoint,
            (actual, token) => AdmitManagedOperation(seed.Command, seed.Execution, false, actual, token),
            default
        );
        Guid replacementIdentity = Guid.NewGuid();
        DmsInstanceIdentity? replacementInstance = null;
        try
        {
            DmsOperationId operation = await atMarker.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (await cleanup.EngineSessionCountAsync())
                .Should()
                .BeGreaterThan(0, "the marker work session is already open");
            await KillAdmissionHolder(operation);
            var acquired = (await Engine().AcquireTargetLockAsync(target, DmsOperationId.New(), default))
                .Should()
                .BeOfType<DmsTargetLockResult.Acquired>()
                .Subject;
            acquired.Endpoint.Should().Be(endpoint);
            await using (acquired.Holder)
            {
                // An explicit external replacement, not managed acceptance: the original aggregate still
                // owns its CMS reservation. The replacement holds the physical lock throughout.
                await ExecuteOnServerAsync(
                    $"ALTER DATABASE {MssqlIdentifiers.Quote(target.Value)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {MssqlIdentifiers.Quote(target.Value)};"
                );
                await ExecuteOnServerAsync($"CREATE DATABASE {MssqlIdentifiers.Quote(target.Value)};");
                await cleanup.ExecuteAsync(
                    await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql"))
                );
                await cleanup.ExecuteAsync(
                    $"UPDATE dms.DataStoreIdentity SET SourceIdentity = '{replacementIdentity:D}'; CREATE TABLE dbo.admission_sentinel (value int); INSERT INTO dbo.admission_sentinel VALUES (1439);"
                );
                replacementInstance = (await Engine().InspectAsync(target, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Present>()
                    .Subject.Instance;
                (await cleanup.EngineSessionCountAsync())
                    .Should()
                    .Be(0, "dropping the old instance terminated its marker session");
            }
        }
        finally
        {
            resume.TrySetResult();
            await SettleAdmissionTask(delayed);
        }
        (await delayed).Should().BeOfType<DmsDataStoreCreateResult.Failure>();
        var present = (await Engine().InspectAsync(target, default))
            .Should()
            .BeOfType<DmsLiveTargetState.Present>()
            .Subject;
        present.Instance.IsSameStableInstance(replacementInstance!).Should().BeTrue();
        (await cleanup.ScalarAsync<Guid>("SELECT SourceIdentity FROM dms.DataStoreIdentity;"))
            .Should()
            .Be(replacementIdentity);
        (await cleanup.ScalarAsync<int>("SELECT value FROM dbo.admission_sentinel;")).Should().Be(1439);
        await WaitForNoSessionsOnAsync(target.Value);
        lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(0);
        lane.OperationEntries.Should().BeEmpty();
        await using var connection = await Open();
        (await DeleteRow(connection, seed.Command.Id))["Status"].Should().Be("CreateInProgress");
    }
}
