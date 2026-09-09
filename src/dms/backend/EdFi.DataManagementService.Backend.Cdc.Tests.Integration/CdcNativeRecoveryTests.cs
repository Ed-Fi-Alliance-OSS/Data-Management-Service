// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category(CdcControllerCategories.NativeRecovery)]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Cdc_Controller_Native_Recovery(CdcProvider provider)
{
    private CdcProviderAdmissionFixture _fixture = null!;
    private CancellationTokenSource _timeout = null!;
    private CancellationToken Token => _timeout.Token;
    private CdcControllerStatusTarget Target => new(_fixture.Request, _fixture.Runtime, 60_000);

    // Cold SQL capture plus final worker inspection can exceed 30 seconds on a shared Docker host.
    // Configure a finite initial observation budget; established recovery retains the fixture default.
    private CdcDeploymentRequest AdmissionRequest =>
        _fixture.WithTiming(
            new(
                _fixture.Request.Timing.CallTimeout,
                _fixture.Request.Timing.WaitTimeout,
                _fixture.Request.Timing.PollInterval,
                TimeSpan.FromMinutes(1)
            )
        );

    private readonly List<object> _evidence = [];

    [SetUp]
    public async Task Setup()
    {
        _evidence.Clear();
        _timeout = new(TimeSpan.FromMinutes(10));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(provider, Token);
        await _fixture.RegisterAsync(Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "native-recovery-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new { Test = TestContext.CurrentContext.Test.Name, Evidence = _evidence }
            )
        );
        TestContext.AddTestAttachment(
            path,
            "Timestamped native recovery, publication and containment evidence; no interval certification"
        );
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
        _timeout.Dispose();
    }

    private async Task AdmitAsync()
    {
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                AdmissionRequest,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        await _fixture.ReopenRuntimeAsync(Token);
        await _fixture.Runtime.StartProcessingAsync(Token);
        await RequireReadyAsync();
    }

    [Test]
    public async Task It_observes_publication_before_crash_revalidation_and_rejects_prior_process_metrics()
    {
        await AdmitAsync();
        CdcTransportResult<CdcConnectorTelemetryObservation> retained = null!;
        _fixture.TransformMetrics = value =>
        {
            retained = value;
            return value;
        };
        await RequireReadyAsync();
        retained.Should().BeOfType<CdcTransportResult<CdcConnectorTelemetryObservation>.Observed>();
        var journal = await _fixture.JournalAsync(Token);
        var before = await OffsetAsync();
        long high = ProgressHighWatermark();
        int calls = _fixture.Infrastructure.ConnectCalls.Count;
        await CrashAsync();
        await WriteHeartbeatAsync();
        await WaitForPublicationAsync(before, high);
        // No controller observation or guarded lifecycle call has run since SIGKILL.
        _evidence.Add(
            new
            {
                At = DateTimeOffset.UtcNow,
                PreValidationPublicationObserved = true,
                UnobservedIntervalCertified = false,
            }
        );
        var recovered = await ObserveAsync();
        RequireInvalidated(recovered);
        _fixture.TransformMetrics = _ => retained;
        var replay = await ObserveAsync();
        replay.Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
        replay.Details.LagMilliseconds.Should().BeNull();
        _fixture.TransformMetrics = value => value;
        int providers = _fixture.ProviderModes.Count;
        int lag = _fixture.Lag.Count;
        var freshAt = DateTimeOffset.UtcNow;
        var ready = await RequireReadyAsync();
        ready.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        ready.Recovery.UnobservedIntervalCertified.Should().BeFalse();
        _fixture
            .ProviderModes.Skip(providers)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(m => m == CdcProviderSetupMode.ValidateOnly);
        _fixture.Lag.Skip(lag).Should().Contain(l => l.ObservedAt >= freshAt);
        _fixture
            .Infrastructure.ConnectCalls.Skip(calls)
            .Should()
            .Contain(
                new[]
                {
                    nameof(ICdcConnectTransport.ReadOffsetEvidenceAsync),
                    nameof(ICdcConnectTransport.ReadConfigurationAsync),
                    nameof(ICdcConnectTransport.ReadStatusAsync),
                }
            );
        RequireNoResume(calls);
        (await _fixture.JournalAsync(Token)).Should().BeEquivalentTo(journal);
    }

    [Test]
    public async Task It_detects_failed_task_recovery_on_the_same_worker_without_certifying_the_gap()
    {
        await AdmitAsync();
        CdcTransportResult<CdcConnectorTelemetryObservation> retained = null!;
        _fixture.TransformMetrics = value =>
        {
            retained = value;
            return value;
        };
        await RequireReadyAsync();
        _fixture.TransformMetrics = value => value;
        var worker = Observed(await _fixture.Infrastructure.Worker.InspectAsync(_fixture.Request, Token));
        var config = Observed(
            await _fixture.Infrastructure.Connect.ReadConfigurationAsync(_fixture.Request, Token)
        );
        int calls = _fixture.Infrastructure.ConnectCalls.Count;
        // PostgreSQL internally retries startup connection errors without exposing FAILED. A valid
        // built-in Cast transform applied to the structured progress record supplies a fatal runtime
        // fault instead. This override is external fault injection, never controller configuration.
        if (provider == CdcProvider.Postgresql)
        {
            var fault = config.ToDictionary(p => p.Key, p => p.Value);
            fault["transforms"] += ",t33Failure";
            fault["transforms.t33Failure.type"] = "org.apache.kafka.connect.transforms.Cast$Value";
            fault["transforms.t33Failure.spec"] = "int32";
            await PutConfigurationAsync(fault);
            await WriteHeartbeatAsync();
        }
        else
        {
            await SetSqlLoginPasswordAsync(false);
            await RestartTaskAsync();
        }
        await CdcControllerFixture.WaitAsync(
            async _ =>
            {
                var status = await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, Token);
                return status is CdcTransportResult<CdcConnectStatus>.Observed value
                    && value.Value.Tasks.Any(t => t.State == CoreCdc.CdcConnectorRuntimeState.Failed);
            },
            _fixture.Request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            Token
        );
        // Restore provider prerequisites before observation; the failed task still needs recovery.
        if (provider == CdcProvider.SqlServer)
        {
            await SetSqlLoginPasswordAsync(true);
        }
        var failed = await ObserveAsync();
        RequireInvalidated(failed);
        if (provider == CdcProvider.Postgresql)
        {
            await PutConfigurationAsync(config);
        }
        else
        {
            await RestartTaskAsync();
        }
        await WaitRunningAsync();
        var recovered = await ObserveAsync();
        RequireInvalidated(recovered);
        _fixture.TransformMetrics = _ => retained;
        (await ObserveAsync()).Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
        _fixture.TransformMetrics = value => value;
        (await RequireReadyAsync()).Recovery.UnobservedIntervalCertified.Should().BeFalse();
        var later = Observed(await _fixture.Infrastructure.Worker.InspectAsync(_fixture.Request, Token));
        (later.ProcessIdentity == worker.ProcessIdentity).Should().BeTrue();
        bool unchanged = Observed(
                await _fixture.Infrastructure.Connect.ReadConfigurationAsync(_fixture.Request, Token)
            )
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual(config.OrderBy(p => p.Key, StringComparer.Ordinal));
        unchanged.Should().BeTrue();
        RequireNoResume(calls);
        _evidence.Add(
            new
            {
                At = DateTimeOffset.UtcNow,
                SameWorkerTaskRecovered = true,
                ExactConfigurationRestored = true,
            }
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_routes_incomplete_or_acknowledged_but_unverified_shutdown_to_native_recovery(
        bool acknowledged
    )
    {
        await AdmitAsync();
        bool stopRequested = false;
        _fixture.Infrastructure.BeforeConnectCall = name =>
        {
            if (name == nameof(ICdcConnectTransport.StopAsync))
            {
                stopRequested = true;
                if (!acknowledged)
                {
                    throw new IOException("t33-private-sentinel");
                }
            }
            if (stopRequested && name == nameof(ICdcConnectTransport.ReadStatusAsync))
            {
                throw new IOException("t33-private-sentinel");
            }
        };
        // Bound unsuccessful stop verification without waiting the normal three-minute setup budget.
        var request = _fixture.WithTiming(
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(250))
        );
        var stopped = await _fixture.Controllers.Lifecycle.ExecuteAsync(
            new(request, _fixture.Runtime, 60_000),
            CdcManagedLifecycleOperation.Stop,
            Token
        );
        _fixture.Infrastructure.BeforeConnectCall = _ => { };
        _evidence.Add(
            new
            {
                At = DateTimeOffset.UtcNow,
                Acknowledged = acknowledged,
                Result = stopped,
            }
        );
        stopRequested.Should().BeTrue();
        stopped.TargetShutdownVerified.Should().BeFalse();
        (await _fixture.JournalAsync(Token))
            .Operations.Last(o => o.Effect == CdcWorkflowEffect.StopConnector)
            .Completions.Should()
            .BeEmpty();
        await CrashAsync();
        var recovery = await ObserveAsync();
        recovery.Recovery.Boundary.Should().Be(CdcRecoveryBoundary.NativeRecovery);
        recovery.Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
        int calls = _fixture.Infrastructure.ConnectCalls.Count;
        var start = await _fixture.Controllers.Lifecycle.ExecuteAsync(
            Target,
            CdcManagedLifecycleOperation.Start,
            Token
        );
        start.Succeeded.Should().BeFalse();
        start.Boundary.Should().Be(CdcManagedLifecycleBoundary.NativeRecovery);
        RequireNoResume(calls);
    }

    [Test]
    public async Task It_rejects_unknown_recovery_evidence_and_unauthorized_controller_mutations()
    {
        await AdmitAsync();
        await CrashAsync();
        RequireInvalidated(await ObserveAsync());
        foreach (string fault in new[] { "provider", "offset", "worker", "status", "metrics" })
        {
            int failures = 0;
            _fixture.Hooks.OnBoundary = e =>
            {
                if (
                    fault == "provider"
                    && e.Boundary == CdcControllerBoundary.ProviderProof
                    && e.Edge == CdcControllerEdge.Before
                )
                {
                    failures++;
                    throw new IOException("t33-private-sentinel");
                }
            };
            _fixture.Infrastructure.BeforeConnectCall = name =>
            {
                if (
                    fault == "offset" && name == nameof(ICdcConnectTransport.ReadOffsetEvidenceAsync)
                    || fault == "status" && name == nameof(ICdcConnectTransport.ReadStatusAsync)
                )
                {
                    failures++;
                    throw new IOException("t33-private-sentinel");
                }
            };
            _fixture.Infrastructure.BeforeWorkerCall = () =>
            {
                if (fault == "worker")
                {
                    failures++;
                    throw new IOException("t33-private-sentinel");
                }
            };
            _fixture.BeforeMetricsCall = () =>
            {
                if (fault == "metrics")
                {
                    failures++;
                    throw new IOException("t33-private-sentinel");
                }
            };
            try
            {
                (await ObserveAsync()).Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
                // Metrics gate running publication; the existing pre-start contract intentionally
                // does not require metrics from a running task. Unknown continuity gates mutations.
                if (fault != "metrics")
                {
                    await RequireRejectedAsync(fault);
                }
                failures.Should().BeGreaterThan(0);
            }
            finally
            {
                _fixture.Hooks.OnBoundary = _ => { };
                _fixture.Infrastructure.BeforeConnectCall = _ => { };
                _fixture.Infrastructure.BeforeWorkerCall = () =>
                {
                    if (fault == "worker")
                    {
                        failures++;
                        throw new IOException("t33-private-sentinel");
                    }
                };
                _fixture.BeforeMetricsCall = () => { };
            }
        }
        await RequireReadyAsync();
    }

    [Test]
    public async Task It_rejects_missing_provenance_after_native_recovery_without_reconstructing_state()
    {
        await AdmitAsync();
        await CrashAsync();
        RequireInvalidated(await ObserveAsync());
        foreach (string directory in new[] { "bindings", "workflows", "source-history" })
        {
            string path = Directory
                .GetFiles(
                    Path.Combine(_fixture.Infrastructure.StateRoot, directory),
                    "*.json",
                    SearchOption.AllDirectories
                )
                .Single();
            string retained = path + ".retained";
            File.Move(path, retained);
            try
            {
                (await ObserveAsync()).Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
                await RequireRejectedAsync(directory);
                File.Exists(path).Should().BeFalse();
            }
            finally
            {
                File.Move(retained, path);
            }
        }
        await RequireReadyAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_contains_recovered_connectors_and_retains_terminal_history_loss(bool retained)
    {
        await AdmitAsync();
        var before = await OffsetAsync();
        long high = ProgressHighWatermark();
        if (retained)
        {
            await LatchIncidentAsync();
        }
        await CrashAsync();
        if (retained)
        {
            // The worker does not consult local incident storage. This deliberately observes genuine
            // publication during native recovery despite a terminal incident predating the crash.
            await WriteHeartbeatAsync();
            await WaitForPublicationAsync(before, high);
        }
        else
        {
            RequireInvalidated(await ObserveAsync());
            await RequireReadyAsync();
            // Erasing committed offsets is an actual authoritative history loss. The external stop is
            // deliberately unjournaled; neither its acknowledgement nor this test authorizes startup.
            await RawLifecycleAsync("stop");
            await CdcControllerFixture.WaitAsync(
                async _ =>
                    Observed(
                        await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, Token)
                    ).IsStopped,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(250),
                Token
            );
            Observed(await _fixture.Infrastructure.Connect.DeleteOffsetsAsync(_fixture.Request, Token));
            Observed(await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token))
                .State.Should()
                .Be(CdcConnectOffsetState.Missing);
        }
        var lost = await ObserveAsync();
        lost.Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
        lost.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        var exact = await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(
            _fixture.Request.Binding,
            Token
        );
        exact.State!.State.Should().Be(CoreCdc.CdcBindingState.IncidentLatched);
        var incident = exact.State.Incident;
        incident.Should().NotBeNull();
        await RequireRejectedAsync(retained ? "retained-terminal" : "new-history-loss");
        var later = await ObserveAsync();
        later.Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
        later.Recovery.UnobservedIntervalCertified.Should().BeFalse();
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(_fixture.Request.Binding, Token))
            .State!.Incident.Should()
            .BeEquivalentTo(incident);
    }

    [Test]
    public async Task It_repeats_the_offline_barrier_sequence_after_worker_crash_interrupts_initial_readiness()
    {
        bool interrupted = false;
        _fixture.AfterRuntimeCall = name =>
        {
            if (!interrupted && name == nameof(ICdcProjectionRuntime.CaptureBarrierAsync))
            {
                interrupted = true;
                throw new IOException("t33-private-sentinel");
            }
        };
        (
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                AdmissionRequest,
                _fixture.Runtime,
                60_000,
                Token
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        interrupted.Should().BeTrue();
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
        int barriers = _fixture.Barriers.Count;
        _fixture.AfterRuntimeCall = _ => { };
        await CrashAsync();
        await _fixture.ReopenRuntimeAsync(Token);
        var recoveredAt = DateTimeOffset.UtcNow;
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                AdmissionRequest,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        _fixture.Barriers.Count.Should().BeGreaterThan(barriers);
        var barrier = _fixture.Barriers[^1];
        barrier.BarrierCapturedAt.Should().BeOnOrAfter(recoveredAt);
        var observations = _fixture.ProjectionObservations.Where(o => o.ObservedAt >= recoveredAt).ToArray();
        observations
            .Should()
            .Contain(o =>
                o.ObservedAt <= barrier.BarrierCapturedAt
                && o.Targets.Single().CaughtUp.Status
                    == EdFi.DataManagementService.Core.DocumentCache.DocumentCacheCaughtUpStatus.CaughtUp
            );
        observations
            .Should()
            .Contain(o =>
                o.ObservedAt >= barrier.BarrierCapturedAt
                && o.Targets.Single().CaughtUp.Status
                    == EdFi.DataManagementService.Core.DocumentCache.DocumentCacheCaughtUpStatus.CaughtUp
            );
        _fixture.Lag.Should().Contain(l => l.ObservedAt >= recoveredAt);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeTrue();
        _evidence.Add(
            new
            {
                At = DateTimeOffset.UtcNow,
                BarriersBefore = barriers,
                BarriersAfter = _fixture.Barriers.Count,
                FreshInitialAuthorization = true,
            }
        );
    }

    private async Task CrashAsync()
    {
        var worker = Observed(await _fixture.Infrastructure.Worker.InspectAsync(_fixture.Request, Token));
        var started = DateTimeOffset.UtcNow;
        await _fixture.Infrastructure.RecoverWorkerAsync(true, Token);
        var recovered = Observed(await _fixture.Infrastructure.Worker.InspectAsync(_fixture.Request, Token));
        (recovered.ProcessIdentity != worker.ProcessIdentity).Should().BeTrue();
        _evidence.Add(
            new
            {
                StartedAt = started,
                RecoveredAt = DateTimeOffset.UtcNow,
                Signal = "SIGKILL",
                ProcessChanged = true,
                ManagedShutdownVerified = false,
            }
        );
        // Read-only REST wait is not controller revalidation.
        var status = Observed(await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, Token));
        if (!status.IsStopped)
        {
            await WaitRunningAsync();
        }
    }

    private Task WaitRunningAsync() =>
        CdcControllerFixture.WaitAsync(
            async _ =>
                (await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, Token))
                    is CdcTransportResult<CdcConnectStatus>.Observed value
                && value.Value.IsRunning,
            _fixture.Request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            Token
        );

    private async Task<CdcControllerTargetStatus> ObserveAsync()
    {
        var result = (await _fixture.Controllers.Status.StatusAsync([Target], Token)).Targets.Single();
        JsonSerializer.Serialize(result).Should().NotContain("t33-private-sentinel");
        _evidence.Add(new { At = DateTimeOffset.UtcNow, Status = result });
        return result;
    }

    private async Task<CdcControllerTargetStatus> RequireReadyAsync()
    {
        CdcControllerTargetStatus result = null!;
        await CdcControllerFixture.WaitAsync(
            async _ =>
            {
                result = await ObserveAsync();
                result
                    .Details.IncidentFailureCategory.Should()
                    .BeNull("healthy fixtures must not retry terminal loss");
                return result.Status.Readiness == CoreCdc.CdcReadiness.Ready;
            },
            _fixture.Request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(500),
            Token
        );
        return result;
    }

    private static void RequireInvalidated(CdcControllerTargetStatus result)
    {
        result.Recovery.Should().Be(new CdcRecoveryObservation(CdcRecoveryBoundary.NativeRecovery, true));
        result.Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
        result.Details.LagMilliseconds.Should().BeNull();
    }

    private async Task RequireRejectedAsync(string scenario)
    {
        int calls = _fixture.Infrastructure.ConnectCalls.Count;
        foreach (
            var operation in new[]
            {
                CdcManagedLifecycleOperation.Start,
                CdcManagedLifecycleOperation.Restart,
                CdcManagedLifecycleOperation.Resume,
            }
        )
        {
            var result = await _fixture.Controllers.Lifecycle.ExecuteAsync(Target, operation, Token);
            _evidence.Add(
                new
                {
                    At = DateTimeOffset.UtcNow,
                    Scenario = scenario,
                    Result = result,
                }
            );
            result.Succeeded.Should().BeFalse(scenario);
            result.Ready.Should().BeFalse(scenario);
        }
        RequireNoResume(calls);
    }

    private void RequireNoResume(int calls) =>
        _fixture
            .Infrastructure.ConnectCalls.Skip(calls)
            .Should()
            .NotContain(name =>
                name == nameof(ICdcConnectTransport.ResumeAsync)
                || name == nameof(ICdcConnectTransport.RestartAsync)
                || name == nameof(ICdcConnectTransport.CreateAsync)
            );

    private Task SetSqlLoginPasswordAsync(bool restore)
    {
        string password = restore
            ? CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword
            : "T33_Private_Fault1!";
        return _fixture.ExecuteAsync($"ALTER LOGIN dms_connector WITH PASSWORD = '{password}'", Token);
    }

    private async Task PutConfigurationAsync(IReadOnlyDictionary<string, string> configuration)
    {
        using var client = CdcConnectRestAdapter.CreateHttpClient();
        using var response = await client.PutAsJsonAsync(
            new Uri(
                _fixture.Infrastructure.ConnectEndpoint,
                "connectors/" + Uri.EscapeDataString(_fixture.Request.Binding.ConnectorName) + "/config"
            ),
            configuration,
            Token
        );
        response
            .IsSuccessStatusCode.Should()
            .BeTrue("the external task fault configuration must be accepted");
        _evidence.Add(new { At = DateTimeOffset.UtcNow, ExternalTaskConfigurationAccepted = true });
    }

    private async Task RestartTaskAsync()
    {
        using var client = CdcConnectRestAdapter.CreateHttpClient();
        using var response = await client.PostAsync(
            new Uri(
                _fixture.Infrastructure.ConnectEndpoint,
                "connectors/"
                    + Uri.EscapeDataString(_fixture.Request.Binding.ConnectorName)
                    + "/tasks/0/restart"
            ),
            null,
            Token
        );
        response.IsSuccessStatusCode.Should().BeTrue("the external task restart must be accepted");
        _evidence.Add(new { At = DateTimeOffset.UtcNow, ExternalTaskRestartAccepted = true });
    }

    private async Task RawLifecycleAsync(string action)
    {
        using var client = CdcConnectRestAdapter.CreateHttpClient();
        using var response = await client.PutAsync(
            new Uri(
                _fixture.Infrastructure.ConnectEndpoint,
                "connectors/" + Uri.EscapeDataString(_fixture.Request.Binding.ConnectorName) + "/" + action
            ),
            null,
            Token
        );
        response.IsSuccessStatusCode.Should().BeTrue();
    }

    private async Task WaitForPublicationAsync(object before, long high)
    {
        await CdcControllerFixture.WaitAsync(
            async _ => !Equals(await OffsetAsync(), before) && ProgressHighWatermark() > high,
            _fixture.Request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            Token
        );
        _evidence.Add(
            new
            {
                At = DateTimeOffset.UtcNow,
                OffsetAdvanced = true,
                ProgressBefore = high,
                ProgressAfter = ProgressHighWatermark(),
            }
        );
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
        _evidence.Add(
            new
            {
                At = DateTimeOffset.UtcNow,
                incident.LatchedAt,
                TerminalIncidentRecorded = true,
            }
        );
        return incident;
    }

    private long ProgressHighWatermark()
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(
            new ConsumerConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
                GroupId = "t33-observation",
                EnableAutoCommit = false,
            }
        ).Build();
        string topic = CoreCdc
            .CdcArtifactNameGenerator.RecoverFromBinding(_fixture.Request.Binding)
            .Inventory!.ProgressTopicName;
        return consumer.QueryWatermarkOffsets(new(topic, 0), TimeSpan.FromSeconds(10)).High.Value;
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
