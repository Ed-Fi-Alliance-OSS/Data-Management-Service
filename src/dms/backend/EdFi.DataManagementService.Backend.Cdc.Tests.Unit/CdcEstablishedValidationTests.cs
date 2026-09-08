// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal partial class Given_CdcEstablishedValidation(Ddl.CdcProvider provider)
    : CdcReadinessTestBase(provider)
{
    private CdcEstablishedValidation _validation = null!;
    private Dictionary<string, byte[]> _before = null!;

    [SetUp]
    public void SetupValidation()
    {
        _validation = new(
            _store,
            _services.GetRequiredService<ICdcBindingLifecycleService>(),
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );
        _before = Snapshot();
        _calls.Clear();
        _trace.Clear();
        Fake.ClearRecordedCalls(_connect);
        Fake.ClearRecordedCalls(_kafka);
        Fake.ClearRecordedCalls(_runtime);
        _onWrite = _ => throw new AssertionException("Validation must not write deployment receipts.");
    }

    [TearDown]
    public void It_has_no_mutation_side_effects()
    {
        Snapshot().Should().BeEquivalentTo(_before);
        _calls
            .Should()
            .NotContain(r =>
                r.Mode != Ddl.CdcProviderSetupMode.ValidateOnly || r.RequireUnconsumedInitialSlot
            );
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() =>
                _runtime.CaptureBarrierAsync(
                    A<CdcDeploymentRequest>._,
                    A<ICdcProviderSourcePositionAdapter>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => _runtime.DisposeAsync()).MustNotHaveHappened();
        Fake.GetCalls(_connect)
            .Should()
            .NotContain(c => !c.Method.Name.StartsWith("Read", StringComparison.Ordinal));
        Fake.GetCalls(_kafka)
            .Should()
            .NotContain(c => !c.Method.Name.StartsWith("Inspect", StringComparison.Ordinal));
    }

    private Dictionary<string, byte[]> Snapshot() =>
        Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);

    private Task<CdcTransportResult<CdcEstablishedValidationObservation>> ValidateAsync(
        CdcEstablishedValidationMode mode = CdcEstablishedValidationMode.PreStart,
        CdcDeploymentIntegrityReport report = CdcDeploymentIntegrityReport.NoReportedLoss,
        CancellationToken token = default
    ) => _validation.ValidateAsync(_request, _runtime, mode, 1000, report, token);

    private async Task<CdcEstablishedValidationObservation> ObserveAsync(
        CdcEstablishedValidationMode mode = CdcEstablishedValidationMode.PreStart
    )
    {
        var result = await ValidateAsync(mode);
        return result
            .Should()
            .BeOfType<CdcTransportResult<CdcEstablishedValidationObservation>.Observed>(
                string.Join(",", result.Diagnostics.Select(d => d.ToString()))
            )
            .Subject.Value;
    }

    private void Stopped()
    {
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                var original = Status();
                return Observed(
                    new CdcConnectStatus(
                        original.Runtime with
                        {
                            ConnectorState = CdcConnectorRuntimeState.Stopped,
                            TaskCount = 0,
                            RunningTaskCount = 0,
                            SoleTaskState = CdcConnectorRuntimeState.Stopped,
                        },
                        original.WorkerId,
                        []
                    )
                );
            });
    }

    [Test]
    public async Task It_accepts_intact_stopped_provenance_without_running_metrics_or_projection_processing()
    {
        Stopped();
        var result = await ObserveAsync();
        result.Diagnostics.Should().BeEmpty();
        result.PreStartEligible.Should().BeTrue();
        result.PublicationReady.Should().BeFalse();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        _trace.Should().NotContain("metrics");
        _projectionReads.Should().Be(0);
    }

    [Test]
    public async Task It_accepts_a_failed_task_before_restart_without_lag()
    {
        _runtimeState = CdcConnectorRuntimeState.Failed;
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                return Observed(
                    new CdcConnectStatus(
                        status.Runtime with
                        {
                            LastErrorCategory = "connect-runtime-failed",
                        },
                        status.WorkerId,
                        status.Tasks
                    )
                );
            });
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeTrue();
        result.PublicationReady.Should().BeFalse();
        _trace.Should().NotContain("metrics");
    }

    [Test]
    public async Task It_requires_fresh_running_projection_and_lag_for_publication()
    {
        var result = await ObserveAsync(CdcEstablishedValidationMode.RunningPublication);
        result.Diagnostics.Should().BeEmpty();
        result.PublicationReady.Should().BeTrue();
        _projectionReads.Should().Be(1);
        _trace.Should().Contain("metrics");
        result.Evidence.ProviderBarrier.Should().BeNull();
        var second = await ObserveAsync(CdcEstablishedValidationMode.RunningPublication);
        second.Evidence.OperationId.Should().NotBe(result.Evidence.OperationId);
        JsonSerializer
            .Serialize(result)
            .Should()
            .NotContain(_request.Binding.PhysicalSourceFingerprint)
            .And.NotContain("worker:8083")
            .And.NotContain("lsn")
            .And.NotContain("password");
    }

    [Test]
    public async Task It_keeps_a_stopped_connector_not_ready_without_attempting_a_scrape()
    {
        Stopped();
        var result = await ObserveAsync(CdcEstablishedValidationMode.RunningPublication);
        result.PreStartEligible.Should().BeTrue();
        result.PublicationReady.Should().BeFalse();
        _trace.Should().NotContain("metrics");
    }

    [Test]
    public async Task It_rejects_exceeded_lag_independently_of_healthy_continuity()
    {
        _lag = 1001;
        var result = await ObserveAsync(CdcEstablishedValidationMode.RunningPublication);
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        result.PublicationReady.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Metrics);
    }

    [Test]
    public async Task It_rechecks_telemetry_age_after_final_worker_inspection()
    {
        _onCall = name =>
        {
            if (name == "worker" && _trace.Contains("metrics"))
            {
                _telemetryClock.Advance(_request.Timing.MaximumObservationAge + TimeSpan.FromSeconds(1));
            }
        };
        (await ObserveAsync(CdcEstablishedValidationMode.RunningPublication))
            .PublicationReady.Should()
            .BeFalse();
    }

    [TestCase("workflows")]
    [TestCase("bindings")]
    [TestCase("source-history")]
    public async Task It_rejects_missing_provenance_despite_healthy_artifacts(string directory)
    {
        Directory.Delete(Path.Combine(_root, directory), true);
        _before = Snapshot();
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [TestCase("workflows", "corrupt")]
    [TestCase("bindings", "corrupt")]
    [TestCase("source-history", "corrupt")]
    [TestCase("workflows", "unreadable")]
    [TestCase("bindings", "unreadable")]
    [TestCase("source-history", "unreadable")]
    public async Task It_rejects_untrusted_files(string directory, string damage)
    {
        var file = Directory
            .GetFiles(Path.Combine(_root, directory), "*.json", SearchOption.AllDirectories)
            .Single();
        if (damage == "corrupt")
        {
            await File.WriteAllTextAsync(file, "{private-corrupt-secret");
        }
        else if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
            );
        }
        _before = Snapshot();
        var result = await ValidateAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("private-corrupt-secret");
        _calls.Should().BeEmpty();
    }

    [TestCase(CdcDeploymentIntegrityReport.IncidentHistoryDeletion)]
    [TestCase(CdcDeploymentIntegrityReport.StateRollback)]
    [TestCase((CdcDeploymentIntegrityReport)99)]
    public async Task It_rejects_reported_loss_without_reconstructing_history(
        CdcDeploymentIntegrityReport report
    )
    {
        (await ValidateAsync(report: report)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [TestCase("CreateProvider")]
    [TestCase("EstablishConnector")]
    [TestCase("RegisterConnector")]
    public async Task It_requires_completed_establishment_and_provider_provenance(string effect)
    {
        EditJournal(node =>
        {
            var operation = node["operations"]!
                .AsArray()
                .Single(o => o!["effect"]!.GetValue<string>() == effect)!;
            operation["completions"] = new JsonArray();
        });
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_unsupported_workflow_version()
    {
        EditJournal(n => n["version"] = 99);
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    private void EditJournal(Action<JsonNode> edit)
    {
        var path = Directory
            .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
            .Single();
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        edit(node);
        File.WriteAllText(path, node.ToJsonString());
        _before = Snapshot();
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.Multiple)]
    [TestCase(CdcConnectOffsetState.SourcePartitionMismatch)]
    [TestCase(CdcConnectOffsetState.Null)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    [TestCase(CdcConnectOffsetState.Snapshot)]
    public async Task It_classifies_authoritative_offset_loss_without_latching_or_stopping(
        CdcConnectOffsetState state
    )
    {
        // Parse actual successful REST payloads, preserving the existing Core match contracts.
        string payload = state switch
        {
            CdcConnectOffsetState.Missing => "{\"offsets\":[]}",
            CdcConnectOffsetState.Multiple => "{\"offsets\":[{},{}]}",
            CdcConnectOffsetState.SourcePartitionMismatch => JsonSerializer.Serialize(
                new
                {
                    offsets = new[]
                    {
                        new
                        {
                            partition = new { server = "foreign", database = "foreign" },
                            offset = new { },
                        },
                    },
                }
            ),
            _ => JsonSerializer.Serialize(
                new
                {
                    offsets = new[]
                    {
                        new
                        {
                            partition = Offsets().SourcePartition,
                            offset = state switch
                            {
                                CdcConnectOffsetState.Null => (object)null!,
                                CdcConnectOffsetState.Snapshot => new { snapshot = true },
                                _ => new { invalid = true },
                            },
                        },
                    },
                }
            ),
        };
        using var doc = JsonDocument.Parse(payload);
        var evidence = CdcConnectOffsetEvidence.Parse(_request, doc.RootElement);
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(evidence));
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.SourceHistory.IncidentCandidate.Should().NotBeNull();
    }

    [TestCase(CdcDeploymentFailure.Unavailable)]
    [TestCase(CdcDeploymentFailure.Timeout)]
    [TestCase(CdcDeploymentFailure.AuthenticationFailed)]
    public async Task It_does_not_infer_missing_offsets_from_unavailable_queries(CdcDeploymentFailure failure)
    {
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Connect, failure)
                )
            );
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.SourceHistory.IncidentCandidate.Should().BeNull();
    }

    [Test]
    public async Task It_keeps_a_retained_incident_terminal_after_offsets_recover()
    {
        _identity = new('b', 64);
        var loss = (await ObserveAsync()).SourceHistory;
        _identity = new('a', 64);
        loss.IncidentCandidate.Should().NotBeNull();
        var bindingService = _services.GetRequiredService<ICdcBindingLifecycleService>();
        (await bindingService.LatchSourceHistoryLossAsync(loss.IncidentCandidate!.ToIncident()))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        _before = Snapshot();
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
    }

    [Test]
    public async Task It_rejects_same_named_recreated_provider_artifacts()
    {
        _identity = new('b', 64);
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.SourceHistory.IncidentCandidate.Should().NotBeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_an_empty_or_populated_different_physical_source(bool populated)
    {
        _rows = populated;
        var original = _change;
        _change = r =>
            original(r) with
            {
                ObservedSourceFingerprint = new(
                    Ddl.CdcSourceFingerprintMetadata.Version,
                    "sha256:" + new string('f', 64)
                ),
            };
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result.PublicationReady.Should().BeFalse();
    }

    [TestCase("tasks.max", "2")]
    [TestCase("producer.override.max.request.size", "1")]
    [TestCase("heartbeat.interval.ms", "0")]
    public async Task It_reports_live_configuration_drift_without_repair(string key, string value)
    {
        _live[key] = value;
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Connect);
    }

    [TestCase(CdcSqlServerSchemaHistoryState.Missing)]
    [TestCase(CdcSqlServerSchemaHistoryState.RequiredRecordLost)]
    [TestCase(CdcSqlServerSchemaHistoryState.Unknown)]
    public async Task It_keeps_schema_history_loss_distinct_from_unavailable_evidence(
        CdcSqlServerSchemaHistoryState state
    )
    {
        A.CallTo(() => _kafka.InspectSchemaHistoryAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(Observed(state));
        var result = await ObserveAsync();
        result
            .Continuity.Should()
            .Be(
                (Provider, state) switch
                {
                    (Ddl.CdcProvider.Postgresql, _) => CdcSourceHistoryContinuity.Healthy,
                    (_, CdcSqlServerSchemaHistoryState.Unknown) => CdcSourceHistoryContinuity.Unknown,
                    _ => CdcSourceHistoryContinuity.Lost,
                }
            );
    }

    [TestCase("provider")]
    [TestCase("worker")]
    [TestCase("status")]
    [TestCase("offset")]
    [TestCase("config")]
    [TestCase("topic")]
    [TestCase("metrics")]
    public async Task It_preserves_cancellation_at_each_boundary(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == stage)
            {
                cancellation.Cancel();
            }
        };
        Func<Task> act = async () =>
            await ValidateAsync(CdcEstablishedValidationMode.RunningPublication, token: cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_bounds_an_unresponsive_provider_call()
    {
        ShortTiming();
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .Returns(new TaskCompletionSource<Ddl.CdcProviderSetupResult>().Task);
        (await ValidateAsync())
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Failure.Should()
            .Be(CdcDeploymentFailure.Timeout);
    }

    [Test]
    public async Task It_holds_the_controller_lock_across_all_live_evidence()
    {
        A.CallTo(() => _connect.ReadConfigurationAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                Func<Task> compete = async () =>
                {
                    await using var session = await _store.AcquireAsync(
                        TimeSpan.FromMilliseconds(25),
                        TimeSpan.FromMilliseconds(5),
                        CancellationToken.None
                    );
                };
                await compete.Should().ThrowAsync<CdcWorkflowStateException>();
                return Observed<IReadOnlyDictionary<string, string>>(_live);
            });
        (await ObserveAsync()).PreStartEligible.Should().BeTrue();
    }
}
