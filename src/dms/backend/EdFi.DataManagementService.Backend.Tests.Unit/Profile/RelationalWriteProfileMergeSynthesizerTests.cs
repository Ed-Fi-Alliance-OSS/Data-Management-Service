// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Model;
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
        _result = UnwrapMergeResult(
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "Ada"),
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
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
        _result = UnwrapMergeResult(
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "request-value"),
                    writableRequestBody: body,
                    currentState: BuildCurrentStateWithSingleRootRow(plan, "stored-value"),
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
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
        _result = UnwrapMergeResult(
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, "flattener-value"),
                    writableRequestBody: body,
                    currentState: BuildCurrentStateWithSingleRootRow(plan, "stored-value"),
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
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
            new NoOpResolver(),
            new ProfileSeparateTableBindingClassifier(),
            new ProfileSeparateTableKeyUnificationResolver(),
            new ProfileSeparateTableMergeDecider()
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
    public void It_does_not_require_same_instance()
    {
        _act.Should().NotThrow();
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
public class Given_Synthesizer_SeparateTable_Contract_Allows_CollectionCandidates_Under_RootExtensionRow
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

        // Nested collection candidate under the extension row — CP3 Task 21 allows
        // request construction so the walker can handle the shape.
        var collectionTableModel = AdapterFactoryTestFixtures.BuildCollectionTableModel();
        var collectionPlan = AdapterFactoryTestFixtures.BuildCollectionTableWritePlan(collectionTableModel);
        var nestedCollectionCandidate = new CollectionWriteCandidate(
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

        var extensionRow = new RootExtensionWriteRowBuffer(
            extensionPlan,
            [new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Blue")],
            collectionCandidates: [nestedCollectionCandidate]
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
    public void It_accepts_the_request_shape()
    {
        _act.Should().NotThrow();
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

        _result = UnwrapMergeResult(
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: BuildFlattenedWriteSetFrom(plan, flattenerValues),
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
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

// ── Slice 3 separate-table matrix fixtures ─────────────────────────────────────
//
// The plan used by these fixtures is the two-table shape produced by
// BuildRootPlusRootExtensionPlan with a single scalar extension binding at
// $._ext.sample.favoriteColor. Extension-table binding indices:
//   [0] = ParentKeyPart DocumentId (StorageManaged, not compared)
//   [1] = Scalar $._ext.sample.favoriteColor

[TestFixture]
public class Given_Synthesizer_SeparateTable_VisiblePresent_NoStored_Creatable_True
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["favoriteColor"] = "Blue" } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample", creatable: true)
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, "Blue"]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_two_table_states() =>
        _outcome.MergeResult!.TablesInDependencyOrder.Length.Should().Be(2);

    [Test]
    public void It_has_no_current_extension_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Should().BeEmpty();

    [Test]
    public void It_has_one_merged_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_carries_request_value_on_merged_extension_row()
    {
        (
            (FlattenedWriteValue.Literal)
                _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[1]
        )
            .Value.Should()
            .Be("Blue");
    }
}

[TestFixture]
public class Given_Synthesizer_SeparateTable_VisiblePresent_NoStored_Creatable_False
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["favoriteColor"] = "Blue" } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample", creatable: false)
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, "Blue"]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_a_rejection() => _outcome.IsRejection.Should().BeTrue();

    [Test]
    public void It_has_no_merge_result() => _outcome.MergeResult.Should().BeNull();

    [Test]
    public void It_identifies_the_extension_scope_as_the_rejected_scope()
    {
        _outcome.CreatabilityRejection!.ScopeJsonScope.Should().Be("$._ext.sample");
    }

    [Test]
    public void It_carries_a_diagnostic_message_mentioning_the_scope()
    {
        _outcome.CreatabilityRejection!.Message.Should().Contain("$._ext.sample");
    }
}

[TestFixture]
public class Given_Synthesizer_SeparateTable_VisiblePresent_Stored_Matched_Updates
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["favoriteColor"] = "Blue" } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample", creatable: true)
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, "Blue"]
        );

        // Stored state: root row = "AdaStored", extension row has DocumentId + "Red".
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_one_current_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);

    [Test]
    public void It_has_one_merged_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_overlays_request_value_on_merged_extension_row()
    {
        (
            (FlattenedWriteValue.Literal)
                _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[1]
        )
            .Value.Should()
            .Be("Blue");
    }

    [Test]
    public void It_preserves_stored_value_on_current_extension_row()
    {
        (
            (FlattenedWriteValue.Literal)
                _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows[0].Values[1]
        )
            .Value.Should()
            .Be("Red");
    }
}

// Empty visible scope — the flattener's emitEmptyRootExtensionBuffers contract produces a
// buffer with no scope-bound scalar data (all JSON-bound values default to Literal(null)).
// The synthesizer must honor the Slice 3 decision-matrix rule that separate-table outcomes
// derive from scope metadata, not buffer content, and should route Insert / Update
// normally. These two fixtures pin that behavior.

[TestFixture]
public class Given_Synthesizer_SeparateTable_VisiblePresent_NoStored_EmptyBuffer_Inserts_With_Null_Scalar
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        // Writable body carries an explicitly empty visible extension scope: _ext.sample = {}.
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject() },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample", creatable: true)
        );
        // Emulates the flattener with EmitEmptyRootExtensionBuffers=true producing a buffer
        // whose scope-bound scalar is null.
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_no_current_extension_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Should().BeEmpty();

    [Test]
    public void It_emits_one_merged_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_carries_null_scalar_on_merged_extension_row()
    {
        (
            (FlattenedWriteValue.Literal)
                _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[1]
        )
            .Value.Should()
            .BeNull();
    }
}

[TestFixture]
public class Given_Synthesizer_SeparateTable_VisiblePresent_Stored_Matched_EmptyBuffer_Clears_Visible_Scalar
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        // Writable body carries an explicitly empty visible extension scope for an update.
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject() },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample", creatable: true)
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_one_current_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);

    [Test]
    public void It_emits_one_merged_extension_row_not_a_delete() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_overlays_null_onto_merged_extension_row_visible_scalar()
    {
        (
            (FlattenedWriteValue.Literal)
                _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[1]
        )
            .Value.Should()
            .BeNull();
    }
}

[TestFixture]
public class Given_Synthesizer_SeparateTable_VisibleAbsent_Stored_Matched_Deletes
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        // Request omits the extension scope → VisibleAbsent.
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope("$._ext.sample")
        );
        // Flattener still emits an extension row (shape invariant); the decider short-circuits
        // to Delete on VisibleAbsent + matched-stored.
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_one_current_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);

    [Test]
    public void It_emits_no_merged_extension_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Should().BeEmpty();
}

[TestFixture]
public class Given_Synthesizer_SeparateTable_HiddenStored_Preserves_With_Identical_Merged_Row
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        // Profile hides $._ext.sample, so both sides classify it as Hidden — C3 emits a
        // Hidden request-scope entry alongside the stored Hidden classification.
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestHiddenScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredHiddenScope("$._ext.sample")
        );
        // Flattener produces an extension row but the decider sees Hidden+row → Preserve.
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_one_current_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);

    [Test]
    public void It_has_one_merged_extension_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    /// <summary>
    /// Critical Preserve invariant: the merged row values must be identical to the current
    /// row values. Omitting the merged row would be interpreted by the shared no-profile
    /// persister as "current + no merged = delete" and silently discard the hidden stored
    /// data.
    /// </summary>
    [Test]
    public void It_emits_merged_row_values_identical_to_current_row_values()
    {
        var tableState = _outcome.MergeResult!.TablesInDependencyOrder[1];
        var currentRow = tableState.CurrentRows[0];
        var mergedRow = tableState.MergedRows[0];
        mergedRow.Values.Length.Should().Be(currentRow.Values.Length);
        for (var i = 0; i < mergedRow.Values.Length; i++)
        {
            var mergedLit = (FlattenedWriteValue.Literal)mergedRow.Values[i];
            var currentLit = (FlattenedWriteValue.Literal)currentRow.Values[i];
            mergedLit.Value.Should().Be(currentLit.Value, $"binding index {i} must be preserved");
        }
    }
}

/// <summary>
/// Regression pin: slice-3 synthesizer must silently skip plan tables whose kind is
/// not <see cref="DbTableKind.RootExtension"/> instead of throwing. Reproduces the
/// multi-table School write-plan scenario (root + root-extension + collection-extension-scope)
/// where the profiled request only exercises root scopes, so the synthesizer must let the
/// collection-extension-scope table pass through untouched (the no-profile persister handles it).
/// </summary>
[TestFixture]
public class Given_Synthesizer_With_Multi_Table_Plan_And_Unused_Collection_Scope_Skips_Silently
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlusUnusedTablePlan(
            extensionBinding: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];

        // Request exercises only the root scope; the extension and collection-extension-scope
        // are never referenced. Per C3, Core still emits a RequestScopeState for every
        // compiled non-collection scope — here the extension classifies as VisibleAbsent
        // because the request omits it. Create-new path (no currentState, no
        // profileAppliedContext).
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$._ext.sample")
        );
        // Flattened set carries the extension row (shape invariant) but NOT the unused
        // collection-extension-scope table — the synthesizer must iterate the plan (which
        // has 3 tables) and skip the third one without throwing.
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_does_not_reject() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_returns_a_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    [Test]
    public void It_skips_the_unused_collection_extension_scope_table()
    {
        // The plan has 3 tables (root + root-extension + collection-extension-scope) but
        // the synthesizer must silently skip the collection-extension-scope; the merge
        // result must never include 3 table states. It carries 1 (root only, when the
        // extension yielded no actionable outcome) or 2 (root + extension) states.
        _outcome.MergeResult!.TablesInDependencyOrder.Length.Should().BeLessThan(3);
    }
}

/// <summary>
/// Regression pin for the Task 5 pre-decider skip predicate: a request-side
/// <see cref="ProfileVisibilityKind.VisibleAbsent"/> on a separate-table scope with no
/// matched stored visible row must be treated as a genuine no-op. The synthesizer must
/// skip the scope entirely rather than calling the decider, which would raise
/// <see cref="InvalidOperationException"/> because the (VisibleAbsent, no-stored) cell
/// has no actionable outcome in the Slice 3 decision matrix.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_VisibleAbsent_NoStoredRow_SkipsSilently
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        // Request omits the extension scope → VisibleAbsent. There is no stored scope state
        // and no stored extension row — the "no stored row" case.
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$._ext.sample")
        );
        var appliedContext = CreateContext(request, visibleStoredBody: null, StoredVisiblePresentScope("$"));
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        // Root row present, but no stored extension row.
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: null
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_produces_root_table_only()
    {
        _outcome.MergeResult.Should().NotBeNull();
        _outcome.MergeResult!.TablesInDependencyOrder.Length.Should().Be(1);
    }
}

/// <summary>
/// Regression pin: request-side <see cref="ProfileVisibilityKind.VisibleAbsent"/> with a
/// stored-side <see cref="ProfileVisibilityKind.VisibleAbsent"/> scope (i.e. no matched
/// stored visible row) must also be treated as a genuine no-op. This covers the matrix
/// cell where the Delete predicate does not trigger because the stored side is not
/// <see cref="ProfileVisibilityKind.VisiblePresent"/>.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_VisibleAbsent_NoMatchedStoredVisibleRow_SkipsSilently
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$._ext.sample")
        );
        // Stored side also reports VisibleAbsent for the extension scope — no visible row
        // to delete. Delete predicate must not trigger; synthesizer must skip.
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisibleAbsentScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: null
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_produces_root_table_only()
    {
        _outcome.MergeResult.Should().NotBeNull();
        _outcome.MergeResult!.TablesInDependencyOrder.Length.Should().Be(1);
    }
}

/// <summary>
/// Regression pin: when neither the request nor the stored context references the
/// separate-table scope at all, the synthesizer must skip the scope silently. This is
/// the "both sides absent entirely" corner of the pre-decider skip predicate.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_NoRequest_NoStoredRow_SkipsSilently
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject { ["firstName"] = "Ada" };
        // Request side classifies the extension as VisibleAbsent (per C3 Core emits a
        // RequestScopeState for every compiled non-collection scope); stored side is
        // absent for the create-new path (no profileAppliedContext, no stored row).
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_produces_root_table_only()
    {
        _outcome.MergeResult.Should().NotBeNull();
        _outcome.MergeResult!.TablesInDependencyOrder.Length.Should().Be(1);
    }
}

/// <summary>
/// Regression pin for the Task 5 fail-closed contract: a request-side
/// <see cref="ProfileVisibilityKind.Hidden"/> scope paired with a matched visible stored
/// row is an inconsistent tuple (the profile's request-side view cannot reasonably Hide a
/// scope whose stored state is VisiblePresent). Task 4's decider throws
/// <see cref="InvalidOperationException"/> on this tuple. The synthesizer's pre-decider
/// skip predicate must NOT swallow this case — it must route it to the decider so the
/// inconsistency surfaces instead of silently preserving drifted profile state.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_HiddenRequest_With_Visible_Stored_And_Row_FailsClosed
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject { ["firstName"] = "Ada" };
        // Request marks the extension scope Hidden — inconsistent with a matched visible
        // stored row.
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestHiddenScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        // Matched visible stored row: storedRowExists must be true for the decider to see
        // the inconsistent (Hidden request, VisiblePresent stored, row) tuple.
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        var synthesizer = BuildProfileSynthesizer();
        _synthesizeAction = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_routes_to_decider_and_throws_fail_closed() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProfileSeparateTableMergeDecider*$._ext.sample*no actionable*");
}

/// <summary>
/// Regression pin for the fail-closed contract on missing request-side scope metadata:
/// per C3 (01a-c3-request-visibility-and-writable-shaping.md), every compiled non-collection
/// scope must have a <see cref="RequestScopeState"/> entry. A null request-scope entry
/// paired with a matched visible stored row is a Core contract violation, and the
/// synthesizer's pre-decider contract check catches this and fails closed with a
/// synthesizer-level message identifying the offending scope instead of deferring the
/// throw to the decider's generic "no actionable" message.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_NullRequest_With_Visible_Stored_And_Row_FailsClosed
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject { ["firstName"] = "Ada" };
        // Request has NO scope state for $._ext.sample (omit from RequestScopeStates) —
        // null-request case; a flattened buffer is still emitted per the shape invariant.
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$")
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        var synthesizer = BuildProfileSynthesizer();
        _synthesizeAction = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_throws_with_synthesizer_contract_violation_message() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$._ext.sample*RequestScopeStates has no entry*");
}

/// <summary>
/// Regression pin for the fail-closed contract on missing stored-side scope metadata:
/// per C6 (01a-c6-stored-state-projection-and-hidden-member-paths.md), every compiled
/// non-collection scope must have a <see cref="StoredScopeState"/> entry when a profile
/// applies. A current stored separate-table row with no StoredScopeState entry would
/// otherwise silently preserve the row on a VisibleAbsent request instead of deleting or
/// failing closed. The synthesizer's pre-decider contract check surfaces this as a
/// Core contract violation.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_StoredRow_Without_StoredScopeState_FailsClosed
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject { ["firstName"] = "Ada" };
        // Request intentionally omits the extension scope (VisibleAbsent); the scope is
        // well-formed on the request side.
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisibleAbsentScope("$._ext.sample")
        );
        // Stored context omits the extension scope from StoredScopeStates even though a
        // stored extension row exists — this is the contract violation the synthesizer
        // must fail closed on rather than silently preserve the row.
        var appliedContext = CreateContext(request, visibleStoredBody: null, StoredVisiblePresentScope("$"));
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, null]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        var synthesizer = BuildProfileSynthesizer();
        _synthesizeAction = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_throws_with_synthesizer_contract_violation_message() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$._ext.sample*StoredScopeStates has no entry*");
}

/// <summary>
/// Regression pin for the narrowed Preserve-dominates rule: a VisiblePresent request
/// paired with a Hidden stored scope and a stored row is an inconsistent tuple (Hidden
/// classification is profile-level and applied uniformly on both sides of a consistent
/// writable profile). The synthesizer must route this through the decider, which throws
/// fail closed instead of returning Preserve and silently discarding the request's
/// visible values.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_VisiblePresentRequest_With_Hidden_Stored_And_Row_FailsClosed
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample", creatable: true)
        );
        // Stored side classifies the extension scope as Hidden with a matched row — the
        // inconsistent tuple the narrowed decider must fail closed on.
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredHiddenScope("$._ext.sample")
        );
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, "Blue"]
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, "Red"]
        );

        var synthesizer = BuildProfileSynthesizer();
        _synthesizeAction = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_routes_to_decider_and_throws_fail_closed() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProfileSeparateTableMergeDecider*$._ext.sample*no actionable*");
}

/// <summary>
/// Regression for the Slice 3 separate-table key-unification scope bug: the
/// synthesizer's matched-update path used to hand the root request body directly
/// to the key-unification resolver context. Production compiles separate-table
/// member paths as scope-relative (<c>$.memberA</c>, not <c>$._ext.sample.memberA</c>),
/// so the resolver could not find the visible member on the root body and wrote
/// <c>null</c> for canonical. The fix resolves the scope-scoped request node from
/// the root body using the table plan's <c>JsonScope</c> before building the
/// resolver context, so scope-relative member paths evaluate against the right node.
/// </summary>
[TestFixture]
public class Given_Synthesizer_SeparateTable_Update_With_ScopeRelative_KeyUnification_Member_Path_Uses_Scoped_Request_Node
{
    private ProfileMergeOutcome _outcome;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        // Production-shaped: member RelativePath is scope-relative ("$.memberA"); the
        // extension table's JsonScope ("$._ext.sample") is the node against which the
        // member must be evaluated.
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        var extensionPlan = plan.TablePlansInDependencyOrder[1];

