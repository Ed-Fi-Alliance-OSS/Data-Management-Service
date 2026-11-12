// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_ProposedAuthorizationFailureClassification
{
    private const int EmittedAuth1Index = 3;

    [TestCase(ReportedSchoolIdIndex, "$.reportedSchoolReference")]
    [TestCase(UnifiedSchoolIdIndex, "$.schoolReference")]
    [TestCase(UnifiedSchoolIdIndex, "$.otherSchoolReference")]
    public void It_authorizes_a_deferred_edorg_from_its_submitted_identity_without_changing_the_root(
        int bindingIndex,
        string referencePath
    )
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        var body = JsonNode.Parse(
            """{"reportedSchoolReference":{"schoolId":900},"schoolReference":{"schoolId":900},"otherSchoolReference":{"schoolId":900}}"""
        )!;

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(EdOrgCheck(7, bindingIndex)),
            writePlan,
            rootRow,
            EmittedAuth1Index,
            deferredPaths: Deferred(referencePath),
            requestBody: body
        );

        var subject = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies[0]
            .Subjects[0];
        subject
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(900L));
        subject.IsNullBecauseDeferredMissingReference.Should().BeFalse();
        rootRow.Values[bindingIndex].Should().Be(new FlattenedWriteValue.Literal(null));
    }

    [TestCase("{}")]
    [TestCase("{\"reportedSchoolReference\":{\"schoolId\":null}}")]
    public void It_keeps_a_truly_absent_or_null_edorg_element_required_even_when_its_reference_is_deferred(
        string json
    )
    {
        var writePlan = CreateWritePlan();
        RelationshipAuthorizationCheckSpec[] checkSpecs = [EdOrgCheck(7, ReportedSchoolIdIndex)];
        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(checkSpecs),
            writePlan,
            CreateRootRow(writePlan),
            EmittedAuth1Index,
            deferredPaths: Deferred("$.reportedSchoolReference"),
            requestBody: JsonNode.Parse(json)!
        );
        var runtimeCheck = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck;

        ProposedAuthorizationFailureClassification
            .CorrectRelationshipFailure(MapFailure(checkSpecs, Missing(0, 0)), runtimeCheck)
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);
    }

    [Test]
    public void It_preserves_a_finalized_unified_edorg_over_a_deferred_body_value()
    {
        var writePlan = CreateWritePlan();
        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(EdOrgCheck(7, UnifiedSchoolIdIndex)),
            writePlan,
            CreateRootRow(writePlan, unifiedSchoolId: 100L),
            EmittedAuth1Index,
            deferredPaths: Deferred("$.schoolReference"),
            requestBody: JsonNode.Parse("""{"schoolReference":{"schoolId":900}}""")!
        );

        result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies[0]
            .Subjects[0]
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(100L));
    }

    [Test]
    public void It_marks_only_the_null_binding_derived_from_the_matching_deferred_reference()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        var deferredPaths = Deferred("$.reportedSchoolReference");

        Classify(writePlan, rootRow, ReportedSchoolDocumentIdIndex, deferredPaths).Should().BeTrue();
        Classify(writePlan, rootRow, ReportedSchoolIdIndex, deferredPaths).Should().BeTrue();
        Classify(writePlan, rootRow, StudentDocumentIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, rootRow, UnifiedSchoolIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, rootRow, HiddenSchoolIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, rootRow, NameIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, rootRow, ReportedSchoolIdIndex, Deferred()).Should().BeFalse();

        var extraction = Extract(
            writePlan,
            rootRow,
            deferredPaths,
            EdOrgCheck(7, ReportedSchoolIdIndex, StudentDocumentIdIndex)
        );

        extraction
            .RuntimeCheck.Strategies[0]
            .Subjects.Select(static subject => subject.IsNullBecauseDeferredMissingReference)
            .Should()
            .Equal(true, false);
        extraction
            .RuntimeCheck.Strategies[0]
            .Subjects[0]
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(null));
    }

    [Test]
    public void It_preserves_a_nonnull_unified_contributor_and_a_genuine_hidden_null()
    {
        var writePlan = CreateWritePlan();
        var deferredPaths = Deferred("$.schoolReference", "$.reportedSchoolReference");

        // A resolved (or hidden-preserved) member supplied the finalized canonical value.
        var resolvedRow = CreateRootRow(
            writePlan,
            unifiedSchoolId: 100L,
            reportedSchoolId: 255901L,
            reportedSchoolDocumentId: 41L
        );

        Classify(writePlan, resolvedRow, UnifiedSchoolIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, resolvedRow, ReportedSchoolIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, resolvedRow, HiddenSchoolIdIndex, deferredPaths).Should().BeFalse();

        // No member supplied a value and one unified member is the deferred reference.
        var deferredRow = CreateRootRow(writePlan);

        Classify(writePlan, deferredRow, UnifiedSchoolIdIndex, deferredPaths).Should().BeTrue();
        Classify(writePlan, deferredRow, HiddenSchoolIdIndex, deferredPaths).Should().BeFalse();
    }

    [Test]
    public void It_does_not_mark_an_unrelated_or_child_reference()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        var deferredPaths = Deferred(
            "$.items[0].reportedSchoolReference",
            "$.studentAssessmentItems[1].schoolReference",
            "$.reportedSchoolReference.schoolId",
            "$.otherSchoolReferences"
        );

        Classify(writePlan, rootRow, ReportedSchoolIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, rootRow, ReportedSchoolDocumentIdIndex, deferredPaths).Should().BeFalse();
        Classify(writePlan, rootRow, UnifiedSchoolIdIndex, deferredPaths).Should().BeFalse();

        var failure = Correct(
            writePlan,
            rootRow,
            deferredPaths,
            [EdOrgCheck(7, ReportedSchoolIdIndex)],
            Missing(0, 0)
        );

        failure
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);
    }

    [Test]
    public void It_corrects_the_exact_failed_subject_across_reordered_configured_indexes()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan, unifiedSchoolId: 100L);
        var deferredPaths = Deferred("$.reportedSchoolReference");
        RelationshipAuthorizationCheckSpec[] checkSpecs =
        [
            EdOrgCheck(7, UnifiedSchoolIdIndex, ReportedSchoolIdIndex),
            EdOrgCheck(2, HiddenSchoolIdIndex),
            EdOrgCheck(4, ReportedSchoolIdIndex),
        ];

        // Strategy 0's unified subject 0 authorized, so its only failure is subject ordinal 1.
        var corrected = Correct(
            writePlan,
            rootRow,
            deferredPaths,
            checkSpecs,
            Missing(0, 1),
            Missing(1, 0),
            Missing(2, 0)
        );

        corrected
            .FailedStrategies.Select(static strategy => strategy.ConfiguredStrategyIndex)
            .Should()
            .Equal(7, 2, 4);
        corrected.FailedStrategies[0].FailedSubjects.Should().ContainSingle();
        corrected.FailedStrategies[0].FailedSubjects[0].SubjectIndex.Should().Be(1);
        corrected
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        corrected
            .FailedStrategies[1]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);
        corrected
            .FailedStrategies[2]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
    }

    [Test]
    public void It_keeps_true_missing_subjects_and_all_failed_strategies()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        var deferredPaths = Deferred("$.reportedSchoolReference");
        RelationshipAuthorizationCheckSpec[] checkSpecs =
        [
            EdOrgCheck(5, ReportedSchoolIdIndex, HiddenSchoolIdIndex),
            DirectStudentCheck(1),
        ];
        var extraction = Extract(writePlan, rootRow, deferredPaths, checkSpecs);
        var original = MapFailure(checkSpecs, Missing(0, 0), Missing(0, 1), Missing(1, 0));
        var noRelationshipHint = MapFailure(checkSpecs, NoRelationship(0, 0), Missing(1, 0))
            .FailedStrategies[0]
            .FailedSubjects[0]
            .Hint;

        var corrected = ProposedAuthorizationFailureClassification.CorrectRelationshipFailure(
            original,
            extraction.RuntimeCheck
        );

        corrected
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        corrected
            .FailedStrategies[0]
            .FailedSubjects[1]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);
        original
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);

        corrected.FailedStrategies[0].FailedSubjects[0].Hint.Should().Be(noRelationshipHint);
        corrected
            .FailedStrategies[0]
            .FailedSubjects[0]
            .Should()
            .BeEquivalentTo(
                original.FailedStrategies[0].FailedSubjects[0],
                options =>
                    options.Excluding(subject => subject.FailureKind).Excluding(subject => subject.Hint)
            );
        corrected
            .FailedStrategies[0]
            .FailedSubjects[1]
            .Should()
            .Be(original.FailedStrategies[0].FailedSubjects[1]);
        corrected.FailedStrategies.Should().HaveCount(2);
        corrected.FailedStrategies[1].Should().BeSameAs(original.FailedStrategies[1]);
        corrected
            .Should()
            .BeEquivalentTo(original, options => options.Excluding(failure => failure.FailedStrategies));
        corrected
            .FailedStrategies[0]
            .Should()
            .BeEquivalentTo(
                original.FailedStrategies[0],
                options => options.Excluding(strategy => strategy.FailedSubjects)
            );
    }

    [Test]
    public void It_returns_the_original_failure_when_no_failed_subject_is_marked()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        RelationshipAuthorizationCheckSpec[] checkSpecs = [EdOrgCheck(0, ReportedSchoolIdIndex)];
        var extraction = Extract(writePlan, rootRow, Deferred(), checkSpecs);
        var original = MapFailure(checkSpecs, Missing(0, 0));

        ProposedAuthorizationFailureClassification
            .CorrectRelationshipFailure(original, extraction.RuntimeCheck)
            .Should()
            .BeSameAs(original);
    }

    [Test]
    public void It_keeps_nonnull_transitive_anchors_with_unavailable_onward_data_missing()
    {
        var writePlan = CreateWritePlan();
        RelationshipAuthorizationCheckSpec[] checkSpecs = [TransitiveStudentCheck(0)];

        // A supplied first hop that did not resolve: the anchor is null because of the deferred reference.
        var unresolvedAnchorRow = CreateRootRow(writePlan);
        var unresolvedAnchor = Extract(
            writePlan,
            unresolvedAnchorRow,
            Deferred("$.schoolReference"),
            checkSpecs
        );

        unresolvedAnchor
            .RuntimeCheck.Strategies[0]
            .Subjects[0]
            .IsNullBecauseDeferredMissingReference.Should()
            .BeTrue();
        unresolvedAnchor
            .RuntimeCheck.Strategies[0]
            .Subjects[0]
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.TransitivePeopleFirstHopAnchorValue(null));
        ProposedAuthorizationFailureClassification
            .CorrectRelationshipFailure(MapFailure(checkSpecs, Missing(0, 0)), unresolvedAnchor.RuntimeCheck)
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);

        // A resolved first hop whose onward traversal is unavailable keeps the proposed-missing outcome,
        // even when another (unrelated) reference was deferred.
        var resolvedAnchorRow = CreateRootRow(writePlan, schoolDocumentId: 555L);
        var resolvedAnchor = Extract(
            writePlan,
            resolvedAnchorRow,
            Deferred("$.reportedSchoolReference"),
            checkSpecs
        );

        resolvedAnchor
            .RuntimeCheck.Strategies[0]
            .Subjects[0]
            .IsNullBecauseDeferredMissingReference.Should()
            .BeFalse();
        ProposedAuthorizationFailureClassification
            .CorrectRelationshipFailure(MapFailure(checkSpecs, Missing(0, 0)), resolvedAnchor.RuntimeCheck)
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);

        // A genuinely omitted first hop stays proposed-missing.
        var omittedAnchor = Extract(writePlan, unresolvedAnchorRow, Deferred(), checkSpecs);

        ProposedAuthorizationFailureClassification
            .CorrectRelationshipFailure(MapFailure(checkSpecs, Missing(0, 0)), omittedAnchor.RuntimeCheck)
            .FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);
    }

    [Test]
    public void It_marks_a_supplied_unresolved_direct_person_reference()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        RelationshipAuthorizationCheckSpec[] checkSpecs = [DirectStudentCheck(0)];

        var unresolved = Extract(writePlan, rootRow, Deferred("$.studentReference"), checkSpecs);
        var omitted = Extract(writePlan, rootRow, Deferred("$.reportedSchoolReference"), checkSpecs);

        unresolved
            .RuntimeCheck.Strategies[0]
            .Subjects[0]
            .IsNullBecauseDeferredMissingReference.Should()
            .BeTrue();
        omitted
            .RuntimeCheck.Strategies[0]
            .Subjects[0]
            .IsNullBecauseDeferredMissingReference.Should()
            .BeFalse();
    }

    [Test]
    public void It_does_not_use_deferred_provenance_for_existing_target_self_people_subjects()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateAuthorized(SelfStudentCheck(0)),
            writePlan,
            rootRow,
            EmittedAuth1Index,
            new RelationalWriteTargetContext.ExistingDocument(
                456789L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                ObservedContentVersion: 101L
            ),
            Deferred("$.reportedSchoolReference", "$.studentReference")
        );

        var subject = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject.RuntimeCheck.Strategies[0]
            .Subjects[0];
        subject
            .RuntimeValue.Should()
            .Be(new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(456789L));
        subject.IsNullBecauseDeferredMissingReference.Should().BeFalse();
    }

    [Test]
    public void It_rejects_invalid_reference_association_metadata()
    {
        var outOfRangeReference = CreateWritePlan(reportedSchoolIdReferenceBindingIndex: 9);
        var mismatchedReference = CreateWritePlan(reportedSchoolIdReferenceObjectPath: "$.schoolReference");

        TryClassify(
                outOfRangeReference,
                CreateRootRow(outOfRangeReference),
                ReportedSchoolIdIndex,
                Deferred("$.reportedSchoolReference"),
                out var outOfRangeMessage
            )
            .Should()
            .BeFalse();
        outOfRangeMessage.Should().Contain("document-reference binding index 9");

        TryClassify(
                mismatchedReference,
                CreateRootRow(mismatchedReference),
                ReportedSchoolIdIndex,
                Deferred("$.reportedSchoolReference"),
                out var mismatchMessage
            )
            .Should()
            .BeFalse();
        mismatchMessage.Should().Contain("'$.schoolReference'").And.Contain("'$.reportedSchoolReference'");

        TryClassify(
                mismatchedReference,
                CreateRootRow(mismatchedReference),
                99,
                Deferred("$.reportedSchoolReference"),
                out var bindingMessage
            )
            .Should()
            .BeFalse();
        bindingMessage.Should().Contain("binding index 99");

        RelationshipAuthorizationProposedValueExtractor
            .Extract(
                CreateAuthorized(EdOrgCheck(0, ReportedSchoolIdIndex)),
                outOfRangeReference,
                CreateRootRow(outOfRangeReference),
                EmittedAuth1Index,
                targetContext: null,
                Deferred("$.reportedSchoolReference")
            )
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.InvalidAuthorizationPlan>()
            .Which.FailureMessage.Should()
            .Be(outOfRangeMessage);
    }

    [Test]
    public void It_keeps_provenance_request_scoped_in_both_request_orders()
    {
        var writePlan = CreateWritePlan();
        var rootRow = CreateRootRow(writePlan);
        RelationshipAuthorizationCheckSpec[] checkSpecs = [EdOrgCheck(0, ReportedSchoolIdIndex)];
        var authorized = CreateAuthorized(checkSpecs);
        var unresolved = Deferred("$.reportedSchoolReference");
        var absent = Deferred();

        ClassifyRequests(unresolved, absent, unresolved)
            .Should()
            .Equal(
                RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                RelationshipAuthorizationSubjectFailureKind.NoRelationship
            );
        ClassifyRequests(absent, unresolved, absent)
            .Should()
            .Equal(
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                RelationshipAuthorizationSubjectFailureKind.NoRelationship,
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
            );
        authorized.CheckSpecs.Should().Equal(checkSpecs);

        RelationshipAuthorizationSubjectFailureKind[] ClassifyRequests(
            params IReadOnlySet<string>[] requests
        ) =>
            [
                .. requests.Select(deferredPaths =>
                {
                    var extraction = RelationshipAuthorizationProposedValueExtractor.Extract(
                        authorized,
                        writePlan,
                        rootRow,
                        EmittedAuth1Index,
                        targetContext: null,
                        deferredPaths
                    );
                    var runtimeCheck = (
                        (ProposedRelationshipAuthorizationExtractionResult.Ready)extraction
                    ).RuntimeCheck;

                    return ProposedAuthorizationFailureClassification
                        .CorrectRelationshipFailure(MapFailure(checkSpecs, Missing(0, 0)), runtimeCheck)
                        .FailedStrategies[0]
                        .FailedSubjects[0]
                        .FailureKind;
                }),
            ];
    }

    [Test]
    public void It_corrects_only_an_emitted_proposed_document_basis_with_deferred_provenance()
    {
        ProposedCustomViewRuntimeValue[] emittedValues =
        [
            new(CustomViewCheck(3), BasisValue: null, IsNullBecauseDeferredMissingReference: true),
            new(CustomViewCheck(5), BasisValue: null),
            new(CustomViewCheck(6), BasisValue: 555L),
        ];
        var unresolved = CustomViewFailure(3);

        var corrected = ProposedAuthorizationFailureClassification.CorrectCustomViewFailure(
            unresolved,
            emittedValues
        );

        corrected.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        corrected
            .Should()
            .BeEquivalentTo(unresolved, options => options.Excluding(failure => failure.FailureKind));
        unresolved.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.ProposedValueMissing);

        // Omitted basis, a nonnull first hop whose onward basis is unavailable, an unemitted check, a stored
        // failure, and an already-no-match failure are each returned unchanged.
        foreach (
            var unchanged in new[]
            {
                CustomViewFailure(5),
                CustomViewFailure(6),
                CustomViewFailure(7),
                CustomViewFailure(3, valueSource: CustomViewAuthorizationFailureValueSource.Stored),
                CustomViewFailure(3, CustomViewAuthorizationFailureKind.NoMatchingRow),
            }
        )
        {
            ProposedAuthorizationFailureClassification
                .CorrectCustomViewFailure(unchanged, emittedValues)
                .Should()
                .BeSameAs(unchanged);
        }
    }

    private static SingleRecordCustomViewAuthorizationCheckSpec CustomViewCheck(int index) =>
        new(
            new ConfiguredAuthorizationStrategy("SchoolWithATag", RawConfiguredIndex: index),
            index,
            CustomViewAuthorizationCheckValueSource.Proposed,
            new DbTableName(new DbSchemaName("auth"), "SchoolWithATag"),
            DocumentIdColumn,
            [new ColumnPathStep(RootTable, ColumnNames[SchoolDocumentIdIndex], null, null)],
            new CustomViewAuthorizationCheckTarget.Proposed(
                RootTable,
                new CustomViewAuthorizationProposedValueBinding(
                    RootTable,
                    ColumnNames[SchoolDocumentIdIndex],
                    "School_DocumentId",
                    "cvBasis"
                )
            ),
            SchoolResource,
            ["SchoolId"],
            "You may need a SchoolWithATag hint."
        );

    private static CustomViewAuthorizationFailure CustomViewFailure(
        int index,
        CustomViewAuthorizationFailureKind failureKind =
            CustomViewAuthorizationFailureKind.ProposedValueMissing,
        CustomViewAuthorizationFailureValueSource valueSource =
            CustomViewAuthorizationFailureValueSource.Proposed
    ) =>
        new(
            failureKind,
            valueSource,
            index,
            "SchoolWithATag",
            ["SchoolId"],
            "You may need a SchoolWithATag hint."
        );

    private static bool Classify(
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        int bindingIndex,
        IReadOnlySet<string> deferredPaths
    )
    {
        TryClassify(
                writePlan,
                rootRow,
                bindingIndex,
                deferredPaths,
                out var failureMessage,
                out var isDeferred
            )
            .Should()
            .BeTrue();
        failureMessage.Should().BeEmpty();

        return isDeferred;
    }

    private static bool TryClassify(
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        int bindingIndex,
        IReadOnlySet<string> deferredPaths,
        out string failureMessage
    ) => TryClassify(writePlan, rootRow, bindingIndex, deferredPaths, out failureMessage, out _);

    private static bool TryClassify(
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        int bindingIndex,
        IReadOnlySet<string> deferredPaths,
        out string failureMessage,
        out bool isDeferred
    ) =>
        ProposedAuthorizationReferenceProvenance.TryClassify(
            writePlan,
            rootRow,
            bindingIndex,
            deferredPaths,
            out isDeferred,
            out failureMessage
        );

    private static ProposedRelationshipAuthorizationExtractionResult.Ready Extract(
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        IReadOnlySet<string> deferredPaths,
        params RelationshipAuthorizationCheckSpec[] checkSpecs
    ) =>
        RelationshipAuthorizationProposedValueExtractor
            .Extract(
                CreateAuthorized(checkSpecs),
                writePlan,
                rootRow,
                EmittedAuth1Index,
                targetContext: null,
                deferredPaths
            )
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;

    private static RelationshipAuthorizationFailure Correct(
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        IReadOnlySet<string> deferredPaths,
        RelationshipAuthorizationCheckSpec[] checkSpecs,
        params RelationshipAuthorizationAuth1SubjectFailure[] subjectFailures
    ) =>
        ProposedAuthorizationFailureClassification.CorrectRelationshipFailure(
            MapFailure(checkSpecs, subjectFailures),
            Extract(writePlan, rootRow, deferredPaths, checkSpecs).RuntimeCheck
        );

    private static RelationshipAuthorizationFailure MapFailure(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        params RelationshipAuthorizationAuth1SubjectFailure[] subjectFailures
    )
    {
        RelationshipAuthorizationFailureMapper
            .TryMapAuth1Failure(
                new RelationshipAuthorizationAuth1FailurePayload(EmittedAuth1Index, subjectFailures),
                EmittedAuth1Index,
                checkSpecs,
                [1234L],
                out var failure
            )
            .Should()
            .BeTrue();

        return failure!;
    }

    private static RelationshipAuthorizationAuth1SubjectFailure Missing(
        int strategyOrdinal,
        int subjectOrdinal
    ) =>
        new(
            strategyOrdinal,
            subjectOrdinal,
            RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
        );

    private static RelationshipAuthorizationAuth1SubjectFailure NoRelationship(
        int strategyOrdinal,
        int subjectOrdinal
    ) =>
        new(strategyOrdinal, subjectOrdinal, RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship);

    private static IReadOnlySet<string> Deferred(params string[] paths) =>
        new HashSet<string>(paths, StringComparer.Ordinal);

    private static RelationshipAuthorizationResult.Authorized CreateAuthorized(
        params RelationshipAuthorizationCheckSpec[] checkSpecs
    ) =>
        new(
            checkSpecs,
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                SqlDialect.Pgsql,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );

    private static RelationshipAuthorizationCheckSpec EdOrgCheck(
        int configuredIndex,
        params int[] subjectBindingIndexes
    )
    {
        var bindings = subjectBindingIndexes.Select(CreateBinding).ToArray();
        var subjects = bindings
            .Select(binding => new RelationshipAuthorizationSubject(
                StudentAssessmentResource,
                RootTable,
                binding.Column,
                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                    RelationshipAuthorizationHierarchyDirection.Normal
                ),
                [
                    new RelationshipAuthorizationSubjectContributor(
                        SecurableElementKind.EducationOrganization,
                        $"$.{binding.Column.Value}",
                        binding.Column.Value
                    ),
                ]
            ))
            .ToArray();

        return CreateCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
            configuredIndex,
            subjects,
            bindings
        );
    }

    private static RelationshipAuthorizationCheckSpec DirectStudentCheck(int configuredIndex)
    {
        var binding = CreateBinding(StudentDocumentIdIndex);

        return CreateCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            configuredIndex,
            [
                CreatePersonSubject(
                    RootTable,
                    binding.Column,
                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                    [new ColumnPathStep(RootTable, binding.Column, StudentTable, DocumentIdColumn)],
                    new RelationshipAuthorizationPersonProposedAnchor(
                        RelationshipAuthorizationPersonProposedAnchorKind.RootRow,
                        binding
                    )
                ),
            ],
            [binding]
        );
    }

    private static RelationshipAuthorizationCheckSpec TransitiveStudentCheck(int configuredIndex)
    {
        var binding = CreateBinding(SchoolDocumentIdIndex);

        return CreateCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
            configuredIndex,
            [
                CreatePersonSubject(
                    StudentSchoolAssociationTable,
                    StudentDocumentIdColumn,
                    RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                    [
                        new ColumnPathStep(
                            RootTable,
                            binding.Column,
                            StudentSchoolAssociationTable,
                            DocumentIdColumn
                        ),
                        new ColumnPathStep(
                            StudentSchoolAssociationTable,
                            StudentDocumentIdColumn,
                            StudentTable,
                            DocumentIdColumn
                        ),
                    ],
                    new RelationshipAuthorizationPersonProposedAnchor(
                        RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                        binding
                    )
                ),
            ],
            [binding]
        );
    }

    private static RelationshipAuthorizationCheckSpec SelfStudentCheck(int configuredIndex)
    {
        var binding = CreateBinding(DocumentIdIndex);

        return CreateCheckSpec(
            AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
            configuredIndex,
            [
                CreatePersonSubject(
                    RootTable,
                    DocumentIdColumn,
                    RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                    [],
                    new RelationshipAuthorizationPersonProposedAnchor(
                        RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId,
                        binding
                    )
                ),
            ],
            [binding]
        );
    }

    private static RelationshipAuthorizationCheckSpec CreateCheckSpec(
        string strategyName,
        int configuredIndex,
        RelationshipAuthorizationSubject[] subjects,
        RelationshipAuthorizationProposedValueBinding[] bindings
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, RawConfiguredIndex: configuredIndex),
            RelationshipLocalOrder: 0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            subjects,
            new RelationshipAuthorizationCheckTarget.Proposed(RootTable, bindings)
        );

    private static RelationshipAuthorizationSubject CreatePersonSubject(
        DbTableName subjectTable,
        DbColumnName subjectColumn,
        RelationshipAuthorizationPersonSubjectPathKind pathKind,
        IReadOnlyList<ColumnPathStep> pathSteps,
        RelationshipAuthorizationPersonProposedAnchor proposedAnchor
    ) =>
        new(
            StudentAssessmentResource,
            subjectTable,
            subjectColumn,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                new RelationshipAuthorizationPersonSubjectPath(pathKind, pathSteps),
                new RelationshipAuthorizationPersonStoredAnchor(RootTable, DocumentIdColumn),
                proposedAnchor
            )
        );

    private static RelationshipAuthorizationProposedValueBinding CreateBinding(int bindingIndex)
    {
        var column = ColumnNames[bindingIndex];

        return new RelationshipAuthorizationProposedValueBinding(
            RootTable,
            column,
            bindingIndex,
            column.Value,
            column.Value
        );
    }

    private static RootWriteRowBuffer CreateRootRow(
        ResourceWritePlan writePlan,
        long? reportedSchoolDocumentId = null,
        long? reportedSchoolId = null,
        long? schoolDocumentId = null,
        long? unifiedSchoolId = null
    ) =>
        new(
            writePlan.TablePlansInDependencyOrder[0],
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(reportedSchoolDocumentId),
                new FlattenedWriteValue.Literal(reportedSchoolId),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(schoolDocumentId),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(unifiedSchoolId),
                new FlattenedWriteValue.Literal(DBNull.Value),
                new FlattenedWriteValue.Literal(null),
            ]
        );

    private static ResourceWritePlan CreateWritePlan(
        int reportedSchoolIdReferenceBindingIndex = ReportedSchoolReferenceBinding,
        string reportedSchoolIdReferenceObjectPath = "$.reportedSchoolReference"
    )
    {
        DocumentReferenceBinding[] referenceBindings =
        [
            ReferenceBinding(
                "reportedSchoolReference",
                ColumnNames[ReportedSchoolDocumentIdIndex],
                SchoolResource
            ),
            ReferenceBinding("studentReference", ColumnNames[StudentDocumentIdIndex], StudentResource),
            ReferenceBinding("schoolReference", ColumnNames[SchoolDocumentIdIndex], SchoolResource),
            ReferenceBinding("otherSchoolReference", ColumnNames[OtherSchoolDocumentIdIndex], SchoolResource),
            ReferenceBinding(
                "hiddenSchoolReference",
                new DbColumnName("HiddenSchool_DocumentId"),
                SchoolResource
            ),
        ];

        var reportedSchoolIdMetadata = ReferenceMetadata(
            reportedSchoolIdReferenceBindingIndex,
            reportedSchoolIdReferenceObjectPath
        );
        WriteValueSource[] sources =
        [
            new WriteValueSource.DocumentId(),
            new WriteValueSource.DocumentReference(ReportedSchoolReferenceBinding),
            new WriteValueSource.ReferenceDerived(reportedSchoolIdMetadata),
            new WriteValueSource.DocumentReference(StudentReferenceBinding),
            new WriteValueSource.DocumentReference(SchoolReferenceBinding),
            new WriteValueSource.DocumentReference(OtherSchoolReferenceBinding),
            new WriteValueSource.Precomputed(),
            new WriteValueSource.ReferenceDerived(
                ReferenceMetadata(HiddenSchoolReferenceBinding, "$.hiddenSchoolReference")
            ),
            new WriteValueSource.Scalar(
                new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                new RelationalScalarType(ScalarKind.String, MaxLength: 75)
            ),
        ];

        DbColumnModel[] columns =
        [
            .. ColumnNames.Select(
                (columnName, index) =>
                    new DbColumnModel(
                        columnName,
                        GetColumnKind(index, sources[index]),
                        index == NameIndex
                            ? new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                            : new RelationalScalarType(ScalarKind.Int64),
                        IsNullable: index != DocumentIdIndex,
                        SourceJsonPath: null,
                        TargetResource: null,
                        new ColumnStorage.Stored()
                    )
            ),
        ];

        var tableModel = new DbTableModel(
            RootTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentAssessment",
                [new DbKeyColumn(DocumentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [DocumentIdColumn],
                [DocumentIdColumn],
                [],
                []
            ),
        };

        var tablePlan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"StudentAssessment\" values (...)",
            UpdateSql: "update edfi.\"StudentAssessment\" set ...",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, columns.Length, 1000),
            ColumnBindings:
            [
                .. columns.Select(
                    (column, index) => new WriteColumnBinding(column, sources[index], column.ColumnName.Value)
                ),
            ],
            KeyUnificationPlans:
            [
                new KeyUnificationWritePlan(
                    ColumnNames[UnifiedSchoolIdIndex],
                    UnifiedSchoolIdIndex,
                    [
                        UnifiedMember(SchoolReferenceBinding, "schoolReference"),
                        UnifiedMember(OtherSchoolReferenceBinding, "otherSchoolReference"),
                    ]
                ),
            ]
        );

        return new ResourceWritePlan(
            new RelationalResourceModel(
                StudentAssessmentResource,
                new DbSchemaName("edfi"),
                ResourceStorageKind.RelationalTables,
                tableModel,
                [tableModel],
                referenceBindings,
                []
            ),
            [tablePlan]
        );
    }

    private static ColumnKind GetColumnKind(int bindingIndex, WriteValueSource source)
    {
        if (bindingIndex == DocumentIdIndex)
        {
            return ColumnKind.ParentKeyPart;
        }

        return source is WriteValueSource.DocumentReference ? ColumnKind.DocumentFk : ColumnKind.Scalar;
    }

    private static DocumentReferenceBinding ReferenceBinding(
        string referenceName,
        DbColumnName fkColumn,
        QualifiedResourceName targetResource
    ) =>
        new(
            IsIdentityComponent: false,
            Path($"$.{referenceName}", referenceName),
            RootTable,
            fkColumn,
            targetResource,
            []
        );

    private static ReferenceDerivedValueSourceMetadata ReferenceMetadata(
        int bindingIndex,
        string referenceObjectPath
    )
    {
        var referenceName = referenceObjectPath[2..];

        return new(
            bindingIndex,
            Path(referenceObjectPath, referenceName),
            Path("$.schoolId", "schoolId"),
            Path($"{referenceObjectPath}.schoolId", referenceName, "schoolId")
        );
    }

    private static KeyUnificationMemberWritePlan UnifiedMember(int bindingIndex, string referenceName) =>
        new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
            new DbColumnName($"{referenceName}_SchoolId"),
            Path($"$.{referenceName}.schoolId", referenceName, "schoolId"),
            ReferenceMetadata(bindingIndex, $"$.{referenceName}"),
            PresenceColumn: null,
            PresenceBindingIndex: null,
            PresenceIsSynthetic: false
        );

    private static JsonPathExpression Path(string canonical, params string[] properties) =>
        new(canonical, [.. properties.Select(static property => new JsonPathSegment.Property(property))]);

    private const int DocumentIdIndex = 0;
    private const int ReportedSchoolDocumentIdIndex = 1;
    private const int ReportedSchoolIdIndex = 2;
    private const int StudentDocumentIdIndex = 3;
    private const int SchoolDocumentIdIndex = 4;
    private const int OtherSchoolDocumentIdIndex = 5;
    private const int UnifiedSchoolIdIndex = 6;
    private const int HiddenSchoolIdIndex = 7;
    private const int NameIndex = 8;

    private const int ReportedSchoolReferenceBinding = 0;
    private const int StudentReferenceBinding = 1;
    private const int SchoolReferenceBinding = 2;
    private const int OtherSchoolReferenceBinding = 3;
    private const int HiddenSchoolReferenceBinding = 4;

    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");
    private static readonly DbColumnName StudentDocumentIdColumn = new("Student_DocumentId");

    private static readonly DbColumnName[] ColumnNames =
    [
        DocumentIdColumn,
        new("ReportedSchool_DocumentId"),
        new("ReportedSchool_SchoolId"),
        StudentDocumentIdColumn,
        new("School_DocumentId"),
        new("OtherSchool_DocumentId"),
        new("SchoolId_Unified"),
        new("HiddenSchool_SchoolId"),
        new("Name"),
    ];

    private static readonly QualifiedResourceName StudentAssessmentResource = new(
        "Ed-Fi",
        "StudentAssessment"
    );
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName StudentResource = new("Ed-Fi", "Student");
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "StudentAssessment");
    private static readonly DbTableName StudentTable = new(new DbSchemaName("edfi"), "Student");
    private static readonly DbTableName StudentSchoolAssociationTable = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );
}
