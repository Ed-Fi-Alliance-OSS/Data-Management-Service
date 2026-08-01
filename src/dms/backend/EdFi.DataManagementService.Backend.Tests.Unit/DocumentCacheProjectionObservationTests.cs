// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheProjectionObservation")]
public class Given_DocumentCacheProjectionObservationProvider
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = StartedAt.AddMinutes(5);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [Test]
    public void It_replaces_current_generation_without_reporting_old_evidence_as_current_health()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));

        store.ObserveTarget(
            TargetHealth(generation: 1, failureDocumentIds: [101, 102], observedAt: ObservedAt)
        );
        store.ObserveTarget(TargetHealth(generation: 2, observedAt: ObservedAt.AddSeconds(1)));

        DocumentCacheProjectionObservationSnapshot snapshot = store.CurrentSnapshot;

        DocumentCacheProjectionTargetHealthSnapshot current = snapshot.GetCurrentTarget(
            ContextKey(generation: 2)
        )!;
        current.Should().NotBeNull();
        current.Generation.Value.Should().Be(2);
        current.FailureDiagnostics.DocumentIds.Should().BeEmpty();
        snapshot.GetCurrentTarget(ContextKey(generation: 1)).Should().BeNull();

        DocumentCacheProjectionTargetEndedDiagnosticSnapshot ended = snapshot
            .LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Subject;
        ended.Generation.Value.Should().Be(1);
        ended.EndReason.Should().Be(DocumentCacheProjectionTargetEndReason.Replaced);
        ended.FinalSnapshot.FailureDiagnostics.DocumentIds.Should().Equal(101, 102);
    }

    [Test]
    public void It_keeps_active_command_snapshots_for_noncurrent_command_owned_generations()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheAdministrativeCommandExecutionId executionId = new(
            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
        );

        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt));
        store.ObserveAdministrativeCommand(
            CommandObservation(executionId, generation: 1, affectedDocumentIds: [301, 302])
        );
        store.ObserveTarget(TargetHealth(generation: 2, observedAt: ObservedAt.AddSeconds(1)));

        DocumentCacheProjectionObservationSnapshot snapshot = store.CurrentSnapshot;

        snapshot.ActiveAdministrativeCommands.Keys.Should().ContainSingle().Which.Should().Be(executionId);
        DocumentCacheAdministrativeCommandObservationSnapshot activeCommand = snapshot.GetActiveCommand(
            executionId
        )!;
        activeCommand.Should().NotBeNull();
        activeCommand.TargetGeneration.Value.Should().Be(1);
        activeCommand.IsCurrentGeneration.Should().BeFalse();
        activeCommand.CurrentTargetGeneration.Should().NotBeNull();
        activeCommand.CurrentTargetGeneration!.Value.Should().Be(2);
        activeCommand.CurrentPhase.Should().Be(DocumentCacheAdministrativeCommandPhase.DrainWork);
        activeCommand.PhaseDiagnostics.Should().ContainSingle();
    }

    [Test]
    public void It_caps_per_document_diagnostics_to_effective_projector_page_size()
    {
        const int effectiveProjectorPageSize = 2;
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheAdministrativeCommandExecutionId executionId = new(
            Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
        );

        store.ObserveTarget(
            TargetHealth(
                generation: 1,
                effectiveProjectorPageSize,
                failureDocumentIds: [1, 2, 3],
                suppressedDocumentIds: [4, 5, 6],
                observedAt: ObservedAt
            )
        );
        store.ObserveAdministrativeCommand(
            CommandObservation(
                executionId,
                generation: 1,
                effectiveProjectorPageSize,
                affectedDocumentIds: [10, 11, 12]
            )
        );

        DocumentCacheProjectionObservationSnapshot snapshot = store.CurrentSnapshot;

        DocumentCacheProjectionTargetHealthSnapshot current = snapshot.GetCurrentTarget(TargetKey)!;
        current.FailureDiagnostics.FailureCount.Should().Be(3);
        current.FailureDiagnostics.DocumentIds.Should().Equal(1, 2);
        current.PoisonTraversal.SuppressedDocumentCount.Should().Be(3);
        current.PoisonTraversal.SuppressedDocumentIds.Should().Equal(4, 5);

        DocumentCacheAdministrativePhaseDiagnostic phaseDiagnostic = snapshot
            .GetActiveCommand(executionId)!
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Subject;
        phaseDiagnostic.AffectedDocumentIds.Should().Equal(10, 11);
    }

    [Test]
    public void It_retains_only_one_last_ended_diagnostic_snapshot_per_target()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));

        store.ObserveTarget(TargetHealth(generation: 1, failureDocumentIds: [101], observedAt: ObservedAt));
        store.ObserveTarget(
            TargetHealth(generation: 2, failureDocumentIds: [201], observedAt: ObservedAt.AddSeconds(1))
        );
        store.ObserveTarget(
            TargetHealth(generation: 3, failureDocumentIds: [301], observedAt: ObservedAt.AddSeconds(2))
        );

        DocumentCacheProjectionObservationSnapshot snapshot = store.CurrentSnapshot;

        snapshot.CurrentTargetHealth.Values.Should().ContainSingle().Which.Generation.Value.Should().Be(3);
        DocumentCacheProjectionTargetEndedDiagnosticSnapshot ended = snapshot
            .LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Subject;
        ended.Generation.Value.Should().Be(2);
        ended.FinalSnapshot.FailureDiagnostics.DocumentIds.Should().Equal(201);
    }

    [Test]
    public void It_does_not_remove_the_current_generation_when_an_old_generation_ends_late()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));

        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt));
        store.ObserveTarget(TargetHealth(generation: 2, observedAt: ObservedAt.AddSeconds(1)));
        store.EndTargetContext(
            ContextKey(generation: 1),
            DocumentCacheProjectionTargetEndReason.Removed,
            ObservedAt.AddSeconds(2)
        );

        DocumentCacheProjectionObservationSnapshot snapshot = store.CurrentSnapshot;

        snapshot.CurrentTargetHealth.Values.Should().ContainSingle().Which.Generation.Value.Should().Be(2);
        snapshot
            .LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Which.Generation.Value.Should()
            .Be(1);
    }

    [Test]
    public void It_keeps_noncurrent_target_health_updates_out_of_current_health()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheProjectionTargetContextKey contextKey = ContextKey(generation: 1);

        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt));
        store.MarkTargetContextNoncurrent(contextKey, ObservedAt.AddSeconds(1));
        store.ObserveTarget(
            TargetHealth(generation: 1, failureDocumentIds: [401], observedAt: ObservedAt.AddSeconds(2))
        );

        DocumentCacheProjectionObservationSnapshot retainedSnapshot = store.CurrentSnapshot;

        retainedSnapshot.GetCurrentTarget(TargetKey).Should().BeNull();
        retainedSnapshot.LastEndedTargetDiagnostics.Should().BeEmpty();

        store.EndTargetContext(
            contextKey,
            DocumentCacheProjectionTargetEndReason.Removed,
            ObservedAt.AddSeconds(3)
        );

        DocumentCacheProjectionTargetEndedDiagnosticSnapshot ended = store
            .CurrentSnapshot.LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Subject;
        ended.Generation.Value.Should().Be(1);
        ended.EndReason.Should().Be(DocumentCacheProjectionTargetEndReason.Removed);
        ended.FinalSnapshot.FailureDiagnostics.DocumentIds.Should().Equal(401);
    }

    private static DocumentCacheProjectionTargetContextKey ContextKey(long generation) =>
        new(TargetKey, new DocumentCacheTargetContextGeneration(generation));

    private static DocumentCacheProjectionTargetHealthSnapshot TargetHealth(
        long generation,
        int effectiveProjectorPageSize = 2,
        long[]? failureDocumentIds = null,
        long[]? suppressedDocumentIds = null,
        DateTimeOffset? observedAt = null
    )
    {
        DateTimeOffset observationTime = observedAt ?? ObservedAt;
        long[] failureIds = failureDocumentIds ?? [];
        long[] suppressedIds = suppressedDocumentIds ?? [];

        return new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(generation),
            effectiveProjectorPageSize,
            observationTime,
            providerToken: RelationalProviderToken.Postgresql,
            physicalSourceFingerprint: Fingerprint,
            executionState: new DocumentCacheProjectionExecutionStateSnapshot(
                isRunning: true,
                isActivelyProcessing: true,
                isWaitingForWorkerGate: false,
                isInBackoff: failureIds.Length > 0,
                backoffUntil: failureIds.Length > 0 ? observationTime.AddSeconds(30) : null,
                cancellationRequested: false,
                cancellationObservedAt: null
            ),
            lastSuccess: new DocumentCacheProjectionSuccessSnapshot(
                documentId: 999,
                contentVersion: 1000,
                completedAt: observationTime
            ),
            pageThroughput: new DocumentCacheProjectionThroughputSnapshot(
                startedCount: 3,
                completedCount: 2,
                itemCount: 8,
                failureCount: failureIds.Length,
                lastStartedAt: observationTime.AddSeconds(-1),
                lastCompletedAt: observationTime,
                lastDuration: TimeSpan.FromSeconds(1)
            ),
            drainThroughput: new DocumentCacheProjectionThroughputSnapshot(
                startedCount: 2,
                completedCount: 1,
                itemCount: 5,
                failureCount: failureIds.Length,
                lastStartedAt: observationTime.AddSeconds(-2),
                lastCompletedAt: observationTime,
                lastDuration: TimeSpan.FromSeconds(2)
            ),
            lifecycleFence: new DocumentCacheProjectionLifecycleFenceSnapshot(
                DocumentCacheProjectionLifecycleFenceState.Eligible,
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
                observationTime,
                diagnosticCategory: null,
                message: "Tracking with clear cache-ahead latch."
            ),
            poisonTraversal: new DocumentCacheProjectionPoisonTraversalSnapshot(
                effectiveProjectorPageSize,
                suppressedIds.Length,
                suppressedIds.Length > 0 ? observationTime.AddSeconds(30) : null,
                suppressedIds
            ),
            failureDiagnostics: new DocumentCacheProjectionFailureDiagnostics(
                effectiveProjectorPageSize,
                failureIds.Length,
                failureIds.Length > 0 ? observationTime.AddSeconds(30) : null,
                evictionCount: 0,
                failureIds.Select(documentId => new DocumentCacheProjectionDocumentDiagnostic(
                    documentId,
                    DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
                    $"Work anomaly for document {documentId}.",
                    observationTime,
                    observationTime.AddSeconds(30)
                ))
            )
        );
    }

    private static DocumentCacheAdministrativeCommandObservationSnapshot CommandObservation(
        DocumentCacheAdministrativeCommandExecutionId executionId,
        long generation,
        int effectiveProjectorPageSize = 2,
        long[]? affectedDocumentIds = null
    )
    {
        ImmutableArray<long> affectedIds = (affectedDocumentIds ?? []).ToImmutableArray();
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> phaseDiagnostics =
        [
            new DocumentCacheAdministrativePhaseDiagnostic(
                DocumentCacheAdministrativeCommandPhase.DrainWork,
                DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                retryable: true,
                DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison,
                affectedIds,
                "Persistent poison documents are delaying drain."
            ),
        ];

        return new(
            executionId,
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            TargetKey,
            new DocumentCacheTargetContextGeneration(generation),
            effectiveProjectorPageSize,
            effectiveWorkflowTimeout: TimeSpan.FromHours(24),
            StartedAt,
            ObservedAt,
            DocumentCacheAdministrativeCommandPhase.DrainWork,
            DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            mutated: true,
            physicalSourceFingerprint: Fingerprint,
            lifecycle: DocumentCacheLifecycleState.Rebuilding,
            cacheAheadRecoveryRequired: false,
            offlineWriterAdmission: null,
            elapsedCommandTime: TimeSpan.FromMinutes(5),
            phaseDiagnostics
        );
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