        // Full root request body, as the synthesizer receives it in production.
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 42 } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope("$._ext.sample")
        );

        // Extension bindings: [0] DocumentId, [1] canonical (resolver-owned), [2] memberA.
        // The flattened buffer's member value (42) is what flattening the scoped node for
        // the visible-present member would produce in production.
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [345L, null, 42]
        );

        // Matched stored extension row with a stored canonical (99) and absent stored
        // memberA; the request-side visible member should win and canonical resolves to 42.
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, 99, null]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_writes_canonical_from_request_body_via_scope_navigation()
    {
        // Before the fix, the resolver evaluated "$.memberA" against the root body and
        // could not find it, so the canonical was written as null. After the fix, the
        // synthesizer navigates to "$._ext.sample" first and the resolver finds 42.
        (
            (FlattenedWriteValue.Literal)
                _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[_canonicalIndex]
        )
            .Value.Should()
            .Be(42);
    }
}

[TestFixture]
public class Given_ProfileMergeRequest_with_attached_aligned_scope_data_on_top_level_candidate
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

        // Build a CollectionExtensionScope plan via the shared fixture helper.
        var alignedTableModel = AdapterFactoryTestFixtures.BuildCollectionExtensionScopeTableModel();
        var alignedPlan = AdapterFactoryTestFixtures.BuildCollectionExtensionScopeTableWritePlan(
            alignedTableModel
        );

        var attachedAlignedScope = new CandidateAttachedAlignedScopeData(
            tableWritePlan: alignedPlan,
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(null),
            ]
        );

        var topLevelCandidate = new CollectionWriteCandidate(
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
            semanticIdentityValues: ["Physical"],
            attachedAlignedScopeData: [attachedAlignedScope]
        );

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal("Ada")],
                collectionCandidates: [topLevelCandidate]
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
    public void It_accepts_the_request_shape()
    {
        _act.Should().NotThrow();
    }
}

/// <summary>
/// CP3 Task 21 retires the constructor fence on AttachedAlignedScopeData at any
/// collection-candidate depth. Nested attached aligned data must now be accepted by
/// request construction and handled by the walker when that topology opens.
/// </summary>
[TestFixture]
public class Given_a_RelationalWriteProfileMergeRequest_with_AttachedAlignedScopeData_on_a_nested_candidate
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

        var alignedTableModel = AdapterFactoryTestFixtures.BuildCollectionExtensionScopeTableModel();
        var alignedPlan = AdapterFactoryTestFixtures.BuildCollectionExtensionScopeTableWritePlan(
            alignedTableModel
        );

        // The NESTED candidate carries non-empty AttachedAlignedScopeData. CP3 Task 21
        // accepts this at construction time.
        var attachedAlignedScope = new CandidateAttachedAlignedScopeData(
            tableWritePlan: alignedPlan,
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(null),
            ]
        );
        var nestedCandidate = new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0, 0],
            requestOrder: 0,
            values:
            [
                FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Mailing"),
            ],
            semanticIdentityValues: ["Mailing"],
            attachedAlignedScopeData: [attachedAlignedScope]
        );

        // The TOP-LEVEL candidate has empty AttachedAlignedScopeData; the nested candidate
        // carries the shape now allowed through construction.
        var topLevelCandidate = new CollectionWriteCandidate(
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
            semanticIdentityValues: ["Physical"],
            collectionCandidates: [nestedCandidate]
        );

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal("Ada")],
                collectionCandidates: [topLevelCandidate]
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
    public void It_accepts_the_request_shape()
    {
        _act.Should().NotThrow();
    }
}

[TestFixture]
public class Given_ProfileMergeRequest_accepts_collection_candidate_under_root_extension_row
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

        var collectionTableModel = AdapterFactoryTestFixtures.BuildCollectionTableModel();
        var collectionPlan = AdapterFactoryTestFixtures.BuildCollectionTableWritePlan(collectionTableModel);
        var nestedCollectionCandidate = new CollectionWriteCandidate(
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

        var extensionRow = new RootExtensionWriteRowBuffer(
            extensionPlan,
            [new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Blue")],
            collectionCandidates: [nestedCollectionCandidate]
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
    public void It_accepts_the_request_shape()
    {
        _act.Should().NotThrow();
    }
}

[TestFixture]
public class Given_root_extension_scope_with_child_collection_candidate
{
    private const long RootExtensionDocumentId = 345L;
    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, extensionPlan, childPlan) = RootExtensionChildCollectionTopologyBuilders.BuildPlan();
        _childPlan = childPlan;

        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["favoriteColor"] = "Blue",
                    ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "C1" }),
                },
            },
        };

        var childCandidate = RootExtensionChildCollectionTopologyBuilders.BuildChildCandidate(
            childPlan,
            "C1",
            requestOrder: 0
        );
        var flattened = RootExtensionChildCollectionTopologyBuilders.BuildFlattenedWriteSet(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [RootExtensionDocumentId, "Blue"],
            childCandidates: [childCandidate]
        );
        var request = RootExtensionChildCollectionTopologyBuilders.BuildVisibleRequest(body, "C1");

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_the_root_extension_child_collection_table_state()
    {
        var childTable = _outcome.MergeResult!.TablesInDependencyOrder.SingleOrDefault(s =>
            s.TableWritePlan.TableModel.Table == _childPlan.TableModel.Table
        );

        childTable.Should().NotBeNull();
        childTable!.MergedRows.Should().HaveCount(1);
        ((FlattenedWriteValue.Literal)childTable.MergedRows[0].Values[3]).Value.Should().Be("C1");
    }

    [Test]
    public void It_stamps_the_root_extension_physical_identity_on_the_child_row()
    {
        var childTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childPlan.TableModel.Table
        );

        ((FlattenedWriteValue.Literal)childTable.MergedRows[0].Values[1])
            .Value.Should()
            .Be(RootExtensionDocumentId);
    }
}

[TestFixture]
public class Given_updated_root_extension_scope_with_child_collection_candidate
{
    private const long RootExtensionDocumentId = 345L;
    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, extensionPlan, childPlan) = RootExtensionChildCollectionTopologyBuilders.BuildPlan();
        _childPlan = childPlan;

        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["favoriteColor"] = "Blue",
                    ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "C1" }),
                },
            },
        };

        var childCandidate = RootExtensionChildCollectionTopologyBuilders.BuildChildCandidate(
            childPlan,
            "C1",
            requestOrder: 0
        );
        var flattened = RootExtensionChildCollectionTopologyBuilders.BuildFlattenedWriteSet(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [RootExtensionDocumentId, "Blue"],
            childCandidates: [childCandidate]
        );
        var request = RootExtensionChildCollectionTopologyBuilders.BuildVisibleRequest(body, "C1");
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope(RootExtensionChildCollectionTopologyBuilders.ExtensionScope)
        );
        var currentState = RootExtensionChildCollectionTopologyBuilders.BuildCurrentState(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [RootExtensionDocumentId, "Red"],
            childRows: []
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_walks_the_child_collection_after_the_root_extension_update()
    {
        var childTable = _outcome.MergeResult!.TablesInDependencyOrder.SingleOrDefault(s =>
            s.TableWritePlan.TableModel.Table == _childPlan.TableModel.Table
        );

        childTable.Should().NotBeNull();
        childTable!.MergedRows.Should().HaveCount(1);
        ((FlattenedWriteValue.Literal)childTable.MergedRows[0].Values[1])
            .Value.Should()
            .Be(RootExtensionDocumentId);
    }
}

[TestFixture]
public class Given_hidden_root_extension_scope_with_current_child_collection_row
{
    private const long RootExtensionDocumentId = 345L;
    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, extensionPlan, childPlan) = RootExtensionChildCollectionTopologyBuilders.BuildPlan();
        _childPlan = childPlan;

        var body = new JsonObject { ["firstName"] = "Ada" };
        var flattened = RootExtensionChildCollectionTopologyBuilders.BuildFlattenedWriteSet(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [RootExtensionDocumentId, null],
            childCandidates: []
        );
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestHiddenScope(RootExtensionChildCollectionTopologyBuilders.ExtensionScope)
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredHiddenScope(RootExtensionChildCollectionTopologyBuilders.ExtensionScope)
        );
        var currentState = RootExtensionChildCollectionTopologyBuilders.BuildCurrentState(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [RootExtensionDocumentId, "Red"],
            childRows:
            [
                [901L, RootExtensionDocumentId, 7, "C1"],
            ]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_preserves_the_root_extension_child_collection_row()
    {
        var childTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childPlan.TableModel.Table
        );

        childTable.CurrentRows.Should().HaveCount(1);
        childTable.MergedRows.Should().HaveCount(1);
        for (var i = 0; i < childTable.CurrentRows[0].Values.Length; i++)
        {
            var currentValue = ((FlattenedWriteValue.Literal)childTable.CurrentRows[0].Values[i]).Value;
            var mergedValue = ((FlattenedWriteValue.Literal)childTable.MergedRows[0].Values[i]).Value;
            mergedValue.Should().Be(currentValue, $"binding index {i} must be preserved");
        }
    }
}

internal static class RootExtensionChildCollectionTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("sample");
    public const string ExtensionScope = "$._ext.sample";
    private const string ChildScope = "$._ext.sample.children[*]";

    public static (ResourceWritePlan Plan, TableWritePlan ExtensionPlan, TableWritePlan ChildPlan) BuildPlan()
    {
        var basePlan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var rootPlan = basePlan.TablePlansInDependencyOrder[0];
        var extensionPlan = basePlan.TablePlansInDependencyOrder[1];
        var childPlan = BuildChildCollectionPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "RootExtensionChildTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    extensionPlan.TableModel,
                    childPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, extensionPlan, childPlan]
        );

        return (resourceWritePlan, extensionPlan, childPlan);
    }

    public static CollectionWriteCandidate BuildChildCandidate(
        TableWritePlan childPlan,
        string identityValue,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[childPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        return new CollectionWriteCandidate(
            tableWritePlan: childPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue]
        );
    }

    public static FlattenedWriteSet BuildFlattenedWriteSet(
        ResourceWritePlan plan,
        TableWritePlan extensionPlan,
        object?[] rootLiteralsByBindingIndex,
        object?[] extensionLiteralsByBindingIndex,
        ImmutableArray<CollectionWriteCandidate> childCandidates
    )
    {
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var rootValues = BuildLiteralValues(rootPlan, rootLiteralsByBindingIndex);
        var extensionValues = BuildLiteralValues(extensionPlan, extensionLiteralsByBindingIndex);
        var extensionRow = new RootExtensionWriteRowBuffer(
            extensionPlan,
            extensionValues,
            collectionCandidates: childCandidates
        );

        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, rootExtensionRows: [extensionRow])
        );
    }

    public static ProfileAppliedWriteRequest BuildVisibleRequest(JsonNode body, string childIdentity) =>
        new(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    new ScopeInstanceAddress(ExtensionScope, []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        ChildScope,
                        new ScopeInstanceAddress(ExtensionScope, []),
                        Identity(childIdentity)
                    ),
                    Creatable: true,
                    RequestJsonPath: "$._ext.sample.children[0]"
                ),
            ]
        );

    public static RelationalWriteCurrentState BuildCurrentState(
        ResourceWritePlan plan,
        object?[] rootRowValues,
        object?[]? extensionRowValues,
        IReadOnlyList<object?[]> childRows
    )
    {
        var rootTableModel = plan.TablePlansInDependencyOrder[0].TableModel;
        var extensionTableModel = plan.TablePlansInDependencyOrder[1].TableModel;
        var childTableModel = plan.TablePlansInDependencyOrder[2].TableModel;
        var extensionRows = extensionRowValues is null ? (IReadOnlyList<object?[]>)[] : [extensionRowValues];
        return new(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(rootTableModel, [rootRowValues]),
                new HydratedTableRows(extensionTableModel, extensionRows),
                new HydratedTableRows(childTableModel, childRows),
            ],
            []
        );
    }

    private static TableWritePlan BuildChildCollectionPlan()
    {
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var documentIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var identityColumn = new DbColumnModel(
            ColumnName: new DbColumnName("IdentityField0"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(
                "$.identityField0",
                [new JsonPathSegment.Property("identityField0")]
            ),
            TargetResource: null
        );
        DbColumnModel[] columns = [childItemIdColumn, documentIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "RootExtensionChildren"),
            JsonScope: new JsonPathExpression(ChildScope, []),
            Key: new TableKey(
                "PK_RootExtensionChildren",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.ExtensionCollection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        identityColumn.SourceJsonPath!.Value,
                        identityColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO sample.\"RootExtensionChildren\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(documentIdColumn, new WriteValueSource.ParentKeyPart(0), "DocumentId"),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    identityColumn,
                    new WriteValueSource.Scalar(
                        identityColumn.SourceJsonPath!.Value,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "IdentityField0"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(identityColumn.SourceJsonPath!.Value, 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE sample.\"RootExtensionChildren\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM sample.\"RootExtensionChildren\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    private static ImmutableArray<FlattenedWriteValue> BuildLiteralValues(
        TableWritePlan tablePlan,
        object?[] literalsByBindingIndex
    )
    {
        var values = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(
                i < literalsByBindingIndex.Length ? literalsByBindingIndex[i] : null
            );
        }
        return [.. values];
    }

    private static ImmutableArray<SemanticIdentityPart> Identity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];
}

[TestFixture]
public class Given_ProfileMergeRequest_with_non_collection_root_candidate_still_rejects
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildSingleScalarBindingRootPlan();
        var body = new JsonObject();
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));

        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // ExtensionCollection is the non-base-Collection kind that CollectionWriteCandidate
        // still accepts but the Slice 4 profile merge gate must fence.
        var extCollectionTableModel = AdapterFactoryTestFixtures.BuildExtensionCollectionTableModel();
        var extCollectionPlan = AdapterFactoryTestFixtures.BuildExtensionCollectionCandidateTableWritePlan(
            extCollectionTableModel
        );
        var candidate = new CollectionWriteCandidate(
            tableWritePlan: extCollectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values:
            [
                FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Code1"),
            ],
            semanticIdentityValues: ["Code1"]
        );

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal("Ada")],
                collectionCandidates: [candidate]
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
    public void It_throws_ArgumentException_about_non_collection_table_kind()
    {
        _act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*DbTableKind.Collection*root-attached base collection*");
    }
}

[TestFixture]
public class Given_ProfileMergeRequest_with_top_level_base_collection_candidate
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
    public void It_does_not_throw()
    {
        // Root-attached base Collection candidate with no nesting or attached-aligned scope passes the Slice 4 gate.
        _act.Should().NotThrow();
    }
}

// ── Slice 4 top-level collection synthesizer fixtures ──────────────────────────
//
// These fixtures exercise the full synthesizer path for top-level collection candidates
// on the root row.  The collection table layout produced by
// Slice4Builders.MinimalCollectionTableWritePlan is:
//   [0] CollectionItemId  (Precomputed)  — StableRowIdentityBindingIndex = 0
//   [1] ParentDocumentId  (DocumentId)   — parent key (not ParentKeyPart, so no rewrite)
//   [2] Ordinal           (Ordinal)      — OrdinalBindingIndex = 2
//   [3] IdentityField0    (Scalar)       — SemanticIdentityBinding 0, binding index 3

/// <summary>
/// Local helpers for Slice-4 synthesizer fixtures. Shared between fixtures in this file.
/// Candidates, stored rows, and request items use the "$.addresses[*]" scope with a single
/// identity field "$.identityField0".
/// </summary>
internal static class CollectionSynthesizerBuilders
{
    public const string CollectionScope = "$.addresses[*]";
    public const string IdentityPath = "$.identityField0";

    // ── Canonical identity builders ────────────────────────────────────────

    public static ImmutableArray<SemanticIdentityPart> Identity(string value) =>
        [new SemanticIdentityPart(IdentityPath, JsonValue.Create(value), IsPresent: true)];

    // ── Write-plan / flattened-write-set builders ──────────────────────────

