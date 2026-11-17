// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;
using Worker = EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores.ManagedDataStoreWorkerProcess;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [TestCase(false), TestCase(true)]
    [Category("DataStoreManageTransientRecovery"), Category("DmsProvisioning")]
    public async Task It_retries_a_real_target_lock_timeout_in_the_same_worker_after_scheduled_backoff(
        bool delete
    )
    {
        _ = RequireAdmissionEndpoint();
        if (!RealTemplatePackage.IsConfigured)
        {
            DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                "DMS_TEMPLATE_PACKAGE_DIRECTORY is required for managed transient qualification."
            );
        }
        var package = RealTemplatePackage.Find("postgresql", RestoreManifest.MinimalTemplateKind);
        await using var lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, new LaneHooks());
        string login = await CreateLiveRuntimeLogin();
        string runtime = LiveRuntimeConnection(login);
        TestDatabase? targetCleanup = null;
        IDmsTargetLockHolder? holder = null;
        try
        {
            using var host = LiveRecoveryHost(lane, runtime);
            using var client = ManagedClient(host);
            var response = await client.PostAsJsonAsync(
                "/v3/dataStores/manage",
                new { name = "Retry" + Guid.NewGuid().ToString("N")[..12], databaseTemplate = "Minimal" }
            );
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            int id = int.Parse(response.Headers.Location!.Segments[^1]);
            var accepted = await LiveRow(id);
            var identity = accepted.Restore(ProtectedDatabaseNames.Create("cms", []));
            targetCleanup = new TestDatabase(identity.TargetName.Value);
            if (delete)
            {
                // Only the prerequisite create is manually claimed; the contested operation and retry
                // are dispatched, renewed and settled by the production worker in a separate process.
                var createClaim = await LiveClaim(host, accepted.CreateJobId, 1);
                (await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(createClaim, default))
                    .Outcome.Should()
                    .Be(JobExecutionOutcome.Completed);
                (await client.DeleteAsync($"/v3/dataStores/manage/{id}"))
                    .StatusCode.Should()
                    .Be(HttpStatusCode.Accepted);
            }
            var pending = await LiveRow(id);
            string jobId = delete ? pending.DeleteJobId! : pending.CreateJobId;
            var physicalBefore = await lane.Engine.InspectAsync(identity.TargetName, default);
            if (delete)
            {
                physicalBefore.Should().BeOfType<DmsLiveTargetState.Present>();
            }
            else
            {
                physicalBefore.Should().BeOfType<DmsLiveTargetState.Absent>();
            }

            var held = (
                await Engine().AcquireTargetLockAsync(identity.TargetName, DmsOperationId.New(), default)
            )
                .Should()
                .BeOfType<DmsTargetLockResult.Acquired>()
                .Subject;
            holder = held.Holder;
            await using var worker = await Worker.StartAsync(
                typeof(Given_data_store_namespace_reservations).Assembly.Location,
                new()
                {
                    CatalogConnection = _database.ConnectionString,
                    RuntimeConnection = runtime,
                    Provisioning = lane.WithEngine(Engine()).Options,
                    ManageId = id,
                    JobId = jobId,
                    Delete = delete,
                    MaxAttempts = 2,
                    StopAt = Worker.StopPoint.None,
                    ContinueAfterRetry = true,
                }
            );
            var retried = await worker.ReadAsync();
            retried.Kind.Should().Be("JobRetryScheduled");
            retried.Attempt.Should().Be(1);
            retried.Claims.Should().Be(1);
            holder.LostToken.IsCancellationRequested.Should().BeFalse();

            var failed = await LiveRow(id);
            failed.Status.Should().Be(delete ? "DeleteFailed" : "CreateFailed");
            failed.LastAttempt.Should().Be(1);
            failed.LastFailureCategory.Should().Be("Transient");
            failed.LastFailureProvisioningCode.Should().Be("LockTimeout");
            failed.LastFailureJobCode.Should().BeNull();
            failed.DataStoreId.Should().Be(pending.DataStoreId);
            failed
                .Restore(ProtectedDatabaseNames.Create("cms", []))
                .PinnedArtifact.Should()
                .Be(lane.Request.PinnedArtifact);
            failed.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
            (await lane.Engine.InspectAsync(identity.TargetName, default)).Should().Be(physicalBefore);
            await using var catalog = await Open();
            var retryJob = await catalog.QuerySingleAsync<(
                string Status,
                int AttemptCount,
                long FencingToken,
                DateTime NextAttemptAt,
                DateTime LastModifiedAt,
                DateTime? FinishedAt,
                string? LeaseOwner,
                DateTime? LeaseExpiresAt
            )>(
                """SELECT "Status", "AttemptCount", "FencingToken", "NextAttemptAt", "LastModifiedAt", "FinishedAt", "LeaseOwner", "LeaseExpiresAt" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );
            retryJob.Status.Should().Be("Pending");
            retryJob.AttemptCount.Should().Be(1);
            retryJob.FinishedAt.Should().BeNull();
            retryJob.LeaseOwner.Should().BeNull();
            retryJob.LeaseExpiresAt.Should().BeNull();
            failed.UpdatedAt.Should().Be(retryJob.LastModifiedAt);
            (retryJob.NextAttemptAt - retryJob.LastModifiedAt).Should().Be(new JobOptions().RetryBackoffBase);
            var databaseNow = await catalog.ExecuteScalarAsync<DateTime>(
                "SELECT clock_timestamp() AT TIME ZONE 'UTC';"
            );
            retryJob.NextAttemptAt.Should().BeAfter(databaseNow);
            (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(delete ? 1 : 0);

            // Release only after the durable paired failure is observed. No due-time/lease edits,
            // manual claim, shortened backoff or host restart: this same worker waits for eligibility.
            await holder.DisposeAsync();
            holder = null;
            var completed = await worker.ReadAsync();
            completed.Kind.Should().Be("JobCompleted");
            completed.ProcessId.Should().Be(retried.ProcessId);
            completed.Attempt.Should().Be(2);
            completed.Claims.Should().Be(2);
            await worker.RequireSuccessfulExitAsync();
            var final = await LiveRow(id);
            final.Status.Should().Be(delete ? "Deleted" : "Created");
            final.LastAttempt.Should().Be(2);
            final.LastFailureCategory.Should().BeNull();
            final.LastFailureProvisioningCode.Should().BeNull();
            final.LastFailureJobCode.Should().BeNull();
            final.CreateJobId.Should().Be(pending.CreateJobId);
            final.DeleteJobId.Should().Be(pending.DeleteJobId);
            final.TargetDatabaseName.Should().Be(pending.TargetDatabaseName);
            final.EndpointDescriptor.Should().Be(pending.EndpointDescriptor);
            final.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
            final
                .Restore(ProtectedDatabaseNames.Create("cms", []))
                .PinnedArtifact.Should()
                .Be(lane.Request.PinnedArtifact);
            if (delete)
            {
                final.DataStoreId.Should().BeNull();
                final.HistoricalDataStoreId.Should().Be(pending.DataStoreId);
                (await lane.Engine.InspectAsync(identity.TargetName, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Absent>();
            }
            else
            {
                final.DataStoreId.Should().NotBeNull();
                (await catalog.ExecuteScalarAsync<int>("""SELECT "Id" FROM dmscs."DataStore";"""))
                    .Should()
                    .Be(final.DataStoreId);
                var present = (await Engine().InspectAsync(identity.TargetName, default))
                    .Should()
                    .BeOfType<DmsLiveTargetState.Present>()
                    .Subject;
                var opened = (await Engine().OpenSessionAsync(identity.TargetName, present.Instance, default))
                    .Should()
                    .BeOfType<DmsSessionOpenResult.Opened>()
                    .Subject.Session;
                await using (opened)
                {
                    (await Engine().ReadIdentityAsync(opened, default))
                        .Should()
                        .Be(new DmsIdentityRead.Found(accepted.ExpectedSourceIdentity));
                }
            }
            (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(delete ? 0 : 1);
            var finishedJob = await catalog.QuerySingleAsync<(
                string Status,
                int AttemptCount,
                long FencingToken,
                DateTime NextAttemptAt,
                DateTime FinishedAt
            )>(
                """SELECT "Status", "AttemptCount", "FencingToken", "NextAttemptAt", "FinishedAt" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );
            finishedJob.Status.Should().Be("Completed");
            finishedJob.AttemptCount.Should().Be(2);
            finishedJob.FencingToken.Should().Be(retryJob.FencingToken + 1);
            finishedJob.NextAttemptAt.Should().Be(retryJob.NextAttemptAt);
            finishedJob.FinishedAt.Should().BeOnOrAfter(retryJob.NextAttemptAt);
            lane.OperationEntries.Should().BeEmpty();
        }
        finally
        {
            if (holder is not null)
            {
                await holder.DisposeAsync();
            }
            if (targetCleanup is not null)
            {
                await targetCleanup.DisposeAsync();
            }
            await DropLiveRuntimeLogin(login);
        }
    }

    [
        TestCase(
            false,
            false,
            TestName = "It_retries_a_withheld_package_before_pinning_and_completes_the_same_job"
        ),
        TestCase(
            true,
            false,
            TestName = "It_retries_a_withheld_attestation_on_an_owned_target_and_reconciles_once"
        ),
        TestCase(false, true, TestName = "It_exhausts_a_package_that_stays_withheld_and_does_not_revive")
    ]
    [Category("DataStoreManageTransientRecovery"), Category("DmsProvisioning")]
    public async Task It_retries_a_temporary_artifact_outage_on_a_ready_host(
        bool afterPhysicalCreate,
        bool permanent
    )
    {
        _ = RequireAdmissionEndpoint();
        if (!RealTemplatePackage.IsConfigured)
        {
            DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                "DMS_TEMPLATE_PACKAGE_DIRECTORY is required for managed transient qualification."
            );
        }
        var package = RealTemplatePackage.Find("postgresql", RestoreManifest.MinimalTemplateKind);
        await using var lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, new LaneHooks());
        string login = await CreateLiveRuntimeLogin();
        string runtime = LiveRuntimeConnection(login);
        // Only this lane's private allowlisted copy is withheld, never a shared package directory.
        string artifact = afterPhysicalCreate
            ? lane.PackagePath + TemplateArtifactCatalog.AttestationExtension
            : lane.PackagePath;
        string withheld = artifact + ".withheld";
        TestDatabase? targetCleanup = null;
        try
        {
            // One host, Ready from startup, accepts the create and runs every attempt below; it never restarts.
            using var host = LiveRecoveryHost(lane, runtime);
            using var client = ManagedClient(host);
            var response = await client.PostAsJsonAsync(
                "/v3/dataStores/manage",
                new { name = "Outage" + Guid.NewGuid().ToString("N")[..12], databaseTemplate = "Minimal" }
            );
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            int id = int.Parse(response.Headers.Location!.Segments[^1]);
            var accepted = await LiveRow(id);
            var target = accepted.Restore(ProtectedDatabaseNames.Create("cms", [])).TargetName;
            targetCleanup = new TestDatabase(target.Value);
            string jobId = accepted.CreateJobId;
            int attempt = 1;
            if (afterPhysicalCreate)
            {
                // The physical create succeeds and its publication rolls back: the target is owned, unpublished.
                using (var faulted = LiveRecoveryHost(lane, runtime, id))
                {
                    (
                        await faulted
                            .Services.GetRequiredService<JobExecutor>()
                            .ExecuteAsync(await LiveClaim(faulted, jobId, attempt), default)
                    )
                        .Outcome.Should()
                        .Be(JobExecutionOutcome.RetryScheduled);
                    ((LiveFinalizationFault)faulted.Services.GetRequiredService<IJobFenceFactory>())
                        .Fired.Should()
                        .BeTrue();
                }
                attempt++;
                await MakeLiveRetryDue(jobId);
            }
            var before = await LiveRow(id);
            var pinBefore = before.Restore(ProtectedDatabaseNames.Create("cms", [])).PinnedArtifact;
            pinBefore.Should().Be(afterPhysicalCreate ? lane.Request.PinnedArtifact : null);
            var physicalBefore = await lane.Engine.InspectAsync(target, default);
            physicalBefore
                .Should()
                .BeOfType(
                    afterPhysicalCreate
                        ? typeof(DmsLiveTargetState.Present)
                        : typeof(DmsLiveTargetState.Absent)
                );
            int maxAttempts = host.Services.GetRequiredService<IOptions<JobOptions>>().Value.MaxAttempts;
            await using var catalog = await Open();

            File.Move(artifact, withheld);
            try
            {
                while (true)
                {
                    bool final = attempt == maxAttempts;
                    (await Attempt(attempt))
                        .Should()
                        .Be(final ? JobExecutionOutcome.Failed : JobExecutionOutcome.RetryScheduled);
                    var failed = await LiveRow(id);
                    failed.CreateJobId.Should().Be(jobId);
                    failed.TargetDatabaseName.Should().Be(accepted.TargetDatabaseName);
                    failed.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
                    failed
                        .Restore(ProtectedDatabaseNames.Create("cms", []))
                        .PinnedArtifact.Should()
                        .Be(pinBefore);
                    failed.DataStoreId.Should().BeNull();
                    failed.LastAttempt.Should().Be(attempt);
                    (await lane.Engine.InspectAsync(target, default)).Should().Be(physicalBefore);
                    (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                        .Should()
                        .Be(0);
                    host.Services.GetRequiredService<IDmsDataStoreProvisionerReadiness>()
                        .Current.Should()
                        .BeOfType<DmsProvisionerReadiness.Ready>();
                    var job = await Job();
                    job.AttemptCount.Should().Be(attempt);
                    if (final)
                    {
                        failed.Status.Should().Be("CreateError");
                        failed.LastFailureCategory.Should().Be("OperatorInspectionRequired");
                        failed.LastFailureProvisioningCode.Should().BeNull();
                        failed.LastFailureJobCode.Should().Be("AttemptsExhausted");
                        job.Status.Should().Be("Error");
                        job.ErrorMessage.Should().Be(JobErrorCode.AttemptsExhausted.Message);
                        break;
                    }
                    failed.Status.Should().Be("CreateFailed");
                    failed.LastFailureCategory.Should().Be("Transient");
                    failed.LastFailureProvisioningCode.Should().Be("ArtifactMissing");
                    failed.LastFailureJobCode.Should().BeNull();
                    job.Status.Should().Be("Pending");
                    job.NextAttemptAt.Should()
                        .BeAfter(
                            await catalog.ExecuteScalarAsync<DateTime>(
                                "SELECT clock_timestamp() AT TIME ZONE 'UTC';"
                            )
                        );
                    attempt++;
                    await MakeLiveRetryDue(jobId);
                    if (!permanent)
                    {
                        break;
                    }
                }
            }
            finally
            {
                File.Move(withheld, artifact);
            }

            if (permanent)
            {
                // Restoring the artifact does not revive the terminal job or its reservation.
                (
                    await host
                        .Services.GetRequiredService<IJobLeaseRepository>()
                        .ClaimNext("live-recovery", 300, maxAttempts, default)
                )
                    .Should()
                    .BeOfType<JobClaimResult.NoneAvailable>();
                (await LiveRow(id)).Status.Should().Be("CreateError");
                (await lane.Engine.InspectAsync(target, default)).Should().Be(physicalBefore);
                return;
            }

            (await Attempt(attempt)).Should().Be(JobExecutionOutcome.Completed);
            var created = await LiveRow(id);
            created.Status.Should().Be("Created");
            created.CreateJobId.Should().Be(jobId);
            created.TargetDatabaseName.Should().Be(accepted.TargetDatabaseName);
            created.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
            created
                .Restore(ProtectedDatabaseNames.Create("cms", []))
                .PinnedArtifact.Should()
                .Be(lane.Request.PinnedArtifact);
            created.LastAttempt.Should().Be(attempt);
            created.LastFailureCategory.Should().BeNull();
            created.LastFailureProvisioningCode.Should().BeNull();
            (await catalog.ExecuteScalarAsync<int>("""SELECT "Id" FROM dmscs."DataStore";"""))
                .Should()
                .Be(created.DataStoreId);
            var present = (await lane.Engine.InspectAsync(target, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject;
            if (afterPhysicalCreate)
            {
                // Reconciled: the same physical instance, never a second create.
                present.Should().Be(physicalBefore);
            }
            var completed = await Job();
            completed.Status.Should().Be("Completed");
            completed.AttemptCount.Should().Be(attempt);
            lane.OperationEntries.Should().BeEmpty();

            async Task<JobExecutionOutcome> Attempt(int number) =>
                (
                    await host
                        .Services.GetRequiredService<JobExecutor>()
                        .ExecuteAsync(await LiveClaim(host, jobId, number), default)
                ).Outcome;

            Task<(string Status, int AttemptCount, DateTime NextAttemptAt, string? ErrorMessage)> Job() =>
                catalog.QuerySingleAsync<(
                    string Status,
                    int AttemptCount,
                    DateTime NextAttemptAt,
                    string? ErrorMessage
                )>(
                    """SELECT "Status", "AttemptCount", "NextAttemptAt", "ErrorMessage" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                    new { JobId = jobId }
                );
        }
        finally
        {
            if (targetCleanup is not null)
            {
                await targetCleanup.DisposeAsync();
            }
            await DropLiveRuntimeLogin(login);
        }
    }
}
