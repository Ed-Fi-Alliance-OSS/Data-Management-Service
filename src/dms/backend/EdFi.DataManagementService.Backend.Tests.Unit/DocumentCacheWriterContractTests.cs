// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheWriterContract
{
    private static readonly MappingSetKey MappingSetKey = new("test-hash", SqlDialect.Pgsql, "v1");

    [Test]
    public void It_reuses_the_resolved_target_context_and_materialization_candidate_contracts()
    {
        var targetContext = CreateTargetContext();
        var candidate = CreateCandidate();

        var request = new DocumentCacheWriterRequest(
            targetContext,
            documentId: 123,
            selectedRequiredContentVersion: 456,
            DocumentCacheWriterPurpose.DurableWorkProjection,
            candidate,
            CancellationToken.None
        );

        request.TargetContext.Should().BeSameAs(targetContext);
        request.Candidate.Should().BeSameAs(candidate);
        typeof(DocumentCacheWriterRequest)
            .GetProperty(nameof(DocumentCacheWriterRequest.TargetContext))!
            .PropertyType.Should()
            .Be(typeof(DocumentCacheMaterializationTargetContext));
        typeof(DocumentCacheWriterRequest)
            .GetProperty(nameof(DocumentCacheWriterRequest.Candidate))!
            .PropertyType.Should()
            .Be(typeof(DocumentCacheMaterializationCandidate));
    }

    [Test]
    public void It_treats_selected_required_content_version_as_diagnostic_context_only()
    {
        var request = new DocumentCacheWriterRequest(
            CreateTargetContext(),
            documentId: 123,
            selectedRequiredContentVersion: 456,
            DocumentCacheWriterPurpose.DirectFill,
            candidate: null,
            CancellationToken.None
        );

        request.SelectedRequiredContentVersion.Should().Be(456);
        request.Candidate.Should().BeNull("null candidate means classify durable state only");
        PublicResultPropertyNames()
            .Should()
            .NotContain(nameof(DocumentCacheWriterRequest.SelectedRequiredContentVersion));
    }

    [Test]
    public void It_exposes_one_shared_writer_method_without_a_cache_only_write_api()
    {
        MethodInfo write = typeof(IDocumentCacheWriter).GetMethod(nameof(IDocumentCacheWriter.WriteAsync))!;

        write.ReturnType.Should().Be(typeof(Task<DocumentCacheWriterResult>));
        write
            .GetParameters()
            .Should()
            .ContainSingle(parameter => parameter.ParameterType == typeof(DocumentCacheWriterRequest));
        typeof(IDocumentCacheWriter)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .BeEquivalentTo(nameof(IDocumentCacheWriter.WriteAsync));
    }

    [Test]
    public void It_restricts_writer_purpose_to_durable_projection_and_direct_fill()
    {
        Enum.GetNames<DocumentCacheWriterPurpose>()
            .Should()
            .BeEquivalentTo("DurableWorkProjection", "DirectFill");

        Action invalidPurpose = () =>
            _ = new DocumentCacheWriterRequest(
                CreateTargetContext(),
                documentId: 123,
                selectedRequiredContentVersion: null,
                (DocumentCacheWriterPurpose)0,
                candidate: null,
                CancellationToken.None
            );

        invalidPurpose.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*purpose*");
    }

    [Test]
    public void It_rejects_invalid_document_and_version_inputs_before_provider_work()
    {
        Action invalidDocumentId = () =>
            _ = new DocumentCacheWriterRequest(
                CreateTargetContext(),
                documentId: 0,
                selectedRequiredContentVersion: null,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate: null,
                CancellationToken.None
            );
        Action invalidSelectedVersion = () =>
            _ = new DocumentCacheWriterRequest(
                CreateTargetContext(),
                documentId: 123,
                selectedRequiredContentVersion: 0,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate: null,
                CancellationToken.None
            );
        Action invalidAcknowledgementVersion = () =>
            _ = new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(0);

        invalidDocumentId.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*documentId*positive*");
        invalidSelectedVersion
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*RequiredContentVersion*positive*");
        invalidAcknowledgementVersion
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*acknowledgedContentVersion*positive*");
    }

    [Test]
    public void It_rejects_candidate_document_id_mismatch_as_a_programming_error()
    {
        var candidate = CreateCandidate(documentId: 999);

        Action createRequest = () =>
            _ = new DocumentCacheWriterRequest(
                CreateTargetContext(),
                documentId: 123,
                selectedRequiredContentVersion: null,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                candidate,
                CancellationToken.None
            );

        createRequest.Should().Throw<ArgumentException>().WithMessage("*Candidate DocumentId*");
    }

    [Test]
    public void It_bounds_all_story_listed_outcomes_in_the_result_model()
    {
        Enum.GetNames<DocumentCacheWriterOutcome>()
            .Should()
            .BeEquivalentTo(
                "AlreadyCurrentAcknowledged",
                "CandidateWrittenAcknowledged",
                "NeedsMaterialization",
                "LifecycleOrLatchFenced",
                "SourceMissingOrDeleted",
                "StaleCandidateSuppressed",
                "WorkAnomaly",
                "CacheAheadLatchSet",
                "CacheAheadDisappeared",
                "RacingWriterLost",
                "RetryBudgetExhausted",
                "CallerAbortedRetry",
                "DeleteRaceRetryExhausted",
                "CacheAheadUnconfirmedCallerAbort",
                "DeterministicInvariantOrTargetFailure",
                "AlreadyCurrentNoWork"
            );

        CreateRepresentativeResults()
            .Select(result => result.Outcome)
            .Should()
            .BeEquivalentTo(Enum.GetValues<DocumentCacheWriterOutcome>());

        typeof(DocumentCacheWriterResult)
            .GetNestedTypes(BindingFlags.Public)
            .Should()
            .OnlyContain(type => type.IsSealed);
    }

    [Test]
    public void It_models_stale_candidate_versions_separately_from_matching_version_invariants()
    {
        var stale = new DocumentCacheWriterResult.StaleCandidateSuppressed(
            currentContentVersion: 11,
            candidateContentVersion: 10
        );
        var invariant = new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
            DocumentCacheWriterInvariantFailureReason.MatchingVersionDocumentUuidMismatch,
            currentContentVersion: 11,
            candidateContentVersion: 11
        );

        stale.Outcome.Should().Be(DocumentCacheWriterOutcome.StaleCandidateSuppressed);
        invariant.Outcome.Should().Be(DocumentCacheWriterOutcome.DeterministicInvariantOrTargetFailure);
        Enum.GetNames<DocumentCacheWriterInvariantFailureReason>()
            .Should()
            .Contain(["MatchingVersionDocumentUuidMismatch", "MatchingVersionResourceMetadataMismatch"]);

        Action matchingVersionStale = () =>
            _ = new DocumentCacheWriterResult.StaleCandidateSuppressed(11, 11);
        Action mismatchedVersionInvariant = () =>
            _ = new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                DocumentCacheWriterInvariantFailureReason.MatchingVersionResourceMetadataMismatch,
                currentContentVersion: 11,
                candidateContentVersion: 10
            );

        matchingVersionStale.Should().Throw<ArgumentException>().WithMessage("*not be represented as stale*");
        mismatchedVersionInvariant.Should().Throw<ArgumentException>().WithMessage("*stale suppression*");
    }

    [Test]
    public void It_distinguishes_tracking_and_rebuilding_work_anomalies()
    {
        var tracking = new DocumentCacheWriterResult.WorkAnomaly(
            DocumentCacheWriterWorkAnomalyKind.MissingWork,
            DocumentCacheLifecycleState.Tracking,
            currentSourceContentVersion: 11,
            workRequiredContentVersion: null
        );
        var rebuilding = new DocumentCacheWriterResult.WorkAnomaly(
            DocumentCacheWriterWorkAnomalyKind.MissingWork,
            DocumentCacheLifecycleState.Rebuilding,
            currentSourceContentVersion: 11,
            workRequiredContentVersion: null
        );

        tracking.LifecycleState.Should().Be(DocumentCacheLifecycleState.Tracking);
        rebuilding.LifecycleState.Should().Be(DocumentCacheLifecycleState.Rebuilding);

        Action invalidLifecycle = () =>
            _ = new DocumentCacheWriterResult.WorkAnomaly(
                DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch,
                DocumentCacheLifecycleState.Disabled,
                currentSourceContentVersion: 11,
                workRequiredContentVersion: 10
            );

        invalidLifecycle.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Tracking*Rebuilding*");
    }

    [Test]
    public void It_rejects_invalid_bounded_result_values()
    {
        Action invalidRetryBudgetAttempts = () => _ = new DocumentCacheWriterResult.RetryBudgetExhausted(0);
        Action invalidCallerAbortAttempts = () => _ = new DocumentCacheWriterResult.CallerAbortedRetry(0);
        Action invalidDeleteRaceAttempts = () =>
            _ = new DocumentCacheWriterResult.DeleteRaceRetryExhausted(0);
        Action invalidCacheAheadVersions = () =>
            _ = new DocumentCacheWriterResult.CacheAheadLatchSet(
                sourceContentVersion: 11,
                cacheContentVersion: 11
            );
        Action invalidFenceReason = () =>
            _ = new DocumentCacheWriterResult.LifecycleOrLatchFenced(
                (DocumentCacheWriterFenceReason)0,
                lifecycleState: null,
                cacheAheadRecoveryRequired: null
            );
        Action invalidFenceLifecycle = () =>
            _ = new DocumentCacheWriterResult.LifecycleOrLatchFenced(
                DocumentCacheWriterFenceReason.LifecycleNotEligible,
                (DocumentCacheLifecycleState)999,
                cacheAheadRecoveryRequired: false
            );
        Action invalidWorkAnomalyKind = () =>
            _ = new DocumentCacheWriterResult.WorkAnomaly(
                (DocumentCacheWriterWorkAnomalyKind)0,
                DocumentCacheLifecycleState.Tracking,
                currentSourceContentVersion: 11,
                workRequiredContentVersion: 10
            );
        Action invalidWorkAnomalyVersion = () =>
            _ = new DocumentCacheWriterResult.WorkAnomaly(
                DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch,
                DocumentCacheLifecycleState.Tracking,
                currentSourceContentVersion: 11,
                workRequiredContentVersion: 0
            );
        Action invalidInvariantReason = () =>
            _ = new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                (DocumentCacheWriterInvariantFailureReason)0,
                currentContentVersion: 11,
                candidateContentVersion: 11
            );

        invalidRetryBudgetAttempts.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
        invalidCallerAbortAttempts.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
        invalidDeleteRaceAttempts.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
        invalidCacheAheadVersions
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*cache ContentVersion*greater than source ContentVersion*");
        invalidFenceReason.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*fence reason*");
        invalidFenceLifecycle.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*lifecycle state*");
        invalidWorkAnomalyKind.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*anomaly kind*");
        invalidWorkAnomalyVersion.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
        invalidInvariantReason
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*invariant failure reason*");
    }

    [Test]
    public void It_requires_candidate_write_acknowledgement_to_match_the_candidate_version()
    {
        var candidate = CreateCandidate(contentVersion: 11);
        var result = new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
            candidate,
            acknowledgedContentVersion: 11
        );

        result.Candidate.Should().BeSameAs(candidate);
        result.AcknowledgedContentVersion.Should().Be(11);

        Action mismatchedAcknowledgement = () =>
            _ = new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
                candidate,
                acknowledgedContentVersion: 12
            );

        mismatchedAcknowledgement
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*candidate ContentVersion*");
    }

    private static DocumentCacheWriterResult[] CreateRepresentativeResults() =>
        [
            new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(11),
            new DocumentCacheWriterResult.AlreadyCurrentNoWork(11),
            new DocumentCacheWriterResult.CandidateWrittenAcknowledged(CreateCandidate(), 11),
            new DocumentCacheWriterResult.NeedsMaterialization(11),
            new DocumentCacheWriterResult.LifecycleOrLatchFenced(
                DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired,
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: true
            ),
            DocumentCacheWriterResult.SourceMissingOrDeleted.Instance,
            new DocumentCacheWriterResult.StaleCandidateSuppressed(11, 10),
            new DocumentCacheWriterResult.WorkAnomaly(
                DocumentCacheWriterWorkAnomalyKind.WorkVersionMismatch,
                DocumentCacheLifecycleState.Tracking,
                currentSourceContentVersion: 11,
                workRequiredContentVersion: 10
            ),
            new DocumentCacheWriterResult.CacheAheadLatchSet(
                sourceContentVersion: 10,
                cacheContentVersion: 11
            ),
            DocumentCacheWriterResult.CacheAheadDisappeared.Instance,
            DocumentCacheWriterResult.RacingWriterLost.Instance,
            new DocumentCacheWriterResult.RetryBudgetExhausted(attemptCount: 3),
            new DocumentCacheWriterResult.CallerAbortedRetry(attemptCount: 2),
            new DocumentCacheWriterResult.DeleteRaceRetryExhausted(attemptCount: 3),
            DocumentCacheWriterResult.CacheAheadUnconfirmedCallerAbort.Instance,
            new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                DocumentCacheWriterInvariantFailureReason.MatchingVersionResourceMetadataMismatch,
                currentContentVersion: 11,
                candidateContentVersion: 11
            ),
        ];

    private static DocumentCacheMaterializationCandidate CreateCandidate(
        long documentId = 123,
        long contentVersion = 11
    ) =>
        new(
            documentId,
            new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            projectName: "Ed-Fi",
            resourceName: "School",
            resourceVersion: "5.3.0",
            contentVersion,
            lastModifiedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            streamEtag: "\"11-fixed-stream\"",
            documentJson: JsonNode
                .Parse(
                    """
                    {"id":"11111111-1111-1111-1111-111111111111","_lastModifiedDate":"2026-01-01T00:00:00Z","nameOfInstitution":"Lincoln High"}
                    """
                )!
                .AsObject()
        );

    private static DocumentCacheMaterializationTargetContext CreateTargetContext() =>
        new(
            new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
            CreateMappingSet(),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
        );

    private static MappingSet CreateMappingSet() =>
        new(
            MappingSetKey,
            Model: null!,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    private static string[] PublicResultPropertyNames() =>
        typeof(DocumentCacheWriterResult)
            .GetNestedTypes(BindingFlags.Public)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