    /// <summary>
    /// Builds a two-table ResourceWritePlan: [0] root, [1] collection table at
    /// <see cref="CollectionScope"/> with a single identity column.
    /// </summary>
    public static (ResourceWritePlan Plan, TableWritePlan CollectionPlan) BuildRootAndCollectionPlan()
    {
        var collectionPlan = Slice4Builders.MinimalCollectionTableWritePlan(CollectionScope, 1);
        var rootPlan = BuildMinimalRootPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Address"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, collectionPlan]
        );
        return (resourceWritePlan, collectionPlan);
    }

    /// <summary>
    /// Builds a minimal root table plan: [0] DocumentId (DocumentId).
    /// </summary>
    private static TableWritePlan BuildMinimalRootPlan()
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var rootTableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Address"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_Address",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        return new TableWritePlan(
            TableModel: rootTableModel,
            InsertSql: "INSERT INTO edfi.\"Address\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Builds a CollectionWriteCandidate at the given array position with the given identity value.
    /// All values default to Literal(null) except index 3 (the identity field).
    /// </summary>
    public static CollectionWriteCandidate BuildCandidate(
        TableWritePlan collectionPlan,
        string identityValue,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[collectionPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        // Stamp the identity field value at index 3
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        return new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue]
        );
    }

    /// <summary>
    /// Builds the FlattenedWriteSet with a root row (single DocumentId literal) and the
    /// supplied collection candidates.
    /// </summary>
    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootPlan,
        ImmutableArray<CollectionWriteCandidate> candidates,
        long documentId = 345L
    )
    {
        var rootValues = new FlattenedWriteValue[] { new FlattenedWriteValue.Literal(documentId) };
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, collectionCandidates: candidates)
        );
    }

    /// <summary>
    /// Builds a VisibleRequestCollectionItem for a given identity value at the given array position.
    /// </summary>
    public static VisibleRequestCollectionItem BuildRequestItem(
        string identityValue,
        bool creatable,
        int arrayIndex
    ) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                Identity(identityValue)
            ),
            creatable,
            $"$.addresses[{arrayIndex}]"
        );

    /// <summary>
    /// Builds a VisibleStoredCollectionRow for a given identity value.
    /// </summary>
    public static VisibleStoredCollectionRow BuildStoredRow(
        string identityValue,
        ImmutableArray<string>? hiddenMemberPaths = null
    ) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                Identity(identityValue)
            ),
            hiddenMemberPaths ?? ImmutableArray<string>.Empty
        );

    /// <summary>
    /// Builds a ProfileAppliedWriteRequest with request-scope states and the supplied
    /// collection items.
    /// </summary>
    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> collectionItems,
        bool rootResourceCreatable = true
    ) =>
        new(
            writableBody,
            rootResourceCreatable,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    rootResourceCreatable
                ),
            ],
            collectionItems
        );

    /// <summary>
    /// Builds a ProfileAppliedWriteContext with the supplied visible stored collection rows.
    /// No StoredScopeStates are included for root-scope — the minimal root table used in
    /// these fixtures has only a DocumentId binding (StorageManaged), which would fail the
    /// classifier's stored-scope drift check if a root stored scope state were present.
    /// The synthesizer's root-table classification only validates stored scopes that have
    /// ordinary (non-StorageManaged) bindings, so omitting the root stored scope is safe for
    /// these collection-only fixtures.
    /// </summary>
    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows
    ) => new(request, new JsonObject(), ImmutableArray<StoredScopeState>.Empty, storedRows);

    /// <summary>
    /// Builds a RelationalWriteCurrentState with a root row and optional collection rows.
    /// Collection rows are: [0]=CollectionItemId, [1]=ParentDocumentId, [2]=Ordinal, [3]=IdentityField0.
    /// </summary>
    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan collectionPlan,
        long documentId,
        IReadOnlyList<object?[]>? collectionRows = null
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [documentId],
                    ]
                ),
                new HydratedTableRows(collectionPlan.TableModel, collectionRows ?? []),
            ],
            []
        );
}

// ------------------------------------------------------------------------
// Slice 4 top-level collection synthesizer fixtures (DMS-1124 Task 2.7).
//
// Two fixtures from the spec are intentionally deferred:
//
// 1. KU-in-collection disagreement propagates as RelationalWriteRequestValidationException.
//    The current test harness has no helper for a key-unification-enabled collection
//    write plan. This fixture is deferred until the overlay tests or the Checkpoint 3
//    integration tests land that capability. The underlying key-unification path is
//    exercised by ProfileKeyUnificationCoreTests + ProfileCollectionMatchedRowOverlayTests.
//
// 2. CP3 Task 21 retires the constructor and emission-site fences for
//    AttachedAlignedScopeData and root-extension child CollectionCandidates. The remaining
//    constructor validation only rejects structurally invalid table kinds.
// ------------------------------------------------------------------------

/// <summary>
/// Fixture 5: create-new path (null context + currentState), all-insert scenario.
/// Two visible request items, both Creatable=true. Expects two merged rows with ordinals 1, 2.
/// </summary>
[TestFixture]
public class Given_Synthesize_top_level_collection_create_new_with_all_inserts
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["identityField0"] = "V1" },
                new JsonObject { ["identityField0"] = "V2" }
            ),
        };

        var candidate0 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V1", 0);
        var candidate1 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V2", 1);

        var requestItems = ImmutableArray.Create(
            CollectionSynthesizerBuilders.BuildRequestItem("V1", creatable: true, arrayIndex: 0),
            CollectionSynthesizerBuilders.BuildRequestItem("V2", creatable: true, arrayIndex: 1)
        );

        var request = CollectionSynthesizerBuilders.BuildRequest(body, requestItems);
        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [candidate0, candidate1]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: null,
                    profileRequest: request,
                    profileAppliedContext: null,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_two_table_states() =>
        _outcome.MergeResult!.TablesInDependencyOrder.Length.Should().Be(2);

    [Test]
    public void It_emits_two_merged_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(2);

    [Test]
    public void It_stamps_ordinal_1_on_first_row()
    {
        var ordinal = (FlattenedWriteValue.Literal)
            _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[2];
        ordinal.Value.Should().Be(1);
    }

    [Test]
    public void It_stamps_ordinal_2_on_second_row()
    {
        var ordinal = (FlattenedWriteValue.Literal)
            _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[1].Values[2];
        ordinal.Value.Should().Be(2);
    }

    [Test]
    public void It_carries_identity_value_on_first_row()
    {
        var identity = (FlattenedWriteValue.Literal)
            _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[3];
        identity.Value.Should().Be("V1");
    }

    [Test]
    public void It_carries_identity_value_on_second_row()
    {
        var identity = (FlattenedWriteValue.Literal)
            _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[1].Values[3];
        identity.Value.Should().Be("V2");
    }

    [Test]
    public void It_has_no_current_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Should().BeEmpty();
}

/// <summary>
/// Fixture 2: creatability rejection propagates. One unmatched non-creatable insert.
/// Expects ProfileMergeOutcome.Reject with ProfileCreatabilityRejection naming the collection scope.
/// </summary>
[TestFixture]
public class Given_Synthesize_top_level_collection_CreatabilityRejection_propagates
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(new JsonObject { ["identityField0"] = "NEW1" }),
        };

        // One new candidate; not creatable → planner must reject.
        var candidate = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "NEW1", 0);
        var requestItems = ImmutableArray.Create(
            CollectionSynthesizerBuilders.BuildRequestItem("NEW1", creatable: false, arrayIndex: 0)
        );

        var request = CollectionSynthesizerBuilders.BuildRequest(body, requestItems);
        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(rootPlan, [candidate]);

        // Existing document: stored rows empty for the collection (no match found → insert → rejected).
        var context = CollectionSynthesizerBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );
        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows: []
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_a_rejection() => _outcome.IsRejection.Should().BeTrue();

    [Test]
    public void It_has_no_merge_result() => _outcome.MergeResult.Should().BeNull();

    [Test]
    public void It_identifies_the_collection_scope()
    {
        _outcome
            .CreatabilityRejection!.ScopeJsonScope.Should()
            .Be(CollectionSynthesizerBuilders.CollectionScope);
    }
}

/// <summary>
/// Fixture 1: existing document happy path with matched + hidden + insert rows.
/// Stored state: [V1, H, V2]. Request: [V1', V2', NEW1] all creatable.
/// Expects Success with merged rows in Section-D order [V1_upd, H, V2_upd, NEW1], ordinals 1..4.
/// </summary>
[TestFixture]
public class Given_Synthesize_top_level_collection_with_matched_and_hidden_and_insert
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        const long documentId = 345L;

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["identityField0"] = "V1" },
                new JsonObject { ["identityField0"] = "V2" },
                new JsonObject { ["identityField0"] = "NEW1" }
            ),
        };

        // Candidates: V1 at [0], V2 at [1], NEW1 at [2].
        var candidateV1 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V1", 0);
        var candidateV2 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V2", 1);
        var candidateNew1 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "NEW1", 2);

        // Request items: V1, V2, NEW1 all creatable.
        var requestItems = ImmutableArray.Create(
            CollectionSynthesizerBuilders.BuildRequestItem("V1", creatable: true, arrayIndex: 0),
            CollectionSynthesizerBuilders.BuildRequestItem("V2", creatable: true, arrayIndex: 1),
            CollectionSynthesizerBuilders.BuildRequestItem("NEW1", creatable: true, arrayIndex: 2)
        );

        var request = CollectionSynthesizerBuilders.BuildRequest(body, requestItems);

        // Stored rows: V1 at ordinal 1 (visible), H at ordinal 2 (hidden), V2 at ordinal 3 (visible).
        // Visible stored rows: V1 and V2 (H is not in VisibleStoredCollectionRows).
        var storedRowV1 = CollectionSynthesizerBuilders.BuildStoredRow("V1");
        var storedRowV2 = CollectionSynthesizerBuilders.BuildStoredRow("V2");
        var context = CollectionSynthesizerBuilders.BuildContext(request, [storedRowV1, storedRowV2]);

        // Current DB rows: CollectionItemId, ParentDocumentId, Ordinal, IdentityField0.
        object?[] dbRowV1 = [10L, documentId, 1, "V1"];
        object?[] dbRowH = [20L, documentId, 2, "H"];
        object?[] dbRowV2 = [30L, documentId, 3, "V2"];

        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId,
            [dbRowV1, dbRowH, dbRowV2]
        );

        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [candidateV1, candidateV2, candidateNew1],
            documentId
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_four_merged_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(4);

    [Test]
    public void It_stamps_ordinals_1_through_4()
    {
        var mergedRows = _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows;
        for (var i = 0; i < 4; i++)
        {
            var ordinal = (FlattenedWriteValue.Literal)mergedRows[i].Values[2];
            ordinal.Value.Should().Be(i + 1, $"row {i} should have ordinal {i + 1}");
        }
    }

    [Test]
    public void It_preserves_hidden_row_identity_field()
    {
        // The hidden row H is at position 2 (ordinal 2, 0-based index 1 in the sequence).
        // Planner Section-D interleaves hidden rows in their original ordinal position.
        var mergedRows = _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows;
        // Find the row with identity "H"
        var hiddenRow = mergedRows.FirstOrDefault(r =>
            r.Values[3] is FlattenedWriteValue.Literal lit && lit.Value?.Equals("H") == true
        );
        hiddenRow.Should().NotBeNull("the hidden row H must be preserved");
    }

    [Test]
    public void It_has_three_current_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(3);
}

/// <summary>
/// Fixture 6: multi-scope short-circuit — two top-level scopes where the first scope's
/// non-creatable unmatched insert causes rejection; the second scope is never synthesized.
/// Since the synthesizer groups by scope (each scope iterates a single table), we simulate
/// by having one collection scope with a non-creatable insert.  The rejection short-circuits
/// the synthesizer before it emits any table state.
/// </summary>
[TestFixture]
public class Given_Synthesize_top_level_collection_multi_scope_first_rejection_short_circuits_rest
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(new JsonObject { ["identityField0"] = "NEWBAD" }),
        };

        // One candidate, not creatable → planner rejects.
        var candidate = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "NEWBAD", 0);
        var requestItems = ImmutableArray.Create(
            CollectionSynthesizerBuilders.BuildRequestItem("NEWBAD", creatable: false, arrayIndex: 0)
        );

        var request = CollectionSynthesizerBuilders.BuildRequest(body, requestItems);
        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(rootPlan, [candidate]);
        var context = CollectionSynthesizerBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );
        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows: []
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_a_rejection() => _outcome.IsRejection.Should().BeTrue();

    [Test]
    public void It_names_the_collection_scope_in_the_rejection()
    {
        _outcome
            .CreatabilityRejection!.ScopeJsonScope.Should()
            .Be(CollectionSynthesizerBuilders.CollectionScope);
    }

    [Test]
    public void It_has_no_merge_result() => _outcome.MergeResult.Should().BeNull();
}

/// <summary>
/// Blocker #1 regression pin: omitted-visible rows must appear in CurrentRows.
/// Stored state: [V1, V2] both visible. Request provides only V1 (V2 is omitted).
/// Planner: V1 → MatchedUpdate (in Sequence), V2 → omitted (not in Sequence).
/// Fix: CurrentRows must contain both V1 and V2 so the persister can delete V2
/// by absence (V2 in CurrentRows, not in MergedRows → delete).
/// </summary>
[TestFixture]
public class Given_Synthesize_top_level_collection_omitted_visible_row_appears_in_CurrentRows
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        const long documentId = 345L;

        // Request body has only V1 (V2 is omitted — not sent by the caller).
        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(new JsonObject { ["identityField0"] = "V1" }),
        };

        // One candidate: V1 at request order 0.
        var candidateV1 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V1", 0);

        // One visible request item: V1 (creatable so no rejection).
        var requestItems = ImmutableArray.Create(
            CollectionSynthesizerBuilders.BuildRequestItem("V1", creatable: true, arrayIndex: 0)
        );

        var request = CollectionSynthesizerBuilders.BuildRequest(body, requestItems);

        // Stored visible rows: V1 and V2 (both visible in the stored profile context).
        var storedRowV1 = CollectionSynthesizerBuilders.BuildStoredRow("V1");
        var storedRowV2 = CollectionSynthesizerBuilders.BuildStoredRow("V2");
        var context = CollectionSynthesizerBuilders.BuildContext(request, [storedRowV1, storedRowV2]);

        // Current DB rows: V1 at ordinal 1, V2 at ordinal 2.
        // [0]=CollectionItemId, [1]=ParentDocumentId, [2]=Ordinal, [3]=IdentityField0
        object?[] dbRowV1 = [10L, documentId, 1, "V1"];
        object?[] dbRowV2 = [20L, documentId, 2, "V2"];

        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId,
            [dbRowV1, dbRowV2]
        );

        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [candidateV1],
            documentId
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_one_merged_collection_row_V1_only() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_carries_V1_identity_in_merged_row()
    {
        var identity = (FlattenedWriteValue.Literal)
            _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[3];
        identity.Value.Should().Be("V1");
    }

    [Test]
    public void It_has_two_current_rows_so_persister_can_delete_V2_by_absence() =>
        // BLOCKER #1 KEY ASSERTION: both V1 and V2 must be in CurrentRows even though
        // V2 is absent from MergedRows. The persister detects V2's delete by set-difference.
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(2);
}

/// <summary>
/// Blocker #2 regression pin: stored-only delete-all-visible scope must be driven even
/// when the request-side has NO collection candidates for this scope.
/// Stored state: [V1, H, V2] where V1/V2 are visible and H is hidden. Request has NO
/// collection candidates for this scope (CollectionCandidates is empty). The synthesizer
/// must still enter the scope because stored/current rows exist, preserve H, and surface
/// all three DB rows in CurrentRows so the persister can delete V1 and V2.
/// </summary>
[TestFixture]
public class Given_Synthesize_top_level_collection_delete_all_visible_while_hidden_remains
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        const long documentId = 345L;

        // Request body has the collection array absent / empty (no visible items submitted).
        var body = new JsonObject { ["addresses"] = new JsonArray() };

        // NO collection candidates in the flattened write set — the request is empty for
        // this scope (triggering the Blocker #2 scenario).
        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(
            rootPlan,
            ImmutableArray<CollectionWriteCandidate>.Empty,
            documentId
        );

        // No visible request items for this scope.
        var request = CollectionSynthesizerBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        // Stored visible rows: V1 and V2 (H is hidden — not in VisibleStoredCollectionRows).
        var storedRowV1 = CollectionSynthesizerBuilders.BuildStoredRow("V1");
        var storedRowV2 = CollectionSynthesizerBuilders.BuildStoredRow("V2");
        var context = CollectionSynthesizerBuilders.BuildContext(request, [storedRowV1, storedRowV2]);

        // Current DB rows: V1 at ordinal 1, H at ordinal 2 (hidden), V2 at ordinal 3.
        // [0]=CollectionItemId, [1]=ParentDocumentId, [2]=Ordinal, [3]=IdentityField0
        object?[] dbRowV1 = [10L, documentId, 1, "V1"];
        object?[] dbRowH = [20L, documentId, 2, "H"];
        object?[] dbRowV2 = [30L, documentId, 3, "V2"];

        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId,
            [dbRowV1, dbRowH, dbRowV2]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_only_the_hidden_H_row_in_merged_rows() =>
        // BLOCKER #2 KEY ASSERTION: only H (the hidden row) must appear in MergedRows.
        // V1 and V2 are absent (deleted by absence), even though the request had no
        // collection candidates at all for this scope.
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_carries_H_identity_in_the_single_merged_row()
    {
        var identity = (FlattenedWriteValue.Literal)
            _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows[0].Values[3];
        identity.Value.Should().Be("H");
    }

    [Test]
    public void It_has_three_current_rows_so_persister_can_delete_V1_and_V2_by_absence() =>
        // All three DB rows (V1, H, V2) must appear in CurrentRows so the persister can
        // compute the set-difference: CurrentRows − MergedRows = {V1, V2} → delete both.
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(3);
}

