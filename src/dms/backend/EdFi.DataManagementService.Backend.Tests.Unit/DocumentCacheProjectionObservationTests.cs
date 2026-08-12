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
    public void It_caps_target_level_diagnostics_without_document_ids()
    {
        const int effectiveProjectorPageSize = 2;
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));

        store.ObserveTarget(
            TargetHealth(
                generation: 1,
                effectiveProjectorPageSize,
                targetDiagnostics:
                [
                    TargetDiagnostic("first"),
                    TargetDiagnostic("second"),
                    TargetDiagnostic("third"),
                ],
                observedAt: ObservedAt
            )
        );

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;

        current.TargetDiagnostics.Select(diagnostic => diagnostic.Message).Should().Equal("second", "third");
        current.FailureDiagnostics.DocumentIds.Should().BeEmpty();
    }

    [Test]
    public void It_appends_target_diagnostic_without_losing_newer_scheduler_observation()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheAdministrativeCommandExecutionId executionId =
            DocumentCacheAdministrativeCommandExecutionId.New();
        DocumentCacheProjectionSuccessSnapshot lastSuccess = new(
            documentId: 701,
            contentVersion: 8001,
            completedAt: ObservedAt.AddSeconds(1)
        );

        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt));
        DocumentCacheProjectionTargetContextKey contextKey = store
            .CurrentSnapshot.GetCurrentTarget(TargetKey)!
            .ContextKey;
        store.ObserveTarget(
            TargetHealth(
                generation: 1,
                failureDocumentIds: [201, 202],
                suppressedDocumentIds: [301],
                observedAt: ObservedAt.AddSeconds(1),
                lastSuccess: lastSuccess,
                activeCommandExecutionId: executionId,
                activeAdministrativeCommand: DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                activeAdministrativePhase: DocumentCacheAdministrativeCommandPhase.DrainWork
            )
        );

        store.AppendTargetDiagnostic(
            contextKey,
            TargetDiagnostic("read-path invariant"),
            ObservedAt.AddSeconds(2)
        );

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current.TargetDiagnostics.Should().ContainSingle().Which.Message.Should().Be("read-path invariant");
        current.FailureDiagnostics.DocumentIds.Should().Equal(201, 202);
        current.PoisonTraversal.SuppressedDocumentIds.Should().Equal(301);
        current.LastSuccess.Should().BeSameAs(lastSuccess);
        current.ActiveCommandExecutionId.Should().Be(executionId);
        current
            .ActiveAdministrativeCommand.Should()
            .Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
        current.ActiveAdministrativePhase.Should().Be(DocumentCacheAdministrativeCommandPhase.DrainWork);
    }

    [Test]
    public void It_retains_bounded_appended_target_diagnostics_across_later_scheduler_observations()
    {
        const int effectiveProjectorPageSize = 2;
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheProjectionTargetContextKey contextKey = ContextKey(generation: 1);

        store.ObserveTarget(TargetHealth(generation: 1, effectiveProjectorPageSize, observedAt: ObservedAt));
        store.AppendTargetDiagnostic(contextKey, TargetDiagnostic("first"), ObservedAt.AddSeconds(1));
        store.AppendTargetDiagnostic(contextKey, TargetDiagnostic("second"), ObservedAt.AddSeconds(2));
        store.AppendTargetDiagnostic(contextKey, TargetDiagnostic("third"), ObservedAt.AddSeconds(3));
        store.ObserveTarget(
            TargetHealth(
                generation: 1,
                effectiveProjectorPageSize,
                failureDocumentIds: [401],
                observedAt: ObservedAt.AddSeconds(4)
            )
        );

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current.TargetDiagnostics.Select(diagnostic => diagnostic.Message).Should().Equal("second", "third");
        current.FailureDiagnostics.DocumentIds.Should().Equal(401);
    }

    [Test]
    public void It_buffers_target_diagnostic_until_first_target_observation()
    {
        RecordingProjectionTelemetry telemetry = new();
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt), telemetry);
        DocumentCacheProjectionTargetContextKey contextKey = ContextKey(generation: 1);

        store.AppendTargetDiagnostic(
            contextKey,
            TargetDiagnostic("read-path invariant before supervisor observation"),
            ObservedAt.AddSeconds(1)
        );
        store.CurrentSnapshot.CurrentTargetHealth.Should().BeEmpty();

        store.ObserveTarget(
            TargetHealth(generation: 1, failureDocumentIds: [401], observedAt: ObservedAt.AddSeconds(2))
        );

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current
            .TargetDiagnostics.Should()
            .ContainSingle()
            .Which.Message.Should()
            .Be("read-path invariant before supervisor observation");
        current.FailureDiagnostics.DocumentIds.Should().Equal(401);

        DocumentCacheProjectionTargetHealthSnapshot recorded = telemetry
            .TargetObservations.Should()
            .ContainSingle()
            .Subject;
        recorded
            .TargetDiagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(current.TargetDiagnostics[0]);
    }

    [Test]
    public void It_caps_pending_target_diagnostics_when_first_target_observation_arrives()
    {
        const int effectiveProjectorPageSize = 2;
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheProjectionTargetContextKey contextKey = ContextKey(generation: 1);

        store.AppendTargetDiagnostic(contextKey, TargetDiagnostic("first"), ObservedAt.AddSeconds(1));
        store.AppendTargetDiagnostic(contextKey, TargetDiagnostic("second"), ObservedAt.AddSeconds(2));
        store.AppendTargetDiagnostic(contextKey, TargetDiagnostic("third"), ObservedAt.AddSeconds(3));
        store.ObserveTarget(
            TargetHealth(generation: 1, effectiveProjectorPageSize, observedAt: ObservedAt.AddSeconds(4))
        );

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current.TargetDiagnostics.Select(diagnostic => diagnostic.Message).Should().Equal("second", "third");
    }

    [Test]
    public void It_uses_configured_pending_target_diagnostic_limit_before_first_target_observation()
    {
        const int pendingTargetDiagnosticLimit = 3;
        const int appendedDiagnosticCount = pendingTargetDiagnosticLimit + 5;
        const int effectiveProjectorPageSize = pendingTargetDiagnosticLimit + 50;
        DocumentCacheProjectionObservationStore store = new(
            new FixedTimeProvider(ObservedAt),
            pendingTargetDiagnosticLimit
        );
        DocumentCacheProjectionTargetContextKey contextKey = ContextKey(generation: 1);

        foreach (int diagnosticIndex in Enumerable.Range(1, appendedDiagnosticCount))
        {
            store.AppendTargetDiagnostic(
                contextKey,
                TargetDiagnostic($"diagnostic {diagnosticIndex}"),
                ObservedAt.AddSeconds(diagnosticIndex)
            );
        }

        store.ObserveTarget(
            TargetHealth(generation: 1, effectiveProjectorPageSize, observedAt: ObservedAt.AddMinutes(1))
        );

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current
            .TargetDiagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .Equal(
                Enumerable
                    .Range(
                        appendedDiagnosticCount - pendingTargetDiagnosticLimit + 1,
                        pendingTargetDiagnosticLimit
                    )
                    .Select(diagnosticIndex => $"diagnostic {diagnosticIndex}")
            );
    }

    [Test]
    public void It_discards_pending_target_diagnostics_when_unobserved_context_ends()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheProjectionTargetContextKey contextKey = ContextKey(generation: 1);

        store.AppendTargetDiagnostic(
            contextKey,
            TargetDiagnostic("read-path invariant before supervisor observation"),
            ObservedAt.AddSeconds(1)
        );
        store.EndTargetContext(
            contextKey,
            DocumentCacheProjectionTargetEndReason.Removed,
            ObservedAt.AddSeconds(2)
        );
        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt.AddSeconds(3)));

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current.TargetDiagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_merge_pending_target_diagnostics_from_an_older_generation()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));

        store.AppendTargetDiagnostic(
            ContextKey(generation: 1),
            TargetDiagnostic("stale read-path invariant"),
            ObservedAt.AddSeconds(1)
        );
        store.ObserveTarget(TargetHealth(generation: 2, observedAt: ObservedAt.AddSeconds(2)));

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current.Generation.Value.Should().Be(2);
        current.TargetDiagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_keeps_pending_target_diagnostics_for_new_generation_when_noncurrent_generation_observes()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheProjectionTargetContextKey oldContextKey = ContextKey(generation: 1);
        DocumentCacheProjectionTargetContextKey newContextKey = ContextKey(generation: 2);

        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt));
        store.MarkTargetContextNoncurrent(oldContextKey, ObservedAt.AddSeconds(1));
        store.AppendTargetDiagnostic(
            newContextKey,
            TargetDiagnostic("new generation read-path invariant", generation: 2),
            ObservedAt.AddSeconds(2)
        );
        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt.AddSeconds(3)));
        store.ObserveTarget(TargetHealth(generation: 2, observedAt: ObservedAt.AddSeconds(4)));

        DocumentCacheProjectionTargetHealthSnapshot current = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        current.Generation.Value.Should().Be(2);
        current
            .TargetDiagnostics.Should()
            .ContainSingle()
            .Which.Message.Should()
            .Be("new generation read-path invariant");
    }

    [Test]
    public void It_ignores_late_target_diagnostic_from_ended_generation()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));

        store.ObserveTarget(TargetHealth(generation: 1, observedAt: ObservedAt));
        store.ObserveTarget(TargetHealth(generation: 2, observedAt: ObservedAt.AddSeconds(1)));
        store.AppendTargetDiagnostic(
            ContextKey(generation: 1),
            TargetDiagnostic("late read-path invariant"),
            ObservedAt.AddSeconds(2)
        );

        DocumentCacheProjectionObservationSnapshot snapshot = store.CurrentSnapshot;
        DocumentCacheProjectionTargetHealthSnapshot current = snapshot.GetCurrentTarget(TargetKey)!;
        current.Generation.Value.Should().Be(2);
        current.TargetDiagnostics.Should().BeEmpty();

        DocumentCacheProjectionTargetEndedDiagnosticSnapshot ended = snapshot
            .LastEndedTargetDiagnostics.Values.Should()
            .ContainSingle()
            .Subject;
        ended.Generation.Value.Should().Be(1);
        ended.FinalSnapshot.TargetDiagnostics.Should().BeEmpty();
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

    [Test]
    public void It_preserves_target_health_success_and_active_command_fields()
    {
        DocumentCacheProjectionObservationStore store = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheAdministrativeCommandExecutionId executionId =
            DocumentCacheAdministrativeCommandExecutionId.New();
        DocumentCacheProjectionSuccessSnapshot lastSuccess = new(
            documentId: 501,
            contentVersion: 6001,
            completedAt: ObservedAt.AddSeconds(1)
        );

        store.ObserveTarget(
            TargetHealth(
                generation: 1,
                lastSuccess: lastSuccess,
                activeCommandExecutionId: executionId,
                activeAdministrativeCommand: DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                activeAdministrativePhase: DocumentCacheAdministrativeCommandPhase.SeedBaseline
            )
        );

        DocumentCacheProjectionTargetHealthSnapshot snapshot = store.CurrentSnapshot.GetCurrentTarget(
            TargetKey
        )!;
        snapshot.LastSuccess.Should().NotBeNull();
        snapshot.LastSuccess!.DocumentId.Should().Be(501);
        snapshot.LastSuccess.ContentVersion.Should().Be(6001);
        snapshot.LastSuccess.CompletedAt.Should().Be(ObservedAt.AddSeconds(1));
        snapshot.ActiveCommandExecutionId.Should().Be(executionId);
        snapshot
            .ActiveAdministrativeCommand.Should()
            .Be(DocumentCacheAdministrativeCommand.OnlineCacheRebuild);
        snapshot.ActiveAdministrativePhase.Should().Be(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
    }

    private static DocumentCacheProjectionTargetContextKey ContextKey(long generation) =>
        new(TargetKey, new DocumentCacheTargetContextGeneration(generation));

    private static DocumentCacheProjectionTargetHealthSnapshot TargetHealth(
        long generation,
        int effectiveProjectorPageSize = 2,
        long[]? failureDocumentIds = null,
        long[]? suppressedDocumentIds = null,
        DateTimeOffset? observedAt = null,
        DocumentCacheProjectionSuccessSnapshot? lastSuccess = null,
        DocumentCacheAdministrativeCommandExecutionId? activeCommandExecutionId = null,
        DocumentCacheAdministrativeCommand? activeAdministrativeCommand = null,
        DocumentCacheAdministrativeCommandPhase? activeAdministrativePhase = null,
        DocumentCacheTargetDiagnostic[]? targetDiagnostics = null
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
            lastSuccess: lastSuccess
                ?? new DocumentCacheProjectionSuccessSnapshot(
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
            ),
            activeCommandExecutionId: activeCommandExecutionId,
            activeAdministrativeCommand: activeAdministrativeCommand,
            activeAdministrativePhase: activeAdministrativePhase,
            targetDiagnostics: targetDiagnostics
        );
    }

    private static DocumentCacheTargetDiagnostic TargetDiagnostic(string message, long generation = 1) =>
        new(
            TargetKey,
            DocumentCacheTargetResolutionState.Resolved,
            RelationalProviderToken.Postgresql,
            new DocumentCacheTargetContextGeneration(generation),
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            DocumentCacheTargetDiagnosticCategory.DeterministicInvariantFailure,
            message
        );

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

    private sealed class RecordingProjectionTelemetry : IDocumentCacheProjectionTelemetry
    {
        public List<DocumentCacheProjectionTargetHealthSnapshot> TargetObservations { get; } = [];

        public void RecordTargetObservation(DocumentCacheProjectionTargetHealthSnapshot snapshot) =>
            TargetObservations.Add(snapshot);

        public void RecordSchedulerDispatch(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            DocumentCacheProjectionSchedulerDispatchResult result,
            DocumentCacheProjectionDrainInvocationKind invocationKind
        ) => _ = targetContext;

        public void RecordItemOutcome(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            DocumentCacheProjectionDrainInvocationKind invocationKind,
            string outcome,
            string category,
            DocumentCacheLifecycleState? lifecycle = null
        ) => _ = targetContext;

        public void RecordAdministrativeCommandObservation(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
            RelationalProviderToken providerToken
        ) => _ = snapshot;

        public void RecordAdministrativeCommandMutation(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
            RelationalProviderToken providerToken
        ) => _ = snapshot;

        public void RecordAdministrativeCommandResult(
            DocumentCacheAdministrativeCommandResult result,
            RelationalProviderToken? providerToken,
            TimeSpan? effectiveWorkflowTimeout = null,
            DocumentCacheAdministrativeCommandPhase? currentPhase = null
        ) => _ = result;

        public void RecordAdministrativeMutexOutcome(
            DocumentCacheAdministrativeCommand command,
            DocumentCacheTargetKey targetKey,
            RelationalProviderToken providerToken,
            string outcome,
            DocumentCacheAdministrativeDiagnosticCategory? category,
            TimeSpan duration
        ) => _ = targetKey;
    }
}
