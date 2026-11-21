// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;
using Worker = EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores.ManagedDataStoreWorkerProcess;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [
        TestCase(false, false, false, TestName = "It_recovers_create_after_process_death_before_commit"),
        TestCase(false, true, false, TestName = "It_recovers_create_after_process_death_after_commit")
    ]
    [
        TestCase(true, false, false, TestName = "It_recovers_delete_after_process_death_before_commit"),
        TestCase(true, true, false, TestName = "It_recovers_delete_after_process_death_after_commit")
    ]
    [
        TestCase(false, false, true, TestName = "It_exhausts_create_after_process_death_before_commit"),
        TestCase(false, true, true, TestName = "It_exhausts_create_after_process_death_after_commit")
    ]
    [
        TestCase(true, false, true, TestName = "It_exhausts_delete_after_process_death_before_commit"),
        TestCase(true, true, true, TestName = "It_exhausts_delete_after_process_death_after_commit")
    ]
    [Category("DataStoreManageProcessRecovery"), Category("DmsProvisioning")]
    public async Task It_recovers_or_exhausts_after_a_worker_process_dies_at_finalization(
        bool delete,
        bool afterCommit,
        bool exhaust
    )
    {
        _ = RequireAdmissionEndpoint();
        if (!RealTemplatePackage.IsConfigured)
        {
            DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                "DMS_TEMPLATE_PACKAGE_DIRECTORY is required for managed process qualification."
            );
        }
        var package = RealTemplatePackage.Find("mssql", RestoreManifest.MinimalTemplateKind);
        await using var lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, new LaneHooks());
        string login = await CreateLiveRuntimeLogin();
        string runtime = LiveRuntimeConnection(login);
        TestDatabase? targetCleanup = null;
        try
        {
            using var host = LiveRecoveryHost(lane, runtime);
            using var client = ManagedClient(host);
            string name = "Crash" + Guid.NewGuid().ToString("N")[..12];
            var response = await client.PostAsJsonAsync(
                "/v3/dataStores/manage",
                new { name, databaseTemplate = "Minimal" }
            );
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            int id = int.Parse(response.Headers.Location!.Segments[^1]);
            var accepted = await LiveRow(id);
            var identity = accepted.Restore(ProtectedDatabaseNames.Create("cms", []));
            targetCleanup = new TestDatabase(identity.TargetName.Value);
            if (delete)
            {
                // Establish the real target/catalog prerequisite; the delete itself runs only in the child.
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
            Worker.Settings Settings(Worker.StopPoint stop, string? observe = null, bool closed = false) =>
                new()
                {
                    CatalogConnection = _database.ConnectionString,
                    RuntimeConnection = runtime,
                    Provisioning = lane.WithEngine(Engine()).Options,
                    ManageId = id,
                    JobId = observe ?? jobId,
                    Delete = delete,
                    MaxAttempts = exhaust ? 1 : 2,
                    StopAt = stop,
                    ClosedReadiness = closed,
                };
            string testAssembly = typeof(Given_data_store_namespace_reservations).Assembly.Location;
            Worker.Signal checkpoint;
            await using (
                var crashed = await Worker.StartAsync(
                    testAssembly,
                    Settings(afterCommit ? Worker.StopPoint.AfterCommit : Worker.StopPoint.BeforeCommit)
                )
            )
            {
                checkpoint = await crashed.ReadAsync();
                checkpoint.Kind.Should().Be("Checkpoint");
                checkpoint.Attempt.Should().Be(1);
                await crashed.TerminateAtCheckpointAsync();
            }

            string operation = delete ? "Delete" : "Create";
            string completedStatus = delete ? "Deleted" : "Created";
            var interrupted = await LiveRow(id);
            interrupted.Status.Should().Be(afterCommit ? completedStatus : operation + "InProgress");
            interrupted
                .Restore(ProtectedDatabaseNames.Create("cms", []))
                .PinnedArtifact.Should()
                .Be(lane.Request.PinnedArtifact);
            interrupted.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
            var physicalBeforeRecovery = await lane.Engine.InspectAsync(identity.TargetName, default);
            if (delete)
            {
                physicalBeforeRecovery.Should().BeOfType<DmsLiveTargetState.Absent>();
            }
            else
            {
                physicalBeforeRecovery.Should().BeOfType<DmsLiveTargetState.Present>();
            }
            await using var catalog = await Open();
            var interruptedJob = await catalog.QuerySingleAsync<(
                string Status,
                int AttemptCount,
                long FencingToken,
                DateTime? FinishedAt
            )>(
                """SELECT "Status", "AttemptCount", "FencingToken", "FinishedAt" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );
            interruptedJob.Status.Should().Be("InProgress");
            interruptedJob.AttemptCount.Should().Be(1);
            interruptedJob.FencingToken.Should().Be(checkpoint.Fence);
            interruptedJob.FinishedAt.Should().BeNull();
            (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(interrupted.Status is "Created" or "DeleteInProgress" ? 1 : 0);

            // The child is dead. Advance this lease's database deadline, not attempts or outcomes,
            // so the real restarted worker's first sweep/claim runs without a five-minute sleep.
            (
                await catalog.ExecuteAsync(
                    """UPDATE dmscs."Job" SET "LeaseExpiresAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "JobId" = @JobId AND "Status" = 'InProgress' AND "FencingToken" = @Fence;""",
                    new { JobId = jobId, checkpoint.Fence }
                )
            )
                .Should()
                .Be(1);

            // A restart while startup readiness is closed must neither claim, attempt nor exhaust the interrupted
            // job, while the same worker still settles unrelated work queued behind it.
            string unrelated = Guid.NewGuid().ToString("N");
            await catalog.ExecuteAsync(
                """
                INSERT INTO dmscs."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "CreatedAt", "NextAttemptAt", "AttemptCount")
                VALUES (@Id, 'Unrelated.Unknown', 1, '{}', 'Pending', SYSUTCDATETIME(), SYSUTCDATETIME(), 0);
                """,
                new { Id = unrelated }
            );
            string paused = await ManagedSnapshot();
            await using (
                var closed = await Worker.StartAsync(
                    testAssembly,
                    Settings(Worker.StopPoint.None, observe: unrelated, closed: true)
                )
            )
            {
                var progressed = await closed.ReadAsync();
                progressed.Kind.Should().Be("JobFailed");
                progressed.ProcessId.Should().NotBe(checkpoint.ProcessId);
                await closed.RequireSuccessfulExitAsync();
            }
            (await ManagedSnapshot()).Should().Be(paused);
            (await lane.Engine.InspectAsync(identity.TargetName, default))
                .Should()
                .Be(physicalBeforeRecovery);
            (
                await catalog.ExecuteScalarAsync<string>(
                    """SELECT "ErrorMessage" FROM dmscs."Job" WHERE "JobId" = @Id AND "Status" = 'Error';""",
                    new { Id = unrelated }
                )
            )
                .Should()
                .Be(JobErrorCode.UnsupportedJobType.Message);

            Worker.Signal settled;
            await using (
                var restarted = await Worker.StartAsync(testAssembly, Settings(Worker.StopPoint.None))
            )
            {
                settled = await restarted.ReadAsync();
                // On its last attempt, a committed success is completed by the sweep, never exhausted.
                string expectedKind = (exhaust, afterCommit) switch
                {
                    (false, _) => "JobCompleted",
                    (true, true) => "ManagedJobsSettled",
                    (true, false) => "JobsExhausted",
                };
                settled.Kind.Should().Be(expectedKind);
                settled.Claims.Should().Be(exhaust ? 0 : 1);
                if (!exhaust)
                {
                    settled.Attempt.Should().Be(2);
                }
                settled.ProcessId.Should().NotBe(checkpoint.ProcessId);
                await restarted.RequireSuccessfulExitAsync();
            }
            await TestContext.Progress.WriteLineAsync(
                $"Worker {checkpoint.ProcessId} terminated; fresh worker {settled.ProcessId}: {settled.Kind}."
            );

            var final = await LiveRow(id);
            final.Status.Should().Be(exhaust && !afterCommit ? operation + "Error" : completedStatus);
            final.ExpectedSourceIdentity.Should().Be(accepted.ExpectedSourceIdentity);
            final
                .Restore(ProtectedDatabaseNames.Create("cms", []))
                .PinnedArtifact.Should()
                .Be(lane.Request.PinnedArtifact);
            (await lane.Engine.InspectAsync(identity.TargetName, default))
                .Should()
                .Be(physicalBeforeRecovery);
            if (afterCommit)
            {
                // Both reclaim and exhaustion preserve every completed lifecycle field/timestamp/link.
                JsonSerializer.Serialize(final).Should().Be(JsonSerializer.Serialize(interrupted));
            }
            else if (exhaust)
            {
                final.LastFailureCategory.Should().Be("OperatorInspectionRequired");
                final.LastFailureJobCode.Should().Be("AttemptsExhausted");
                final.LastAttempt.Should().Be(1);
                final.DataStoreId.Should().Be(pending.DataStoreId);
                var duplicate = await client.PostAsJsonAsync(
                    "/v3/dataStores/manage",
                    new { name, databaseTemplate = "Minimal" }
                );
                duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await duplicate.Content.ReadAsStringAsync())
                    .Should()
                    .Contain("A data store with this name already exists.");
            }
            else
            {
                final.LastAttempt.Should().Be(2);
            }
            int expectedRows = final.Status is "Created" or "DeleteError" ? 1 : 0;
            (await catalog.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStore";"""))
                .Should()
                .Be(expectedRows);
            var finishedJob = await catalog.QuerySingleAsync<(
                string Status,
                int AttemptCount,
                long FencingToken,
                string? ErrorMessage,
                DateTime? FinishedAt
            )>(
                """SELECT "Status", "AttemptCount", "FencingToken", "ErrorMessage", "FinishedAt" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );
            bool failed = exhaust && !afterCommit;
            finishedJob.Status.Should().Be(failed ? "Error" : "Completed");
            finishedJob.AttemptCount.Should().Be(exhaust ? 1 : 2);
            finishedJob.ErrorMessage.Should().Be(failed ? JobErrorCode.AttemptsExhausted.Message : null);
            finishedJob.FinishedAt.Should().NotBeNull();
            // Both reclamation and exhaustion invalidate the dead owner's fence.
            finishedJob.FencingToken.Should().Be(checkpoint.Fence + 1);
            lane.OperationEntries.Should().BeEmpty();

            // The returned job ID and the managed resource report the same public outcome.
            using (var polled = await client.GetAsync($"/v3/jobs/{jobId}"))
            {
                polled.StatusCode.Should().Be(HttpStatusCode.OK);
                var job = JsonNode.Parse(await polled.Content.ReadAsStringAsync())!;
                job["status"]!.GetValue<string>().Should().Be(failed ? "Error" : "Completed");
                (job["errorMessage"]?.GetValue<string>())
                    .Should()
                    .Be(failed ? JobErrorCode.AttemptsExhausted.Message : null);
            }
            using (var managed = await client.GetAsync($"/v3/dataStores/manage/{id}"))
            {
                managed.StatusCode.Should().Be(HttpStatusCode.OK);
                JsonNode.Parse(await managed.Content.ReadAsStringAsync())!["status"]!
                    .GetValue<string>()
                    .Should()
                    .Be(final.Status);
            }

            async Task<string> ManagedSnapshot() =>
                JsonSerializer.Serialize(
                    new
                    {
                        Row = await LiveRow(id),
                        Job = (
                            (IDictionary<string, object>)
                                await catalog.QuerySingleAsync(
                                    """SELECT * FROM dmscs."Job" WHERE "JobId" = @JobId;""",
                                    new { JobId = jobId }
                                )
                        ).ToDictionary(),
                        DataStores = (
                            await catalog.QueryAsync("""SELECT * FROM dmscs."DataStore" ORDER BY "Id";""")
                        )
                            .Select(row => ((IDictionary<string, object>)row).ToDictionary())
                            .ToArray(),
                    }
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

    // Nested, not derived: this worker fixture never runs the controller's database deploy/clear/drop.
    [TestFixture, NonParallelizable]
    public sealed class Given_isolated_managed_process_worker
    {
        [Test, Explicit("Launched only by the managed process recovery tests using a private pipe.")]
        public async Task It_runs_the_isolated_managed_process_worker()
        {
            await using var channel = await Worker.Channel.ConnectAsync();
            var settings = channel.Configuration;
            var observer = new Worker.SettlementObserver(settings.JobId, settings.ContinueAfterRetry);
            try
            {
                await using (var host = ProcessWorkerHost(channel, observer))
                using (var client = host.CreateClient())
                {
                    if (settings.ContinueAfterRetry)
                    {
                        await channel.SendAsync(await observer.Retried.WaitAsync(TimeSpan.FromMinutes(6)));
                    }
                    var settled = await observer.Settled.WaitAsync(TimeSpan.FromMinutes(6));
                    await channel.SendAsync(settled);
                }
            }
            catch (Exception error)
            {
                await channel.SendAsync(new("WorkerError:" + error.GetType().Name, Environment.ProcessId));
                throw;
            }
        }
    }

    private static WebApplicationFactory<Program> ProcessWorkerHost(
        Worker.Channel channel,
        Worker.SettlementObserver observer
    )
    {
        var settings = channel.Configuration;
        var options = Options.Create(settings.Provisioning);
        var secrets = Options.Create(new SecretsOptions());
        var ready = new DmsDataStoreProvisionerReadiness();
        ready.Record(new DmsProvisionerReadiness.Ready("mssql", DateTimeOffset.UtcNow));
        var provisioner = new DmsDataStoreProvisioningService(
            ready,
            Engine(),
            new TemplateArtifactCatalog(options, NullLogger<TemplateArtifactCatalog>.Instance),
            options,
            ProtectedDatabaseNames.Create("cms", []),
            new DmsAdminConnectionSource(
                options,
                new SecretValueCache(secrets),
                secrets,
                TimeProvider.System
            ),
            TimeProvider.System,
            NullLogger<DmsDataStoreProvisioningService>.Instance
        );
        return DmsDataStoreProvisioningStartupTests.Given_managed_data_store_startup.Host(
            "true",
            true,
            target: "mssql",
            catalog: "mssql",
            overrides: new()
            {
                ["DatabaseSettings:DatabaseConnection"] = settings.CatalogConnection,
                ["DatabaseSettings:EncryptionKey"] = "publication-test-only-key".PadRight(32, '0'),
                ["DmsDataStoreProvisioning:AdminConnectionString"] = settings
                    .Provisioning
                    .AdminConnectionString!,
                ["DataStoreManagement:RuntimeConnectionString"] = settings.RuntimeConnection,
                ["JobSettings:WorkerEnabled"] = "true",
                // A worker needs a configured DMS reader to start; the scheduler stays off, so it is never called.
                ["DmsEducationOrganizationProjectionSettings:DmsBaseUrl"] = "http://127.0.0.1:9/",
                ["DmsEducationOrganizationProjectionSettings:Credentials:ClientId"] = "unused",
                ["DmsEducationOrganizationProjectionSettings:Credentials:ClientSecret"] = "unused",
                ["JobSettings:MaxAttempts"] = settings.MaxAttempts.ToString(CultureInfo.InvariantCulture),
            },
            ready: !settings.ClosedReadiness,
            configure: services =>
            {
                // Closed readiness keeps the provisioner production registers for a NotReady verdict.
                if (!settings.ClosedReadiness)
                {
                    services.AddSingleton<IDmsDataStoreProvisioner>(provisioner);
                }
                services.AddSingleton<ILogger<JobExecutor>>(observer);
                services.AddSingleton<ILogger<JobWorkerService>>(observer);
                if (settings.StopAt != Worker.StopPoint.None)
                {
                    var databaseOptions = Options.Create(
                        new DatabaseOptions
                        {
                            DatabaseConnection = settings.CatalogConnection,
                            EncryptionKey = "publication-test-only-key",
                        }
                    );
                    services.AddSingleton<IJobFenceFactory>(
                        new ProcessCheckpointFenceFactory(
                            new MssqlJobFenceFactory(databaseOptions, new JobOptions().LeaseTimings),
                            channel
                        )
                    );
                }
            }
        );
    }

    private sealed class ProcessCheckpointFenceFactory(IJobFenceFactory inner, Worker.Channel channel)
        : IJobFenceFactory
    {
        public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
            new ProcessCheckpointFence(inner.Create(job, ownership), channel, job);
    }

    private sealed class ProcessCheckpointFence(IJobFence inner, Worker.Channel channel, ClaimedJob job)
        : IJobFence
    {
        public async Task ExecuteAsync(
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            TaskCompletionSource<bool> finalized = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var settings = channel.Configuration;
            await inner.ExecuteAsync(
                async (transaction, token) =>
                {
                    await work(transaction, token);
                    string? status = await transaction.Connection!.ExecuteScalarAsync<string>(
                        new CommandDefinition(
                            """SELECT "Status" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                            new { Id = settings.ManageId },
                            transaction,
                            cancellationToken: token
                        )
                    );
                    bool isFinal = status == (settings.Delete ? "Deleted" : "Created");
                    finalized.SetResult(isFinal);
                    if (isFinal && settings.StopAt == Worker.StopPoint.BeforeCommit)
                    {
                        await channel.TerminateHereAsync(job);
                    }
                },
                cancellationToken
            );
            if (await finalized.Task && settings.StopAt == Worker.StopPoint.AfterCommit)
            {
                await channel.TerminateHereAsync(job);
            }
        }
    }
}