/// <summary>
/// Shared helpers for document-reference-backed semantic identity canonicalization fixtures.
/// </summary>
internal static class DocumentReferenceCanonicalizeBuilders
{
    public const string CollectionScope = "$.addresses[*]";
    public const string ReferenceObjectPath = "$.addresses[*].schoolReference";
    public const string ReferenceConcretePath = "$.addresses[0].schoolReference";
    public const string SchoolIdPath = "$.addresses[*].schoolReference.schoolId";
    public const string EducationOrganizationIdPath =
        "$.addresses[*].schoolReference.educationOrganizationId";
    public const string SchoolIdRelativePath = "$.schoolReference.schoolId";
    public const string EducationOrganizationIdRelativePath = "$.schoolReference.educationOrganizationId";
    public const string SchoolId = "255901";
    public const string EducationOrganizationId = "123";
    public const long SchoolDocumentId = 501L;

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static (ResourceWritePlan Plan, TableWritePlan CollectionPlan) BuildPlan()
    {
        var collectionPlan = BuildReferenceCollectionPlan();
        var rootPlan = BuildRootPlan();

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path(ReferenceObjectPath),
            Table: collectionPlan.TableModel.Table,
            FkColumn: new DbColumnName("SchoolReference_DocumentId"),
            TargetResource: SchoolResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    Path("$.schoolId"),
                    Path(SchoolIdPath),
                    new DbColumnName("SchoolReference_SchoolId")
                ),
                new ReferenceIdentityBinding(
                    Path("$.educationOrganizationId"),
                    Path(EducationOrganizationIdPath),
                    new DbColumnName("SchoolReference_EducationOrganizationId")
                ),
            ]
        );

        var plan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Student"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
                DocumentReferenceBindings: [binding],
                DescriptorEdgeSources: []
            ),
            [rootPlan, collectionPlan]
        );
        return (plan, collectionPlan);
    }

    public static CollectionWriteCandidate BuildCandidate(TableWritePlan collectionPlan)
    {
        var values = new FlattenedWriteValue[collectionPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }

        values[3] = new FlattenedWriteValue.Literal(SchoolDocumentId);
        values[4] = new FlattenedWriteValue.Literal(SchoolId);
        values[5] = new FlattenedWriteValue.Literal(EducationOrganizationId);

        return new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values: values,
            semanticIdentityValues: [SchoolDocumentId, SchoolDocumentId]
        );
    }

    public static VisibleRequestCollectionItem BuildRequestItem() =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                NaturalKeyIdentity()
            ),
            Creatable: true,
            RequestJsonPath: "$.addresses[0]"
        );

    public static VisibleStoredCollectionRow BuildStoredRow() =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                NaturalKeyIdentity()
            ),
            ImmutableArray<string>.Empty
        );

    public static ResolvedReferenceSet BuildResolvedReferenceSet() =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath(ReferenceConcretePath)] = new ResolvedDocumentReference(
                    Reference: new DocumentReference(
                        ResourceInfo: new BaseResourceInfo(
                            new ProjectName("Ed-Fi"),
                            new ResourceName("School"),
                            false
                        ),
                        DocumentIdentity: new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.schoolId"), SchoolId),
                            new DocumentIdentityElement(
                                new JsonPath("$.educationOrganizationId"),
                                EducationOrganizationId
                            ),
                        ]),
                        ReferentialId: new ReferentialId(Guid.NewGuid()),
                        Path: new JsonPath(ReferenceConcretePath)
                    ),
                    DocumentId: SchoolDocumentId,
                    ResourceKeyId: 11
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan collectionPlan,
        long documentId
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [documentId],
                    ]
                ),
                new HydratedTableRows(
                    collectionPlan.TableModel,
                    [
                        [1L, documentId, 1, SchoolDocumentId, SchoolId, EducationOrganizationId],
                    ]
                ),
            ],
            []
        );

    private static ImmutableArray<SemanticIdentityPart> NaturalKeyIdentity() =>
        [
            new SemanticIdentityPart(SchoolIdRelativePath, JsonValue.Create(SchoolId), IsPresent: true),
            new SemanticIdentityPart(
                EducationOrganizationIdRelativePath,
                JsonValue.Create(EducationOrganizationId),
                IsPresent: true
            ),
        ];

    private static TableWritePlan BuildRootPlan()
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"Student\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan BuildReferenceCollectionPlan()
    {
        var collectionKeyColumn = Column("CollectionItemId", ColumnKind.CollectionKey);
        var parentKeyColumn = Column("ParentDocumentId", ColumnKind.ParentKeyPart);
        var ordinalColumn = Column("Ordinal", ColumnKind.Ordinal);
        var fkColumn = Column(
            "SchoolReference_DocumentId",
            ColumnKind.DocumentFk,
            new RelationalScalarType(ScalarKind.Int64)
        );
        var schoolIdColumn = Column(
            "SchoolReference_SchoolId",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            Path(SchoolIdPath)
        );
        var educationOrganizationIdColumn = Column(
            "SchoolReference_EducationOrganizationId",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            Path(EducationOrganizationIdPath)
        );

        var columns = new[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            fkColumn,
            schoolIdColumn,
            educationOrganizationIdColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: Path(CollectionScope),
            Key: new TableKey(
                "PK_StudentAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(Path(SchoolIdRelativePath), fkColumn.ColumnName),
                    new CollectionSemanticIdentityBinding(
                        Path(EducationOrganizationIdRelativePath),
                        fkColumn.ColumnName
                    ),
                ]
            ),
        };

        var schoolIdReferenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: Path(ReferenceObjectPath),
            IdentityJsonPath: Path("$.schoolId"),
            ReferenceJsonPath: Path(SchoolIdPath)
        );
        var educationOrganizationReferenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: Path(ReferenceObjectPath),
            IdentityJsonPath: Path("$.educationOrganizationId"),
            ReferenceJsonPath: Path(EducationOrganizationIdPath)
        );

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"StudentAddress\" VALUES (@CollectionItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    fkColumn,
                    new WriteValueSource.DocumentReference(0),
                    "SchoolReference_DocumentId"
                ),
                new WriteColumnBinding(
                    schoolIdColumn,
                    new WriteValueSource.ReferenceDerived(schoolIdReferenceSource),
                    "SchoolReference_SchoolId"
                ),
                new WriteColumnBinding(
                    educationOrganizationIdColumn,
                    new WriteValueSource.ReferenceDerived(educationOrganizationReferenceSource),
                    "SchoolReference_EducationOrganizationId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(Path(SchoolIdRelativePath), 3),
                    new CollectionMergeSemanticIdentityBinding(Path(EducationOrganizationIdRelativePath), 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"StudentAddress\" SET X=@X WHERE \"CollectionItemId\"=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"StudentAddress\" WHERE \"CollectionItemId\"=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 5, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static DbColumnModel Column(
        string name,
        ColumnKind kind,
        RelationalScalarType? scalarType = null,
        JsonPathExpression? sourceJsonPath = null
    ) =>
        new(
            ColumnName: new DbColumnName(name),
            Kind: kind,
            ScalarType: scalarType,
            IsNullable: false,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}

/// <summary>
/// Regression: reference-backed top-level collection identities must keep the original FK-based
/// model. Core emits reference natural-key parts; the backend candidate and current rows use the
/// resolved referenced document id for each semantic identity part.
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_document_reference_backed_semantic_identity_matches_when_core_emits_reference_identity_parts
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = DocumentReferenceCanonicalizeBuilders.BuildPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        const long documentId = 345L;

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject
                {
                    ["schoolReference"] = new JsonObject
                    {
                        ["schoolId"] = DocumentReferenceCanonicalizeBuilders.SchoolId,
                        ["educationOrganizationId"] =
                            DocumentReferenceCanonicalizeBuilders.EducationOrganizationId,
                    },
                }
            ),
        };

        var requestItem = DocumentReferenceCanonicalizeBuilders.BuildRequestItem();
        var storedRow = DocumentReferenceCanonicalizeBuilders.BuildStoredRow();
        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItem]
        );
        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRow]
        );
        var currentState = DocumentReferenceCanonicalizeBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId
        );
        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(documentId)],
                collectionCandidates: [DocumentReferenceCanonicalizeBuilders.BuildCandidate(collectionPlan)]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: DocumentReferenceCanonicalizeBuilders.BuildResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_produces_one_merged_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_tracks_one_current_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);
}

/// <summary>
/// Shared helpers for descriptor-identity canonicalization fixtures.
/// <para>
/// These fixtures verify that URI strings emitted by Core for descriptor-backed semantic
/// identity parts are canonicalized to Int64 ids before the planner runs. The flattener
/// (backend-side) already carries Int64s in CollectionWriteCandidate.SemanticIdentityValues;
/// Core emits URI strings in VisibleRequestCollectionItem and VisibleStoredCollectionRow.
/// The merge synthesizer must reconcile these two representations so the planner sees
/// matching keys on both sides.
/// </para>
/// Builds a collection plan with one DescriptorFk identity column at
/// "$.addresses[*].addressTypeDescriptor".
/// </summary>
internal static class DescriptorCanonicalizeBuilders
{
    public const string CollectionScope = "$.addresses[*]";
    public const string DescriptorPath = "$.addresses[*].addressTypeDescriptor";
    public const string DescriptorRelativePath = "$.addressTypeDescriptor";

    public static readonly QualifiedResourceName AddressTypeDescriptorResource = new(
        "Ed-Fi",
        "AddressTypeDescriptor"
    );

    public const string AddressTypeUri = "uri://ed-fi.org/AddressTypeDescriptor#Physical";
    public const long AddressTypeId = 42L;

    /// <summary>
    /// Builds a two-table ResourceWritePlan: [0] root, [1] collection table with
    /// one DescriptorFk identity column at <see cref="DescriptorPath"/>.
    /// The resource model includes a DescriptorEdgeSource so
    /// FlatteningResolvedReferenceLookupSet.Create can validate the descriptor path.
    /// </summary>
    public static (ResourceWritePlan Plan, TableWritePlan CollectionPlan) BuildPlan()
    {
        var collectionPlan = BuildDescriptorCollectionPlan();
        var rootPlan = BuildRootPlan();

        var plan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "Student"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: Path(DescriptorPath),
                        Table: collectionPlan.TableModel.Table,
                        FkColumn: new DbColumnName("AddressTypeDescriptor_Id"),
                        DescriptorResource: AddressTypeDescriptorResource
                    ),
                ]
            ),
            [rootPlan, collectionPlan]
        );
        return (plan, collectionPlan);
    }

    private static TableWritePlan BuildRootPlan()
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"Student\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan BuildDescriptorCollectionPlan()
    {
        // Layout:
        //   [0] CollectionItemId  (CollectionKey / Precomputed) — StableRowIdentityBindingIndex = 0
        //   [1] ParentDocumentId  (ParentKeyPart / DocumentId)
        //   [2] Ordinal           (Ordinal)
        //   [3] AddressTypeDescriptor_Id  (DescriptorFk) — SemanticIdentityBinding 0

        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var descriptorColumn = new DbColumnModel(
            ColumnName: new DbColumnName("AddressTypeDescriptor_Id"),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: Path(DescriptorPath),
            TargetResource: AddressTypeDescriptorResource
        );

        var allColumns = new DbColumnModel[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            descriptorColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddresses"),
            JsonScope: Path(CollectionScope),
            Key: new TableKey(
                "PK_StudentAddresses",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        Path(DescriptorRelativePath),
                        descriptorColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"StudentAddresses\" VALUES (@CollectionItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, allColumns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    descriptorColumn,
                    new WriteValueSource.DescriptorReference(
                        AddressTypeDescriptorResource,
                        Path(DescriptorPath),
                        DescriptorValuePath: null
                    ),
                    "AddressTypeDescriptor_Id"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(Path(DescriptorRelativePath), 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"StudentAddresses\" SET X=@X WHERE \"CollectionItemId\"=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"StudentAddresses\" WHERE \"CollectionItemId\"=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    /// <summary>
    /// Builds a CollectionWriteCandidate whose semantic identity carries the Int64 descriptor id
    /// (as the backend flattener would emit after resolving the URI).
    /// </summary>
    public static CollectionWriteCandidate BuildCandidate(TableWritePlan collectionPlan, long descriptorId)
    {
        var values = new FlattenedWriteValue[collectionPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }

        // Stamp the descriptor id at index 3 (identity field).
        values[3] = new FlattenedWriteValue.Literal(descriptorId);

        return new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values: values,
            semanticIdentityValues: [descriptorId]
        );
    }

    /// <summary>
    /// Builds a VisibleRequestCollectionItem whose semantic identity carries the URI string
    /// (as Core emits it).
    /// </summary>
    public static VisibleRequestCollectionItem BuildRequestItemWithUri(string uri, bool creatable) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                [new SemanticIdentityPart(DescriptorRelativePath, JsonValue.Create(uri), IsPresent: true)]
            ),
            creatable,
            "$.addresses[0]"
        );

    /// <summary>
    /// Builds a VisibleStoredCollectionRow whose semantic identity carries the URI string
    /// (as Core emits it from the stored document).
    /// </summary>
    public static VisibleStoredCollectionRow BuildStoredRowWithUri(string uri) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                [new SemanticIdentityPart(DescriptorRelativePath, JsonValue.Create(uri), IsPresent: true)]
            ),
            ImmutableArray<string>.Empty
        );

    /// <summary>
    /// Builds a ResolvedReferenceSet with a single descriptor reference entry resolving the
    /// given URI to the given id. The concrete path is "$.addresses[0].addressTypeDescriptor".
    /// </summary>
    public static ResolvedReferenceSet BuildResolvedReferenceSetWithDescriptor(
        string uri,
        long descriptorId
    ) =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.addresses[0].addressTypeDescriptor")] = new ResolvedDescriptorReference(
                    Reference: new DescriptorReference(
                        ResourceInfo: new BaseResourceInfo(
                            new ProjectName("Ed-Fi"),
                            new ResourceName("AddressTypeDescriptor"),
                            true
                        ),
                        DocumentIdentity: new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.descriptor"), uri),
                        ]),
                        ReferentialId: new ReferentialId(Guid.NewGuid()),
                        Path: new JsonPath("$.addresses[0].addressTypeDescriptor")
                    ),
                    DocumentId: descriptorId,
                    ResourceKeyId: 1
                ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

    /// <summary>
    /// Builds a RelationalWriteCurrentState for the two-table plan with a root row and
    /// optional collection rows (layout: [CollectionItemId, ParentDocumentId, Ordinal, DescriptorId]).
    /// </summary>
    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan collectionPlan,
        long documentId,
        IReadOnlyList<object?[]>? collectionRows = null
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [documentId],
                    ]
                ),
                new HydratedTableRows(collectionPlan.TableModel, collectionRows ?? []),
            ],
            []
        );

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}

/// <summary>
/// Fixture: descriptor-backed identity in request + stored both carry the URI; the cache
/// resolves it to Int64. Planner must produce a MatchedUpdateEntry.
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_descriptor_backed_semantic_identity_matches_when_uri_in_cache
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = DescriptorCanonicalizeBuilders.BuildPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["addressTypeDescriptor"] = DescriptorCanonicalizeBuilders.AddressTypeUri }
            ),
        };

        // Backend candidate carries Int64 (as flattener produces after resolving).
        var candidate = DescriptorCanonicalizeBuilders.BuildCandidate(
            collectionPlan,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );

        // Core emits the URI string in the request item.
        var requestItem = DescriptorCanonicalizeBuilders.BuildRequestItemWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            creatable: true
        );

        // Core emits the URI string in the stored row.
        var storedRow = DescriptorCanonicalizeBuilders.BuildStoredRowWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri
        );

        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItem]
        );
        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRow]
        );

        // Current DB state: one stored row with Int64 descriptor id.
        var currentState = DescriptorCanonicalizeBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows:
            [
                [1L, 345L, 1, DescriptorCanonicalizeBuilders.AddressTypeId],
            ]
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidate]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    [Test]
    public void It_produces_one_merged_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_has_one_current_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);
}

/// <summary>
/// Regression: in production, <c>DescriptorExtractor.CreateDescriptorReference</c> lowercases
/// descriptor URIs before publishing them into <see cref="ResolvedReferenceSet"/>, but Core's
/// <c>AddressDerivationEngine</c> reads the raw JSON value (typically mixed-case) into
/// <see cref="VisibleStoredCollectionRow"/> identity parts. The URI-keyed cache lookup must
/// normalize the input on both insert and probe so the documented "URI in cache" path works
/// reliably against typical mixed-case Ed-Fi descriptor URIs (e.g.
/// <c>uri://ed-fi.org/AddressTypeDescriptor#Physical</c>).
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_descriptor_backed_semantic_identity_matches_when_uri_case_differs_between_cache_and_stored_row
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = DescriptorCanonicalizeBuilders.BuildPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["addressTypeDescriptor"] = DescriptorCanonicalizeBuilders.AddressTypeUri }
            ),
        };

        var candidate = DescriptorCanonicalizeBuilders.BuildCandidate(
            collectionPlan,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );

        // Mixed-case URI on the request item (raw JSON input).
        var requestItem = DescriptorCanonicalizeBuilders.BuildRequestItemWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            creatable: true
        );

        // Mixed-case URI on the stored row (raw JSON from stored doc).
        var storedRow = DescriptorCanonicalizeBuilders.BuildStoredRowWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri
        );

        // Cache populated with the LOWERCASED URI as production's DescriptorExtractor produces.
        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            DescriptorCanonicalizeBuilders.AddressTypeUri.ToLowerInvariant(),
            DescriptorCanonicalizeBuilders.AddressTypeId
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItem]
        );
        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRow]
        );

        // Two current DB rows; only one is visible to this profile (descriptor-only identity
        // with a hidden row interleaved). This makes the URI-cache hit load-bearing: the
        // positional / scalar-parts fallback would refuse with "row counts differ" because the
        // current and stored row counts diverge, so the test will fail without the case-
        // insensitive lookup.
        var currentState = DescriptorCanonicalizeBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows:
            [
                [1L, 345L, 1, DescriptorCanonicalizeBuilders.AddressTypeId],
                [
                    2L,
                    345L,
                    2,
                    99L, /* hidden row's descriptor id */
                ],
            ]
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidate]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_produces_two_merged_rows_one_visible_update_and_one_hidden_preserve() =>
        // Without case-insensitive URI lookup the cache miss falls through to the count-divergence
        // throw at CanonicalizeStoredIdentityParts, so this length assertion is the regression gate.
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(2);
}

