// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheWriterClassification
{
    [Test]
    public void It_maps_healthy_pending_projection_to_candidate_write_and_conditional_acknowledgement()
    {
        DocumentCacheMaterializationCandidate candidate = CreateCandidate(contentVersion: 11);

        DocumentCacheWriterClassificationSelection selection = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateMatches(candidate)
        );

        selection.Action.Should().Be(DocumentCacheWriterSelectedAction.WriteCandidateThenAcknowledgeWork);
        selection.Outcome.Should().Be(DocumentCacheWriterOutcome.CandidateWrittenAcknowledged);
        selection.WritesCache.Should().BeTrue();
        selection.AcknowledgesWork.Should().BeTrue();
        selection.RequestsCacheAheadLatchFlow.Should().BeFalse();
        selection.ExpectedContentVersion.Should().Be(11);
        selection.Candidate.Should().BeSameAs(candidate);
        selection.TerminalResult.Should().BeNull();
    }

    [Test]
    public void It_selects_equal_version_acknowledgement_when_candidate_is_absent_or_stale()
    {
        DocumentCacheWriterClassificationSelection absentCandidate = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 11,
            workRequiredContentVersion: 11
        );
        DocumentCacheWriterClassificationSelection staleCandidate = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 11,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 10))
        );

        absentCandidate.Action.Should().Be(DocumentCacheWriterSelectedAction.AcknowledgeAlreadyCurrentWork);
        staleCandidate.Action.Should().Be(DocumentCacheWriterSelectedAction.AcknowledgeAlreadyCurrentWork);
        absentCandidate.ExpectedContentVersion.Should().Be(11);
        staleCandidate.ExpectedContentVersion.Should().Be(11);
        absentCandidate.WritesCache.Should().BeFalse();
        staleCandidate.WritesCache.Should().BeFalse();
        absentCandidate.AcknowledgesWork.Should().BeTrue();
        staleCandidate.AcknowledgesWork.Should().BeTrue();
    }

    [Test]
    public void It_selects_no_cache_dml_or_acknowledgement_when_cache_is_current_and_work_is_absent()
    {
        DocumentCacheWriterClassificationSelection selection = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 11,
            workRequiredContentVersion: null
        );

        selection.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnAlreadyCurrentWithoutWork);
        selection.Outcome.Should().Be(DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged);
        selection.WritesCache.Should().BeFalse();
        selection.AcknowledgesWork.Should().BeFalse();
        selection.RequestsCacheAheadLatchFlow.Should().BeFalse();
        selection.ExpectedContentVersion.Should().BeNull();
        selection.TerminalResult.Should().BeOfType<DocumentCacheWriterResult.AlreadyCurrentAcknowledged>();
    }

    [Test]
    public void It_requests_cache_ahead_latch_flow_only_for_current_cache_ahead_relationship()
    {
        DocumentCacheWriterClassificationSelection cacheAhead = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 12,
            workRequiredContentVersion: null
        );

        DocumentCacheWriterClassificationSelection lifecycleFence = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 12,
            workRequiredContentVersion: 11,
            cacheAheadRecoveryRequired: true
        );
        DocumentCacheWriterClassificationSelection missingSource = Select(
            sourceContentVersion: null,
            cacheContentVersion: 12,
            workRequiredContentVersion: 12
        );
        DocumentCacheWriterClassificationSelection staleCandidate = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 10))
        );
        DocumentCacheWriterClassificationSelection workMismatch = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 12,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 11))
        );
        DocumentCacheWriterClassificationSelection missingWork = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: null,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 11))
        );

        cacheAhead.Action.Should().Be(DocumentCacheWriterSelectedAction.RequestCacheAheadLatchFlow);
        cacheAhead.Outcome.Should().Be(DocumentCacheWriterOutcome.CacheAheadLatchSet);
        cacheAhead.WritesCache.Should().BeFalse();
        cacheAhead.AcknowledgesWork.Should().BeFalse();
        cacheAhead.RequestsCacheAheadLatchFlow.Should().BeTrue();

        new[] { lifecycleFence, missingSource, staleCandidate, workMismatch, missingWork }
            .Should()
            .OnlyContain(selection => !selection.RequestsCacheAheadLatchFlow);
    }

    [Test]
    public void It_selects_needs_materialization_when_current_work_exists_without_a_candidate()
    {
        DocumentCacheWriterClassificationSelection selection = Select(
            sourceContentVersion: 11,
            cacheContentVersion: null,
            workRequiredContentVersion: 11
        );

        selection.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnNeedsMaterialization);
        selection.WritesCache.Should().BeFalse();
        selection.AcknowledgesWork.Should().BeFalse();
        selection.ExpectedContentVersion.Should().BeNull();
        selection
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.NeedsMaterialization>()
            .Which.CurrentContentVersion.Should()
            .Be(11);
    }

    [Test]
    public void It_suppresses_stale_candidates_only_when_current_matching_work_still_needs_cache()
    {
        DocumentCacheWriterClassificationSelection staleCandidate = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 10))
        );
        DocumentCacheWriterClassificationSelection staleCandidateWithMetadataMismatch = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateWithMetadataMismatch(
                CreateCandidate(contentVersion: 10),
                DocumentCacheWriterCandidateMetadataComparison.DocumentUuidMismatch
            )
        );

        staleCandidate.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnStaleCandidateSuppressed);
        staleCandidate.WritesCache.Should().BeFalse();
        staleCandidate.AcknowledgesWork.Should().BeFalse();
        staleCandidate
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.StaleCandidateSuppressed>()
            .Which.CandidateContentVersion.Should()
            .Be(10);

        staleCandidateWithMetadataMismatch
            .Action.Should()
            .Be(DocumentCacheWriterSelectedAction.ReturnStaleCandidateSuppressed);
        staleCandidateWithMetadataMismatch
            .Outcome.Should()
            .Be(DocumentCacheWriterOutcome.StaleCandidateSuppressed);
    }

    [Test]
    public void It_reports_matching_version_candidate_metadata_mismatch_as_deterministic_invariant_failure()
    {
        DocumentCacheWriterClassificationSelection uuidMismatch = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateWithMetadataMismatch(
                CreateCandidate(contentVersion: 11),
                DocumentCacheWriterCandidateMetadataComparison.DocumentUuidMismatch
            )
        );
        DocumentCacheWriterClassificationSelection resourceMismatch = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateWithMetadataMismatch(
                CreateCandidate(contentVersion: 11),
                DocumentCacheWriterCandidateMetadataComparison.ResourceMetadataMismatch
            )
        );
        DocumentCacheWriterClassificationSelection targetMismatch = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            candidateObservation: CandidateWithMetadataMismatch(
                CreateCandidate(contentVersion: 11),
                DocumentCacheWriterCandidateMetadataComparison.TargetMappingMismatch
            )
        );

        uuidMismatch
            .Action.Should()
            .Be(DocumentCacheWriterSelectedAction.ReturnDeterministicInvariantOrTargetFailure);
        uuidMismatch.WritesCache.Should().BeFalse();
        uuidMismatch.AcknowledgesWork.Should().BeFalse();
        uuidMismatch.RequestsCacheAheadLatchFlow.Should().BeFalse();
        uuidMismatch
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.MatchingVersionDocumentUuidMismatch);

        resourceMismatch
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.MatchingVersionResourceMetadataMismatch);
        targetMismatch
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.TargetMappingMismatch);
    }

    [Test]
    public void It_leaves_work_version_mismatches_pending_for_repair()
    {
        DocumentCacheWriterClassificationSelection workBehind = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 10,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 11))
        );
        DocumentCacheWriterClassificationSelection workAhead = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 12,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 11))
        );

        workBehind.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnWorkAnomaly);
        workAhead.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnWorkAnomaly);
        workBehind.WritesCache.Should().BeFalse();
        workAhead.WritesCache.Should().BeFalse();
        workBehind.AcknowledgesWork.Should().BeFalse();
        workAhead.AcknowledgesWork.Should().BeFalse();
        workBehind
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.Kind.Should()
            .Be(DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch);
        workAhead
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.Kind.Should()
            .Be(DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch);
    }

    [Test]
    public void It_distinguishes_tracking_missing_work_from_possible_rebuilding_baseline_rows()
    {
        DocumentCacheWriterClassificationSelection tracking = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: null,
            lifecycleState: DocumentCacheLifecycleState.Tracking
        );
        DocumentCacheWriterClassificationSelection rebuilding = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: null,
            lifecycleState: DocumentCacheLifecycleState.Rebuilding
        );

        tracking.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnWorkAnomaly);
        rebuilding.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnWorkAnomaly);

        tracking
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.LifecycleState.Should()
            .Be(DocumentCacheLifecycleState.Tracking);
        rebuilding
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
            .Which.LifecycleState.Should()
            .Be(DocumentCacheLifecycleState.Rebuilding);
    }

    [Test]
    public void It_applies_same_no_dml_rules_for_direct_fill_when_no_current_matching_work_exists()
    {
        DocumentCacheWriterClassificationSelection missingWork = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: null,
            purpose: DocumentCacheWriterPurpose.DirectFill,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 11))
        );
        DocumentCacheWriterClassificationSelection mismatchedWork = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 12,
            purpose: DocumentCacheWriterPurpose.DirectFill,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 11))
        );

        missingWork.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnWorkAnomaly);
        mismatchedWork.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnWorkAnomaly);
        missingWork.WritesCache.Should().BeFalse();
        mismatchedWork.WritesCache.Should().BeFalse();
        missingWork.AcknowledgesWork.Should().BeFalse();
        mismatchedWork.AcknowledgesWork.Should().BeFalse();
    }

    [Test]
    public void It_uses_the_same_action_and_result_model_for_direct_fill_when_current_work_exists()
    {
        DocumentCacheMaterializationCandidate currentCandidate = CreateCandidate(contentVersion: 11);

        DocumentCacheWriterClassificationSelection healthyPending = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 10,
            workRequiredContentVersion: 11,
            purpose: DocumentCacheWriterPurpose.DirectFill,
            candidateObservation: CandidateMatches(currentCandidate)
        );
        DocumentCacheWriterClassificationSelection equalVersion = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 11,
            workRequiredContentVersion: 11,
            purpose: DocumentCacheWriterPurpose.DirectFill
        );
        DocumentCacheWriterClassificationSelection needsMaterialization = Select(
            sourceContentVersion: 11,
            cacheContentVersion: null,
            workRequiredContentVersion: 11,
            purpose: DocumentCacheWriterPurpose.DirectFill
        );
        DocumentCacheWriterClassificationSelection staleCandidate = Select(
            sourceContentVersion: 11,
            cacheContentVersion: null,
            workRequiredContentVersion: 11,
            purpose: DocumentCacheWriterPurpose.DirectFill,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 10))
        );
        DocumentCacheWriterClassificationSelection invariantFailure = Select(
            sourceContentVersion: 11,
            cacheContentVersion: null,
            workRequiredContentVersion: 11,
            purpose: DocumentCacheWriterPurpose.DirectFill,
            candidateObservation: CandidateWithMetadataMismatch(
                currentCandidate,
                DocumentCacheWriterCandidateMetadataComparison.TargetMappingMismatch
            )
        );

        healthyPending
            .Action.Should()
            .Be(DocumentCacheWriterSelectedAction.WriteCandidateThenAcknowledgeWork);
        healthyPending.Outcome.Should().Be(DocumentCacheWriterOutcome.CandidateWrittenAcknowledged);
        healthyPending.Candidate.Should().BeSameAs(currentCandidate);
        healthyPending.ExpectedContentVersion.Should().Be(11);
        healthyPending.TerminalResult.Should().BeNull();

        equalVersion.Action.Should().Be(DocumentCacheWriterSelectedAction.AcknowledgeAlreadyCurrentWork);
        equalVersion.Outcome.Should().Be(DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged);
        equalVersion.ExpectedContentVersion.Should().Be(11);

        needsMaterialization
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.NeedsMaterialization>()
            .Which.CurrentContentVersion.Should()
            .Be(11);
        staleCandidate
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.StaleCandidateSuppressed>()
            .Which.CandidateContentVersion.Should()
            .Be(10);
        invariantFailure
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterInvariantFailureReason.TargetMappingMismatch);
    }

    [Test]
    public void It_returns_source_missing_without_cache_dml_acknowledgement_or_latch_mutation()
    {
        DocumentCacheWriterClassificationSelection selection = Select(
            sourceContentVersion: null,
            cacheContentVersion: 12,
            workRequiredContentVersion: 12,
            candidateObservation: CandidateMatches(CreateCandidate(contentVersion: 12))
        );

        selection.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnSourceMissingOrDeleted);
        selection.Outcome.Should().Be(DocumentCacheWriterOutcome.SourceMissingOrDeleted);
        selection.WritesCache.Should().BeFalse();
        selection.AcknowledgesWork.Should().BeFalse();
        selection.RequestsCacheAheadLatchFlow.Should().BeFalse();
        selection.TerminalResult.Should().BeSameAs(DocumentCacheWriterResult.SourceMissingOrDeleted.Instance);
    }

    [Test]
    public void It_fences_lifecycle_or_set_latch_before_source_cache_work_actions()
    {
        DocumentCacheWriterClassificationSelection missingState =
            DocumentCacheWriterClassificationSelector.Select(
                new DocumentCacheWriterClassificationRequest(
                    DocumentCacheWriterPurpose.DurableWorkProjection,
                    DocumentCacheLifecycleReadResult.Failure(
                        DocumentCacheLifecycleReadStatus.Missing,
                        "missing"
                    ),
                    new DocumentCacheWriterCurrentStateObservation(11, 12, 11),
                    DocumentCacheWriterCandidateObservation.Absent
                )
            );
        DocumentCacheWriterClassificationSelection disabled = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 12,
            workRequiredContentVersion: 11,
            lifecycleState: DocumentCacheLifecycleState.Disabled
        );
        DocumentCacheWriterClassificationSelection setLatch = Select(
            sourceContentVersion: 11,
            cacheContentVersion: 12,
            workRequiredContentVersion: 11,
            cacheAheadRecoveryRequired: true
        );

        missingState.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnLifecycleOrLatchFence);
        disabled.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnLifecycleOrLatchFence);
        setLatch.Action.Should().Be(DocumentCacheWriterSelectedAction.ReturnLifecycleOrLatchFence);
        missingState.RequestsCacheAheadLatchFlow.Should().BeFalse();
        disabled.RequestsCacheAheadLatchFlow.Should().BeFalse();
        setLatch.RequestsCacheAheadLatchFlow.Should().BeFalse();
        missingState
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.StateMissing);
        disabled
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.LifecycleNotEligible);
        setLatch
            .TerminalResult.Should()
            .BeOfType<DocumentCacheWriterResult.LifecycleOrLatchFenced>()
            .Which.Reason.Should()
            .Be(DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired);
    }

    [Test]
    public void It_rejects_invalid_classification_inputs()
    {
        Action invalidPurpose = () =>
            _ = new DocumentCacheWriterClassificationRequest(
                (DocumentCacheWriterPurpose)0,
                Lifecycle(),
                new DocumentCacheWriterCurrentStateObservation(11, 10, 11),
                DocumentCacheWriterCandidateObservation.Absent
            );
        Action invalidVersion = () => _ = new DocumentCacheWriterCurrentStateObservation(11, 0, 11);
        Action missingCandidateWithMetadataComparison = () =>
            _ = new DocumentCacheWriterCandidateObservation(
                candidate: null,
                DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource
            );
        Action candidateWithoutMetadataComparison = () =>
            _ = new DocumentCacheWriterCandidateObservation(
                CreateCandidate(contentVersion: 11),
                DocumentCacheWriterCandidateMetadataComparison.NotSupplied
            );
        Action invalidMetadataComparison = () =>
            _ = new DocumentCacheWriterCandidateObservation(
                CreateCandidate(contentVersion: 11),
                (DocumentCacheWriterCandidateMetadataComparison)0
            );

        invalidPurpose.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*purpose*");
        invalidVersion.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*cacheContentVersion*");
        missingCandidateWithMetadataComparison
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*Missing candidates*");
        candidateWithoutMetadataComparison
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*Supplied candidates*");
        invalidMetadataComparison.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*metadata*");
    }

    private static DocumentCacheWriterClassificationSelection Select(
        long? sourceContentVersion,
        long? cacheContentVersion,
        long? workRequiredContentVersion,
        DocumentCacheLifecycleState lifecycleState = DocumentCacheLifecycleState.Tracking,
        bool cacheAheadRecoveryRequired = false,
        DocumentCacheWriterPurpose purpose = DocumentCacheWriterPurpose.DurableWorkProjection,
        DocumentCacheWriterCandidateObservation? candidateObservation = null
    ) =>
        DocumentCacheWriterClassificationSelector.Select(
            new DocumentCacheWriterClassificationRequest(
                purpose,
                Lifecycle(lifecycleState, cacheAheadRecoveryRequired),
                new DocumentCacheWriterCurrentStateObservation(
                    sourceContentVersion,
                    cacheContentVersion,
                    workRequiredContentVersion
                ),
                candidateObservation ?? DocumentCacheWriterCandidateObservation.Absent
            )
        );

    private static DocumentCacheLifecycleReadResult Lifecycle(
        DocumentCacheLifecycleState lifecycleState = DocumentCacheLifecycleState.Tracking,
        bool cacheAheadRecoveryRequired = false
    ) =>
        DocumentCacheLifecycleReadResult.Success(
            new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired)
        );

    private static DocumentCacheWriterCandidateObservation CandidateMatches(
        DocumentCacheMaterializationCandidate candidate
    ) => new(candidate, DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource);

    private static DocumentCacheWriterCandidateObservation CandidateWithMetadataMismatch(
        DocumentCacheMaterializationCandidate candidate,
        DocumentCacheWriterCandidateMetadataComparison metadataComparison
    ) => new(candidate, metadataComparison);

    private static DocumentCacheMaterializationCandidate CreateCandidate(long contentVersion) =>
        new(
            documentId: 123,
            documentUuid: new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            projectName: "Ed-Fi",
            resourceName: "School",
            resourceVersion: "5.3.0",
            contentVersion,
            DateTimeOffset.Parse("2024-01-02T03:04:05Z", CultureInfo.InvariantCulture),
            streamEtag: "etag-11",
            new JsonObject { ["id"] = "11111111-1111-1111-1111-111111111111" }
        );
}
