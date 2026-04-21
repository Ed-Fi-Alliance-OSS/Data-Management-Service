// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_ProfileSynthesizer_for_CreateNew_with_single_scalar_binding
{
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.firstName");
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));

        var synthesizer = BuildProfileSynthesizer();
        _result = synthesizer.Synthesize(
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "Ada"),
                writableRequestBody: body,
                currentState: null,
                profileRequest: request,
                profileAppliedContext: null,
                resolvedReferences: EmptyResolvedReferenceSet()
            )
        );
    }

    [Test]
    public void It_does_not_support_guarded_no_op() => _result.SupportsGuardedNoOp.Should().BeFalse();

    [Test]
    public void It_produces_single_table_state() => _result.TablesInDependencyOrder.Length.Should().Be(1);

    [Test]
    public void It_has_empty_current_rows() =>
        _result.TablesInDependencyOrder[0].CurrentRows.Should().BeEmpty();

    [Test]
    public void It_has_one_merged_row_with_flattener_value()
    {
        _result.TablesInDependencyOrder[0].MergedRows.Should().ContainSingle();
        ((FlattenedWriteValue.Literal)_result.TablesInDependencyOrder[0].MergedRows[0].Values[0])
            .Value.Should()
            .Be("Ada");
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_for_ExistingDocument_with_hidden_scalar_binding
{
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.birthDate");
        var body = new JsonObject { ["birthDate"] = "2026-01-01" };
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "birthDate")
        );

        var synthesizer = BuildProfileSynthesizer();
        _result = synthesizer.Synthesize(
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "request-value"),
                writableRequestBody: body,
                currentState: BuildCurrentStateWithSingleRootRow(plan, "stored-value"),
                profileRequest: request,
                profileAppliedContext: appliedContext,
                resolvedReferences: EmptyResolvedReferenceSet()
            )
        );
    }

    [Test]
    public void It_preserves_stored_value_on_hidden_binding()
    {
        ((FlattenedWriteValue.Literal)_result.TablesInDependencyOrder[0].MergedRows[0].Values[0])
            .Value.Should()
            .Be("stored-value");
    }

    [Test]
    public void It_includes_projected_current_row()
    {
        _result.TablesInDependencyOrder[0].CurrentRows.Should().ContainSingle();
        ((FlattenedWriteValue.Literal)_result.TablesInDependencyOrder[0].CurrentRows[0].Values[0])
            .Value.Should()
            .Be("stored-value");
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_for_ExistingDocument_with_visible_absent_inlined_scope
{
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.birthData.birthCity");
        var body = new JsonObject();
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$.birthData")
        );
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisibleAbsentScope("$.birthData")
        );

        var synthesizer = BuildProfileSynthesizer();
        _result = synthesizer.Synthesize(
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "flattener-value"),
                writableRequestBody: body,
                currentState: BuildCurrentStateWithSingleRootRow(plan, "stored-value"),
                profileRequest: request,
                profileAppliedContext: appliedContext,
                resolvedReferences: EmptyResolvedReferenceSet()
            )
        );
    }

    [Test]
    public void It_writes_null_literal_for_visible_absent_binding()
    {
        ((FlattenedWriteValue.Literal)_result.TablesInDependencyOrder[0].MergedRows[0].Values[0])
            .Value.Should()
            .BeNull();
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_with_HiddenPreserved_binding_and_null_CurrentState
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.firstName");
        var body = new JsonObject();
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));

        // Force HiddenPreserved disposition using a stub classifier, while passing null
        // CurrentState. The synthesizer must refuse to proceed and surface the drift as
        // an InvalidOperationException.
        var classification = new ProfileRootTableBindingClassification(
            BindingsByIndex: [RootBindingDisposition.HiddenPreserved],
            ResolverOwnedBindingIndices: ImmutableHashSet<int>.Empty
        );
        var synthesizer = new RelationalWriteProfileMergeSynthesizer(
            new StubClassifier(classification),
            new NoOpResolver()
        );

        _act = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "value"),
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*HiddenPreserved*no current row is available*");
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_request_with_mismatched_applied_context_request
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan();
        var body = new JsonObject();
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var otherRequest = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var mismatchedAppliedContext = CreateContext(otherRequest);

        _act = () =>
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "x"),
                writableRequestBody: body,
                currentState: BuildCurrentStateWithSingleRootRow(plan, "stored"),
                profileRequest: request,
                profileAppliedContext: mismatchedAppliedContext,
                resolvedReferences: EmptyResolvedReferenceSet()
            );
    }

    [Test]
    public void It_throws_ArgumentException_about_same_instance()
    {
        _act.Should().Throw<ArgumentException>().WithMessage("*same instance*");
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_request_with_inconsistent_current_state_and_applied_context
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan();
        var body = new JsonObject();
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(request);

        _act = () =>
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "x"),
                writableRequestBody: body,
                currentState: null,
                profileRequest: request,
                profileAppliedContext: appliedContext,
                resolvedReferences: EmptyResolvedReferenceSet()
            );
    }

    [Test]
    public void It_throws_ArgumentException_about_both_null_or_both_non_null()
    {
        _act.Should().Throw<ArgumentException>().WithMessage("*both be null*both be non-null*");
    }
}