/// <summary>
/// Fixture: stored row carries a descriptor URI that is NOT present in the request-cycle
/// cache AND there are no current rows to fall back to. This is a truly pathological case —
/// Core claims a visible stored row exists, but the backend has no corresponding current rows
/// for the scope (e.g., data inconsistency between Core and backend). The synthesizer must
/// throw with the specific diagnostic phrase "descriptor URI not resolvable at merge boundary".
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_descriptor_backed_identity_throws_when_stored_uri_not_in_cache_and_no_current_rows
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = DescriptorCanonicalizeBuilders.BuildPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["addressTypeDescriptor"] = DescriptorCanonicalizeBuilders.AddressTypeUri }
            ),
        };

        var candidate = DescriptorCanonicalizeBuilders.BuildCandidate(
            collectionPlan,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );
        var requestItem = DescriptorCanonicalizeBuilders.BuildRequestItemWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            creatable: true
        );

        // Stored row references a URI that is NOT in the cache.
        const string unknownUri = "uri://ed-fi.org/AddressTypeDescriptor#UNKNOWN";
        var storedRow = DescriptorCanonicalizeBuilders.BuildStoredRowWithUri(unknownUri);

        // Cache only contains the known URI — not the unknown one.
        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItem]
        );
        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRow]
        );

        // Pathological case: NO current rows for the collection scope.
        // Core says there is a visible stored row, but the backend current state has none —
        // an inconsistency that makes the positional/scalar-part fallback unable to help.
        var currentState = DescriptorCanonicalizeBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows: [] // Empty — no current rows available for fallback.
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidate]
            )
        );

        var synthesizer = BuildProfileSynthesizer();

        _synthesizeAction = () =>
            synthesizer.Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_throws_with_diagnostic_phrase() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*descriptor URI not resolvable at merge boundary*");
}

/// <summary>
/// Fixture: delete-by-absence with descriptor-backed identity. The stored document has two
/// addresses (URI A and URI B); the PUT request includes only address A. URI B is absent from
/// the request body and therefore absent from the request-cycle descriptor-resolution cache.
///
/// <para>The canonicalization must NOT throw. Instead it falls back to the matching
/// <see cref="CurrentCollectionRowSnapshot"/> via positional correspondence (both arrays are
/// ordered by stored ordinal and the identity is descriptor-only so no hidden rows
/// interleave).</para>
///
/// <para>The planner emits a <c>MatchedUpdateEntry</c> for address A and omits the visible
/// slot for address B (delete-by-absence). The resulting merged state must contain one merged
/// row and two current rows (so the persister can calculate the deletion via set-difference).</para>
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_descriptor_backed_identity_delete_by_absence_matches_without_throwing
{
    private ProfileMergeOutcome _outcome;

    // Two descriptor URIs/ids used in this fixture.
    private const string AddressTypeUriA = DescriptorCanonicalizeBuilders.AddressTypeUri; // "Physical"
    private const long AddressTypeIdA = DescriptorCanonicalizeBuilders.AddressTypeId; // 42L
    private const string AddressTypeUriB = "uri://ed-fi.org/AddressTypeDescriptor#Home";
    private const long AddressTypeIdB = 99L;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = DescriptorCanonicalizeBuilders.BuildPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body includes ONLY address A — address B is omitted (delete-by-absence).
        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(new JsonObject { ["addressTypeDescriptor"] = AddressTypeUriA }),
        };

        // Backend candidate for address A (the one being updated/kept).
        var candidateA = DescriptorCanonicalizeBuilders.BuildCandidate(collectionPlan, AddressTypeIdA);

        // Request item for address A (URI string from Core).
        var requestItemA = DescriptorCanonicalizeBuilders.BuildRequestItemWithUri(
            AddressTypeUriA,
            creatable: true
        );

        // Both stored rows are visible per profile.
        var storedRowA = DescriptorCanonicalizeBuilders.BuildStoredRowWithUri(AddressTypeUriA);
        var storedRowB = DescriptorCanonicalizeBuilders.BuildStoredRowWithUri(AddressTypeUriB);

        // Cache contains ONLY URI A — URI B is absent (it was not in the request body).
        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            AddressTypeUriA,
            AddressTypeIdA
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItemA]
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRowA, storedRowB] // Both stored rows visible.
        );

        // Current DB state: two rows — one for each address.
        // Layout: [CollectionItemId, ParentDocumentId, Ordinal, DescriptorId]
        // Row for address A is at ordinal 1; row for address B is at ordinal 2.
        var currentState = DescriptorCanonicalizeBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows:
            [
                [1L, 345L, 1, AddressTypeIdA],
                [2L, 345L, 2, AddressTypeIdB],
            ]
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidateA]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    /// <summary>
    /// Only address A is in the plan sequence (address B was omitted → delete-by-absence).
    /// </summary>
    [Test]
    public void It_produces_one_merged_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    /// <summary>
    /// Both current rows are tracked so the persister can delete address B by absence.
    /// </summary>
    [Test]
    public void It_has_two_current_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(2);
}

/// <summary>
/// Fixture: two-part identity with one scalar part and one descriptor part. The scalar part
/// must pass through unchanged; the descriptor URI must be canonicalized to Int64.
/// Planner must match request item against stored row after both canonicalizations apply.
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_mixed_scalar_and_descriptor_identity_matches_correctly
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        // Build a two-identity-column collection plan:
        //   [0] CollectionItemId  (CollectionKey / Precomputed)
        //   [1] ParentDocumentId  (ParentKeyPart / DocumentId)
        //   [2] Ordinal           (Ordinal)
        //   [3] CityName          (Scalar)            — identity part 0
        //   [4] AddressTypeDescriptor_Id (DescriptorFk) — identity part 1
        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var cityColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CityName"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression("$.addresses[*].city", []),
            TargetResource: null
        );
        var descriptorColumn = new DbColumnModel(
            ColumnName: new DbColumnName("AddressTypeDescriptor_Id"),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(DescriptorCanonicalizeBuilders.DescriptorPath, []),
            TargetResource: DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource
        );

        var allColumns = new DbColumnModel[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            cityColumn,
            descriptorColumn,
        };

        var collectionScope = DescriptorCanonicalizeBuilders.CollectionScope;
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "MixedAddresses"),
            JsonScope: new JsonPathExpression(collectionScope, []),
            Key: new TableKey(
                "PK_MixedAddresses",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.city", []),
                        cityColumn.ColumnName
                    ),
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(DescriptorCanonicalizeBuilders.DescriptorRelativePath, []),
                        descriptorColumn.ColumnName
                    ),
                ]
            ),
        };

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"MixedAddresses\" VALUES (@CollectionItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, allColumns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    cityColumn,
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.addresses[*].city", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "CityName"
                ),
                new WriteColumnBinding(
                    descriptorColumn,
                    new WriteValueSource.DescriptorReference(
                        DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource,
                        new JsonPathExpression(DescriptorCanonicalizeBuilders.DescriptorPath, []),
                        DescriptorValuePath: null
                    ),
                    "AddressTypeDescriptor_Id"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.city", []), 3),
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(DescriptorCanonicalizeBuilders.DescriptorRelativePath, []),
                        4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"MixedAddresses\" SET X=@X WHERE \"CollectionItemId\"=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"MixedAddresses\" WHERE \"CollectionItemId\"=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var rootDocId = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var rootTableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "MixedStudent"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_MixedStudent",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [rootDocId],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        var rootPlan = new TableWritePlan(
            TableModel: rootTableModel,
            InsertSql: "INSERT INTO edfi.\"MixedStudent\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(rootDocId, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );

        var plan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "MixedStudent"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTableModel,
                TablesInDependencyOrder: [rootTableModel, tableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: new JsonPathExpression(
                            DescriptorCanonicalizeBuilders.DescriptorPath,
                            []
                        ),
                        Table: tableModel.Table,
                        FkColumn: descriptorColumn.ColumnName,
                        DescriptorResource: DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource
                    ),
                ]
            ),
            [rootPlan, collectionPlan]
        );

        // Candidate carries: city = "Springfield", descriptorId = Int64 (Int64 from flattener).
        var candidateValues = new FlattenedWriteValue[]
        {
            new FlattenedWriteValue.Literal(null), // CollectionItemId
            new FlattenedWriteValue.Literal(null), // ParentDocumentId
            new FlattenedWriteValue.Literal(null), // Ordinal
            new FlattenedWriteValue.Literal("Springfield"), // CityName
            new FlattenedWriteValue.Literal(DescriptorCanonicalizeBuilders.AddressTypeId), // DescriptorId
        };
        var candidate = new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values: candidateValues,
            semanticIdentityValues: ["Springfield", DescriptorCanonicalizeBuilders.AddressTypeId]
        );

        // Request item carries: city = "Springfield" (scalar, unchanged), descriptorUri (string from Core).
        var requestItem = new VisibleRequestCollectionItem(
            new CollectionRowAddress(
                collectionScope,
                new ScopeInstanceAddress("$", []),
                [
                    new SemanticIdentityPart("$.city", JsonValue.Create("Springfield"), IsPresent: true),
                    new SemanticIdentityPart(
                        DescriptorCanonicalizeBuilders.DescriptorRelativePath,
                        JsonValue.Create(DescriptorCanonicalizeBuilders.AddressTypeUri),
                        IsPresent: true
                    ),
                ]
            ),
            Creatable: true,
            RequestJsonPath: "$.addresses[0]"
        );

        // Stored row carries: city = "Springfield" (scalar), descriptorUri (string from Core).
        var storedRow = new VisibleStoredCollectionRow(
            new CollectionRowAddress(
                collectionScope,
                new ScopeInstanceAddress("$", []),
                [
                    new SemanticIdentityPart("$.city", JsonValue.Create("Springfield"), IsPresent: true),
                    new SemanticIdentityPart(
                        DescriptorCanonicalizeBuilders.DescriptorRelativePath,
                        JsonValue.Create(DescriptorCanonicalizeBuilders.AddressTypeUri),
                        IsPresent: true
                    ),
                ]
            ),
            ImmutableArray<string>.Empty
        );

        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject
                {
                    ["city"] = "Springfield",
                    ["addressTypeDescriptor"] = DescriptorCanonicalizeBuilders.AddressTypeUri,
                }
            ),
        };

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItem]
        );
        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRow]
        );

        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootTableModel,
                    [
                        [345L],
                    ]
                ),
                new HydratedTableRows(
                    tableModel,
                    [
                        [1L, 345L, 1, "Springfield", DescriptorCanonicalizeBuilders.AddressTypeId],
                    ]
                ),
            ],
            []
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidate]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    [Test]
    public void It_produces_one_merged_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    [Test]
    public void It_has_one_current_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(1);
}

/// <summary>
/// Shared helpers for two-part mixed-identity (city scalar + addressTypeDescriptor FK)
/// collection fixtures used by the scalar-parts fallback and counts-differ tests.
/// </summary>
internal static class MixedIdentityBuilders
{
    public const string CollectionScope = "$.addresses[*]";
    public const string DescriptorPath = "$.addresses[*].addressTypeDescriptor";
    public const string DescriptorRelativePath = "$.addressTypeDescriptor";

    public static readonly QualifiedResourceName AddressTypeDescriptorResource =
        DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource;

    public const string PhysicalUri = DescriptorCanonicalizeBuilders.AddressTypeUri; // "uri://ed-fi.org/AddressTypeDescriptor#Physical"
    public const long PhysicalId = DescriptorCanonicalizeBuilders.AddressTypeId; // 42L
    public const string MailingUri = "uri://ed-fi.org/AddressTypeDescriptor#Mailing";
    public const long MailingId = 50L;

    /// <summary>
    /// Builds the five-column mixed-identity collection plan:
    ///   [0] CollectionItemId (CollectionKey)
    ///   [1] ParentDocumentId (ParentKeyPart)
    ///   [2] Ordinal           (Ordinal)
    ///   [3] CityName          (Scalar)        — semantic identity index 0
    ///   [4] AddressTypeDescriptor_Id (DescriptorFk) — semantic identity index 1
    /// </summary>
    public static (ResourceWritePlan Plan, TableWritePlan CollectionPlan, TableWritePlan RootPlan) BuildPlan()
    {
        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var cityColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CityName"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: Path("$.addresses[*].city"),
            TargetResource: null
        );
        var descriptorColumn = new DbColumnModel(
            ColumnName: new DbColumnName("AddressTypeDescriptor_Id"),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: Path(DescriptorPath),
            TargetResource: AddressTypeDescriptorResource
        );

        var allColumns = new DbColumnModel[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            cityColumn,
            descriptorColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "MixedAddresses"),
            JsonScope: Path(CollectionScope),
            Key: new TableKey(
                "PK_MixedAddresses",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(Path("$.city"), cityColumn.ColumnName),
                    new CollectionSemanticIdentityBinding(
                        Path(DescriptorRelativePath),
                        descriptorColumn.ColumnName
                    ),
                ]
            ),
        };

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"MixedAddresses\" VALUES (@CollectionItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, allColumns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    cityColumn,
                    new WriteValueSource.Scalar(
                        Path("$.addresses[*].city"),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "CityName"
                ),
                new WriteColumnBinding(
                    descriptorColumn,
                    new WriteValueSource.DescriptorReference(
                        AddressTypeDescriptorResource,
                        Path(DescriptorPath),
                        DescriptorValuePath: null
                    ),
                    "AddressTypeDescriptor_Id"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(Path("$.city"), 3),
                    new CollectionMergeSemanticIdentityBinding(Path(DescriptorRelativePath), 4),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"MixedAddresses\" SET X=@X WHERE \"CollectionItemId\"=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"MixedAddresses\" WHERE \"CollectionItemId\"=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var rootDocId = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var rootTableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "MixedStudent"),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_MixedStudent",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [rootDocId],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        var rootPlan = new TableWritePlan(
            TableModel: rootTableModel,
            InsertSql: "INSERT INTO edfi.\"MixedStudent\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(rootDocId, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );

        var plan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "MixedStudent"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTableModel,
                TablesInDependencyOrder: [rootTableModel, tableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: Path(DescriptorPath),
                        Table: tableModel.Table,
                        FkColumn: descriptorColumn.ColumnName,
                        DescriptorResource: AddressTypeDescriptorResource
                    ),
                ]
            ),
            [rootPlan, collectionPlan]
        );

        return (plan, collectionPlan, rootPlan);
    }

    /// <summary>Builds a two-column semantic identity: city (scalar) + descriptor URI (string).</summary>
    public static ImmutableArray<SemanticIdentityPart> BuildStoredIdentity(
        string city,
        string descriptorUri
    ) =>
        [
            new SemanticIdentityPart("$.city", JsonValue.Create(city), IsPresent: true),
            new SemanticIdentityPart(
                DescriptorRelativePath,
                JsonValue.Create(descriptorUri),
                IsPresent: true
            ),
        ];

    public static VisibleStoredCollectionRow BuildStoredRow(string city, string descriptorUri) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                BuildStoredIdentity(city, descriptorUri)
            ),
            ImmutableArray<string>.Empty
        );

    public static VisibleRequestCollectionItem BuildRequestItem(
        string city,
        string descriptorUri,
        int requestOrder
    ) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                [
                    new SemanticIdentityPart("$.city", JsonValue.Create(city), IsPresent: true),
                    new SemanticIdentityPart(
                        DescriptorRelativePath,
                        JsonValue.Create(descriptorUri),
                        IsPresent: true
                    ),
                ]
            ),
            Creatable: true,
            RequestJsonPath: $"$.addresses[{requestOrder}]"
        );

    public static CollectionWriteCandidate BuildCandidate(
        TableWritePlan collectionPlan,
        string city,
        long descriptorId,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[]
        {
            new FlattenedWriteValue.Literal(null), // CollectionItemId
            new FlattenedWriteValue.Literal(null), // ParentDocumentId
            new FlattenedWriteValue.Literal(null), // Ordinal
            new FlattenedWriteValue.Literal(city), // CityName
            new FlattenedWriteValue.Literal(descriptorId), // DescriptorId
        };
        return new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [city, descriptorId]
        );
    }

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}

