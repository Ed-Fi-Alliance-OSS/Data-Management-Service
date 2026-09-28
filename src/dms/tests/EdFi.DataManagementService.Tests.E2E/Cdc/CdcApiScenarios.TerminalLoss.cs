// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    // The fixed runner invokes this last. The terminal binding is left for governed teardown.
    public Task TerminalLossAsync(CancellationToken token) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                const string scenarioId = "CDC-E2E-08";
                var request = _context.Request;
                var binding = request.Binding;
                var runtime = phase.Runtime;
                var scanner = new ScenarioScan(this, scenarioId);
                var consumer = new MessageContractConsumer(DateTimeOffset.UtcNow);
                consumer.Assign(await scanner.InitializeAsync(ct));
                await phase.Gate.WaitUntilIdleAsync(ct);
                await runtime.StartProcessingAsync(ct);
                var body = CdcApiClient.NewStudent("BeforeTerminalLoss");
                Guid uuid = await _context.Api.PostAsync(CdcApiResource.Student, body, ct);
                var source = (await _context.Documents.ReadSourceAsync(uuid, ct))
                    .Should()
                    .ContainSingle()
                    .Subject;
                source.EffectiveSchemaHash.Should().Be(_context.EffectiveSchemaHash);
                CdcCrudAssertions.AssertApiBody(
                    await _context.Api.GetAsync(CdcApiResource.Student, uuid, ct),
                    body,
                    uuid
                );
                var envelope = CdcEnvelopeExpectations.Create(CdcApiResource.Student, body, source);
                await AwaitPublicationAsync(CdcApiResource.Student, source, body, ct);
                var scan = await scanner.ScanAsync("healthy-publication", ct);
                CdcCrudAssertions.ApplyPublicScan(
                    binding,
                    uuid,
                    new Dictionary<long, JsonElement> { [source.ContentVersion] = envelope },
                    scan,
                    consumer,
                    false
                );
                consumer.Documents.Keys.Should().Equal(uuid.ToString("D"));
                consumer.Documents[uuid.ToString("D")].ContentVersion.Should().Be(source.ContentVersion);
                MessageContractJson.ShouldEqual(consumer.Documents[uuid.ToString("D")].Envelope, envelope);
                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    deadline.CancelAfter(request.Timing.WaitTimeout);
                    var since = DateTimeOffset.UtcNow;
                    while (true)
                    {
                        var observation = (await phase.StatusAsync(deadline.Token))
                            .Targets.Should()
                            .ContainSingle()
                            .Subject;
                        observation.Status.SourceHistory.IncidentLatched.Should().BeFalse();
                        if (observation.Status.Readiness == CdcReadiness.Ready)
                        {
                            CdcUnavailableEvidenceAssertions.AssertHealthy(observation, binding, since);
                            break;
                        }
                        await Task.Delay(request.Timing.PollInterval, deadline.Token);
                    }
                }
                CdcUnavailableEvidenceAssertions.AssertRetained(
                    await phase.ReadRetainedBindingAsync(ct),
                    binding
                );
                int unavailableReads = phase.UnavailableOffsetReads;
                var healthyOffsets = CdcTerminalLossFault.Require(
                    await _context.Connect.ReadOffsetEvidenceAsync(request, ct)
                );
                healthyOffsets.State.Should().Be(CdcConnectOffsetState.Streaming);
                phase
                    .UnavailableOffsetReads.Should()
                    .Be(unavailableReads, "the phase 07 fault must have been removed");
                await CheckpointAsync(scenarioId, "healthy-before-loss", source, source.ContentVersion);

                await CdcTerminalLossFault.InjectAsync(_context.Connect, request, request.Timing, ct);
                await EventAsync("connector-stopped-tasks-zero-offsets-deleted-once-authoritative-Missing");
                var lost = (await phase.StatusAsync(ct)).Targets.Should().ContainSingle().Subject;
                CdcTerminalLossAssertions.AssertLost(lost);
                await CdcTerminalLossFault.VerifyStoppedAsync(_context.Connect, request, request.Timing, ct);
                var incident = CdcTerminalLossAssertions.AssertRetained(
                    await phase.ReadRetainedBindingAsync(ct),
                    binding
                );
                await EventAsync("lost-NotReady-ConnectOffsetMissing-persisted-contained");

                // Recreate production controllers (and their real local stores), retaining only transports
                // and the designated executor. Healthy offset replay never mutates committed offsets.
                await phase.RunWithHealthyOffsetsAsync(
                    healthyOffsets,
                    async replayToken =>
                    {
                        // Prove the offered evidence works even if a controller short-circuits its read.
                        CdcTerminalLossFault
                            .Require(await _context.Connect.ReadOffsetEvidenceAsync(request, replayToken))
                            .Should()
                            .BeSameAs(healthyOffsets);
                        int reads = phase.ReplayedOffsetReads;
                        int startCalls = phase.ConnectorStartCalls;
                        phase.ReopenControllers();
                        CdcTerminalLossAssertions.AssertLost(
                            (await phase.StatusAsync(replayToken)).Targets.Should().ContainSingle().Subject
                        );
                        foreach (
                            var operation in new[]
                            {
                                CdcManagedLifecycleOperation.Restart,
                                CdcManagedLifecycleOperation.Resume,
                            }
                        )
                        {
                            var rejected = await phase.ManageAsync(operation, replayToken);
                            CdcTerminalLossAssertions.AssertRejected(rejected, operation);
                            phase.Runtime.Should().BeSameAs(runtime);
                            await EventAsync($"retained-incident-rejected-{operation}");
                        }
                        phase
                            .ConnectorStartCalls.Should()
                            .Be(
                                startCalls,
                                "retained loss must reject before any connector restart/resume effect"
                            );
                        CdcTerminalLossAssertions
                            .AssertRetained(await phase.ReadRetainedBindingAsync(replayToken), binding)
                            .Should()
                            .BeEquivalentTo(incident);
                        await CdcTerminalLossFault.VerifyStoppedAsync(
                            _context.Connect,
                            request,
                            request.Timing,
                            replayToken
                        );
                        await EventAsync(
                            $"fresh-controller-retained-incident:healthy-offset-reads={phase.ReplayedOffsetReads - reads}"
                        );
                    },
                    ct
                );
                CdcTerminalLossFault
                    .Require(await _context.Connect.ReadOffsetEvidenceAsync(request, ct))
                    .State.Should()
                    .Be(
                        CdcConnectOffsetState.Missing,
                        "replay must restore real evidence without repairing offsets"
                    );
                await EventAsync("healthy-evidence-decorator-removed-real-offsets-Missing");

                await phase.Gate.WaitUntilIdleAsync(ct);
                phase.Gate.Pause(source.DocumentId);
                try
                {
                    body["firstName"] = "AfterTerminalContainment";
                    await _context.Api.PutAsync(CdcApiResource.Student, uuid, body, ct);
                    var held = await phase.Gate.WaitUntilPausedAsync(ct);
                    var updated = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    updated.DocumentId.Should().Be(source.DocumentId);
                    updated.ContentVersion.Should().BeGreaterThan(source.ContentVersion);
                    await AssertHeldAsync(updated, held, ct);
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(CdcApiResource.Student, uuid, ct),
                        body,
                        uuid
                    );
                    CdcCrudAssertions.AssertCache(
                        (await _context.Documents.ReadCacheAsync(source.DocumentId, ct))
                            .Should()
                            .ContainSingle()
                            .Subject,
                        source,
                        envelope
                    );
                    CdcTerminalLossAssertions.AssertLost(
                        (await phase.StatusAsync(ct)).Targets.Should().ContainSingle().Subject
                    );
                    CdcTerminalLossAssertions
                        .AssertRetained(await phase.ReadRetainedBindingAsync(ct), binding)
                        .Should()
                        .BeEquivalentTo(incident);
                    await CdcTerminalLossFault.VerifyStoppedAsync(
                        _context.Connect,
                        request,
                        request.Timing,
                        ct
                    );
                    await CheckpointAsync(
                        scenarioId,
                        "api-success-after-containment-work-held",
                        updated,
                        source.ContentVersion,
                        updated.ContentVersion
                    );
                }
                finally
                {
                    // Stop/cancel the held executor before disposal; leave the durable backlog for teardown.
                    await phase.StopRuntimeAsync();
                }
                return true;

                Task EventAsync(string name) => WriteDiagnosticAsync($"{scenarioId}:{name}");
            },
            token
        );
}

