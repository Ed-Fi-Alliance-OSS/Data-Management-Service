// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
internal class Given_Cdc_command_enable_retry(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private ICdcBindingLifecycleService _bindings = null!;
    private string _settingsPath = null!;
    private bool _failRuntimePreparation;
    private ICdcWorkerStartupTransport _infrastructure = null!;
    private string JournalPath =>
        Directory.GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories).Single();

    [SetUp]
    public void SetupCommand()
    {
        _failRuntimePreparation = false;
        _bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        // Reuse the qualified synthetic transport inventory, starting before provider intent.
        // Every interruption and subsequent retry below goes through the real command/coordinator.
        var journal = JsonNode.Parse(File.ReadAllText(JournalPath))!;
        var operations = journal["operations"]!.AsArray();
        while (operations.Count > 4)
        {
            operations.RemoveAt(operations.Count - 1);
        }
        File.WriteAllText(JournalPath, journal.ToJsonString());
        var historyPath = Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .Single(p => JsonNode.Parse(File.ReadAllText(p))?["transitions"] is not null);
        var history = JsonNode.Parse(File.ReadAllText(historyPath))!;
        var transitions = history["transitions"]!.AsArray();
        while (transitions.Count > 2)
        {
            transitions.RemoveAt(transitions.Count - 1);
        }
        File.WriteAllText(historyPath, history.ToJsonString());
        _connectorExists = _exists = false;
        _posts = 0;
        _calls.Clear();
        _trace.Clear();
        ShortTiming(1000);
        _infrastructure = A.Fake<ICdcWorkerStartupTransport>();
        A.CallTo(() => _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => Trace("broker-start"));
        A.CallTo(() => _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => Trace("worker-start"));
        _settingsPath = Path.Combine(_root, "settings.json");
        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["AppSettings:Datastore"] =
                        Provider == Ddl.CdcProvider.Postgresql ? "postgresql" : "mssql",
                    ["Cdc:Provider"] = Provider == Ddl.CdcProvider.Postgresql ? "postgresql" : "sqlserver",
                    ["Cdc:DeploymentKey"] = Target.DeploymentKey,
                    ["Cdc:InstanceKey"] = Target.InstanceKey,
                    ["Cdc:DataStoreId"] = Target.DataStoreId,
                    ["Cdc:Generation"] = Target.Generation.ToString(),
                    ["Cdc:LagThresholdMilliseconds"] = "1000",
                    ["Cdc:Compose:Project"] = "test",
                    ["Cdc:Compose:File"] = "/unused-compose",
                    ["Cdc:Compose:EnvironmentFile"] = "/unused-env",
                    ["Cdc:Compose:BrokerSizeOverrideFile"] = "/unused-size",
                    ["Cdc:DurabilityProfile"] = "LocalSingleBroker",
                    ["Cdc:AuthorizationProfile"] = "AuthorizationDisabledLocal",
                    ["Cdc:KafkaAdminBootstrapServers"] = "127.0.0.1:1",
                    ["Cdc:SetupConnectionString"] =
                        Provider == Ddl.CdcProvider.Postgresql ? "Host=localhost" : "Server=localhost",
                }
            )
        );
        Fake.ClearRecordedCalls(_runtime);
    }

    private Task<CdcCommandResult> CommandAsync(CancellationToken token = default)
    {
        var runner = new CdcCommandRunner(
            A.Fake<IApiSchemaFileLoader>(),
            new(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>())
        )
        {
            CreateRequest = (_, _, _, _, _, _, _, _) => Task.FromResult(_request),
            CreateProjectionRuntime = (_, _, _, _) =>
            {
                Trace("prepare-runtime");
                if (_failRuntimePreparation)
                {
                    throw new IOException("controlled CMS runtime preparation failure");
                }
                return Task.FromResult(Observed(_runtime));
            },
            ConfigureEnableWorkflow = _ =>
                new(
                    _root,
                    _provider,
                    _templates,
                    _kafka,
                    _connect,
                    _worker,
                    _infrastructure,
                    _metrics,
                    _positions
                )
                {
                    Bindings = _bindings,
                },
        };
        return runner.RunAsync(
            new(CdcCommandOperation.Enable, _settingsPath, _root, 1, Target.Generation, false, "", false),
            TextWriter.Null,
            token
        );
    }

    private async Task InterruptAfterEstablishmentAsync()
    {
        _onCall = name =>
        {
            if (name == "metrics")
            {
                throw new IOException("controlled initial interruption");
            }
        };
        (await CommandAsync()).Succeeded.Should().BeFalse();
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .ContainSingle();
        _onCall = _ => { };
        _trace.Clear();
    }

    private void ConfigureInitialStop()
    {
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("stop");
                _runtimeState = CdcConnectorRuntimeState.Stopped;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                if (_runtimeState != CdcConnectorRuntimeState.Stopped)
                {
                    return Observed(status);
                }
                Trace("stopped-readback");
                return Observed(
                    new CdcConnectStatus(
                        status.Runtime with
                        {
                            ConnectorState = CdcConnectorRuntimeState.Stopped,
                            TaskCount = 0,
                            RunningTaskCount = 0,
                            SoleTaskState = CdcConnectorRuntimeState.Unknown,
                        },
                        status.WorkerId,
                        []
                    )
                );
            });
    }

    [TestCase("provider-missing", false)]
    [TestCase("provider-recreated", false)]
    [TestCase("provider-missing", true)]
    [TestCase("provider-recreated", true)]
    public async Task It_contains_provider_loss_before_enable_retry_setup_rejects_it(
        string failure,
        bool failRetainedRuntimePreparation
    )
    {
        await InterruptAfterEstablishmentAsync();
        var healthy = _change;
        ConfigureInitialProviderLoss(failure);
        ConfigureInitialStop();
        (await CommandAsync()).Succeeded.Should().BeFalse();
        var state = await _bindings.ExactMatchBindingAsync(_request.Binding);
        state.State!.Incident.Should().NotBeNull();
        state
            .State.Incident!.FailureCategory.Should()
            .Be(
                failure == "provider-missing"
                    ? CdcIncidentFailureCategory.ProviderArtifactMissing
                    : CdcIncidentFailureCategory.ProviderArtifactRecreated
            );
        _trace.Should().Contain("stopped-readback");
        _trace.Should().NotContain("prepare-runtime").And.NotContain("dispose");
        _trace.Should().NotContain("start").And.NotContain("barrier");
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        _change = healthy;
        _identity = new('a', 64);
        _runtimeState = CdcConnectorRuntimeState.Running;
        _failRuntimePreparation = failRetainedRuntimePreparation;
        _trace.Clear();
        (await CommandAsync()).Succeeded.Should().BeFalse();
        _trace.Should().NotContain("provider").And.NotContain("prepare-runtime");
        _trace.Should().ContainInOrder("stop", "stopped-readback");
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.Incident.Should()
            .BeEquivalentTo(state.State.Incident);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [TestCase("provider-missing")]
    [TestCase("offset-missing")]
    [TestCase("healthy")]
    [TestCase("unknown")]
    public async Task It_inspects_initial_retry_continuity_before_failed_runtime_preparation(string evidence)
    {
        await InterruptAfterEstablishmentAsync();
        _failRuntimePreparation = true;
        ConfigureInitialStop();
        ConfigureInitialProviderLoss(evidence);
        if (evidence == "offset-missing")
        {
            _offsetState = CdcConnectOffsetState.Missing;
        }
        if (evidence == "unknown")
        {
            var healthy = _change;
            _change = result => healthy(result) with { ProviderHistoryObservations = [] };
        }
        var result = await CommandAsync();
        result.Succeeded.Should().BeFalse();
        _trace.Should().Contain("provider");
        var state = await _bindings.ExactMatchBindingAsync(_request.Binding);
        if (evidence is "provider-missing" or "offset-missing")
        {
            state.State!.Incident.Should().NotBeNull();
            state
                .State.Incident!.FailureCategory.Should()
                .Be(
                    evidence == "provider-missing"
                        ? CdcIncidentFailureCategory.ProviderArtifactMissing
                        : CdcIncidentFailureCategory.ConnectOffsetMissing
                );
            _trace.Should().ContainInOrder("stop", "stopped-readback");
            _trace.Should().NotContain("prepare-runtime");
        }
        else
        {
            state.State!.Incident.Should().BeNull();
            _trace.Should().ContainInOrder("provider", "offset", "prepare-runtime");
            _trace.Should().NotContain("stop");
        }
        _trace.Should().NotContain("start").And.NotContain("barrier").And.NotContain("dispose");
        _trace.Should().NotContain("broker-start").And.NotContain("worker-start");
        _posts.Should().Be(1);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        JsonSerializer.Serialize(result).Should().NotContain("controlled CMS runtime preparation failure");
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }

    [Test]
    public async Task It_contains_retained_history_loss_while_enable_waits_for_the_barrier()
    {
        await InterruptAfterEstablishmentAsync();
        ShortTiming(300);
        bool waiting = false;
        var healthy = _change;
        _change = result =>
            waiting
                ? healthy(result) with
                {
                    ProviderHistoryObservations = healthy(result)
                        .ProviderHistoryObservations.Select(h =>
                            h with
                            {
                                SafeObservedValues = h.SafeObservedValues.ToDictionary(
                                    kv => kv.Key,
                                    kv =>
                                        kv.Key switch
                                        {
                                            "restart_lsn" or "confirmed_flush_lsn" => "0_11",
                                            "retained_min_lsn" => "0x00000001000000020004",
                                            _ => kv.Value,
                                        }
                                ),
                            }
                        )
                        .ToArray(),
                }
                : healthy(result);
        A.CallTo(() =>
                _runtime.CaptureBarrierAsync(
                    A<CdcDeploymentRequest>._,
                    A<ICdcProviderSourcePositionAdapter>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                Trace("barrier");
                waiting = true;
                return Provider == Ddl.CdcProvider.Postgresql
                    ? CdcProviderBarrierCaptureResult.PostgresqlSuccess("0/20", DateTimeOffset.UtcNow)
                    : CdcProviderBarrierCaptureResult.SqlServerSuccess(
                        "00000001:00000002:0004",
                        "00000001:00000002:0004",
                        DateTimeOffset.UtcNow
                    );
            });
        ConfigureInitialStop();
        (await CommandAsync()).Succeeded.Should().BeFalse();
        var state = await _bindings.ExactMatchBindingAsync(_request.Binding);
        state.State!.Incident.Should().NotBeNull();
        state.State.Incident!.FailureCategory.Should().Be(CdcIncidentFailureCategory.RetainedHistoryGap);
        _trace.IndexOf("barrier").Should().BeLessThan(_trace.IndexOf("stop"));
        _trace.IndexOf("stopped-readback").Should().BeLessThan(_trace.IndexOf("dispose"));
        _trace.Should().NotContain("metrics");
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [TestCase("command", false, true)]
    [TestCase("workflow", false, true)]
    [TestCase("command", true, true)]
    [TestCase("command", false, false)]
    [TestCase("workflow", false, false)]
    public async Task It_contains_initial_loss_across_enclosing_deadlines_during_persistence(
        string deadline,
        bool cancelCaller,
        bool observeLoss
    )
    {
        await InterruptAfterEstablishmentAsync();
        ShortTiming(250); // 1.25s workflow deadline; independent 250ms persistence calls.
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath))!;
        settings["Cdc:Timing:WaitMilliseconds"] = deadline == "command" ? "900" : "5000";
        settings["Cdc:Timing:CallMilliseconds"] = "250";
        settings["Cdc:Timing:PollMilliseconds"] = "5";
        await File.WriteAllTextAsync(_settingsPath, settings.ToJsonString());
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var real = _bindings;
        var delayed = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => delayed.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken ct) => real.ExactMatchBindingAsync(binding, ct)
            );
        using var caller = new CancellationTokenSource();
        A.CallTo(() => delayed.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcIncident incident, CancellationToken ct) =>
                {
                    Trace("persist");
                    Func<Task> contend = async () =>
                    {
                        await using var other = await _store.AcquireAsync(
                            TimeSpan.FromMilliseconds(15),
                            TimeSpan.FromMilliseconds(1),
                            default
                        );
                    };
                    await contend.Should().ThrowAsync<CdcWorkflowStateException>();
                    if (cancelCaller)
                    {
                        await caller.CancelAsync();
                    }
                    await Task.Delay(200, ct);
                    return await real.LatchSourceHistoryLossAsync(incident, ct);
                }
            );
        _bindings = delayed;
        ConfigureInitialStop();
        // Spend the original budget in ordinary barrier polling, then expose real lost offsets.
        A.CallTo(() =>
                _runtime.CaptureBarrierAsync(
                    A<CdcDeploymentRequest>._,
                    A<ICdcProviderSourcePositionAdapter>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
                Provider == Ddl.CdcProvider.Postgresql
                    ? CdcProviderBarrierCaptureResult.PostgresqlSuccess("0/20", DateTimeOffset.UtcNow)
                    : CdcProviderBarrierCaptureResult.SqlServerSuccess(
                        "00000001:00000002:0004",
                        "00000001:00000002:0004",
                        DateTimeOffset.UtcNow
                    )
            );
        _onCall = name =>
        {
            if (
                observeLoss
                && name == "offset"
                && elapsed.ElapsedMilliseconds > (deadline == "command" ? 800 : 1150)
            )
            {
                _offsetState = CdcConnectOffsetState.Missing;
            }
        };
        elapsed.Restart();
        if (cancelCaller)
        {
            await FluentActions
                .Awaiting(() => CommandAsync(caller.Token))
                .Should()
                .ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = await CommandAsync(caller.Token);
            result.Succeeded.Should().BeFalse();
            if (!observeLoss)
            {
                result.Diagnostics.Should().Contain(d => d.Failure == CdcDeploymentFailure.Timeout);
                (await real.ExactMatchBindingAsync(_request.Binding)).State!.Incident.Should().BeNull();
                _trace.Should().NotContain("persist").And.NotContain("stop");
                elapsed
                    .Elapsed.Should()
                    .BeLessThan(TimeSpan.FromMilliseconds(deadline == "command" ? 1300 : 1650));
                ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
                return;
            }
            _trace.Should().Contain("persist");
            (await real.ExactMatchBindingAsync(_request.Binding)).State!.Incident.Should().NotBeNull();
            _trace.Should().Contain("stopped-readback");
            _trace.IndexOf("persist").Should().BeLessThan(_trace.IndexOf("stop"));
            _trace.IndexOf("stopped-readback").Should().BeLessThan(_trace.IndexOf("dispose"));
        }
        _trace.Should().Contain("persist");
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }

    [TestCase("broker-start", false)]
    [TestCase("worker-start", false)]
    [TestCase("broker-start", true)]
    [TestCase("worker-start", true)]
    public async Task It_CdcWorkerStartup_serializes_initial_command_infrastructure(
        string boundary,
        bool cancel
    )
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        async Task Pause(CancellationToken token)
        {
            Trace(boundary);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            if (!cancel)
            {
                throw new IOException("controlled infrastructure failure");
            }
        }
        if (boundary == "broker-start")
        {
            A.CallTo(() =>
                    _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily((CdcDeploymentRequest _, CancellationToken token) => Pause(token));
        }
        else
        {
            A.CallTo(() =>
                    _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily((CdcDeploymentRequest _, CancellationToken token) => Pause(token));
        }
        var command = CommandAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var contender = _store.AcquireAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1), default);
        try
        {
            contender
                .IsCompleted.Should()
                .BeFalse(
                    "the initial workflow must retain authorization through both infrastructure effects"
                );
            if (cancel)
            {
                await caller.CancelAsync();
                await FluentActions.Awaiting(() => command).Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                release.SetResult();
                (await command).Succeeded.Should().BeFalse();
            }
        }
        finally
        {
            await caller.CancelAsync();
            release.TrySetResult();
            try
            {
                await command;
            }
            catch (OperationCanceledException)
            {
                // Join the cancelled command even when a preceding assertion failed.
            }
            await using var competing = await contender;
        }
        // Failed infrastructure must release the session; retry uses fresh original provenance.
        if (!cancel)
        {
            A.CallTo(() =>
                    _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .Returns(Task.CompletedTask);
            A.CallTo(() =>
                    _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .Returns(Task.CompletedTask);
            (await CommandAsync()).Succeeded.Should().BeTrue();
        }
    }

    [TestCase("provider")]
    [TestCase("broker-start")]
    [TestCase("worker-start")]
    [TestCase("preflight")]
    [TestCase("post-after")]
    [TestCase("barrier")]
    [TestCase("metrics")]
    public async Task It_resumes_the_same_command_after_a_stage_interruption(string boundary)
    {
        var bindingPath = Directory
            .GetFiles(Path.Combine(_root, "bindings"), "*.json", SearchOption.AllDirectories)
            .Single();
        var binding = (await File.ReadAllBytesAsync(bindingPath));
        var workflow = ReadJournal().WorkflowId;
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == boundary)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            (await CommandAsync(cancellation.Token)).Succeeded.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            cancellation.IsCancellationRequested.Should().BeTrue();
        }
        _trace.Should().Contain(boundary);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        var interrupted = ReadJournal();
        var consumptionPossible = interrupted.Operations.Any(o =>
            o.Effect == CdcWorkflowEffect.RegisterConnector
        );
        _onCall = _ => { };
        _calls.Clear();
        _trace.Clear();
        int barriers = _barriers;
        var result = await CommandAsync();
        result
            .Succeeded.Should()
            .BeTrue(JsonSerializer.Serialize(result.Diagnostics) + string.Join(",", _trace));
        ReadJournal().WorkflowId.Should().Be(workflow);
        ReadJournal().WriterPublicationAuthorized.Should().BeTrue();
        _barriers.Should().BeGreaterThan(barriers);
        _posts.Should().Be(1);
        (await File.ReadAllBytesAsync(bindingPath)).Should().Equal(binding);
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        if (consumptionPossible)
        {
            _calls
                .Should()
                .OnlyContain(c =>
                    c.Mode == Ddl.CdcProviderSetupMode.ValidateOnly && !c.RequireUnconsumedInitialSlot
                );
            _trace.Should().NotContain("broker-start");
            _trace.Should().NotContain("worker-start");
        }
        var before = (await File.ReadAllBytesAsync(JournalPath));
        (await CommandAsync())
            .Succeeded.Should()
            .BeFalse("publication intent closes initial retry permanently");
        (await File.ReadAllBytesAsync(JournalPath)).Should().Equal(before);
    }

    [TestCase("provider-missing")]
    [TestCase("connector-missing")]
    [TestCase("offset-missing")]
    [TestCase("config-drift")]
    [TestCase("cache-ahead")]
    [TestCase("nonempty")]
    public async Task It_rejects_changed_live_evidence_after_interrupted_readiness(string defect)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == "metrics")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            (await CommandAsync(cancellation.Token)).Succeeded.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            cancellation.IsCancellationRequested.Should().BeTrue();
        }
        _trace.Should().Contain("metrics");
        _onCall = _ => { };
        _trace.Clear();
        switch (defect)
        {
            case "provider-missing":
                _exists = false;
                break;
            case "connector-missing":
                _connectorExists = false;
                break;
            case "offset-missing":
                _offsetState = CdcConnectOffsetState.Missing;
                break;
            case "config-drift":
                _live["topic.prefix"] = "changed";
                break;
            case "cache-ahead":
                _latch = true;
                break;
            case "nonempty":
                _rows = true;
                break;
        }
        var retained = ReadJournal().Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider);
        (await CommandAsync()).Succeeded.Should().BeFalse();
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        JsonSerializer
            .Serialize(ReadJournal().Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider))
            .Should()
            .Be(JsonSerializer.Serialize(retained));
        _posts.Should().Be(1);
        _trace.Should().NotContain("broker-start");
    }

    [TestCase("missing")]
    [TestCase("corrupt")]
    [TestCase("source-history-only")]
    [TestCase("source-mismatch")]
    [TestCase("missing-history")]
    [TestCase("missing-binding")]
    [TestCase("cache-ahead")]
    [TestCase("nonempty")]
    [TestCase("managed-stop")]
    [TestCase("contradictory-provider")]
    public async Task It_rejects_ineligible_original_evidence_before_provider_or_Kafka_effects(string defect)
    {
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        switch (defect)
        {
            case "missing":
                File.Delete(JournalPath);
                break;
            case "corrupt":
                await File.WriteAllTextAsync(JournalPath, "{");
                break;
            case "source-history-only":
                journal["purpose"] = "SourceHistoryOnly";
                var operations = journal["operations"]!.AsArray();
                while (operations.Count > 2)
                {
                    operations.RemoveAt(operations.Count - 1);
                }
                await File.WriteAllTextAsync(JournalPath, journal.ToJsonString());
                break;
            case "source-mismatch":
                journal["operations"]![1]!["completions"]![0]!["evidence"]!["physicalSourceFingerprint"] =
                    "sha256:" + new string('f', 64);
                await File.WriteAllTextAsync(JournalPath, journal.ToJsonString());
                break;
            case "missing-history":
                Directory.Delete(Path.Combine(_root, "source-history"), true);
                break;
            case "missing-binding":
                Directory.Delete(Path.Combine(_root, "bindings"), true);
                break;
            case "cache-ahead":
                _latch = true;
                break;
            case "nonempty":
                _rows = true;
                break;
            case "managed-stop":
                await CompleteAsync(CdcWorkflowEffect.StopConnector);
                break;
            case "contradictory-provider":
                journal["operations"]![3]!["effect"] = "CreateProvider";
                await File.WriteAllTextAsync(JournalPath, journal.ToJsonString());
                break;
        }
        var result = await CommandAsync();
        result.Succeeded.Should().BeFalse();
        _trace.Should().NotContain("provider");
        _trace.Should().NotContain("broker-start");
        _posts.Should().Be(0);
        JsonSerializer.Serialize(result).Should().NotContain("private-source");
    }
}