/// <summary>
/// Fixture: two-part mixed identity (city scalar + addressTypeDescriptor FK).
/// The stored document has two rows: Austin/Physical and Dallas/Mailing.
/// The PUT request includes only Austin/Physical — Physical URI is in the request-cycle
/// descriptor cache but Mailing URI is NOT (delete-by-absence for the Dallas row).
///
/// <para>The canonicalization must NOT throw. The scalar-parts fallback (Strategy 1) matches
/// the Dallas stored row by its city="Dallas" scalar against the current rows and extracts
/// the Mailing descriptor id from the matched current row.</para>
///
/// <para>The planner emits a <c>MatchedUpdateEntry</c> for Austin and omits Dallas (delete-by-absence).
/// The merged state must contain one merged row and two current rows.</para>
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_mixed_identity_and_cache_miss_resolves_via_scalar_parts
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan, rootPlan) = MixedIdentityBuilders.BuildPlan();

        // Request body: only Austin/Physical — Dallas/Mailing is omitted.
        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject
                {
                    ["city"] = "Austin",
                    ["addressTypeDescriptor"] = MixedIdentityBuilders.PhysicalUri,
                }
            ),
        };

        // Candidate for the Austin/Physical request item (id from flattener).
        var candidateAustin = MixedIdentityBuilders.BuildCandidate(
            collectionPlan,
            "Austin",
            MixedIdentityBuilders.PhysicalId,
            requestOrder: 0
        );

        // Request item: Austin/Physical URI (string from Core).
        var requestItemAustin = MixedIdentityBuilders.BuildRequestItem(
            "Austin",
            MixedIdentityBuilders.PhysicalUri,
            requestOrder: 0
        );

        // Both stored rows visible per profile.
        var storedRowAustin = MixedIdentityBuilders.BuildStoredRow(
            "Austin",
            MixedIdentityBuilders.PhysicalUri
        );
        var storedRowDallas = MixedIdentityBuilders.BuildStoredRow(
            "Dallas",
            MixedIdentityBuilders.MailingUri
        );

        // Cache contains ONLY Physical (Austin's URI) — Mailing (Dallas) is absent.
        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            MixedIdentityBuilders.PhysicalUri,
            MixedIdentityBuilders.PhysicalId
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItemAustin]
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRowAustin, storedRowDallas] // Both stored rows visible.
        );

        // Current DB state: two rows — Austin (typeId=42) and Dallas (typeId=50).
        // Layout: [CollectionItemId, ParentDocumentId, Ordinal, CityName, DescriptorId]
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [345L],
                    ]
                ),
                new HydratedTableRows(
                    collectionPlan.TableModel,
                    [
                        [1L, 345L, 1, "Austin", MixedIdentityBuilders.PhysicalId],
                        [2L, 345L, 2, "Dallas", MixedIdentityBuilders.MailingId],
                    ]
                ),
            ],
            []
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidateAustin]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    /// <summary>
    /// Only Austin/Physical is in the plan sequence (Dallas/Mailing was omitted → delete-by-absence).
    /// </summary>
    [Test]
    public void It_produces_one_merged_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    /// <summary>
    /// Both current rows are tracked so the persister can delete Dallas/Mailing by absence.
    /// </summary>
    [Test]
    public void It_has_two_current_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(2);
}

/// <summary>
/// Fixture: mixed identity (city scalar + addressTypeDescriptor FK) where two stored rows
/// share the same scalar value (city = "Dallas") and differ only on the descriptor part —
/// the duplicate-scalar/different-descriptor shape. The PUT request includes only
/// Dallas/Physical, omitting Dallas/Mailing. The Physical URI is in the request-cycle
/// descriptor cache; the Mailing URI is NOT (delete-by-absence cache miss for the omitted
/// stored row).
///
/// <para>Strategy 1 (scalar-parts match) is ambiguous here — both current rows match
/// city = "Dallas". The implementation must fall through to Strategy 2 (count-equal
/// positional matching), which safely disambiguates because
/// <c>currentRows.Length == storedRowsLength</c> implies no hidden rows are interleaved.
/// The canonicalization must NOT throw.</para>
///
/// <para>The planner emits a <c>MatchedUpdateEntry</c> for Dallas/Physical and omits
/// Dallas/Mailing (delete-by-absence). The merged state must contain one merged row and
/// two current rows.</para>
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_mixed_identity_and_duplicate_scalar_resolves_via_positional_fallback
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan, rootPlan) = MixedIdentityBuilders.BuildPlan();

        // Request body: only Dallas/Physical — Dallas/Mailing is omitted.
        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject
                {
                    ["city"] = "Dallas",
                    ["addressTypeDescriptor"] = MixedIdentityBuilders.PhysicalUri,
                }
            ),
        };

        var candidatePhysical = MixedIdentityBuilders.BuildCandidate(
            collectionPlan,
            "Dallas",
            MixedIdentityBuilders.PhysicalId,
            requestOrder: 0
        );

        var requestItemPhysical = MixedIdentityBuilders.BuildRequestItem(
            "Dallas",
            MixedIdentityBuilders.PhysicalUri,
            requestOrder: 0
        );

        // Both stored rows visible per profile, both with city = "Dallas".
        // Stored body iteration order: Physical at index 0, Mailing at index 1.
        var storedRowPhysical = MixedIdentityBuilders.BuildStoredRow(
            "Dallas",
            MixedIdentityBuilders.PhysicalUri
        );
        var storedRowMailing = MixedIdentityBuilders.BuildStoredRow(
            "Dallas",
            MixedIdentityBuilders.MailingUri
        );

        // Cache contains ONLY Physical (the request URI) — Mailing is absent (cache miss).
        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            MixedIdentityBuilders.PhysicalUri,
            MixedIdentityBuilders.PhysicalId
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItemPhysical]
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRowPhysical, storedRowMailing] // Both stored rows visible, same city.
        );

        // Current DB state: two rows — Dallas/Physical (typeId=42) at ordinal 1 and
        // Dallas/Mailing (typeId=50) at ordinal 2. Layout matches storedRow ordinal order so
        // positional correspondence holds: currentRows[0] = Physical, currentRows[1] = Mailing.
        // Layout: [CollectionItemId, ParentDocumentId, Ordinal, CityName, DescriptorId]
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [345L],
                    ]
                ),
                new HydratedTableRows(
                    collectionPlan.TableModel,
                    [
                        [1L, 345L, 1, "Dallas", MixedIdentityBuilders.PhysicalId],
                        [2L, 345L, 2, "Dallas", MixedIdentityBuilders.MailingId],
                    ]
                ),
            ],
            []
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidatePhysical]
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    /// <summary>
    /// Only Dallas/Physical is in the plan sequence (Dallas/Mailing was omitted →
    /// delete-by-absence). The positional fallback must have resolved the Mailing
    /// stored row's descriptor id without throwing.
    /// </summary>
    [Test]
    public void It_produces_one_merged_collection_row() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].MergedRows.Length.Should().Be(1);

    /// <summary>
    /// Both current rows are tracked so the persister can delete Dallas/Mailing by absence.
    /// </summary>
    [Test]
    public void It_has_two_current_collection_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(2);
}

/// <summary>
/// Fixture: mixed identity (city scalar + addressTypeDescriptor FK) where two stored rows
/// share the same city scalar and the visible stored row count differs from the current DB
/// row count (simulating a profile Filter that restricts visible stored rows while the DB
/// still holds the full set). With duplicate scalars Strategy 1 is ambiguous; Strategy 2
/// must refuse to use positional correspondence when counts differ — instead the
/// synthesizer must throw with a diagnostic.
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_mixed_identity_duplicate_scalar_throws_when_row_counts_differ
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan, rootPlan) = MixedIdentityBuilders.BuildPlan();

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject
                {
                    ["city"] = "Dallas",
                    ["addressTypeDescriptor"] = MixedIdentityBuilders.PhysicalUri,
                }
            ),
        };

        var candidatePhysical = MixedIdentityBuilders.BuildCandidate(
            collectionPlan,
            "Dallas",
            MixedIdentityBuilders.PhysicalId,
            requestOrder: 0
        );

        var requestItemPhysical = MixedIdentityBuilders.BuildRequestItem(
            "Dallas",
            MixedIdentityBuilders.PhysicalUri,
            requestOrder: 0
        );

        // Only ONE visible stored row (Mailing) — simulates a profile Filter that hid the
        // Physical row from the stored snapshot.
        var storedRowMailing = MixedIdentityBuilders.BuildStoredRow(
            "Dallas",
            MixedIdentityBuilders.MailingUri
        );

        // Cache contains ONLY Physical — Mailing URI not resolved (cache miss for the
        // visible stored Mailing row).
        var resolvedRefs = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            MixedIdentityBuilders.PhysicalUri,
            MixedIdentityBuilders.PhysicalId
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItemPhysical]
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRowMailing] // 1 visible stored row.
        );

        // Current DB state: two rows with same city — counts differ from the 1 visible
        // stored row. Strategy 1 finds two scalar matches (ambiguous); Strategy 2 must
        // refuse positional fallback because currentRows.Length (2) != storedRowsLength (1).
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: 345L,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [345L],
                    ]
                ),
                new HydratedTableRows(
                    collectionPlan.TableModel,
                    [
                        [1L, 345L, 1, "Dallas", MixedIdentityBuilders.PhysicalId],
                        [2L, 345L, 2, "Dallas", MixedIdentityBuilders.MailingId],
                    ]
                ),
            ],
            []
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidatePhysical]
            )
        );

        _synthesizeAction = () =>
            BuildProfileSynthesizer()
                .Synthesize(
                    new RelationalWriteProfileMergeRequest(
                        writePlan: plan,
                        flattenedWriteSet: flattened,
                        writableRequestBody: body,
                        currentState: currentState,
                        profileRequest: request,
                        profileAppliedContext: context,
                        resolvedReferences: resolvedRefs
                    )
                );
    }

    [Test]
    public void It_throws_with_diagnostic_message() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*descriptor URI not resolvable at merge boundary*");

    [Test]
    public void It_includes_current_rows_count_in_message() =>
        _synthesizeAction.Should().Throw<InvalidOperationException>().WithMessage("*Current rows count: 2*");

    [Test]
    public void It_includes_stored_rows_count_in_message() =>
        _synthesizeAction.Should().Throw<InvalidOperationException>().WithMessage("*stored rows count: 1*");
}

/// <summary>
/// Fixture: descriptor-only identity where the visible stored rows count differs from the
/// current DB rows count (simulating a profile Filter that restricts visible stored rows while
/// the DB holds all rows in currentRows). The positional fallback must NOT silently pick the
/// wrong row — instead it must return null and trigger the throw.
///
/// <para>Setup: 1 visible stored row (Physical URI, cache miss) but 2 current DB rows.
/// The positional guard enforces storedRows.Length == currentRows.Length before using
/// positional correspondence. Here they differ (1 vs 2), so the guard fires and the
/// synthesizer throws.</para>
/// </summary>
[TestFixture]
public class Given_top_level_collection_with_descriptor_only_identity_throws_when_row_counts_differ
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = DescriptorCanonicalizeBuilders.BuildPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["addressTypeDescriptor"] = DescriptorCanonicalizeBuilders.AddressTypeUri }
            ),
        };

        // Candidate for the Physical request item.
        var candidate = DescriptorCanonicalizeBuilders.BuildCandidate(
            collectionPlan,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );
        // Request item with Physical URI.
        var requestItem = DescriptorCanonicalizeBuilders.BuildRequestItemWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            creatable: true
        );

        // Only ONE visible stored row (Physical URI) — simulates a profile Filter that hid
        // the Mailing row from the stored snapshot, giving 1 visible stored row.
        var storedRowPhysical = DescriptorCanonicalizeBuilders.BuildStoredRowWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri
        );

        // Cache is EMPTY — Physical URI not resolved in this request cycle (cache miss).
        var resolvedRefs = new ResolvedReferenceSet(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [requestItem]
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRowPhysical] // 1 visible stored row.
        );

        // Current DB state has TWO rows — counts differ from the 1 visible stored row.
        // This simulates the profile-filtered scenario described in Issue 1.
        var currentState = DescriptorCanonicalizeBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId: 345L,
            collectionRows:
            [
                [1L, 345L, 1, DescriptorCanonicalizeBuilders.AddressTypeId], // Physical (id=42)
                [2L, 345L, 2, 50L], // Mailing (id=50) — hidden from stored snapshot
            ]
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: [candidate]
            )
        );

        _synthesizeAction = () =>
            BuildProfileSynthesizer()
                .Synthesize(
                    new RelationalWriteProfileMergeRequest(
                        writePlan: plan,
                        flattenedWriteSet: flattened,
                        writableRequestBody: body,
                        currentState: currentState,
                        profileRequest: request,
                        profileAppliedContext: context,
                        resolvedReferences: resolvedRefs
                    )
                );
    }

    [Test]
    public void It_throws_with_diagnostic_message() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*descriptor URI not resolvable at merge boundary*");

    [Test]
    public void It_includes_current_rows_count_in_message() =>
        _synthesizeAction.Should().Throw<InvalidOperationException>().WithMessage("*Current rows count: 2*");

    [Test]
    public void It_includes_stored_rows_count_in_message() =>
        _synthesizeAction.Should().Throw<InvalidOperationException>().WithMessage("*stored rows count: 1*");
}

/// <summary>
/// Shared helpers for reference-backed top-level collection identity whose referenced
/// document's natural key contains a descriptor URI part (the
/// "<c>ProgramReference{programId, programTypeDescriptor}</c>" shape). The collection table
/// denormalizes both natural-key parts into local columns:
/// <c>ProgramReference_ProgramId</c> (Scalar) and
/// <c>ProgramReference_ProgramTypeDescriptor_Id</c> (DescriptorFk).
///
/// <para>These helpers exercise the Strategy 2 scalar-parts-only fallback in
/// <c>TryResolveDocumentIdFromCurrentRows</c> when the descriptor URI inside the reference
/// natural key cannot be resolved against the request-cycle cache.</para>
/// </summary>
internal static class ReferenceWithDescriptorIdentityBuilders
{
    public const string CollectionScope = "$.programs[*]";
    public const string ReferenceObjectPath = "$.programs[*].programReference";
    public const string ProgramIdPath = "$.programs[*].programReference.programId";
    public const string ProgramTypeDescriptorPath = "$.programs[*].programReference.programTypeDescriptor";
    public const string ProgramIdRelativePath = "$.programReference.programId";
    public const string ProgramTypeDescriptorRelativePath = "$.programReference.programTypeDescriptor";

    public const string ProgramAId = "42";
    public const string ProgramBId = "99";
    public const string AthleticUri = "uri://ed-fi.org/ProgramTypeDescriptor#Athletic";
    public const string CareerUri = "uri://ed-fi.org/ProgramTypeDescriptor#Career";
    public const long AthleticDescriptorId = 10L;
    public const long CareerDescriptorId = 20L;
    public const long ProgramADocumentId = 501L;
    public const long ProgramBDocumentId = 502L;

