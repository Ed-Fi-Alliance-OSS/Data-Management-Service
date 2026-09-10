// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal partial class Given_CdcEstablishedValidation
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_blocks_a_pending_rollout_even_when_live_limits_match_the_requested_ceiling(
        bool aligned
    )
    {
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            _onWrite = _ => { };
            var journal = await session.ReadAsync(Target, CancellationToken.None);
            int ceiling = _request.ConnectorPolicy.MaxRecordBytes;
            await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.IncreaseRecordSize,
                [
                    new(
                        _request.Binding.ToCompleteBindingIdentity(),
                        aligned ? ceiling - 1 : ceiling,
                        aligned ? ceiling : ceiling + 1,
                        [new(Guid.NewGuid(), "operator", DateTimeOffset.UtcNow, true, [])]
                    ),
                ],
                CancellationToken.None
            );
        }
        _before = Snapshot();
        _onWrite = _ => throw new AssertionException("Validation must not complete a pending rollout.");
        var result = await ObserveAsync(CdcEstablishedValidationMode.RunningPublication);
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        result.HasPendingRecordSizeIncrease.Should().BeTrue();
        result.PreStartEligible.Should().BeFalse();
        result.PublicationReady.Should().BeFalse();
    }

    [Test]
    public async Task It_uses_the_current_operational_policy_after_a_completed_increase()
    {
        // The old payload hash remains provenance; it is not current size policy after a rollout.
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            _onWrite = _ => { };
            var journal = await session.ReadAsync(Target, CancellationToken.None);
            var id = Guid.NewGuid();
            int ceiling = _request.ConnectorPolicy.MaxRecordBytes;
            await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                id,
                CdcWorkflowEffect.IncreaseRecordSize,
                [
                    new(
                        _request.Binding.ToCompleteBindingIdentity(),
                        ceiling - 1,
                        ceiling,
                        [new(Guid.NewGuid(), "operator", DateTimeOffset.UtcNow, true, [])]
                    ),
                ],
                CancellationToken.None
            );
            await session.AppendAcknowledgementAsync(
                Target,
                journal.WorkflowId,
                id,
                new(Guid.NewGuid(), "operator", DateTimeOffset.UtcNow, true, []),
                CancellationToken.None
            );
            await session.ReconcileCompletionAsync(
                Target,
                journal.WorkflowId,
                id,
                (_, _) =>
                    Task.FromResult(Observed<CdcWorkflowCompletion>(new CdcWorkflowCompletion.Reconciled())),
                CancellationToken.None
            );
        }
        EditJournal(n =>
            n["operations"]!.AsArray().Single(o => o!["effect"]!.GetValue<string>() == "RegisterConnector")![
                "connectorRegistration"
            ]![0]!["configSha256"] = "sha256:" + new string('e', 64)
        );
        _onWrite = _ => throw new AssertionException("No writes.");
        (await ObserveAsync()).PreStartEligible.Should().BeTrue();
    }

    [TestCase("database.password")]
    [TestCase("producer.override.sasl.jaas.config")]
    public async Task It_reuses_the_existing_masked_credential_rules(string property)
    {
        if (_live.ContainsKey(property))
        {
            _live[property] = "********";
        }
        (await ObserveAsync()).PreStartEligible.Should().BeTrue();
    }

    [TestCase("provider")]
    [TestCase("worker")]
    [TestCase("config")]
    [TestCase("topic")]
    [TestCase("metrics")]
    public async Task It_sanitizes_adapter_exceptions(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-source-and-password");
            }
        };
        var result = await ValidateAsync(CdcEstablishedValidationMode.RunningPublication);
        if (result is CdcTransportResult<CdcEstablishedValidationObservation>.Observed observed)
        {
            observed.Value.PreStartEligible.Should().BeFalse();
            observed.Value.PublicationReady.Should().BeFalse();
        }
        else
        {
            result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        }
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("private-source-and-password");
    }

    [Test]
    public async Task It_rejects_worker_replacement_during_the_pass()
    {
        _onCall = name =>
        {
            if (name == "config")
            {
                _workerEvidence = new(
                    "replaced",
                    _workerEvidence.MetricsEndpoint,
                    _workerEvidence.EffectiveConfiguration,
                    _workerEvidence.ImageDigest,
                    _workerEvidence.HeapBytes,
                    _workerEvidence.ConnectWorkerId
                );
            }
        };
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase(CdcConnectorRuntimeState.Paused)]
    [TestCase(CdcConnectorRuntimeState.Unassigned)]
    [TestCase(CdcConnectorRuntimeState.Unknown)]
    public async Task It_does_not_authorize_start_from_unverified_task_states(CdcConnectorRuntimeState state)
    {
        _runtimeState = state;
        (await ObserveAsync()).PreStartEligible.Should().BeFalse();
    }

    [Test]
    public async Task It_accepts_the_real_running_connector_with_failed_task_shape()
    {
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var current = Status();
                return Observed(
                    new CdcConnectStatus(
                        current.Runtime with
                        {
                            SoleTaskState = CdcConnectorRuntimeState.Failed,
                            RunningTaskCount = 0,
                            LastErrorCategory = "connect-runtime-failed",
                        },
                        current.WorkerId,
                        [new(0, CdcConnectorRuntimeState.Failed, current.WorkerId)]
                    )
                );
            });
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeTrue();
        result.PublicationReady.Should().BeFalse();
    }

    [TestCase(DocumentCacheLifecycleState.Disabled)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding)]
    public async Task It_rejects_an_ineligible_current_projection_lifecycle(DocumentCacheLifecycleState state)
    {
        _lifecycle = state;
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_projection_backlog_for_publication_but_allows_a_continuous_restart()
    {
        _backlog = true;
        var result = await ObserveAsync(CdcEstablishedValidationMode.RunningPublication);
        result.PreStartEligible.Should().BeTrue();
        result.PublicationReady.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Projection);
    }

    [Test]
    public async Task It_does_not_authorize_start_with_unknown_shared_offset_store_policy()
    {
        A.CallTo(() =>
                _kafka.InspectTopicAsync(
                    A<CdcDeploymentRequest>._,
                    _request.WorkerPolicy.OffsetStorageTopic.Value,
                    A<CancellationToken>._
                )
            )
            .Returns(
                new CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                )
            );
        var result = await ObserveAsync();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        result.PreStartEligible.Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_classifies_provider_history_missing_or_unavailable(bool missing)
    {
        var original = _change;
        _change = r =>
        {
            var result = original(r);
            var state = missing
                ? Ddl.CdcProviderArtifactState.Missing
                : Ddl.CdcProviderArtifactState.Unavailable;
            return result with
            {
                ArtifactInventory = result
                    .ArtifactInventory.Select(a =>
                        a.ArtifactKind
                            is Ddl.CdcProviderArtifactKind.PostgresqlReplicationSlot
                                or Ddl.CdcProviderArtifactKind.SqlServerCaptureInstance
                            ? a with
                            {
                                State = state,
                            }
                            : a
                    )
                    .ToArray(),
                Outcome = Ddl.CdcProviderSetupOutcome.Failed,
            };
        };
        var result = await ObserveAsync();
        result.PreStartEligible.Should().BeFalse();
        result
            .Continuity.Should()
            .Be(missing ? CdcSourceHistoryContinuity.Lost : CdcSourceHistoryContinuity.Unknown);
    }

    [Test]
    public async Task It_rejects_missing_connector_configuration_without_creating_a_replacement()
    {
        _connectorExists = false;
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("worker")]
    [TestCase("status")]
    [TestCase("config")]
    [TestCase("topic")]
    public async Task It_preserves_terminal_evidence_when_a_later_inspection_fails(string stage)
    {
        _identity = new('b', 64);
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-downstream-failure");
            }
        };
        var result = await ObserveAsync();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.SourceHistory.IncidentCandidate.Should().NotBeNull();
        result.PreStartEligible.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public async Task It_keeps_an_absent_offset_endpoint_unknown()
    {
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcConnectOffsetEvidence>.Absent());
        var result = await ObserveAsync();
        result.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.PreStartEligible.Should().BeFalse();
    }

    [TestCase("workflowId")]
    [TestCase("receiptId")]
    [TestCase("source")]
    public async Task It_rejects_contradictory_source_history(string field)
    {
        var file = Directory
            .GetFiles(Path.Combine(_root, "source-history"), "*.json", SearchOption.AllDirectories)
            .Single();
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        if (field == "receiptId")
        {
            node["creationReceipt"]!["receiptId"] = Guid.NewGuid().ToString();
        }
        else if (field == "source")
        {
            node["physicalSourceFingerprint"] = "sha256:" + new string('f', 64);
        }
        else
        {
            node["workflowId"] = Guid.NewGuid().ToString();
        }
        await File.WriteAllTextAsync(file, node.ToJsonString());
        _before = Snapshot();
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_a_corrupt_retained_incident()
    {
        _identity = new('b', 64);
        var loss = (await ObserveAsync()).SourceHistory;
        var bindingService = _services.GetRequiredService<ICdcBindingLifecycleService>();
        (await bindingService.LatchSourceHistoryLossAsync(loss.IncidentCandidate!.ToIncident()))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        var file = Directory
            .GetFiles(Path.Combine(_root, "incidents"), "*.json", SearchOption.AllDirectories)
            .Single();
        await File.WriteAllTextAsync(file, "{private-corrupt-incident");
        _before = Snapshot();
        _calls.Clear();
        _identity = new('a', 64);
        (await ValidateAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [TestCase(CdcEstablishedValidationMode.PreStart, "")]
    [TestCase(CdcEstablishedValidationMode.PreStart, "private-other-broker:9092")]
    [TestCase(CdcEstablishedValidationMode.RunningPublication, "")]
    [TestCase(CdcEstablishedValidationMode.RunningPublication, "private-other-broker:9092")]
    public async Task It_rejects_worker_broker_drift_despite_otherwise_healthy_evidence(
        CdcEstablishedValidationMode mode,
        string endpoint
    )
    {
        ChangeWorkerBootstrap(endpoint);
        var result = await ValidateAsync(mode);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Component.Should()
            .Be(CdcDeploymentComponent.Worker);
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("private-other-broker");
    }

    [Test]
    public async Task It_identifies_an_authoritatively_absent_worker()
    {
        A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcWorkerInspection>.Absent());
        (await ValidateAsync())
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Component.Should()
            .Be(CdcDeploymentComponent.Worker);
    }
}
