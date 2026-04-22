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
public class Given_Synthesizer_SeparateTable_Contract_Rejects_CollectionCandidates_On_RootRow
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
    public void It_throws_ArgumentException_about_root_level_collection_fence()
    {
        _act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*root-level collection candidates*later slice*");
    }
}

[TestFixture]
public class Given_Synthesizer_SeparateTable_Contract_Rejects_CollectionCandidates_Under_RootExtensionRow
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

        // Nested collection candidate under the extension row — must fail closed.
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
    public void It_throws_ArgumentException_about_nested_collection_fence()
    {
        _act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*collection candidates nested under root-extension*later slice*");
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
        // Request has no visible scope for $._ext.sample because the profile hides it.
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$")
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
/// where the profiled request only exercises root scopes — the executor's slice-fence
/// keeps the request from touching the collection-extension-scope table, so the
/// synthesizer must let it pass through untouched (the no-profile persister handles it).
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
        // are never referenced. Create-new path (no currentState, no profileAppliedContext).
        var body = new JsonObject { ["firstName"] = "Ada" };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$")
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
        // No request-side or stored-side scope for $._ext.sample at all.
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$")
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
/// Regression pin for the Task 5 fail-closed contract: a request with NO scope state for
/// the separate-table scope (null request) paired with a matched visible stored row is a
/// caller-contract violation (the request-view must classify every scope referenced by
/// the profile). Task 4's decider throws <see cref="InvalidOperationException"/> on this
/// tuple. The synthesizer's pre-decider skip predicate must NOT swallow this case — it
/// must route it to the decider so the contract violation surfaces instead of silently
/// preserving drifted profile state.
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
        // null-request case.
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
        // Matched visible stored row: storedRowExists must be true for the decider to see
        // the inconsistent (null request, VisiblePresent stored, row) tuple.
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