/// <summary>The sole destructive fault: verified stop, one offset deletion, authoritative Missing read-back.</summary>
internal static class CdcTerminalLossFault
{
    public static async Task InjectAsync(
        ICdcConnectTransport connect,
        CdcDeploymentRequest request,
        CdcDeploymentTiming timing,
        CancellationToken token
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timing.WaitTimeout);
        Require(await connect.StopAsync(request, deadline.Token));
        await VerifyStoppedAsync(connect, request, timing, deadline.Token);
        Require(await connect.DeleteOffsetsAsync(request, deadline.Token));
        Require(await connect.ReadOffsetEvidenceAsync(request, deadline.Token))
            .State.Should()
            .Be(CdcConnectOffsetState.Missing);
    }

    public static async Task VerifyStoppedAsync(
        ICdcConnectTransport connect,
        CdcDeploymentRequest request,
        CdcDeploymentTiming timing,
        CancellationToken token
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timing.WaitTimeout);
        while (true)
        {
            var status = Require(await connect.ReadStatusAsync(request, deadline.Token));
            if (status.IsStopped && status.Runtime.TaskCount == 0 && status.Runtime.RunningTaskCount == 0)
            {
                return;
            }
            await Task.Delay(timing.PollInterval, deadline.Token);
        }
    }

    public static T Require<T>(CdcTransportResult<T> result)
        where T : notnull => result.Should().BeOfType<CdcTransportResult<T>.Observed>().Subject.Value;
}

