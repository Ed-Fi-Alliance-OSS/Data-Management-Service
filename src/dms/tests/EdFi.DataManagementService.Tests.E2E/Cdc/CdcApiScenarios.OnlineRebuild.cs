// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    public Task OnlineRebuildAsync(CancellationToken token) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                const string scenarioId = "CDC-E2E-05";
                var binding = _context.Request.Binding;
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
                List<CdcSourceDocument> sources = [];

                await phase.Gate.WaitUntilIdleAsync(ct);
                await phase.Runtime.StartProcessingAsync(ct);
                for (int index = 0; index < 3; index++)
                {
                    var body = CdcApiClient.NewStudent("Rebuild" + index);
                    Guid uuid = await _context.Api.PostAsync(CdcApiResource.Student, body, ct);
                    var source = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    sources.Add(source);
                    expected.Add(
                        uuid,
                        new Dictionary<long, JsonElement>
                        {
                            [source.ContentVersion] = CdcEnvelopeExpectations.Create(
                                CdcApiResource.Student,
                                body,
                                source
                            ),
                        }
                    );
                    await AwaitPublicationAsync(CdcApiResource.Student, source, body, ct);
                }
                await ScanAsync("published-before-rebuild", false, ct);
                CdcRebuildAssertions.AssertConsumer(consumer, expected);
                var before = await phase.Runtime.ObserveEstablishedDatabaseAsync(ct);
                CdcRebuildAssertions.AssertTracking(before, binding);
                var statusBefore = await AssertContinuityAsync();
                var offsetsBefore = await ReadOffsetsAsync();
                long versionSequence = await _context.Documents.ReadChangeVersionSequenceAsync(ct);
                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);
                await phase.Gate.WaitUntilIdleAsync(ct);

                // The command owns the provider mutex and production drain. No held item, second
                // supervisor, HTTP mutation or test gate participates in the administrative workflow.
                var rebuild = await phase.RebuildOnlineAsync(ct);
                foreach (var completed in rebuild.Phases)
                {
                    await WriteDiagnosticAsync(
                        $"{scenarioId}:administration: completed={completed.Phase} lifecycle={completed.Lifecycle} cacheAhead={completed.CacheAheadRecoveryRequired}"
                    );
                }
                CdcRebuildAssertions.AssertCompleted(rebuild);
                (await _context.Documents.ReadChangeVersionSequenceAsync(ct)).Should().Be(versionSequence);
                var after = await phase.Runtime.ObserveEstablishedDatabaseAsync(ct);
                CdcRebuildAssertions.AssertTracking(after, binding);
                after.TargetKey.Should().Be(before.TargetKey);
                after.Provider.Should().Be(before.Provider);
                foreach (var source in sources)
                {
                    // Rebuild must neither restamp canonical documents nor alter their identities.
                    (await _context.Documents.ReadSourceAsync(source.DocumentUuid, ct))
                        .Should()
                        .Equal(source);
                    (await _context.Documents.ReadWorkAsync(source.DocumentId, ct)).Should().BeEmpty();
                    var cache = (await _context.Documents.ReadCacheAsync(source.DocumentId, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    CdcCrudAssertions.AssertCache(
                        cache,
                        source,
                        expected[source.DocumentUuid][source.ContentVersion]
                    );
                    await CheckpointAsync(
                        scenarioId,
                        "repopulated-and-drained",
                        source,
                        source.ContentVersion
                    );
                }

                // Retain both reducer and partition positions. Every record spanning clearing and
                // repopulation is classified; even a transient tombstone followed by replay must fail.
                await ScanAsync("rebuild-consumed", false, ct);
                CdcRebuildAssertions.AssertConsumer(consumer, expected);
                var statusAfter = await AssertContinuityAsync();
                statusAfter.TargetIdentity.Should().Be(statusBefore.TargetIdentity);
                CdcRebuildAssertions.AssertOffsets(binding.Provider, offsetsBefore, await ReadOffsetsAsync());
                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);
                await WriteDiagnosticAsync(
                    $"{scenarioId}:rebuild-complete: liveKeys={expected.Count} consumerKeys={consumer.Documents.Count} explicitBaseline=true"
                );

                // Cleanup is outside the measured rebuild interval and follows ordinary canonical deletes.
                foreach (var source in sources)
                {
                    await _context.Api.DeleteAsync(CdcApiResource.Student, source.DocumentUuid, ct);
                    (await _context.Documents.ReadSourceAsync(source.DocumentUuid, ct)).Should().BeEmpty();
                    (await _context.Documents.ReadCacheAsync(source.DocumentId, ct)).Should().BeEmpty();
                    (await _context.Documents.ReadWorkAsync(source.DocumentId, ct)).Should().BeEmpty();
                }
                await ScanAsync("cleanup-deletes-consumed", true, ct);
                consumer.Documents.Should().BeEmpty();
                return true;

                async Task<CdcTargetStatus> AssertContinuityAsync()
                {
                    var status = (await phase.StatusAsync(ct))
                        .Targets.Should()
                        .ContainSingle()
                        .Subject.Status;
                    status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
                    status.SourceHistory.IncidentLatched.Should().BeFalse();
                    status.Binding.State.Should().Be(CdcComponentState.Satisfied);
                    return status;
                }

                async Task<CdcConnectOffsetEvidence> ReadOffsetsAsync()
                {
                    var result = await _context.Connect.ReadOffsetEvidenceAsync(_context.Request, ct);
                    return result
                        .Should()
                        .BeOfType<CdcTransportResult<CdcConnectOffsetEvidence>.Observed>()
                        .Subject.Value;
                }

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

internal static class CdcRebuildAssertions
{
    public static void AssertTracking(CdcInitialDatabaseObservation observation, CdcBinding binding)
    {
        observation.PhysicalSourceFingerprint.Should().Be(binding.PhysicalSourceFingerprint);
        observation.Lifecycle.State.Should().Be(DocumentCacheLifecycleState.Tracking);
        observation.Lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
        observation.Tables.CanonicalDocumentsEmpty.Should().BeFalse();
        observation.Tables.DocumentCacheEmpty.Should().BeFalse();
        observation.Tables.DocumentProjectionWorkEmpty.Should().BeTrue();
    }

    public static void AssertCompleted(CdcRebuildObservation observation)
    {
        observation.Result.Command.Should().Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
        observation.Result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        observation
            .Result.Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        observation.Result.Mutated.Should().BeTrue();
        DocumentCacheAdministrativeCommandPhase[] required =
        [
            DocumentCacheAdministrativeCommandPhase.EnterResetting,
            DocumentCacheAdministrativeCommandPhase.ClearCache,
            DocumentCacheAdministrativeCommandPhase.EnterRebuilding,
            DocumentCacheAdministrativeCommandPhase.CaptureBoundary,
            DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            DocumentCacheAdministrativeCommandPhase.DrainWork,
            DocumentCacheAdministrativeCommandPhase.EnterTracking,
            DocumentCacheAdministrativeCommandPhase.Complete,
        ];
        observation
            .Phases.Where(p => required.Contains(p.Phase))
            .Select(p => p.Phase)
            .Should()
            .Equal(required);
        foreach (var phase in observation.Phases.Where(p => required.Contains(p.Phase)))
        {
            var lifecycle = phase.Phase switch
            {
                DocumentCacheAdministrativeCommandPhase.EnterResetting
                or DocumentCacheAdministrativeCommandPhase.ClearCache =>
                    DocumentCacheLifecycleState.Resetting,
                DocumentCacheAdministrativeCommandPhase.EnterTracking
                or DocumentCacheAdministrativeCommandPhase.Complete => DocumentCacheLifecycleState.Tracking,
                _ => DocumentCacheLifecycleState.Rebuilding,
            };
            phase.Lifecycle.Should().Be(lifecycle);
            phase.CacheAheadRecoveryRequired.Should().BeFalse();
        }
    }

    public static void AssertConsumer(
        MessageContractConsumer consumer,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<long, JsonElement>> expected
    )
    {
        consumer.Documents.Keys.Should().BeEquivalentTo(expected.Keys.Select(key => key.ToString("D")));
        foreach (var (uuid, versions) in expected)
        {
            var envelope = versions.Single();
            var actual = consumer.Documents[uuid.ToString("D")];
            actual.ContentVersion.Should().Be(envelope.Key);
            MessageContractJson.ShouldEqual(actual.Envelope, envelope.Value);
        }
    }

    public static void AssertOffsets(
        CdcProvider provider,
        CdcConnectOffsetEvidence before,
        CdcConnectOffsetEvidence after
    )
    {
        before.State.Should().Be(CdcConnectOffsetState.Streaming);
        after.State.Should().Be(CdcConnectOffsetState.Streaming);
        after.SourcePartitionHash.Should().Be(before.SourcePartitionHash);
        if (provider == CdcProvider.Postgresql)
        {
            CdcPostgresqlProviderPosition
                .CompareCommittedOffsetToBarrier(
                    new(unchecked((ulong)before.Postgresql.LsnProc!.Value)),
                    after.Postgresql
                )
                .Succeeded.Should()
                .BeTrue();
        }
        else
        {
            // The post-rebuild fence commits a new heartbeat transaction. Compare commit LSNs
            // directly so a valid idle offset (change_lsn=NULL) is never parsed as a row position.
            var previous = CdcSqlServerProviderPositionParser.ParseLsn(
                before.SqlServer.CommitLsn!,
                "$.commitLsn"
            );
            var current = CdcSqlServerProviderPositionParser.ParseLsn(
                after.SqlServer.CommitLsn!,
                "$.commitLsn"
            );
            previous.Succeeded.Should().BeTrue();
            current.Succeeded.Should().BeTrue();
            current.Lsn!.Value.CompareTo(previous.Lsn!.Value).Should().BePositive();
        }
    }
}

internal sealed record CdcRebuildPhase(
    DocumentCacheAdministrativeCommandPhase Phase,
    DocumentCacheLifecycleState Lifecycle,
    bool CacheAheadRecoveryRequired
);

internal sealed record CdcRebuildObservation(
    DocumentCacheAdministrativeCommandResult Result,
    IReadOnlyList<CdcRebuildPhase> Phases
);

/// <summary>Only the online-rebuild scenario records phases. Retains one payload-free entry per
/// completed phase; ordinary target/status observations still reach the production store.</summary>
internal sealed class CdcRebuildObservations(DocumentCacheTargetKey target)
{
    private readonly object _sync = new();
    private readonly List<CdcRebuildPhase> _phases = [];
    private DocumentCacheAdministrativeCommandExecutionId _execution = null!;
    private bool _active;

    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IDocumentCacheProjectionObservationSink>(provider => new Sink(
            provider.GetRequiredService<DocumentCacheProjectionObservationStore>(),
            this
        ));

    public async Task<CdcRebuildObservation> RunAsync(
        Func<CancellationToken, Task<DocumentCacheAdministrativeCommandResult>> command,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        lock (_sync)
        {
            if (_active)
            {
                throw new InvalidOperationException("CDC_API_REBUILD_ALREADY_ACTIVE");
            }
            _phases.Clear();
            _execution = null!;
            _active = true;
        }
        try
        {
            var result = await command(timeout.Token);
            lock (_sync)
            {
                return new(result, _phases.ToArray());
            }
        }
        finally
        {
            lock (_sync)
            {
                _active = false;
            }
        }
    }

    internal void Observe(DocumentCacheAdministrativeCommandObservationSnapshot snapshot)
    {
        lock (_sync)
        {
            if (
                !_active
                || snapshot.Command != DocumentCacheAdministrativeCommand.OnlineCacheRebuild
                || snapshot.TargetKey != target
            )
            {
                return;
            }
            _execution ??= snapshot.ExecutionId;
            if (_execution != snapshot.ExecutionId)
            {
                throw new InvalidOperationException("CDC_API_REBUILD_MULTIPLE_EXECUTIONS");
            }
            if (
                snapshot.LastCompletedPhase is { } phase
                && snapshot.Lifecycle is { } lifecycle
                && snapshot.CacheAheadRecoveryRequired is { } latch
                && !_phases.Exists(p => p.Phase == phase)
            )
            {
                _phases.Add(new(phase, lifecycle, latch));
            }
        }
    }

    private sealed class Sink(
        DocumentCacheProjectionObservationStore inner,
        CdcRebuildObservations observations
    ) : IDocumentCacheProjectionObservationSink, IDocumentCacheProjectionCurrentTargetHealthSink
    {
        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) =>
            inner.ObserveTarget(snapshot);

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => inner.EndTargetContext(contextKey, endReason, endedAt);

        public void MarkTargetContextNoncurrent(
            DocumentCacheProjectionTargetContextKey contextKey,
            DateTimeOffset? observedAt = null
        ) => inner.MarkTargetContextNoncurrent(contextKey, observedAt);

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        )
        {
            inner.ObserveAdministrativeCommand(snapshot);
            observations.Observe(snapshot);
        }

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            inner.EndAdministrativeCommand(executionId);

        public void EndAdministrativeCommand(
            DocumentCacheAdministrativeCommandExecutionId executionId,
            DocumentCacheAdministrativeCommandResult result,
            DateTimeOffset? endedAt = null
        ) => inner.EndAdministrativeCommand(executionId, result, endedAt);
    }
}