    public static readonly QualifiedResourceName ProgramResource = new("Ed-Fi", "Program");
    public static readonly QualifiedResourceName ProgramTypeDescriptorResource = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    public static (ResourceWritePlan Plan, TableWritePlan CollectionPlan, TableWritePlan RootPlan) BuildPlan()
    {
        var collectionPlan = BuildReferenceCollectionPlan();
        var rootPlan = BuildRootPlan();

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path(ReferenceObjectPath),
            Table: collectionPlan.TableModel.Table,
            FkColumn: new DbColumnName("ProgramReference_DocumentId"),
            TargetResource: ProgramResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    Path("$.programId"),
                    Path(ProgramIdPath),
                    new DbColumnName("ProgramReference_ProgramId")
                ),
                new ReferenceIdentityBinding(
                    Path("$.programTypeDescriptor"),
                    Path(ProgramTypeDescriptorPath),
                    new DbColumnName("ProgramReference_ProgramTypeDescriptor_Id")
                ),
            ]
        );

        var plan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "School"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
                DocumentReferenceBindings: [binding],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: Path(ProgramTypeDescriptorPath),
                        Table: collectionPlan.TableModel.Table,
                        FkColumn: new DbColumnName("ProgramReference_ProgramTypeDescriptor_Id"),
                        DescriptorResource: ProgramTypeDescriptorResource
                    ),
                ]
            ),
            [rootPlan, collectionPlan]
        );
        return (plan, collectionPlan, rootPlan);
    }

    public static ImmutableArray<SemanticIdentityPart> BuildStoredIdentity(
        string programId,
        string programTypeUri
    ) =>
        [
            new SemanticIdentityPart(ProgramIdRelativePath, JsonValue.Create(programId), IsPresent: true),
            new SemanticIdentityPart(
                ProgramTypeDescriptorRelativePath,
                JsonValue.Create(programTypeUri),
                IsPresent: true
            ),
        ];

    public static VisibleStoredCollectionRow BuildStoredRow(string programId, string programTypeUri) =>
        new(
            new CollectionRowAddress(
                CollectionScope,
                new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty),
                BuildStoredIdentity(programId, programTypeUri)
            ),
            ImmutableArray<string>.Empty
        );

    public static ResolvedReferenceSet BuildEmptyResolvedReferenceSet() =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

    private static TableWritePlan BuildRootPlan()
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "School"),
            JsonScope: Path("$"),
            Key: new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"School\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan BuildReferenceCollectionPlan()
    {
        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var fkColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ProgramReference_DocumentId"),
            Kind: ColumnKind.DocumentFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: Path(ReferenceObjectPath),
            TargetResource: ProgramResource
        );
        var programIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ProgramReference_ProgramId"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: Path(ProgramIdPath),
            TargetResource: null
        );
        var programTypeDescriptorColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ProgramReference_ProgramTypeDescriptor_Id"),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: Path(ProgramTypeDescriptorPath),
            TargetResource: ProgramTypeDescriptorResource
        );

        var allColumns = new[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            fkColumn,
            programIdColumn,
            programTypeDescriptorColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "SchoolProgram"),
            JsonScope: Path(CollectionScope),
            Key: new TableKey(
                "PK_SchoolProgram",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(Path(ProgramIdRelativePath), fkColumn.ColumnName),
                    new CollectionSemanticIdentityBinding(
                        Path(ProgramTypeDescriptorRelativePath),
                        fkColumn.ColumnName
                    ),
                ]
            ),
        };

        var programIdReferenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: Path(ReferenceObjectPath),
            IdentityJsonPath: Path("$.programId"),
            ReferenceJsonPath: Path(ProgramIdPath)
        );
        var programTypeReferenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: Path(ReferenceObjectPath),
            IdentityJsonPath: Path("$.programTypeDescriptor"),
            ReferenceJsonPath: Path(ProgramTypeDescriptorPath)
        );

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"SchoolProgram\" VALUES (@CollectionItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, allColumns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    fkColumn,
                    new WriteValueSource.DocumentReference(0),
                    "ProgramReference_DocumentId"
                ),
                new WriteColumnBinding(
                    programIdColumn,
                    new WriteValueSource.ReferenceDerived(programIdReferenceSource),
                    "ProgramReference_ProgramId"
                ),
                new WriteColumnBinding(
                    programTypeDescriptorColumn,
                    new WriteValueSource.ReferenceDerived(programTypeReferenceSource),
                    "ProgramReference_ProgramTypeDescriptor_Id"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(Path(ProgramIdRelativePath), 3),
                    new CollectionMergeSemanticIdentityBinding(Path(ProgramTypeDescriptorRelativePath), 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"SchoolProgram\" SET X=@X WHERE \"CollectionItemId\"=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"SchoolProgram\" WHERE \"CollectionItemId\"=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 5, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static JsonPathExpression Path(string canonical) => new(canonical, []);
}

/// <summary>
/// Fixture: reference-backed top-level collection whose ProgramReference natural key
/// contains a descriptor URI (programTypeDescriptor). One stored row is visible — its
/// descriptor URI is absent from the request-cycle cache (delete-by-absence), and the
/// scalar programId part of the natural key uniquely identifies the corresponding current
/// row even though the visible-stored row count differs from the current DB row count
/// (a hidden DB row is interleaved). Strategy 2 scalar-parts-only matching must resolve the
/// document id without throwing.
/// </summary>
[TestFixture]
public class Given_reference_backed_top_level_collection_with_descriptor_in_natural_key_resolves_via_scalar_parts_on_cache_miss
{
    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan, rootPlan) = ReferenceWithDescriptorIdentityBuilders.BuildPlan();
        const long parentDocumentId = 345L;

        // PUT request body has no programs — delete-by-absence for the visible stored row.
        var body = new JsonObject { ["programs"] = new JsonArray() };

        // One visible stored row (Program B / Career) — Career URI is NOT in the cache.
        var storedRowB = ReferenceWithDescriptorIdentityBuilders.BuildStoredRow(
            ReferenceWithDescriptorIdentityBuilders.ProgramBId,
            ReferenceWithDescriptorIdentityBuilders.CareerUri
        );

        // Cache is empty — request body has no references for this scope.
        var resolvedRefs = ReferenceWithDescriptorIdentityBuilders.BuildEmptyResolvedReferenceSet();

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRowB] // 1 visible stored row.
        );

        // Current DB state: TWO rows. Row A (programId=42, Athletic, ordinal 1) is hidden
        // by the profile (not in visible stored rows). Row B (programId=99, Career,
        // ordinal 2) is the visible stored row. Counts differ (1 stored vs 2 current).
        // Layout: [CollectionItemId, ParentDocumentId, Ordinal, ProgramReference_DocumentId,
        //          ProgramReference_ProgramId, ProgramReference_ProgramTypeDescriptor_Id]
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: parentDocumentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [parentDocumentId],
                    ]
                ),
                new HydratedTableRows(
                    collectionPlan.TableModel,
                    [
                        [
                            1L,
                            parentDocumentId,
                            1,
                            ReferenceWithDescriptorIdentityBuilders.ProgramADocumentId,
                            ReferenceWithDescriptorIdentityBuilders.ProgramAId,
                            ReferenceWithDescriptorIdentityBuilders.AthleticDescriptorId,
                        ],
                        [
                            2L,
                            parentDocumentId,
                            2,
                            ReferenceWithDescriptorIdentityBuilders.ProgramBDocumentId,
                            ReferenceWithDescriptorIdentityBuilders.ProgramBId,
                            ReferenceWithDescriptorIdentityBuilders.CareerDescriptorId,
                        ],
                    ]
                ),
            ],
            []
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(parentDocumentId)],
                collectionCandidates: ImmutableArray<CollectionWriteCandidate>.Empty
            )
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: resolvedRefs
                )
            );
    }

    /// <summary>
    /// The synthesizer must complete without throwing — Strategy 2 (scalar-parts-only
    /// match on programId) resolved the visible stored Program B row's identity even
    /// though the Career descriptor URI was absent from the request-cycle cache and
    /// counts differed (1 stored vs 2 current). The exact plan composition (which rows
    /// appear in <c>MergedRows</c> for delete-by-absence + hidden-preservation) is
    /// already covered by the descriptor-only and mixed-identity fixtures; this fixture
    /// only proves the cache-miss canonicalization path itself does not fail closed.
    /// </summary>
    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_has_merge_result() => _outcome.MergeResult.Should().NotBeNull();

    [Test]
    public void It_tracks_two_current_rows() =>
        _outcome.MergeResult!.TablesInDependencyOrder[1].CurrentRows.Length.Should().Be(2);
}

/// <summary>
/// Fixture: reference-backed top-level collection whose ProgramReference natural key
/// contains a descriptor URI. One visible stored row whose descriptor URI is absent from
/// the cache; the scalar programId part of the natural key matches MULTIPLE current rows
/// (ambiguous), and the visible-stored row count differs from the current DB row count so
/// positional fallback also cannot resolve. The synthesizer must throw with a deterministic
/// diagnostic naming the unresolvable document reference.
/// </summary>
[TestFixture]
public class Given_reference_backed_top_level_collection_with_descriptor_in_natural_key_throws_when_scalars_ambiguous_and_counts_differ
{
    private Action _synthesizeAction = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan, rootPlan) = ReferenceWithDescriptorIdentityBuilders.BuildPlan();
        const long parentDocumentId = 345L;

        var body = new JsonObject { ["programs"] = new JsonArray() };

        // One visible stored row (programId=42, Career URI). Career URI not in cache.
        var storedRow = ReferenceWithDescriptorIdentityBuilders.BuildStoredRow(
            ReferenceWithDescriptorIdentityBuilders.ProgramAId,
            ReferenceWithDescriptorIdentityBuilders.CareerUri
        );

        var resolvedRefs = ReferenceWithDescriptorIdentityBuilders.BuildEmptyResolvedReferenceSet();

        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            [storedRow] // 1 visible stored row.
        );

        // Current DB state: TWO rows BOTH with programId=42 but different program-type
        // descriptors. Strategy 2 (scalar match on programId) is ambiguous — both rows
        // match. Counts differ (1 stored vs 2 current) so Strategy 3 positional fallback
        // is also fenced out.
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: parentDocumentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-ffffffffffff"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [parentDocumentId],
                    ]
                ),
                new HydratedTableRows(
                    collectionPlan.TableModel,
                    [
                        [
                            1L,
                            parentDocumentId,
                            1,
                            ReferenceWithDescriptorIdentityBuilders.ProgramADocumentId,
                            ReferenceWithDescriptorIdentityBuilders.ProgramAId,
                            ReferenceWithDescriptorIdentityBuilders.AthleticDescriptorId,
                        ],
                        [
                            2L,
                            parentDocumentId,
                            2,
                            ReferenceWithDescriptorIdentityBuilders.ProgramBDocumentId,
                            ReferenceWithDescriptorIdentityBuilders.ProgramAId,
                            ReferenceWithDescriptorIdentityBuilders.CareerDescriptorId,
                        ],
                    ]
                ),
            ],
            []
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(parentDocumentId)],
                collectionCandidates: ImmutableArray<CollectionWriteCandidate>.Empty
            )
        );

        _synthesizeAction = () =>
            BuildProfileSynthesizer()
                .Synthesize(
                    new RelationalWriteProfileMergeRequest(
                        writePlan: plan,
                        flattenedWriteSet: flattened,
                        writableRequestBody: body,
                        currentState: currentState,
                        profileRequest: request,
                        profileAppliedContext: context,
                        resolvedReferences: resolvedRefs
                    )
                );
    }

    [Test]
    public void It_throws_with_diagnostic_message() =>
        _synthesizeAction
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*document reference not resolvable at merge boundary*");

    [Test]
    public void It_includes_current_rows_count_in_message() =>
        _synthesizeAction.Should().Throw<InvalidOperationException>().WithMessage("*Current rows count: 2*");

    [Test]
    public void It_includes_stored_rows_count_in_message() =>
        _synthesizeAction.Should().Throw<InvalidOperationException>().WithMessage("*stored rows count: 1*");
}

// ────────────────────────────────────────────────────────────────────────────
// CP2 Task 13 — Synthesizer-level fixtures for nested-base-collection merge.
//
// These fixtures call IRelationalWriteProfileMergeSynthesizer.Synthesize directly
// with realized inputs that exercise the walker's nested-recursion code paths
// end-to-end at the merge-result level. They reuse NestedTopologyBuilders from
// ProfileCollectionWalkerTests.cs (same project, same namespace) for write-plan
// and arrangement helpers, extending them with synthesizer-test-specific
// variants only where necessary (e.g., the extended children plan that carries
// an extra scalar column for hidden-member-path coverage).
//
// The walker's behavior is directly testable via Synthesize because CP2 Task 12
// retired the constructor-side fence on nested CollectionCandidates. These tests
// exercise walker behavior directly instead of going through the full executor pipeline.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fixture 1 (Task 13). Top-level parent matched-update; under that parent, two nested
/// children — one visible (matched-update), one hidden (preserved). Asserts:
/// (a) parents table emits one merged row,
/// (b) children table emits two merged rows,
/// (c) the visible child's merged row carries the request scalar value,
/// (d) the hidden child's merged row carries the stored scalar value unchanged.
/// </summary>
[TestFixture]
public class Given_nested_base_collection_visible_row_update_with_hidden_row_preservation
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2HiddenItemId = 1002L;

    private ProfileMergeOutcome _outcome;
    private TableWritePlan _parentsPlan = null!;
    private TableWritePlan _childrenPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _parentsPlan = parentsPlan;
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: parent A with one visible child A1.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "A1" }),
                }
            ),
        };

        // Build nested child candidate for A1 and attach to parent A's candidate.
        var childA1Candidate = NestedTopologyBuilders.BuildChildCandidate(childrenPlan, "A1", 0);
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childA1Candidate]
        );

        // Visible items: parent A, child A1 (under parent A).
        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildChildRequestItem(
                parentSemanticIdentity: "A",
                childIdentity: "A1",
                parentArrayIndex: 0,
                childArrayIndex: 0
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        // Stored visible: parent A, child A1 only. A2 is a hidden stored row — present in
        // current state but not in VisibleStoredCollectionRows, so the planner emits a
        // HiddenPreserveEntry for A2 that reaches the merged side via the walker's
        // existing top-level switch path.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A1")
        );

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2HiddenItemId, ParentAItemId, 2, "A2"],
            ]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_one_merged_parent_row()
    {
        var parentsTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _parentsPlan.TableModel.Table
        );
        parentsTable.MergedRows.Length.Should().Be(1);
    }

    [Test]
    public void It_emits_two_merged_child_rows()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.MergedRows.Length.Should().Be(2);
    }

    [Test]
    public void It_carries_visible_and_hidden_child_identities_in_the_merged_set()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        var identities = childrenTable
            .MergedRows.Select(r => ((FlattenedWriteValue.Literal)r.Values[3]).Value as string)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        identities.Should().Equal("A1", "A2");
    }

    [Test]
    public void It_attaches_each_merged_child_row_to_parent_As_physical_identity()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        var parentItemIdValues = childrenTable
            .MergedRows.Select(r => ((FlattenedWriteValue.Literal)r.Values[1]).Value)
            .Select(v => v is null ? -1 : Convert.ToInt64(v))
            .ToList();
        parentItemIdValues.Should().AllBeEquivalentTo(ParentAItemId);
    }
}

/// <summary>
/// Fixture 2 (Task 13). Top-level parent matched-update; three stored nested children — two
/// visible (one in request → matched-update; one omitted → delete-by-absence), one hidden
/// (preserved). Asserts: nested table has 2 merged rows (the matched + the hidden); the
/// omitted-visible row is absent from the merged set so the persister deletes it by
/// set-difference. CurrentRows on the children table-state must include all three stored
/// rows so the persister can compute the difference.
/// </summary>
[TestFixture]
public class Given_nested_base_collection_visible_row_delete_with_hidden_row_preservation
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ChildA1ItemId = 1001L; // visible, kept (matched-update)
    private const long ChildA2ItemId = 1002L; // visible, omitted (delete-by-absence)
    private const long ChildA3HiddenItemId = 1003L; // hidden, preserved

    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childrenPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request: parent A with only A1 attached as a child (A2 omitted).
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "A1" }),
                }
            ),
        };

        var childA1Candidate = NestedTopologyBuilders.BuildChildCandidate(childrenPlan, "A1", 0);
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childA1Candidate]
        );

        // Visible items: parent A; child A1 only (A2 is also visible-stored but omitted
        // from the request — the planner emits ProfileCollectionPlanEntry for A2 as
        // "omitted" and the walker's per-table builder leaves A2 out of the merged set).
        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildChildRequestItem(
                parentSemanticIdentity: "A",
                childIdentity: "A1",
                parentArrayIndex: 0,
                childArrayIndex: 0
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        // Stored visible: A1, A2 (visible). A3 is hidden (not in stored visible).
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A1"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A2")
        );

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2ItemId, ParentAItemId, 2, "A2"],
                [ChildA3HiddenItemId, ParentAItemId, 3, "A3"],
            ]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_two_merged_child_rows_excluding_the_omitted_visible_row()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.MergedRows.Length.Should().Be(2);
        var identities = childrenTable
            .MergedRows.Select(r => ((FlattenedWriteValue.Literal)r.Values[3]).Value as string)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        // A2 must be absent from the merged set — the persister deletes it by set-difference
        // against CurrentRows.
        identities.Should().Equal("A1", "A3");
    }

    [Test]
    public void It_includes_all_three_stored_rows_in_current_rows_for_set_difference()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.CurrentRows.Length.Should().Be(3);
    }
}

/// <summary>
/// Fixture 3 (Task 13). Top-level parent matched-update; nested visible-request item with no
/// matching stored row, Creatable=false. The planner rejects with RejectCreateDenied at the
/// nested children scope and the walker's recursion propagates the rejection through to
/// Synthesize's outcome. Asserts: outcome is Reject(ProfileCreatabilityRejection) carrying
/// the children scope.
/// </summary>
[TestFixture]
public class Given_nested_visible_request_item_with_no_visible_stored_match_when_creatable_is_false
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;

    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "NEWCHILD" }),
                }
            ),
        };

        var childCandidate = NestedTopologyBuilders.BuildChildCandidate(childrenPlan, "NEWCHILD", 0);
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childCandidate]
        );

        // Nested request item: NEWCHILD with creatable=false. The planner sees no matching
        // visible-stored row → unmatched → rejection.
        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildChildRequestItem(
                parentSemanticIdentity: "A",
                childIdentity: "NEWCHILD",
                parentArrayIndex: 0,
                childArrayIndex: 0,
                creatable: false
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        var storedRows = ImmutableArray.Create(NestedTopologyBuilders.BuildParentStoredRow("A"));

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
            ],
            childRows: []
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_a_rejection() => _outcome.IsRejection.Should().BeTrue();

    [Test]
    public void It_has_no_merge_result() => _outcome.MergeResult.Should().BeNull();

    [Test]
    public void It_identifies_the_nested_children_scope_in_the_rejection()
    {
        _outcome.CreatabilityRejection!.ScopeJsonScope.Should().Be(NestedTopologyBuilders.ChildrenScope);
    }
}

/// <summary>
/// Fixture 4 (Task 13). Mirror of Fixture 3 but with Creatable=true on the nested request
/// item. Asserts: success outcome with one merged row inserted on the children table; the
/// inserted row's ParentItemId column matches the parent's PhysicalRowIdentity.
/// </summary>
[TestFixture]
public class Given_nested_visible_request_item_with_no_visible_stored_match_when_creatable_is_true
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;

    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childrenPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "NEWCHILD" }),
                }
            ),
        };

        var childCandidate = NestedTopologyBuilders.BuildChildCandidate(childrenPlan, "NEWCHILD", 0);
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childCandidate]
        );

        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildChildRequestItem(
                parentSemanticIdentity: "A",
                childIdentity: "NEWCHILD",
                parentArrayIndex: 0,
                childArrayIndex: 0,
                creatable: true
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        var storedRows = ImmutableArray.Create(NestedTopologyBuilders.BuildParentStoredRow("A"));

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
            ],
            childRows: []
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_one_merged_child_row_for_the_insert()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.MergedRows.Length.Should().Be(1);
        ((FlattenedWriteValue.Literal)childrenTable.MergedRows[0].Values[3]).Value.Should().Be("NEWCHILD");
    }

    [Test]
    public void It_stamps_the_parent_physical_identity_on_the_inserted_child()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        var parentItemId = ((FlattenedWriteValue.Literal)childrenTable.MergedRows[0].Values[1]).Value;
        Convert.ToInt64(parentItemId).Should().Be(ParentAItemId);
    }
}

