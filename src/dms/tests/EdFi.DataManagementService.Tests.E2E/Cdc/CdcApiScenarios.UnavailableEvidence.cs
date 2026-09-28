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
    public Task UnavailableEvidenceAsync(CancellationToken token) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                const string scenarioId = "CDC-E2E-07";
                var binding = _context.Request.Binding;
                var runtime = phase.Runtime;
                string progressTopic = CdcArtifactNameGenerator
                    .RecoverFromBinding(binding)
                    .Inventory!.ProgressTopicName;
                var positions = await CaptureAsync(ct);
                var consumer = new MessageContractConsumer(DateTimeOffset.UtcNow);
                consumer.Assign(
                    positions
                        .Where(b => b.Topic == binding.TopicName)
                        .Select(b => new MessageContractPartitionBounds(
                            b.Partition,
                            b.EndOffset,
                            b.EndOffset
                        ))
                        .ToArray()
                );
                Dictionary<Guid, IReadOnlyDictionary<long, JsonElement>> expected = [];
                Dictionary<long, JsonElement> versions = [];
                var body = CdcApiClient.NewStudent("BeforeEvidenceFault");
                await phase.Gate.WaitUntilIdleAsync(ct);
                await runtime.StartProcessingAsync(ct);
                Guid uuid = await _context.Api.PostAsync(CdcApiResource.Student, body, ct);
                expected.Add(uuid, versions);
                var source = await ObserveApiAsync(ct);
                await AwaitPublicationAsync(CdcApiResource.Student, source, body, ct);
                await ScanAsync("healthy-publication", false, ct);
                AssertConsumed();
                await AwaitHealthyAsync(ct);
                var configuration = await ReadConfigurationAsync(ct);
                await AssertRetainedAsync(ct);
                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);
                await CheckpointAsync(scenarioId, "healthy-before-fault", source, source.ContentVersion);

                await phase.Gate.WaitUntilIdleAsync(ct);
                phase.Gate.Pause(source.DocumentId);
                try
                {
                    await phase.RunWithUnavailableOffsetsAsync(
                        async faultToken =>
                        {
                            int startCalls = phase.ConnectorStartCalls;
                            int reads = phase.UnavailableOffsetReads;
                            var unknown = (await phase.StatusAsync(faultToken))
                                .Targets.Should()
                                .ContainSingle()
                                .Subject;
                            CdcUnavailableEvidenceAssertions.AssertUnknown(unknown);
                            CdcUnavailableEvidenceAssertions.AssertNoConnectorStart(
                                startCalls,
                                phase.ConnectorStartCalls
                            );
                            await AssertLivePrerequisitesAsync(faultToken);
                            phase.UnavailableOffsetReads.Should().BeGreaterThan(reads);
                            await EventAsync("unknown-not-ready-offset-unavailable");
                            foreach (
                                var operation in new[]
                                {
                                    CdcManagedLifecycleOperation.Restart,
                                    CdcManagedLifecycleOperation.Resume,
                                }
                            )
                            {
                                reads = phase.UnavailableOffsetReads;
                                var rejected = await phase.ManageAsync(operation, faultToken);
                                CdcUnavailableEvidenceAssertions.AssertRejected(rejected, operation);
                                CdcUnavailableEvidenceAssertions.AssertNoConnectorStart(
                                    startCalls,
                                    phase.ConnectorStartCalls
                                );
                                await AssertLivePrerequisitesAsync(faultToken);
                                phase.UnavailableOffsetReads.Should().BeGreaterThan(reads);
                                phase.Runtime.Should().BeSameAs(runtime);
                                await EventAsync($"rejected-{operation}-Connect-Unavailable");
                            }
                            await AssertRetainedAsync(faultToken);
                            var previousSource = source;
                            long previous = source.ContentVersion;
                            body["firstName"] = "DuringEvidenceFault";
                            await _context.Api.PutAsync(CdcApiResource.Student, uuid, body, faultToken);
                            var held = await phase.Gate.WaitUntilPausedAsync(faultToken);
                            source = await ObserveApiAsync(faultToken);
                            source.ContentVersion.Should().BeGreaterThan(previous);
                            await AssertHeldAsync(source, held, faultToken);
                            var stale = (
                                await _context.Documents.ReadCacheAsync(source.DocumentId, faultToken)
                            )
                                .Should()
                                .ContainSingle()
                                .Subject;
                            stale.ContentVersion.Should().Be(previous);
                            CdcCrudAssertions.AssertCache(stale, previousSource, versions[previous]);
                            await CheckpointAsync(
                                scenarioId,
                                "api-success-work-held-during-fault",
                                source,
                                previous,
                                source.ContentVersion
                            );
                        },
                        ct
                    );
                }
                finally
                {
                    // RunWithUnavailableOffsetsAsync restores delegation even on failure/cancellation.
                    phase.Gate.Release();
                }

                var restoredAt = DateTimeOffset.UtcNow;
                await EventAsync("offset-delegation-restored");
                var recovered = await phase.ManageAsync(CdcManagedLifecycleOperation.Resume, ct);
                recovered.Succeeded.Should().BeTrue();
                recovered.Ready.Should().BeTrue();
                recovered.Operation.Should().Be(CdcManagedLifecycleOperation.Resume);
                CdcUnavailableEvidenceAssertions.AssertHealthy(recovered.Observation, binding, restoredAt);
                phase.Runtime.Should().BeSameAs(runtime);
                await AssertRetainedAsync(ct);
                await AwaitPublicationAsync(CdcApiResource.Student, source, body, ct);
                await ScanAsync("in-fault-write-recovered", false, ct);
                AssertConsumed();

                // Require a new API mutation after affirmative managed recovery on the original binding.
                long recoveredVersion = source.ContentVersion;
                body["firstName"] = "AfterEvidenceRecovery";
                await _context.Api.PutAsync(CdcApiResource.Student, uuid, body, ct);
                source = await ObserveApiAsync(ct);
                source.ContentVersion.Should().BeGreaterThan(recoveredVersion);
                await AwaitPublicationAsync(CdcApiResource.Student, source, body, ct);
                await ScanAsync("same-binding-recovery-consumed", false, ct);
                AssertConsumed();
                await AwaitHealthyAsync(ct);
                await AssertRetainedAsync(ct);
                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);
                await CheckpointAsync(
                    scenarioId,
                    "fresh-recovery-publication",
                    source,
                    source.ContentVersion
                );

                await _context.Api.DeleteAsync(CdcApiResource.Student, uuid, ct);
                await ScanAsync("cleanup-delete-consumed", true, ct);
                consumer.Documents.Should().BeEmpty();
                return true;

                async Task<CdcSourceDocument> ObserveApiAsync(CancellationToken cancellation)
                {
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(CdcApiResource.Student, uuid, cancellation),
                        body,
                        uuid
                    );
                    var observed = (await _context.Documents.ReadSourceAsync(uuid, cancellation))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    observed.EffectiveSchemaHash.Should().Be(_context.EffectiveSchemaHash);
                    versions.Add(
                        observed.ContentVersion,
                        CdcEnvelopeExpectations.Create(CdcApiResource.Student, body, observed)
                    );
                    return observed;
                }

                void AssertConsumed()
                {
                    consumer.Documents.Keys.Should().Equal(uuid.ToString("D"));
                    var actual = consumer.Documents[uuid.ToString("D")];
                    actual.ContentVersion.Should().Be(source.ContentVersion);
                    MessageContractJson.ShouldEqual(actual.Envelope, versions[source.ContentVersion]);
                }

                async Task AssertLivePrerequisitesAsync(CancellationToken cancellation)
                {
                    (await ReadConfigurationAsync(cancellation))
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .SequenceEqual(configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        .Should()
                        .BeTrue("live connector configuration must remain unchanged");
                    var status = (await _context.Connect.ReadStatusAsync(_context.Request, cancellation))
                        .Should()
                        .BeOfType<CdcTransportResult<CdcConnectStatus>.Observed>()
                        .Subject.Value;
                    status
                        .IsRunning.Should()
                        .BeTrue("missing offset proof must not hide stopped or failed tasks");
                }

                async Task<IReadOnlyDictionary<string, string>> ReadConfigurationAsync(
                    CancellationToken cancellation
                )
                {
                    var result = await _context.Connect.ReadConfigurationAsync(
                        _context.Request,
                        cancellation
                    );
                    // Compare privately: assertion failure must never print connector credentials.
                    return result
                        .Should()
                        .BeOfType<CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed>()
                        .Subject.Value;
                }

                async Task AssertRetainedAsync(CancellationToken cancellation) =>
                    CdcUnavailableEvidenceAssertions.AssertRetained(
                        await phase.ReadRetainedBindingAsync(cancellation),
                        binding
                    );

                async Task AwaitHealthyAsync(CancellationToken cancellation)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    deadline.CancelAfter(_context.Request.Timing.WaitTimeout);
                    var started = DateTimeOffset.UtcNow;
                    while (true)
                    {
                        var status = (await phase.StatusAsync(deadline.Token))
                            .Targets.Should()
                            .ContainSingle()
                            .Subject;
                        status.Status.SourceHistory.IncidentLatched.Should().BeFalse();
                        if (status.Status.Readiness == CdcReadiness.Ready)
                        {
                            CdcUnavailableEvidenceAssertions.AssertHealthy(status, binding, started);
                            return;
                        }
                        await Task.Delay(_context.Request.Timing.PollInterval, deadline.Token);
                    }
                }

                Task EventAsync(string name) =>
                    WriteDiagnosticAsync($"{scenarioId}:{name}: utc={DateTimeOffset.UtcNow:O}");

                async Task<IReadOnlyList<MessageContractKafkaBoundary>> CaptureAsync(
                    CancellationToken cancellation
                )
                {
                    var publicBounds = await _context.Kafka.CaptureKafkaBoundariesAsync(
                        binding.TopicName,
                        cancellation
                    );
                    var progressBounds = await _context.Kafka.CaptureKafkaBoundariesAsync(
                        progressTopic,
                        cancellation
                    );
                    return [.. publicBounds, .. progressBounds];
                }

                async Task ScanAsync(string checkpoint, bool allowTombstones, CancellationToken cancellation)
                {
                    string label = scenarioId + ":" + checkpoint;
                    await WriteDiagnosticAsync($"{label}:provider-fence-started");
                    if (binding.Provider == CdcProvider.Postgresql)
                    {
                        await _context.Fences.FencePostgresqlSourceAsync(label, cancellation);
                    }
                    else
                    {
                        await _context.Fences.FenceSqlServerSourceAsync(label, cancellation);
                    }
                    await WriteDiagnosticAsync($"{label}:provider-fence-completed");
                    var ends = await CaptureAsync(cancellation);
                    var bounds = ends.Select(end =>
                            end with
                            {
                                StartOffset = positions
                                    .Single(p => p.Topic == end.Topic && p.Partition == end.Partition)
                                    .EndOffset,
                            }
                        )
                        .ToArray();
                    var scan = await _context.Kafka.ConsumeThroughAsync(bounds, cancellation);
                    CdcCrudAssertions.ApplyPublicScan(
                        binding,
                        expected,
                        new(
                            scan.Records.Where(r => r.Topic == binding.TopicName).ToArray(),
                            scan.CompletedBoundaries.Where(b => b.Topic == binding.TopicName).ToArray()
                        ),
                        consumer,
                        allowTombstones
                    );
                    foreach (var record in scan.Records.Where(r => r.Topic == progressTopic))
                    {
                        MessageContractProgressAssertions.AssertHeartbeat(binding, record);
                    }
                    await _context.AssertTopicInventoryAsync(cancellation);
                    positions = scan.CompletedBoundaries;
                    foreach (var bound in positions)
                    {
                        await WriteDiagnosticAsync(
                            $"{label}: {(bound.Topic == binding.TopicName ? "public" : "progress")} partition={bound.Partition} start={bound.StartOffset} end={bound.EndOffset}"
                        );
                    }
                }
            },
            token
        );
}

