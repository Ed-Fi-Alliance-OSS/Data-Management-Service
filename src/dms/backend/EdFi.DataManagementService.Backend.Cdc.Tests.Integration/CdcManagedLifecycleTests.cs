// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category(CdcControllerCategories.ManagedLifecycle)]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Cdc_Controller_Managed_Lifecycle(CdcProvider provider)
{
    private CdcProviderAdmissionFixture _fixture = null!;
    private CancellationTokenSource _timeout = null!;
    private CancellationToken Token => _timeout.Token;
    private CdcControllerStatusTarget Target => new(_fixture.Request, _fixture.Runtime, 60_000);
    private readonly List<object> _evidence = [];

    [SetUp]
    public async Task Setup()
    {
        _evidence.Clear();
        _timeout = new(TimeSpan.FromMinutes(10));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(provider, Token);
        await _fixture.RegisterAsync(Token);
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        await _fixture.ReopenRuntimeAsync(Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-lifecycle-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new { Test = TestContext.CurrentContext.Test.Name, Evidence = _evidence }
            )
        );
        TestContext.AddTestAttachment(path, "Sanitized managed lifecycle observations");
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
        _timeout.Dispose();
    }

    [Test]
    public async Task It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start()
    {
        var stop = await ExecuteAsync(CdcManagedLifecycleOperation.Stop);
        stop.Succeeded.Should().BeTrue("{0}", string.Join(", ", stop.Diagnostics));
        stop.TargetShutdownVerified.Should().BeTrue();
        stop.Boundary.Should().Be(CdcManagedLifecycleBoundary.VerifiedManagedStop);
        var offset = await OffsetAsync();
        long published = ProgressHighWatermark();
        await WriteHeartbeatAsync();
        await _fixture.Infrastructure.RecoverWorkerAsync(false, Token);
        for (int sample = 0; sample < 5; sample++)
        {
            var status = Observed(
                await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, Token)
            );
            status.IsStopped.Should().BeTrue();
            status.Tasks.Should().BeEmpty();
            (await OffsetAsync()).Should().BeEquivalentTo(offset);
            ProgressHighWatermark().Should().Be(published);
            _evidence.Add(
                new
                {
                    At = DateTimeOffset.UtcNow,
                    Stopped = status.IsStopped,
                    TaskCount = status.Tasks.Count,
                    OffsetUnchanged = true,
                    ProgressHighWatermark = published,
                }
            );
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
        }
        var before = Observed(
            await _fixture.Controllers.Validation.ValidateAsync(
                _fixture.Request,
                _fixture.Runtime,
                CdcEstablishedValidationMode.PreStart,
                60_000,
                cancellationToken: Token
            )
        );
        before.PreStartEligible.Should().BeTrue("{0}", string.Join(", ", before.Diagnostics));
        before.PublicationReady.Should().BeFalse();
        // Managed start owns processing after its preflight, then requires a fresh running observation.
        var start = await ExecuteAsync(CdcManagedLifecycleOperation.Start);
        start.Succeeded.Should().BeTrue("{0}", string.Join(", ", start.Diagnostics));
        start.Ready.Should().BeTrue();
        Func<Task> initial = () => _fixture.Runtime.ObserveInitialDatabaseAsync(Token);
        await initial.Should().ThrowAsync<InvalidOperationException>();
        await CdcControllerFixture.WaitAsync(
            async _ => !Equals(await OffsetAsync(), offset),
            _fixture.Request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            Token
        );
        _fixture.Lag.Should().Contain(l => l.ObservedAt > before.ObservedAt);
        _fixture.ProviderModes.Skip(1).Should().NotContain(m => m != CdcProviderSetupMode.ValidateOnly);
        var intact = await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(
            _fixture.Request.Binding,
            Token
        );
        intact.State!.State.Should().Be(CoreCdc.CdcBindingState.BindingPresent);
        intact.State.Incident.Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_waits_for_retained_projection_work_in_the_single_start_invocation(bool persistent)
    {
        (await ExecuteAsync(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        // A canonical resource write leaves real coalesced projection work while processing is offline.
        await _fixture.ExecuteAsync(
            provider == CdcProvider.Postgresql
                ? "INSERT INTO dms.\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") SELECT gen_random_uuid(), \"ResourceKeyId\" FROM dms.\"ResourceKey\" WHERE \"ResourceName\" = 'Widget'; INSERT INTO testproject.\"Widget\" (\"DocumentId\", \"WidgetId\", \"WidgetName\") SELECT \"DocumentId\", 1, 'queued' FROM dms.\"Document\";"
                : "INSERT INTO dms.Document (DocumentUuid, ResourceKeyId) SELECT NEWID(), ResourceKeyId FROM dms.ResourceKey WHERE ResourceName = 'Widget'; INSERT INTO testproject.Widget (DocumentId, WidgetId, WidgetName) SELECT DocumentId, 1, 'queued' FROM dms.Document;",
            Token
        );
        string work =
            provider == CdcProvider.Postgresql
                ? "dms.\"DocumentProjectionWork\""
                : "dms.DocumentProjectionWork";
        (await _fixture.ScalarAsync<int>($"SELECT CAST(COUNT(*) AS int) FROM {work}", Token)).Should().Be(1);
        await using System.Data.Common.DbConnection connection =
            provider == CdcProvider.Postgresql
                ? new NpgsqlConnection(_fixture.ConnectionString)
                : new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            provider == CdcProvider.Postgresql
                ? $"SELECT * FROM {work} FOR UPDATE"
                : $"SELECT * FROM {work} WITH (UPDLOCK, ROWLOCK)";
        await command.ExecuteNonQueryAsync(Token);
        int resumes = 0;
        int postResumePasses = 0;
        bool released = false;
        _fixture.Infrastructure.BeforeConnectCall = method =>
        {
            if (method == nameof(ICdcConnectTransport.ResumeAsync))
            {
                resumes++;
            }
        };
        _fixture.BeforeRuntimeCall = method =>
        {
            if (method == nameof(ICdcProjectionRuntime.ObserveAsync) && resumes > 0)
            {
                postResumePasses++;
                if (postResumePasses == 4 && !persistent)
                {
                    transaction.Commit();
                    released = true;
                }
            }
        };
        // SQL Server's live preflight is slower; leave time for completed resume and catch-up
        // observations within this one deadline, as in the fixture's provider-specific defaults.
        var wait = TimeSpan.FromSeconds(provider == CdcProvider.SqlServer ? 60 : 20);
        // Bound backlog waiting without shortening the fixture's observation freshness window.
        var request = persistent
            ? _fixture.WithTiming(
                new(
                    wait / 2,
                    wait,
                    TimeSpan.FromMilliseconds(250),
                    _fixture.Request.Timing.MaximumObservationAge
                )
            )
            : _fixture.Request;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _fixture.Controllers.Lifecycle.ExecuteAsync(
                new(request, _fixture.Runtime, 60_000),
                CdcManagedLifecycleOperation.Start,
                Token
            );
            _evidence.Add(
                new
                {
                    Result = result,
                    PostResumePasses = postResumePasses,
                    Resumes = resumes,
                    Elapsed = elapsed.Elapsed,
                }
            );
            result.Succeeded.Should().Be(!persistent, "{0}", JsonSerializer.Serialize(result));
            result.Ready.Should().Be(!persistent);
            resumes.Should().Be(1);
            (await _fixture.JournalAsync(Token)).Operations.Last().Completions.Should().ContainSingle();
            if (persistent)
            {
                // The deadline, not machine-dependent poll throughput, bounds persistent backlog.
                postResumePasses.Should().BeGreaterThan(1);
                elapsed
                    .Elapsed.Should()
                    .BeGreaterThanOrEqualTo(request.Timing.WaitTimeout - TimeSpan.FromMilliseconds(50))
                    .And.BeLessThan(request.Timing.WaitTimeout + TimeSpan.FromSeconds(15));
                result.Diagnostics.Should().Contain(d => d.Failure == CdcDeploymentFailure.Timeout);
                // Expiry may interrupt a new observation. Do not require stale backlog diagnostics
                // from the preceding pass; verify that the actual queue remains blocked instead.
                (await _fixture.ScalarAsync<int>($"SELECT CAST(COUNT(*) AS int) FROM {work}", Token))
                    .Should()
                    .Be(1);
            }
            else
            {
                postResumePasses.Should().BeGreaterThan(3);
                result.Observation.Status.Projection.State.Should().Be(CoreCdc.CdcComponentState.Satisfied);
                result.Diagnostics.Should().BeEmpty();
            }
        }
        finally
        {
            _fixture.BeforeRuntimeCall = _ => { };
            _fixture.Infrastructure.BeforeConnectCall = _ => { };
            if (!released)
            {
                await transaction.RollbackAsync(Token);
            }
        }
    }

    [Test]
    public async Task It_restarts_an_intact_running_connector_with_fresh_ready_evidence()
    {
        await _fixture.Runtime.StartProcessingAsync(Token);
        var result = await ExecuteAsync(CdcManagedLifecycleOperation.Restart);
        result.Succeeded.Should().BeTrue("{0}", string.Join(", ", result.Diagnostics));
        result.Ready.Should().BeTrue();
        (await _fixture.JournalAsync(Token))
            .Operations.Should()
            .Contain(o => o.Effect == CdcWorkflowEffect.ResumeConnector && o.Completions.Length == 1);
    }

    [Test]
    public async Task It_rejects_missing_corrupt_and_incomplete_provenance_without_authorizing_resume()
    {
        (await ExecuteAsync(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        foreach (string directory in new[] { "bindings", "workflows", "source-history" })
        {
            string path = Directory
                .GetFiles(
                    Path.Combine(_fixture.Infrastructure.StateRoot, directory),
                    "*.json",
                    SearchOption.AllDirectories
                )
                .Single();
            byte[] original = await File.ReadAllBytesAsync(path, Token);
            foreach (string damage in new[] { "missing", "corrupt", "unsafe-permissions", "contradictory" })
            {
                try
                {
                    if (damage == "missing")
                    {
                        File.Delete(path);
                    }
                    else if (damage == "corrupt")
                    {
                        await File.WriteAllTextAsync(path, "{t32-private-sentinel", Token);
                    }
                    else if (damage == "contradictory")
                    {
                        var altered = JsonNode.Parse(original)!;
                        if (directory == "bindings")
                        {
                            altered["partitionCount"] = 2;
                        }
                        else if (directory == "source-history")
                        {
                            altered["physicalSourceFingerprint"] = "sha256:" + new string('f', 64);
                        }
                        else
                        {
                            altered["target"]!["instanceKey"] = "contradictory";
                        }
                        await File.WriteAllTextAsync(path, altered.ToJsonString(), Token);
                    }
                    else
                    {
                        File.SetUnixFileMode(
                            path,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                        );
                    }
                    await RequireRejectedAsync(directory + ":" + damage);
                }
                finally
                {
                    await File.WriteAllBytesAsync(path, original, Token);
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        string journalPath = Directory
            .GetFiles(
                Path.Combine(_fixture.Infrastructure.StateRoot, "workflows"),
                "*.json",
                SearchOption.AllDirectories
            )
            .Single();
        byte[] journal = await File.ReadAllBytesAsync(journalPath, Token);
        foreach (string effect in new[] { "CreateProvider", "RegisterConnector", "EstablishConnector" })
        {
            try
            {
                var node = JsonNode.Parse(journal)!;
                node["operations"]!.AsArray().Single(o => o!["effect"]!.GetValue<string>() == effect)![
                    "completions"
                ] = new JsonArray();
                await File.WriteAllTextAsync(journalPath, node.ToJsonString(), Token);
                await RequireRejectedAsync("incomplete:" + effect);
            }
            finally
            {
                await File.WriteAllBytesAsync(journalPath, journal, Token);
            }
        }
        foreach (
            var report in new[]
            {
                CdcDeploymentIntegrityReport.IncidentHistoryDeletion,
                CdcDeploymentIntegrityReport.StateRollback,
            }
        )
        {
            await RequireRejectedAsync(report.ToString(), report);
        }
        var restored = Observed(
            await _fixture.Controllers.Validation.ValidateAsync(
                _fixture.Request,
                _fixture.Runtime,
                CdcEstablishedValidationMode.PreStart,
                60_000,
                cancellationToken: Token
            )
        );
        restored.PreStartEligible.Should().BeTrue();
    }

    [TestCase("offset")]
    [TestCase("provider")]
    public async Task It_rejects_unavailable_live_evidence_while_stopped(string fault)
    {
        (await ExecuteAsync(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        int failures = 0;
        if (fault == "offset")
        {
            _fixture.Infrastructure.BeforeConnectCall = name =>
            {
                if (name == nameof(ICdcConnectTransport.ReadOffsetEvidenceAsync))
                {
                    failures++;
                    throw new IOException("t32-private-sentinel");
                }
            };
        }
        else
        {
            _fixture.Hooks.OnBoundary = e =>
            {
                if (e.Boundary == CdcControllerBoundary.ProviderProof && e.Edge == CdcControllerEdge.Before)
                {
                    failures++;
                    throw new IOException("t32-private-sentinel");
                }
            };
        }
        try
        {
            await RequireRejectedAsync(fault, checkInitial: false);
        }
        finally
        {
            _fixture.Infrastructure.BeforeConnectCall = _ => { };
            _fixture.Hooks.OnBoundary = _ => { };
        }
        failures.Should().BeGreaterThan(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_an_independent_empty_or_populated_source_without_mutating_either(
        bool populated
    )
    {
        (await ExecuteAsync(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        var config = Observed(
            await _fixture.Infrastructure.Connect.ReadConfigurationAsync(_fixture.Request, Token)
        );
        var offset = await OffsetAsync();
        byte[] binding = await File.ReadAllBytesAsync(
            Directory
                .GetFiles(
                    Path.Combine(_fixture.Infrastructure.StateRoot, "bindings"),
                    "*.json",
                    SearchOption.AllDirectories
                )
                .Single()
        );
        await _fixture.SelectDifferentSourceAsync("replacement_" + Guid.NewGuid().ToString("N"), Token);
        if (populated)
        {
            await _fixture.ExecuteAsync(
                provider == CdcProvider.Postgresql
                    ? "INSERT INTO dms.\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") SELECT gen_random_uuid(), \"ResourceKeyId\" FROM dms.\"ResourceKey\" LIMIT 1"
                    : "INSERT INTO dms.Document (DocumentUuid, ResourceKeyId) SELECT TOP (1) NEWID(), ResourceKeyId FROM dms.ResourceKey",
                Token
            );
        }
        string identitySql =
            provider == CdcProvider.Postgresql
                ? "SELECT \"SourceIdentity\"::text FROM dms.\"DataStoreIdentity\""
                : "SELECT CONVERT(varchar(36), SourceIdentity) FROM dms.DataStoreIdentity";
        string source = await _fixture.ScalarAsync<string>(identitySql, Token);
        int providerCalls = _fixture.ProviderModes.Count;
        await RequireRejectedAsync(populated ? "populated-independent-source" : "empty-independent-source");
        (await _fixture.ScalarAsync<string>(identitySql, Token) == source).Should().BeTrue();
        (
            await _fixture.ScalarAsync<string>(
                provider == CdcProvider.Postgresql
                    ? "SELECT \"ProjectionLifecycleState\" FROM dms.\"DocumentCacheState\""
                    : "SELECT ProjectionLifecycleState FROM dms.DocumentCacheState",
                Token
            )
        )
            .Should()
            .Be("Disabled");
        (await OffsetAsync()).Should().BeEquivalentTo(offset);
        ConfigurationHash(
                Observed(
                    await _fixture.Infrastructure.Connect.ReadConfigurationAsync(_fixture.Request, Token)
                )
            )
            .Should()
            .Be(ConfigurationHash(config));
        (
            await File.ReadAllBytesAsync(
                Directory
                    .GetFiles(
                        Path.Combine(_fixture.Infrastructure.StateRoot, "bindings"),
                        "*.json",
                        SearchOption.AllDirectories
                    )
                    .Single()
            )
        )
            .Should()
            .Equal(binding);
        _fixture
            .ProviderModes.Skip(providerCalls)
            .Should()
            .NotContain(m => m != CdcProviderSetupMode.ValidateOnly);
    }

    [Test]
    public async Task It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history()
    {
        var request = _fixture.Request;
        var incident = await LatchIncidentAsync();
        var peers = CoreCdc
            .CdcArtifactNameGenerator.Render(
                new("dms", "edfi.documents", "peer", 1, request.Binding.Provider)
            )
            .Inventory!;
        using var admin = new AdminClientBuilder(
            new AdminClientConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            }
        ).Build();
        await admin.CreateTopicsAsync([
            new TopicSpecification
            {
                Name = peers.TopicName,
                NumPartitions = 1,
                ReplicationFactor = 1,
            },
        ]);
        var peerBefore = Observed(await _fixture.Kafka.InspectTopicAsync(request, peers.TopicName, Token));
        var providerCleanup = new CdcProviderArtifactCleanupAdapter(
            provider == CdcProvider.Postgresql ? NpgsqlFactory.Instance : SqlClientFactory.Instance,
            _fixture.ConnectionString
        );
        var interruptedCleanup = new InterruptedCleanup(_fixture.Kafka);
        var retirement = _fixture.Controllers.Retirement(interruptedCleanup, providerCleanup);
        bool connectorDeletionInterrupted = false;
        _fixture.Infrastructure.BeforeConnectCall = name =>
        {
            if (name == nameof(ICdcConnectTransport.DeleteAsync))
            {
                connectorDeletionInterrupted = true;
                throw new IOException("t32-private-sentinel");
            }
        };
        var offsetsRemoved = await retirement.RetireAsync(request, request.Binding.Generation, true, Token);
        _evidence.Add(new { At = DateTimeOffset.UtcNow, Retirement = offsetsRemoved });
        offsetsRemoved.Succeeded.Should().BeFalse();
        connectorDeletionInterrupted.Should().BeTrue();
        Observed(await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, Token))
            .State.Should()
            .Be(CdcConnectOffsetState.Missing);
        Observed(await _fixture.Infrastructure.Connect.ReadStatusAsync(request, Token))
            .IsStopped.Should()
            .BeTrue();
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, Token))
            .State!.Incident.Should()
            .BeEquivalentTo(incident);
        _fixture.Infrastructure.BeforeConnectCall = _ => { };
        _evidence.Add(new { At = DateTimeOffset.UtcNow, StoppedOffsetAbsenceVerified = true });
        var partial = await retirement.RetireAsync(request, request.Binding.Generation, true, Token);
        _evidence.Add(new { At = DateTimeOffset.UtcNow, Retirement = partial });
        partial.Succeeded.Should().BeFalse();
        interruptedCleanup.Interrupted.Should().BeTrue();
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, Token))
            .State!.Incident.Should()
            .BeEquivalentTo(incident);
        var complete = await retirement.RetireAsync(request, request.Binding.Generation, true, Token);
        _evidence.Add(new { At = DateTimeOffset.UtcNow, Retirement = complete });
        complete.Succeeded.Should().BeTrue("{0}", string.Join(", ", complete.Diagnostics));
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.BindingMissing);
        Directory
            .GetFiles(
                Path.Combine(_fixture.Infrastructure.StateRoot, "incidents"),
                "*.json",
                SearchOption.AllDirectories
            )
            .Should()
            .BeEmpty();
        var names = CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(request.Binding).Inventory!;
        foreach (
            string topic in new[]
            {
                names.TopicName,
                names.ProgressTopicName,
                names.SchemaHistoryTopicName,
            }.OfType<string>()
        )
        {
            (await _fixture.Kafka.InspectTopicAsync(request, topic!, Token))
                .State.Should()
                .Be(CdcTransportEvidenceState.Absent);
        }
        Observed(await _fixture.Kafka.InspectTopicAsync(request, peers.TopicName, Token))
            .Should()
            .BeEquivalentTo(peerBefore);
        (await _fixture.Infrastructure.Connect.ReadStatusAsync(request, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
        // After deletion, this route's 404 cannot prove offset absence. The independent stopped
        // read above and the retained offset-removal checkpoint supply that evidence.
        (await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        Observed(
            await _fixture.Kafka.InspectTopicAsync(
                request,
                request.WorkerPolicy.OffsetStorageTopic.Value,
                Token
            )
        );
        (
            await _fixture.ScalarAsync<int>(
                provider == CdcProvider.Postgresql
                    ? "SELECT COUNT(*)::int FROM pg_replication_slots"
                    : "SELECT COUNT(*) FROM cdc.change_tables",
                Token
            )
        )
            .Should()
            .Be(0);
        await using (
            var session = await _fixture
                .Infrastructure.CreateJournalStore()
                .AcquireAsync(request.Timing.CallTimeout, request.Timing.PollInterval, Token)
        )
        {
            var history = await session.ReadSourcePublicationHistoryAsync(
                request.TargetIdentity,
                request.Binding.PhysicalSourceFingerprint,
                Token
            );
            history
                .Transitions[^1]
                .Status.Should()
                .Be(
                    EdFi.DataManagementService
                        .Core
                        .DocumentCache
                        .DocumentCacheDownstreamPublicationStatus
                        .Historical
                );
            var journal = await session.ReadAsync(request.TargetIdentity, Token);
            journal
                .Operations.Should()
                .Contain(o => o.Effect == CdcWorkflowEffect.CreateDatabase && o.Completions.Length == 1);
            journal
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.Retire)
                .Completions.Should()
                .ContainSingle();
        }
        (await _fixture.Controllers.Activation.ActivateAsync(request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await retirement.RetireAsync(request, request.Binding.Generation, true, Token))
            .Succeeded.Should()
            .BeTrue();
    }

    [Test]
    public async Task It_retains_a_terminal_incident_despite_healthy_current_provider_and_offset_evidence()
    {
        (await ExecuteAsync(CdcManagedLifecycleOperation.Stop)).TargetShutdownVerified.Should().BeTrue();
        // A durable incident from an earlier unobserved interval cannot be cleared by current health.
        var incident = await LatchIncidentAsync();
        var offset = await OffsetAsync();
        await RequireRejectedAsync("retained-terminal-incident");
        (await OffsetAsync()).Should().BeEquivalentTo(offset);
        var retained = await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(
            _fixture.Request.Binding,
            Token
        );
        retained.State!.State.Should().Be(CoreCdc.CdcBindingState.IncidentLatched);
        retained.State.Incident.Should().BeEquivalentTo(incident);
    }

    private async Task<CoreCdc.CdcIncident> LatchIncidentAsync()
    {
        var incident = new CoreCdc.CdcIncident(
            1,
            CoreCdc.CdcIncidentType.SourceHistoryContinuityLost,
            DateTimeOffset.UtcNow,
            _fixture.Request.Binding.ToCompleteBindingIdentity(),
            CoreCdc.CdcIncidentFailureCategory.ConnectOffsetMissing,
            new(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [CoreCdc.CdcIncidentUnavailableFact.ConnectOffset]
            )
        );
        (await _fixture.Infrastructure.Bindings.LatchSourceHistoryLossAsync(incident, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        return incident;
    }

    private sealed class InterruptedCleanup(ICdcKafkaArtifactCleanupAdapter inner)
        : ICdcKafkaArtifactCleanupAdapter
    {
        public Task<CdcTransportResult<CdcRetirementOffsetState>> InspectRetirementOffsetsAsync(
            CdcArtifactCleanupScope scope,
            CancellationToken cancellationToken
        ) => inner.InspectRetirementOffsetsAsync(scope, cancellationToken);

        public bool Interrupted { get; private set; }

        public async Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> DeleteAsync(
            CdcArtifactCleanupScope scope,
            CoreCdc.CdcGovernedArtifactKind kind,
            CancellationToken cancellationToken
        )
        {
            var result = await inner.DeleteAsync(scope, kind, cancellationToken);
            if (!Interrupted && result.State == CdcTransportEvidenceState.Observed)
            {
                Interrupted = true;
                throw new IOException("t32-private-sentinel");
            }
            return result;
        }
    }

    private async Task RequireRejectedAsync(
        string scenario,
        CdcDeploymentIntegrityReport report = CdcDeploymentIntegrityReport.NoReportedLoss,
        bool checkInitial = true
    )
    {
        int calls = _fixture.Infrastructure.ConnectCalls.Count;
        var stateBefore = SnapshotState();
        int providerCalls = _fixture.ProviderModes.Count;
        var validation = await _fixture.Controllers.Validation.ValidateAsync(
            _fixture.Request,
            _fixture.Runtime,
            CdcEstablishedValidationMode.PreStart,
            60_000,
            report,
            Token
        );
        (
            validation is CdcTransportResult<CdcEstablishedValidationObservation>.Observed observed
            && observed.Value.PreStartEligible
        )
            .Should()
            .BeFalse(scenario);
        foreach (
            var operation in new[]
            {
                CdcManagedLifecycleOperation.Start,
                CdcManagedLifecycleOperation.Restart,
                CdcManagedLifecycleOperation.Resume,
            }
        )
        {
            var result = await _fixture.Controllers.Lifecycle.ExecuteAsync(
                new(_fixture.Request, _fixture.Runtime, 60_000, report),
                operation,
                Token
            );
            _evidence.Add(
                new
                {
                    Scenario = scenario,
                    At = DateTimeOffset.UtcNow,
                    Result = result,
                }
            );
            result.Succeeded.Should().BeFalse(scenario);
            result.Ready.Should().BeFalse(scenario);
            JsonSerializer.Serialize(result).Should().NotContain("t32-private-sentinel");
        }
        if (checkInitial)
        {
            (await _fixture.Controllers.Activation.ActivateAsync(_fixture.Request, _fixture.Runtime, Token))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        _fixture
            .Infrastructure.ConnectCalls.Skip(calls)
            .Should()
            .NotContain(name =>
                name == nameof(ICdcConnectTransport.ResumeAsync)
                || name == nameof(ICdcConnectTransport.RestartAsync)
                || name == nameof(ICdcConnectTransport.CreateAsync)
                || name == nameof(ICdcConnectTransport.UpdateConfigurationForRecordSizeIncreaseAsync)
            );
        Observed(await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, Token))
            .IsStopped.Should()
            .BeTrue();
        SnapshotState().Should().BeEquivalentTo(stateBefore, scenario);
        _fixture
            .ProviderModes.Skip(providerCalls)
            .Should()
            .NotContain(m => m != CdcProviderSetupMode.ValidateOnly);
    }

    private static string ConfigurationHash(IReadOnlyDictionary<string, string> configuration) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(configuration.OrderBy(p => p.Key, StringComparer.Ordinal))
            )
        );

    private Dictionary<string, string> SnapshotState() =>
        Directory
            .GetFiles(_fixture.Infrastructure.StateRoot, "*.json", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(_fixture.Infrastructure.StateRoot, p),
                p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))
            );

    private long ProgressHighWatermark()
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(
            new ConsumerConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
                GroupId = "t32-observation",
                EnableAutoCommit = false,
            }
        ).Build();
        string topic = CoreCdc
            .CdcArtifactNameGenerator.RecoverFromBinding(_fixture.Request.Binding)
            .Inventory!.ProgressTopicName;
        return consumer.QueryWatermarkOffsets(new(topic, 0), TimeSpan.FromSeconds(10)).High.Value;
    }

    private async Task<CdcManagedLifecycleResult> ExecuteAsync(CdcManagedLifecycleOperation operation)
    {
        var result = await _fixture.Controllers.Lifecycle.ExecuteAsync(Target, operation, Token);
        _evidence.Add(new { At = DateTimeOffset.UtcNow, Result = result });
        return result;
    }

    private async Task<object> OffsetAsync()
    {
        var offset = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token)
        );
        offset.State.Should().Be(CdcConnectOffsetState.Streaming);
        return provider == CdcProvider.Postgresql ? offset.Postgresql : offset.SqlServer;
    }

    private Task WriteHeartbeatAsync() =>
        _fixture.ExecuteAsync(
            provider == CdcProvider.Postgresql
                ? "UPDATE dms.\"CdcHeartbeat\" SET \"HeartbeatSequence\" = \"HeartbeatSequence\" + 1, \"HeartbeatAt\" = now() WHERE \"HeartbeatId\" = 1"
                : "UPDATE dms.CdcHeartbeat SET HeartbeatSequence = HeartbeatSequence + 1, HeartbeatAt = SYSUTCDATETIME() WHERE HeartbeatId = 1",
            Token
        );
}