internal static class CdcTerminalLossAssertions
{
    public static void AssertLost(CdcControllerTargetStatus observation)
    {
        observation.Status.Readiness.Should().Be(CdcReadiness.NotReady);
        observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        observation.Status.SourceHistory.State.Should().Be(CdcComponentState.NotSatisfied);
        observation.Status.SourceHistory.Category.Should().Be(CdcBlockingCategory.SourceHistoryLost);
        observation.Status.SourceHistory.IncidentLatched.Should().BeTrue();
        observation
            .Details.IncidentFailureCategory.Should()
            .Be(CdcIncidentFailureCategory.ConnectOffsetMissing);
        observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
    }

    public static CdcIncident AssertRetained(CdcBindingLifecycleResult result, CdcBinding binding)
    {
        result.Status.Should().Be(CdcControlPlaneOperationStatus.Succeeded);
        result.State.Should().NotBeNull();
        result.State!.State.Should().Be(CdcBindingState.IncidentLatched);
        result.State.Binding.Should().Be(binding);
        result.State.Incident.Should().NotBeNull();
        var incident = result.State.Incident!;
        incident.IncidentType.Should().Be(CdcIncidentType.SourceHistoryContinuityLost);
        incident.FailureCategory.Should().Be(CdcIncidentFailureCategory.ConnectOffsetMissing);
        incident.BindingIdentity.Should().Be(binding.ToCompleteBindingIdentity());
        return incident;
    }

    public static void AssertRejected(
        CdcManagedLifecycleResult result,
        CdcManagedLifecycleOperation operation
    )
    {
        result.Operation.Should().Be(operation);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        result.Observation.Should().NotBeNull();
        AssertLost(result.Observation);
    }
}