internal static class CdcUnavailableEvidenceAssertions
{
    public static void AssertNoConnectorStart(int before, int after) =>
        after
            .Should()
            .Be(before, "unavailable continuity evidence must prevent any connector restart/resume effect");

    public static void AssertUnknown(CdcControllerTargetStatus observation)
    {
        var status = observation.Status;
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        status.SourceHistory.State.Should().Be(CdcComponentState.Unknown);
        status.SourceHistory.Category.Should().Be(CdcBlockingCategory.ProviderHistoryUnknown);
        status.SourceHistory.IncidentLatched.Should().BeFalse();
        observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.NotRequired);
        observation.Containment.Should().Be(CdcConnectorContainmentState.NotRequired);
        observation.Details.IncidentFailureCategory.Should().BeNull();
        observation
            .Details.Positions.UnavailableFacts.Should()
            .Equal(CdcIncidentUnavailableFact.ConnectOffset);
        observation
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Unavailable
            );
        // These are the production pre-start prerequisites. A different unavailable prerequisite
        // cannot masquerade as the intended rejection merely because the offset fault also fired.
        status.Binding.State.Should().Be(CdcComponentState.Satisfied);
        status.ProviderSetup.State.Should().Be(CdcComponentState.Satisfied);
        status.KafkaPolicy.State.Should().Be(CdcComponentState.Satisfied);
        status.ConnectOffsetStore.State.Should().Be(CdcComponentState.Satisfied);
        // Live template validation requires the offset source-partition proof; its absence also
        // invalidates configuration classification. The scenario independently compares the real
        // configuration to the healthy baseline, without printing its private values.
        status.ConnectorConfig.State.Should().Be(CdcComponentState.NotSatisfied);
        status.ConnectorConfig.Category.Should().Be(CdcBlockingCategory.ConnectorConfigInvalid);
        // REST omits snapshot state. Without streaming offset proof the controller cannot
        // resolve that omission; the scenario separately verifies actual RUNNING tasks.
        status.ConnectorRuntime.State.Should().Be(CdcComponentState.Unknown);
        status.ConnectorRuntime.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        observation.HasPendingRecordSizeIncrease.Should().BeFalse();
        observation.HasSharedOffsetStoreIssue.Should().BeFalse();
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
        AssertUnknown(result.Observation);
        result
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Unavailable
            );
    }

    public static void AssertHealthy(
        CdcControllerTargetStatus observation,
        CdcBinding binding,
        DateTimeOffset since
    )
    {
        observation.ObservedAt.Should().BeOnOrAfter(since);
        observation.Status.TargetIdentity.Should().Be(binding.ToTargetIdentity());
        observation.Status.Readiness.Should().Be(CdcReadiness.Ready);
        observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        observation.Status.SourceHistory.ObservedAt.Should().BeOnOrAfter(since);
        observation.Status.SourceHistory.IncidentLatched.Should().BeFalse();
        observation.Details.IncidentFailureCategory.Should().BeNull();
        observation.Diagnostics.Should().BeEmpty();
    }

    public static void AssertRetained(CdcBindingLifecycleResult result, CdcBinding binding)
    {
        result.Status.Should().Be(CdcControlPlaneOperationStatus.Succeeded);
        result.State.Should().NotBeNull();
        result.State!.Binding.Should().Be(binding);
        result.State.Incident.Should().BeNull();
        result.State.State.Should().Be(CdcBindingState.BindingPresent);
    }
}