/// <summary>
/// Fixture 5 (Task 13). Three-level chain: top-level parent matched-update; nested level-2
/// collection matched-update; nested level-3 visible-request-item with no stored match and
/// Creatable=false. Asserts: rejection carries the level-3 (grandchildren) scope, proving
/// that walker recursion threads the rejection up through both nested levels without losing
/// the originating scope.
/// </summary>
[TestFixture]
public class Given_three_level_chain_with_update_allowed_at_levels_1_and_2_create_denied_at_level_3
{
    private ProfileMergeOutcome _outcome;
    private const string GrandchildrenScope = "$.parents[*].children[*].grandchildren[*]";

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan, grandchildrenPlan) =
            ThreeLevelTopologyBuilders.BuildThreeLevelPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        const long documentId = 345L;
        const long parentAItemId = 100L;
        const long childC1ItemId = 1001L;

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["children"] = new JsonArray(
                        new JsonObject
                        {
                            ["identityField0"] = "C1",
                            ["grandchildren"] = new JsonArray(new JsonObject { ["identityField0"] = "G1" }),
                        }
                    ),
                }
            ),
        };

        var grandchildG1Candidate = ThreeLevelTopologyBuilders.BuildGrandchildCandidate(
            grandchildrenPlan,
            "G1",
            0
        );
        var childC1Candidate = ThreeLevelTopologyBuilders.BuildChildCandidate(
            childrenPlan,
            "C1",
            0,
            nestedGrandchildren: [grandchildG1Candidate]
        );
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childC1Candidate]
        );

        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildChildRequestItem(
                parentSemanticIdentity: "A",
                childIdentity: "C1",
                parentArrayIndex: 0,
                childArrayIndex: 0
            ),
            ThreeLevelTopologyBuilders.BuildGrandchildRequestItem(
                parentSemanticIdentity: "A",
                childSemanticIdentity: "C1",
                grandchildIdentity: "G1",
                parentArrayIndex: 0,
                childArrayIndex: 0,
                grandchildArrayIndex: 0,
                creatable: false
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        // Stored visible: A and A.C1 only (no grandchildren). The grandchildren request item
        // has no match → rejection at level 3.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "C1")
        );

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = ThreeLevelTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            grandchildrenPlan,
            documentId,
            parentRows:
            [
                [parentAItemId, documentId, 1, "A"],
            ],
            childRows:
            [
                [childC1ItemId, parentAItemId, 1, "C1"],
            ],
            grandchildRows: []
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_a_rejection() => _outcome.IsRejection.Should().BeTrue();

    [Test]
    public void It_identifies_the_grandchildren_scope_in_the_rejection()
    {
        _outcome.CreatabilityRejection!.ScopeJsonScope.Should().Be(GrandchildrenScope);
    }
}

/// <summary>
/// Fixture 6 (Task 13). Top-level parent matched-update; under that parent, three stored
/// nested children: two visible (both omitted from request → delete-by-absence), one hidden
/// (preserved). Asserts: nested merged rows = 1 (the hidden); the two visible rows are
/// absent from the merged set so the persister deletes both.
/// </summary>
[TestFixture]
public class Given_nested_delete_all_visible_with_hidden_rows_remaining
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;
    private const long ChildA3HiddenItemId = 1003L;

    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childrenPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request: parent A with NO children attached. Both A1 and A2 are visible-stored
        // but omitted from the request → delete-by-absence. A3 is hidden → preserved.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(new JsonObject { ["identityField0"] = "A" }),
        };

        var candidateA = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "A", 0);

        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0)
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        // Visible stored: A, A1, A2. A3 hidden.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A1"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A2")
        );

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2ItemId, ParentAItemId, 2, "A2"],
                [ChildA3HiddenItemId, ParentAItemId, 3, "A3"],
            ]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_only_the_hidden_child_in_the_merged_set()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.MergedRows.Length.Should().Be(1);
        ((FlattenedWriteValue.Literal)childrenTable.MergedRows[0].Values[3]).Value.Should().Be("A3");
    }

    [Test]
    public void It_includes_all_three_stored_rows_in_current_rows_for_set_difference()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.CurrentRows.Length.Should().Be(3);
    }
}

/// <summary>
/// Fixture 7 (Task 13). Top-level parent matched-update; nested matched-update where the
/// stored row carries a hidden-member-path on a non-identity scalar. Asserts: the merged
/// row preserves the stored value at the hidden path while overlaying the request value at
/// visible paths. Uses the extended children plan that adds a second scalar binding
/// (<c>$.scalarField1</c>) so the hidden binding is observable.
/// </summary>
[TestFixture]
public class Given_nested_matched_update_preserves_hidden_member_paths
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ChildA1ItemId = 1001L;
    private const string StoredScalarValue = "stored-hidden-scalar";
    private const string RequestScalarValue = "request-overlay-scalar";

    private ProfileMergeOutcome _outcome;
    private TableWritePlan _childrenPlan = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            NestedTopologyBuilders.BuildRootParentsAndChildrenPlanWithExtraChildScalar();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: parent A with one child A1, where A1 carries a request value at
        // the (visible-from-request perspective, hidden-from-stored perspective) scalar
        // path. The stored row's HiddenMemberPaths include "scalarField1" — the matched-row
        // overlay must preserve the stored value at scalarField1 and discard the request
        // value at the hidden path.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["children"] = new JsonArray(
                        new JsonObject { ["identityField0"] = "A1", ["scalarField1"] = RequestScalarValue }
                    ),
                }
            ),
        };

        var childA1Candidate = NestedTopologyBuilders.BuildChildCandidate(
            childrenPlan,
            "A1",
            0,
            scalarFieldValue: RequestScalarValue
        );
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childA1Candidate]
        );

        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildChildRequestItem(
                parentSemanticIdentity: "A",
                childIdentity: "A1",
                parentArrayIndex: 0,
                childArrayIndex: 0
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA]);

        // Visible stored: A and A.A1 — but A1 has scalarField1 marked as a hidden member
        // path. The matched-row overlay must preserve the stored value at that path.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A1", hiddenMemberPaths: ["scalarField1"])
        );

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1", StoredScalarValue],
            ]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: context,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_emits_one_merged_child_row()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        childrenTable.MergedRows.Length.Should().Be(1);
    }

    [Test]
    public void It_preserves_the_stored_value_at_the_hidden_scalar_path()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        // ScalarField1 is at binding index 4 (the extended children plan's 5th column).
        var mergedScalar = ((FlattenedWriteValue.Literal)childrenTable.MergedRows[0].Values[4]).Value;
        mergedScalar
            .Should()
            .Be(
                StoredScalarValue,
                "the matched-row overlay must preserve the stored value at the hidden "
                    + "member path, ignoring the request-side value"
            );
    }

    [Test]
    public void It_carries_the_visible_identity_value_unchanged()
    {
        var childrenTable = _outcome.MergeResult!.TablesInDependencyOrder.Single(s =>
            s.TableWritePlan.TableModel.Table == _childrenPlan.TableModel.Table
        );
        ((FlattenedWriteValue.Literal)childrenTable.MergedRows[0].Values[3]).Value.Should().Be("A1");
    }
}

/// <summary>
/// Slice 5 CP5 Task 6 regression pin: when a separate-table Update merges a row whose
/// extension table inlines a non-collection descendant scope and the descendant's stored
/// scope state names a hidden member path covering the descendant binding, the synthesizer
/// must preserve the stored value at the descendant binding's column while still overlaying
/// the request value at the direct-scope binding's column. Before BuildUpdateState was
/// wired to collect descendant scope states and pass them to the instance-aware classifier
/// and resolver, the descendant binding fell through to the direct scope's governance and
/// the request body silently clobbered the hidden stored column.
/// </summary>
[TestFixture]
public class Given_RootExtensionUpdate_hidden_descendant_scope_preserves_stored_value
{
    private const string DirectScope = "$._ext.sample";
    private const string DescendantScope = "$._ext.sample.detail";
    private const string DescendantBindingPath = "$._ext.sample.detail.someField";
    private const string StoredDirectValue = "stored_direct";
    private const string StoredDescendantValue = "stored_descendant";
    private const string RequestDirectValue = "new_direct";
    private const string RequestDescendantValue = "request_clobber_attempt";

    private ProfileMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        // Plan: root + RootExtension at $._ext.sample. The extension table carries the
        // direct-scope FavoriteColor binding plus a descendant-scope DetailField binding
        // sourced from $._ext.sample.detail.someField. The descendant scope is inlined
        // onto the direct extension table.
        var plan = BuildRootPlusRootExtensionPlanWithInlinedDescendantScope(
            descendantScopeRelativePath: DescendantScope,
            descendantBindingRelativePath: DescendantBindingPath
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];

        // Request body: the direct-scope value is overlaid normally; the descendant-scope
        // value is supplied as well, but the descendant's stored hidden-member-path covers
        // it so the merged row must preserve the stored value rather than the request value.
        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["favoriteColor"] = RequestDirectValue,
                    ["detail"] = new JsonObject { ["someField"] = RequestDescendantValue },
                },
            },
        };

        // Direct extension scope is VisiblePresent. Descendant scope is also VisiblePresent
        // but its stored state names "someField" as a hidden member path — which translates
        // (via descendant-state collection) into HiddenPreserved governance for the
        // DetailField binding on this same physical extension table.
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope(DirectScope, creatable: true),
            RequestVisiblePresentScope(DescendantScope)
        );
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            StoredVisiblePresentScope(DirectScope),
            StoredVisiblePresentScope(DescendantScope, "someField")
        );

        // Flattener buffer: extension binding-index ordering matches BuildRootPlusRootExtensionPlan
        // — [0] DocumentId (ParentKeyPart), [1] FavoriteColor (direct), [2] DetailField (descendant).
        // The buffer carries both request values; the synthesizer's matched-row overlay must
        // honor the descendant-scope hidden disposition and discard the descendant request value.
        var flattened = BuildFlattenedWriteSetWithExtensionRow(
            plan,
            extensionPlan,
            rootLiteralsByBindingIndex: ["Ada"],
            extensionLiteralsByBindingIndex: [null, RequestDirectValue, RequestDescendantValue]
        );

        // Stored row carries non-null values at both the direct-scope and descendant-scope
        // columns, ensuring the matched-row branch is selected and the descendant column has
        // a real value to be preserved.
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [345L, StoredDirectValue, StoredDescendantValue]
        );

        _outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    writePlan: plan,
                    flattenedWriteSet: flattened,
                    writableRequestBody: body,
                    currentState: currentState,
                    profileRequest: request,
                    profileAppliedContext: appliedContext,
                    resolvedReferences: EmptyResolvedReferenceSet()
                )
            );
    }

    [Test]
    public void It_returns_success() => _outcome.IsRejection.Should().BeFalse();

    [Test]
    public void It_routes_the_extension_table_through_the_Update_branch()
    {
        var extensionTable = _outcome.MergeResult!.TablesInDependencyOrder[1];
        extensionTable.CurrentRows.Length.Should().Be(1);
        extensionTable.MergedRows.Length.Should().Be(1);
        var currentDirect = ((FlattenedWriteValue.Literal)extensionTable.CurrentRows[0].Values[1]).Value;
        var mergedDirect = ((FlattenedWriteValue.Literal)extensionTable.MergedRows[0].Values[1]).Value;
        // Update branch overlays request values; Preserve emits byte-identical rows. The
        // direct-scope column must change to distinguish Update from Preserve.
        mergedDirect.Should().NotBe(currentDirect);
    }

    [Test]
    public void It_overlays_the_request_value_on_the_direct_scope_binding()
    {
        var extensionTable = _outcome.MergeResult!.TablesInDependencyOrder[1];
        var mergedDirect = ((FlattenedWriteValue.Literal)extensionTable.MergedRows[0].Values[1]).Value;
        mergedDirect
            .Should()
            .Be(
                RequestDirectValue,
                "the direct-scope binding is VisiblePresent with no hidden-member match — "
                    + "the request value must overlay normally"
            );
    }

    [Test]
    public void It_preserves_the_stored_value_on_the_hidden_descendant_binding()
    {
        var extensionTable = _outcome.MergeResult!.TablesInDependencyOrder[1];
        var mergedDescendant = ((FlattenedWriteValue.Literal)extensionTable.MergedRows[0].Values[2]).Value;
        mergedDescendant
            .Should()
            .Be(
                StoredDescendantValue,
                "the descendant-scope binding's hidden-member-path comes from the descendant "
                    + "stored scope state — BuildUpdateState must collect descendant states and "
                    + "pass them to the classifier/resolver so the matched-row overlay preserves "
                    + "the stored column rather than letting the request value clobber it"
            );
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Three-level topology helpers used by Fixture 5 (Task 13). Adds a grandchildren
// collection scope at $.parents[*].children[*].grandchildren[*]. The grandchildren
// plan's ParentItemId binding is a ParentKeyPart referring to slot 0 of the children
// table's PhysicalRowIdentity (i.e., the children's ChildItemId).
// ────────────────────────────────────────────────────────────────────────────
internal static class ThreeLevelTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");

    public const string ParentsScope = NestedTopologyBuilders.ParentsScope;
    public const string ChildrenScope = NestedTopologyBuilders.ChildrenScope;
    public const string GrandchildrenScope = "$.parents[*].children[*].grandchildren[*]";

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan,
        TableWritePlan GrandchildrenPlan
    ) BuildThreeLevelPlan()
    {
        // Reuse the parents/children plans from NestedTopologyBuilders for symmetry, then
        // append a grandchildren plan keyed on ChildItemId.
        var (_, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = BuildMinimalRootPlan();
        var grandchildrenPlan = BuildGrandchildrenCollectionPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "ThreeLevelTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                    grandchildrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan, grandchildrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan, grandchildrenPlan);
    }

    private static TableWritePlan BuildMinimalRootPlan()
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var rootTableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ThreeLevelTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_ThreeLevelTest",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
        return new TableWritePlan(
            TableModel: rootTableModel,
            InsertSql: "INSERT INTO edfi.\"ThreeLevelTest\" DEFAULT VALUES",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan BuildGrandchildrenCollectionPlan()
    {
        var grandchildItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("GrandchildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var identityColumn = new DbColumnModel(
            ColumnName: new DbColumnName("IdentityField0"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(
                "$.identityField0",
                [new JsonPathSegment.Property("identityField0")]
            ),
            TargetResource: null
        );
        DbColumnModel[] columns = [grandchildItemIdColumn, childItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "GrandchildrenTable"),
            JsonScope: new JsonPathExpression(GrandchildrenScope, []),
            Key: new TableKey(
                "PK_GrandchildrenTable",
                [new DbKeyColumn(new DbColumnName("GrandchildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("GrandchildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ChildItemId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        identityColumn.SourceJsonPath!.Value,
                        identityColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"GrandchildrenTable\" VALUES (@GrandchildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    grandchildItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "GrandchildItemId"
                ),
                new WriteColumnBinding(
                    childItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ChildItemId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    identityColumn,
                    new WriteValueSource.Scalar(
                        identityColumn.SourceJsonPath!.Value,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "IdentityField0"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(identityColumn.SourceJsonPath!.Value, 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"GrandchildrenTable\" SET X = @X WHERE \"GrandchildItemId\" = @GrandchildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"GrandchildrenTable\" WHERE \"GrandchildItemId\" = @GrandchildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("GrandchildItemId"),
                0
            )
        );
    }

    public static CollectionWriteCandidate BuildChildCandidate(
        TableWritePlan childrenPlan,
        string identityValue,
        int requestOrder,
        IEnumerable<CollectionWriteCandidate>? nestedGrandchildren = null
    )
    {
        var values = new FlattenedWriteValue[childrenPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        return new CollectionWriteCandidate(
            tableWritePlan: childrenPlan,
            ordinalPath: [0, requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue],
            collectionCandidates: nestedGrandchildren
        );
    }

    public static CollectionWriteCandidate BuildGrandchildCandidate(
        TableWritePlan grandchildrenPlan,
        string identityValue,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[grandchildrenPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        return new CollectionWriteCandidate(
            tableWritePlan: grandchildrenPlan,
            ordinalPath: [0, 0, requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue]
        );
    }

    private static ImmutableArray<SemanticIdentityPart> Identity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static ScopeInstanceAddress ChildRowAddress(
        string parentSemanticIdentity,
        string childSemanticIdentity
    ) =>
        new(
            ChildrenScope,
            [
                new AncestorCollectionInstance(ParentsScope, Identity(parentSemanticIdentity)),
                new AncestorCollectionInstance(ChildrenScope, Identity(childSemanticIdentity)),
            ]
        );

    public static VisibleRequestCollectionItem BuildGrandchildRequestItem(
        string parentSemanticIdentity,
        string childSemanticIdentity,
        string grandchildIdentity,
        int parentArrayIndex,
        int childArrayIndex,
        int grandchildArrayIndex,
        bool creatable = true
    ) =>
        new(
            new CollectionRowAddress(
                GrandchildrenScope,
                ChildRowAddress(parentSemanticIdentity, childSemanticIdentity),
                Identity(grandchildIdentity)
            ),
            creatable,
            $"$.parents[{parentArrayIndex}].children[{childArrayIndex}].grandchildren[{grandchildArrayIndex}]"
        );

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        TableWritePlan grandchildrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> childRows,
        IReadOnlyList<object?[]> grandchildRows
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [documentId],
                    ]
                ),
                new HydratedTableRows(parentsPlan.TableModel, parentRows),
                new HydratedTableRows(childrenPlan.TableModel, childRows),
                new HydratedTableRows(grandchildrenPlan.TableModel, grandchildRows),
            ],
            []
        );
}