[TestFixture]
public class Given_ProfileMergeRequest_with_root_extension_rows_present
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan();
        var body = new JsonObject();
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));

        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var extensionRow = new RootExtensionWriteRowBuffer(
            extensionPlan,
            [new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Blue")]
        );
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal("Ada")],
                rootExtensionRows: [extensionRow]
            )
        );

        _act = () =>
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: flattenedWriteSet,
                writableRequestBody: body,
                currentState: null,
                profileRequest: request,
                profileAppliedContext: null,
                resolvedReferences: EmptyResolvedReferenceSet()
            );
    }

    [Test]
    public void It_throws_ArgumentException_about_root_only_shape()
    {
        _act.Should().Throw<ArgumentException>().WithMessage("*root-only*fenced upstream*");
    }
}

[TestFixture]
public class Given_ProfileMergeRequest_with_root_collection_candidates_present
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan();
        var body = new JsonObject();
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));

        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var collectionTableModel = AdapterFactoryTestFixtures.BuildCollectionTableModel();
        var collectionPlan = AdapterFactoryTestFixtures.BuildCollectionTableWritePlan(collectionTableModel);
        var collectionCandidate = new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values:
            [
                FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Physical"),
            ],
            semanticIdentityValues: ["Physical"]
        );
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal("Ada")],
                collectionCandidates: [collectionCandidate]
            )
        );

        _act = () =>
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: flattenedWriteSet,
                writableRequestBody: body,
                currentState: null,
                profileRequest: request,
                profileAppliedContext: null,
                resolvedReferences: EmptyResolvedReferenceSet()
            );
    }

    [Test]
    public void It_throws_ArgumentException_about_root_only_shape()
    {
        _act.Should().Throw<ArgumentException>().WithMessage("*root-only*fenced upstream*");
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_with_key_unification_plan_visible_member
{
    private RelationalWriteMergeResult _result = null!;
    private int _canonicalIndex;
    private int _presenceIndex;
    private int _memberIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIndicesByPath["$.memberA"];
        // Bindings: 0=canonical (Precomputed), 1=presence (Precomputed), 2=member (Scalar).
        _memberIndex = 2;

        var body = new JsonObject { ["memberA"] = 42 };
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));

        var synthesizer = BuildProfileSynthesizer();
        // Flattener sourced value goes at the member (scalar) binding index. Canonical and
        // presence bindings are resolver-owned so their flattener positions are ignored.
        var flattenerValues = new object?[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        flattenerValues[_memberIndex] = 42;

        _result = synthesizer.Synthesize(
            new RelationalWriteProfileMergeRequest(
                writePlan: plan,
                flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, flattenerValues),
                writableRequestBody: body,
                currentState: null,
                profileRequest: request,
                profileAppliedContext: null,
                resolvedReferences: EmptyResolvedReferenceSet()
            )
        );
    }

    [Test]
    public void It_does_not_support_guarded_no_op() => _result.SupportsGuardedNoOp.Should().BeFalse();

    [Test]
    public void It_writes_canonical_from_resolver()
    {
        (
            (FlattenedWriteValue.Literal)
                _result.TablesInDependencyOrder[0].MergedRows[0].Values[_canonicalIndex]
        )
            .Value.Should()
            .Be(42);
    }

    [Test]
    public void It_writes_synthetic_presence_true_from_resolver()
    {
        ((FlattenedWriteValue.Literal)_result.TablesInDependencyOrder[0].MergedRows[0].Values[_presenceIndex])
            .Value.Should()
            .Be(true);
    }

    [Test]
    public void It_preserves_flattener_sourced_member_binding()
    {
        ((FlattenedWriteValue.Literal)_result.TablesInDependencyOrder[0].MergedRows[0].Values[_memberIndex])
            .Value.Should()
            .Be(42);
    }
}

[TestFixture]
public class Given_ProfileSynthesizer_propagates_resolver_disagreement_exception
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, _, _) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberB",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);

        // memberA visible (from body) = 1, memberB hidden-preserved from stored = 2.
        // Resolver's first-present-wins should detect disagreement and raise a validation
        // exception, which the synthesizer must propagate unchanged.
        var body = new JsonObject { ["memberA"] = 1 };
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "memberB")
        );

        var rootTable = plan.TablePlansInDependencyOrder[0];
        // Columns: 0=canonical, 1=memberA, 2=memberB (no presence columns since synthetic=false).
        var storedRow = new object?[rootTable.TableModel.Columns.Count];
        storedRow[0] = null;
        storedRow[1] = null;
        storedRow[2] = 2;

        var synthesizer = BuildProfileSynthesizer();
        _act = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: BuildFlattenedWriteSetFrom(plan),
                    writableRequestBody: body,
                    currentState: BuildCurrentStateWithSingleRootRow(plan, storedRow),
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_propagates_RelationalWriteRequestValidationException()
    {
        _act.Should().Throw<RelationalWriteRequestValidationException>();
    }
}
