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

/// <summary>
/// CP2 verifies the four per-merge indexes built at walker construction. WalkChildren
/// consumes them at dispatch time (Task 8 onward). This fixture confirms construction
/// populates each index correctly from the request inputs.
/// </summary>
[TestFixture]
public class Given_a_walker_constructed_for_a_top_level_base_collection
{
    private ProfileCollectionWalker _walker = null!;
    private DbTableName _collectionTable;
    private ImmutableArray<FlattenedWriteValue> _expectedParentValues;

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
                new JsonObject { ["identityField0"] = "V2" }
            ),
        };

        var candidateV1 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V1", 0);
        var candidateV2 = CollectionSynthesizerBuilders.BuildCandidate(collectionPlan, "V2", 1);

        var requestItems = ImmutableArray.Create(
            CollectionSynthesizerBuilders.BuildRequestItem("V1", creatable: true, arrayIndex: 0),
            CollectionSynthesizerBuilders.BuildRequestItem("V2", creatable: true, arrayIndex: 1)
        );

        var request = CollectionSynthesizerBuilders.BuildRequest(body, requestItems);
        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [candidateV1, candidateV2],
            documentId
        );

        // Two current collection rows under the same parent identity (documentId).
        // Layout matches MinimalCollectionTableWritePlan: [CollectionItemId, ParentDocumentId, Ordinal, IdentityField0].
        object?[] dbRowV1 = [10L, documentId, 1, "V1"];
        object?[] dbRowV2 = [20L, documentId, 2, "V2"];
        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId,
            [dbRowV1, dbRowV2]
        );

        // Profile context exists (existing-document path) but carries no visible stored
        // rows for this scope — the visible-stored index should be empty in this test.
        var context = CollectionSynthesizerBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        var resolvedReferenceLookups = EmptyResolvedReferenceLookups(plan);

        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(mergeRequest, resolvedReferenceLookups, tableStateBuilders);

        _collectionTable = collectionPlan.TableModel.Table;
        _expectedParentValues = [new FlattenedWriteValue.Literal(documentId)];
    }

    [Test]
    public void It_indexes_two_current_collection_rows_under_the_root_parent_identity()
    {
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        _walker
            .CurrentCollectionRowsByTableAndParentIdentity.Should()
            .ContainKey(
                key,
                "the index must surface a bucket for the collection table under the root parent"
            );

        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];
        bucket.Count.Should().Be(2);
        bucket[0].StoredOrdinal.Should().Be(1);
        bucket[1].StoredOrdinal.Should().Be(2);

        // Semantic identity values come from the IdentityField0 binding.
        bucket[0].SemanticIdentityInOrder.Length.Should().Be(1);
        bucket[0].SemanticIdentityInOrder[0].Value!.GetValue<string>().Should().Be("V1");
        bucket[1].SemanticIdentityInOrder[0].Value!.GetValue<string>().Should().Be("V2");
    }

    [Test]
    public void It_carries_the_stable_row_identity_for_each_indexed_row()
    {
        // Stable row identity is the CollectionItemId from the projected hydrated row —
        // populated at index-construction time so the projection is a strict superset of
        // CurrentCollectionRowSnapshot.
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];
        bucket[0].StableRowIdentity.Should().Be(10L);
        bucket[1].StableRowIdentity.Should().Be(20L);
    }

    [Test]
    public void It_carries_the_column_name_keyed_hydrated_row_for_each_indexed_row()
    {
        // CurrentRowByColumnName covers every column on the table model — consumed by the
        // matched-row overlay's hidden key-unification preservation. Confirm the dict is
        // populated and at least one binding-driven column is reachable by name.
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];
        bucket[0].CurrentRowByColumnName.Should().NotBeNull();
        bucket[0].CurrentRowByColumnName.Should().NotBeEmpty();
        bucket[1].CurrentRowByColumnName.Should().NotBeEmpty();
    }

    [Test]
    public void It_can_adapt_a_projection_to_the_planner_snapshot_shape()
    {
        // The walker consumes projections at the planner-input use site by adapting via
        // ToSnapshot(). Confirm the adapter populates each field of the snapshot from the
        // projection's superset.
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];

        var snapshot = bucket[0].ToSnapshot();
        snapshot.StableRowIdentity.Should().Be(bucket[0].StableRowIdentity);
        snapshot.SemanticIdentityInOrder.Should().Equal(bucket[0].SemanticIdentityInOrder);
        snapshot.StoredOrdinal.Should().Be(bucket[0].StoredOrdinal);
        snapshot.ProjectedCurrentRow.Should().BeSameAs(bucket[0].ProjectedRow);
        snapshot.CurrentRowByColumnName.Should().BeSameAs(bucket[0].CurrentRowByColumnName);
    }

    [Test]
    public void It_has_a_single_collection_row_index_entry_for_this_scope()
    {
        _walker.CurrentCollectionRowsByTableAndParentIdentity.Should().HaveCount(1);
    }

    [Test]
    public void It_has_no_separate_scope_rows_for_a_collection_only_plan()
    {
        _walker.CurrentSeparateScopeRowsByTableAndParentIdentity.Should().BeEmpty();
    }

    [Test]
    public void It_has_no_visible_stored_rows_when_profile_context_carries_none()
    {
        _walker.VisibleStoredRowsByChildScopeAndParent.Should().BeEmpty();
    }

    [Test]
    public void It_indexes_two_visible_request_items_under_the_root_address()
    {
        var rootAddress = new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty);
        var key = (CollectionSynthesizerBuilders.CollectionScope, rootAddress);
        _walker.VisibleRequestItemsByChildScopeAndParent.Should().ContainKey(key);
        _walker.VisibleRequestItemsByChildScopeAndParent[key].Length.Should().Be(2);
    }

    [Test]
    public void It_uses_structural_parent_key_equality_for_dictionary_lookup()
    {
        // Build a fresh ParentIdentityKey carrying the same values but a different array
        // instance, to confirm Equals/GetHashCode are structural rather than reference-based.
        var equivalentKey = new ParentIdentityKey([new FlattenedWriteValue.Literal(345L)]);
        var lookupKey = (_collectionTable, equivalentKey);

        _walker.CurrentCollectionRowsByTableAndParentIdentity.Should().ContainKey(lookupKey);
    }
}

/// <summary>
/// When the walker emits a matched parent row at <c>$.parents[*]</c>, it must recurse
/// <see cref="ProfileCollectionWalker.WalkChildren"/> with a nested-row context so direct
/// child collection scopes (here, <c>$.parents[*].children[*]</c>) get planned. This
/// fixture deliberately supplies no nested CollectionCandidates so the recursion path
/// can be observed in isolation: the walker still visits the nested children scope
/// under each matched parent, reads the children's current rows from the per-merge index
/// keyed by the parent's PhysicalRowIdentity, and the per-table builder aggregates the
/// children rows across recursion calls into ONE consolidated
/// <see cref="RelationalWriteMergedTableState"/> for the children table. Stored visible
/// rows under each parent are reverse-coverage-checked and (with no candidates available)
/// omitted from the merged sequence, producing 3 currentCollectionRows + 0 merged rows
/// on the consolidated children table state — the correct partitioning the persister
/// consumes for delete-by-absence.
/// </summary>
[TestFixture]
public class Given_two_top_level_collection_rows_each_with_a_nested_base_collection
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ParentBItemId = 200L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;
    private const long ChildB1ItemId = 2001L;

    private TableWritePlan _parentsPlan = null!;
    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _parentsPlan = parentsPlan;
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body shape: parent rows with no children-array attached. This fixture is
        // purposely scoped to "recursion + delete-by-absence partitioning"; richer
        // nested-merge scenarios are covered by their own fixtures. The request body still
        // references parents A and B at the top level.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject { ["identityField0"] = "A" },
                new JsonObject { ["identityField0"] = "B" }
            ),
        };

        // Top-level CollectionCandidates carry parents A, B with empty nested
        // CollectionCandidates. The candidate Values arrays follow the parents table
        // binding order: [ParentItemId, ParentDocumentId, Ordinal, IdentityField0].
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "A", 0);
        var candidateB = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "B", 1);

        // Request items: parents A, B only. The nested children scope has no visible
        // request items here because no nested CollectionCandidates are attached; the
        // planner's request-side coverage invariant requires every visible request item
        // to have a matching candidate, so including child request items without children
        // candidates would fail-closed. Keeping the structure scoped this way isolates the
        // recursion path so the structural behavior is observable.
        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildParentRequestItem("B", arrayIndex: 1)
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA, candidateB]);

        // VisibleStoredCollectionRows: parents A, B AND nested children A1, A2 (under A)
        // and B1 (under B). Each child row's address carries the parent's synthetic
        // ScopeInstanceAddress so the per-merge index keys lookup-by-parent correctly.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildParentStoredRow("B"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A1"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A2"),
            NestedTopologyBuilders.BuildChildStoredRow("B", "B1")
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
                [ParentBItemId, DocumentId, 2, "B"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2ItemId, ParentAItemId, 2, "A2"],
                [ChildB1ItemId, ParentBItemId, 1, "B1"],
            ]
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        // Drive the walker directly so the nested recursion is exercised in isolation
        // from the broader Synthesize pipeline (root-table merge, separate-table merge).
        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_populates_the_parents_table_builder_with_two_merged_rows()
    {
        // Two top-level matched-update entries (A, B) feed the parents builder, producing
        // two merged rows on the consolidated parents TableState.
        var parentsBuilder = _tableStateBuilders[_parentsPlan.TableModel.Table];
        parentsBuilder.HasContent.Should().BeTrue();
        var state = parentsBuilder.Build();
        state.MergedRows.Length.Should().Be(2);
    }

    [Test]
    public void It_aggregates_three_current_child_rows_into_a_single_children_table_state()
    {
        // The walker recurses once per matched parent row, but per the spec's
        // Section 3 "Table-state aggregation" requirement (and Task 9a's builder
        // refactor) all recursion calls feed the SAME per-table builder. The
        // synthesizer finalizes one consolidated children TableState carrying all
        // three current rows (A1, A2 under parent A; B1 under parent B). Without
        // Task 9's recursion code, no children rows would be in the builder at all.
        var childrenBuilder = _tableStateBuilders[_childrenPlan.TableModel.Table];
        childrenBuilder.HasContent.Should().BeTrue("the recursion must visit the children scope");
        var state = childrenBuilder.Build();
        state.CurrentRows.Length.Should().Be(3);
    }

    [Test]
    public void It_attaches_each_child_current_row_to_its_correct_parent_physical_identity()
    {
        // Across the consolidated children TableState, the three current rows must carry
        // the correct ParentItemId values: two rows under parent A (ParentItemId =
        // ParentAItemId) and one row under parent B (ParentItemId = ParentBItemId). The
        // recursion's per-(scope, parent-instance) dispatch reads the children currentRows
        // index by (childTable, parentPhysicalRowIdentity); cross-parent contamination or
        // mis-keyed lookups would surface here as wrong ParentItemId values or missing rows.
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("ParentItemId")
        );
        var state = _tableStateBuilders[_childrenPlan.TableModel.Table].Build();
        var parentItemIdValues = state
            .CurrentRows.Select(r =>
                r.Values[parentItemIdBindingIndex] is FlattenedWriteValue.Literal lit ? lit.Value : null
            )
            .OrderBy(v => v is null ? long.MaxValue : Convert.ToInt64(v))
            .ToList();
        parentItemIdValues
            .Should()
            .BeEquivalentTo(new object?[] { ParentAItemId, ParentAItemId, ParentBItemId });
    }
}

/// <summary>
/// CP3 Task 18: a collection row can own a collection-aligned 1:1 extension scope at
/// <c>$.parents[*]._ext.aligned</c>. The walker must treat that
/// <see cref="DbTableKind.CollectionExtensionScope"/> as a direct topological child of the
/// matched parent row and dispatch it through the shared separate-scope synthesis seam
/// with a structural address for the specific parent instance.
/// </summary>
[TestFixture]
public class Given_a_top_level_collection_with_a_collection_aligned_extension_scope
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ParentBItemId = 200L;

    private TableWritePlan _alignedPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;
    private readonly List<AlignedScopeInvocation> _calls = [];

    [SetUp]
    public void Setup()
    {
        _calls.Clear();

        var (plan, parentsPlan, alignedPlan) =
            AlignedExtensionScopeTopologyBuilders.BuildRootParentsAndAlignedScopePlan();
        _alignedPlan = alignedPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["_ext"] = new JsonObject { ["aligned"] = new JsonObject { ["favoriteColor"] = "Blue" } },
                },
                new JsonObject { ["identityField0"] = "B" }
            ),
        };

        var attachedAlignedScope = new CandidateAttachedAlignedScopeData(
            alignedPlan,
            [new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Blue")]
        );
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            attachedAlignedScopeData: [attachedAlignedScope]
        );
        var candidateB = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "B", 1);

        var request = AlignedExtensionScopeTopologyBuilders.BuildRequest(
            body,
            [
                NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
                NestedTopologyBuilders.BuildParentRequestItem("B", arrayIndex: 1),
            ],
            [
                AlignedExtensionScopeTopologyBuilders.BuildRequestScopeState(
                    "A",
                    ProfileVisibilityKind.VisiblePresent,
                    creatable: true
                ),
                AlignedExtensionScopeTopologyBuilders.BuildRequestScopeState(
                    "B",
                    ProfileVisibilityKind.Hidden,
                    creatable: false
                ),
            ]
        );

        var context = AlignedExtensionScopeTopologyBuilders.BuildContext(
            request,
            [
                NestedTopologyBuilders.BuildParentStoredRow("A"),
                NestedTopologyBuilders.BuildParentStoredRow("B"),
            ],
            [
                AlignedExtensionScopeTopologyBuilders.BuildStoredScopeState(
                    "A",
                    ProfileVisibilityKind.VisiblePresent
                ),
                AlignedExtensionScopeTopologyBuilders.BuildStoredScopeState(
                    "B",
                    ProfileVisibilityKind.Hidden
                ),
            ]
        );

        var currentState = AlignedExtensionScopeTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            alignedPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
                [ParentBItemId, DocumentId, 2, "B"],
            ],
            alignedRows:
            [
                [ParentAItemId, "Red"],
                [ParentBItemId, "Green"],
            ]
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: NestedTopologyBuilders.BuildFlattenedWriteSet(
                rootPlan,
                [candidateA, candidateB]
            ),
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            _tableStateBuilders,
            SynthesizeAlignedScope
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: mergeRequest.FlattenedWriteSet.RootRow,
            ParentRequestNode: body
        );

        var outcome = walker.WalkChildren(rootContext, WalkMode.Normal);

        outcome.Should().BeNull("the aligned scope dispatch completes without rejection");
    }

    [Test]
    public void It_dispatches_once_per_matched_parent_row_with_structural_scope_addresses()
    {
        _calls.Should().HaveCount(2);

        ScopeInstanceAddressComparer
            .ScopeInstanceAddressEquals(
                _calls[0].ScopeAddress,
                AlignedExtensionScopeTopologyBuilders.AlignedScopeAddress("A")
            )
            .Should()
            .BeTrue();
        ScopeInstanceAddressComparer
            .ScopeInstanceAddressEquals(
                _calls[1].ScopeAddress,
                AlignedExtensionScopeTopologyBuilders.AlignedScopeAddress("B")
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_passes_per_instance_scope_state_and_current_row_projection_to_the_dispatch()
    {
        _calls[0].RequestScope!.Visibility.Should().Be(ProfileVisibilityKind.VisiblePresent);
        _calls[0].StoredScope!.Visibility.Should().Be(ProfileVisibilityKind.VisiblePresent);
        _calls[0].CurrentRowProjection.Should().NotBeNull();
        _calls[0].ParentPhysicalIdentityValues.Should().Equal(new FlattenedWriteValue.Literal(ParentAItemId));

        _calls[1].RequestScope!.Visibility.Should().Be(ProfileVisibilityKind.Hidden);
        _calls[1].StoredScope!.Visibility.Should().Be(ProfileVisibilityKind.Hidden);
        _calls[1].CurrentRowProjection.Should().NotBeNull();
        _calls[1].ParentPhysicalIdentityValues.Should().Equal(new FlattenedWriteValue.Literal(ParentBItemId));
    }

    [Test]
    public void It_passes_attached_aligned_scope_buffer_to_the_dispatch()
    {
        _calls[0].Buffer.Should().NotBeNull();
        _calls[0].Buffer!.Value.TableWritePlan.Should().BeSameAs(_alignedPlan);
        _calls[0]
            .Buffer!.Value.Values.Should()
            .Equal(new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Blue"));

        _calls[1].Buffer.Should().BeNull();
    }

    [Test]
    public void It_resolves_the_scoped_request_node_from_the_parent_row_node()
    {
        _calls[0].ScopedRequestNode.Should().NotBeNull();
        _calls[0].ScopedRequestNode!["favoriteColor"]!.GetValue<string>().Should().Be("Blue");
        _calls[1].ScopedRequestNode.Should().BeNull();
    }

    [Test]
    public void It_aggregates_the_aligned_scope_table_state_returned_by_the_dispatch()
    {
        var state = _tableStateBuilders[_alignedPlan.TableModel.Table].Build();

        state.CurrentRows.Should().HaveCount(2);
        state.MergedRows.Should().HaveCount(2);

        var favoriteColorBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _alignedPlan,
            new DbColumnName("FavoriteColor")
        );
        state
            .MergedRows.Select(row =>
                row.Values[favoriteColorBindingIndex] is FlattenedWriteValue.Literal literal
                    ? literal.Value
                    : null
            )
            .Should()
            .Equal("Blue", "Green");
    }

    private SeparateScopeSynthesisResult SynthesizeAlignedScope(
        TableWritePlan tablePlan,
        ScopeInstanceAddress scopeAddress,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
        SeparateScopeBuffer? buffer,
        JsonNode? scopedRequestNode,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        CurrentSeparateScopeRowProjection? currentRowProjection
    )
    {
        _calls.Add(
            new AlignedScopeInvocation(
                tablePlan,
                scopeAddress,
                parentPhysicalIdentityValues,
                buffer,
                scopedRequestNode,
                requestScope,
                storedScope,
                currentRowProjection
            )
        );

        currentRowProjection.Should().NotBeNull();
        var currentRow = currentRowProjection!.ProjectedRow;

        if (requestScope is { Visibility: ProfileVisibilityKind.VisiblePresent })
        {
            var mergedValues = currentRow.Values.ToArray();
            mergedValues[1] = new FlattenedWriteValue.Literal("Blue");
            var mergedRow = new RelationalWriteMergedTableRow(
                mergedValues,
                RelationalWriteMergeSupport.ProjectComparableValues(tablePlan, mergedValues)
            );

            return SeparateScopeSynthesisResult.Table(
                ProfileSeparateTableMergeOutcome.Update,
                new RelationalWriteMergedTableState(tablePlan, [currentRow], [mergedRow])
            );
        }

        return SeparateScopeSynthesisResult.Table(
            ProfileSeparateTableMergeOutcome.Preserve,
            new RelationalWriteMergedTableState(tablePlan, [currentRow], [currentRow])
        );
    }

    private sealed record AlignedScopeInvocation(
        TableWritePlan TablePlan,
        ScopeInstanceAddress ScopeAddress,
        ImmutableArray<FlattenedWriteValue> ParentPhysicalIdentityValues,
        SeparateScopeBuffer? Buffer,
        JsonNode? ScopedRequestNode,
        RequestScopeState? RequestScope,
        StoredScopeState? StoredScope,
        CurrentSeparateScopeRowProjection? CurrentRowProjection
    );
}

/// <summary>
/// CP2 Task 10: when the walker hits a <c>HiddenPreserveEntry</c> for a top-level parent
/// row, it must (a) emit the hidden parent's clone-with-recomputed-ordinal merged row in
/// the parents scope (existing Slice 4 top-level behavior) and (b) recurse into the
/// hidden parent's descendants in <see cref="WalkMode.Preserve"/> so the entire hidden
/// subtree is echoed through verbatim — same projected values, stored ordinals preserved.
/// Preserve mode never invokes the planner: the contrast with Normal-mode hidden-row
/// handling under a <em>visible</em> parent (whose hidden child IS routed through the
/// planner and therefore IS re-ordinaled) is exactly what this fixture pins.
/// </summary>
[TestFixture]
public class Given_a_hidden_top_level_parent_with_nested_descendants
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ParentBItemId = 200L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;
    private const long ChildB1ItemId = 2001L;

    // B's child stored ordinal is intentionally non-1 so the Normal-mode-hidden
    // recomputation (HiddenPreserveEntry switch case in WalkChildren) is observable as
    // a distinct value from the stored ordinal.
    private const int ChildB1StoredOrdinal = 5;

    private TableWritePlan _parentsPlan = null!;
    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _parentsPlan = parentsPlan;
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: only parent B is in the request (parent A is hidden, so the
        // profile cannot expose it for write). No children are attached to B in the
        // request body — this fixture deliberately omits nested CollectionCandidates so
        // the Normal-mode HiddenPreserveEntry path for the nested child can be exercised
        // in isolation.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(new JsonObject { ["identityField0"] = "B" }),
        };

        // Top-level CollectionCandidate for B only — A is hidden and has no candidate.
        // B carries empty nested CollectionCandidates.
        var candidateB = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "B", 0);

        // Visible-request-items: B at $.parents[0] (creatable so an unmatched insert
        // would also be valid; the planner sees B as matched here because B is in
        // VisibleStoredCollectionRows).
        //
        // No visible-request-item for B's child: this fixture supplies no nested
        // candidate, and the planner's request-side coverage invariant requires every
        // visible request item to have a matching candidate. So B's child is represented
        // to the planner via current rows only — a hidden row in the nested children
        // scope, routed through the Normal-mode HiddenPreserveEntry switch case (which
        // DOES recompute the ordinal) for exactly the contrast this fixture pins.
        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("B", arrayIndex: 0)
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateB]);

        // VisibleStoredCollectionRows: parent B only. Parent A is hidden (omitted from
        // the visible-stored set) so the planner emits HiddenPreserveEntry for parent A.
        // The entire A subtree is hidden, so none of the A children appear in
        // visible-stored either — they reach the children-table builder only via
        // Preserve recursion under parent A.
        //
        // The B child is also omitted from visible-stored (no nested CollectionCandidate
        // for B means no profile-visible nested item). Under B in Normal mode the planner
        // sees the B child as a hidden current row and emits HiddenPreserveEntry —
        // recomputing its ordinal. That is the deliberate contrast with the A subtree.
        var storedRows = ImmutableArray.Create(NestedTopologyBuilders.BuildParentStoredRow("B"));

        var context = NestedTopologyBuilders.BuildContext(request, storedRows);
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
                [ParentBItemId, DocumentId, 2, "B"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2ItemId, ParentAItemId, 2, "A2"],
                [ChildB1ItemId, ParentBItemId, ChildB1StoredOrdinal, "B1"],
            ]
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_emits_one_table_state_for_parents_with_two_merged_rows()
    {
        // Parents scope: A (HiddenPreserve clone with recomputed ordinal 0) + B
        // (MatchedUpdate with recomputed ordinal 1). Both walk through the parents
        // builder via the Normal-mode planner path.
        var parentsBuilder = _tableStateBuilders[_parentsPlan.TableModel.Table];
        parentsBuilder.HasContent.Should().BeTrue();
        var state = parentsBuilder.Build();
        state.MergedRows.Length.Should().Be(2);
    }

    [Test]
    public void It_preserves_As_descendants_verbatim_with_stored_ordinals_unchanged()
    {
        // A's two children are echoed through Preserve recursion: same projected values,
        // stored ordinals (1, 2) unchanged. No re-ordinaling happens inside the hidden
        // subtree.
        var childrenState = _tableStateBuilders[_childrenPlan.TableModel.Table].Build();
        var ordinalBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("Ordinal")
        );
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("ParentItemId")
        );

        var aChildOrdinals = childrenState
            .MergedRows.Where(r =>
                r.Values[parentItemIdBindingIndex] is FlattenedWriteValue.Literal lit
                && lit.Value is not null
                && Convert.ToInt64(lit.Value) == ParentAItemId
            )
            .Select(r => Convert.ToInt32(((FlattenedWriteValue.Literal)r.Values[ordinalBindingIndex]).Value!))
            .OrderBy(o => o)
            .ToList();

        aChildOrdinals
            .Should()
            .Equal(
                [1, 2],
                "Preserve mode emits identity merged rows — stored ordinals on A's two "
                    + "children must reach the merged side unchanged"
            );
    }

    [Test]
    public void It_emits_Bs_visible_child_with_recomputed_ordinal()
    {
        // B's child reaches the children builder via Normal-mode recursion under B.
        // The planner sees the child as a hidden current row (no visible-stored entry),
        // emits HiddenPreserveEntry, and the walker's existing top-level switch case
        // recomputes the ordinal to finalOrdinal = 0 (the only entry in the sequence).
        // Stored ordinal was ChildB1StoredOrdinal (5) — recomputation must overwrite it.
        var childrenState = _tableStateBuilders[_childrenPlan.TableModel.Table].Build();
        var ordinalBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("Ordinal")
        );
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("ParentItemId")
        );

        var bChildMergedRow = childrenState.MergedRows.SingleOrDefault(r =>
            r.Values[parentItemIdBindingIndex] is FlattenedWriteValue.Literal lit
            && lit.Value is not null
            && Convert.ToInt64(lit.Value) == ParentBItemId
        );
        bChildMergedRow.Should().NotBeNull("the children scope under B must emit B's child");

        var ordinal = ((FlattenedWriteValue.Literal)bChildMergedRow!.Values[ordinalBindingIndex]).Value;
        Convert.ToInt32(ordinal).Should().Be(0, "Normal-mode hidden recomputation stamps finalOrdinal=0");
    }

    [Test]
    public void It_does_not_invoke_planner_for_descendants_under_the_hidden_parent()
    {
        // Practical assertion: A's two children appear in the children TableState
        // exactly as their stored projected rows — same StableRowIdentity (the
        // CollectionItemId), same Ordinal, same IdentityField0. If the planner had been
        // invoked on the A subtree, it would either (a) fail-closed because A's children
        // have no visible metadata at all (no VisibleRequestCollectionItem and no
        // VisibleStoredCollectionRow), or (b) pass through with empty inputs — in either
        // case the children rows would not be merged-row-emitted under A. Preserve mode
        // bypasses the planner entirely and emits identity rows.
        var childrenState = _tableStateBuilders[_childrenPlan.TableModel.Table].Build();
        var stableRowIdentityBindingIndex = _childrenPlan.CollectionMergePlan!.StableRowIdentityBindingIndex;
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("ParentItemId")
        );

        var aChildStableIds = childrenState
            .MergedRows.Where(r =>
                r.Values[parentItemIdBindingIndex] is FlattenedWriteValue.Literal lit
                && lit.Value is not null
                && Convert.ToInt64(lit.Value) == ParentAItemId
            )
            .Select(r =>
                Convert.ToInt64(((FlattenedWriteValue.Literal)r.Values[stableRowIdentityBindingIndex]).Value!)
            )
            .OrderBy(id => id)
            .ToList();

        aChildStableIds
            .Should()
            .Equal(
                [ChildA1ItemId, ChildA2ItemId],
                "Preserve mode emits each of A's children verbatim — both stored "
                    + "CollectionItemIds reach the merged side unchanged"
            );
    }
}

/// <summary>
/// CP2 Task 11: pins per-(scope, parent-instance) partitioning of the four canonicalize
/// helpers' inputs. The walker's Normal-mode body invokes
/// <c>CanonicalizeDocumentReferenceRequestItems</c>,
/// <c>CanonicalizeDescriptorRequestItems</c>,
/// <c>CanonicalizeDocumentReferenceStoredRows</c>, and
/// <c>CanonicalizeDescriptorStoredRows</c> inside the per-table iteration in
/// <see cref="ProfileCollectionWalker.WalkChildren"/>; their <c>visibleStoredRowsForScope</c>,
/// <c>visibleRequestItemsForScope</c>, and <c>currentRowsForScope</c> arguments are looked up
/// from the per-merge indexes by <c>(jsonScope, parentScopeAddress)</c> /
/// <c>(table, parentIdentityKey)</c>. The descriptor URI cache fence shape (URI absent +
/// scalar ambiguous + count divergent) computes count divergence from the per-partition
/// stored-versus-current row counts — fences fire on the offending partition only.
/// <para>
/// This fixture proves the partitioning shape structurally: two parents A and B with
/// disjoint nested-children sets must produce two independent index buckets keyed by each
/// parent's <see cref="ScopeInstanceAddress"/>, and each bucket must contain only that
/// parent's children. Cross-pollination between siblings is the bug class this test
/// guards against. The descriptor-URI fence-throw scenario is covered at the helper level
/// by Slice 4's existing
/// <c>RelationalWriteProfileMergeSynthesizer.CanonicalizeDescriptorStoredRows</c> tests
/// (see <c>RelationalWriteProfileMergeSynthesizerTests.cs</c>); the partitioning pinned
/// here is the structural prerequisite that lets those helper-level fences fire on the
/// offending partition without affecting siblings.
/// </para>
/// </summary>
[TestFixture]
public class Given_two_parent_instances_with_per_partition_canonicalization_inputs
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ParentBItemId = 200L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;
    private const long ChildB1ItemId = 2001L;

    private TableWritePlan _childrenPlan = null!;
    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject { ["identityField0"] = "A" },
                new JsonObject { ["identityField0"] = "B" }
            ),
        };

        var candidateA = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "A", 0);
        var candidateB = NestedTopologyBuilders.BuildParentCandidate(parentsPlan, "B", 1);

        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildParentRequestItem("B", arrayIndex: 1)
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA, candidateB]);

        // Two distinct nested-children sets keyed by parent: A1, A2 under A; B1 under B.
        // The per-merge visible-stored index must split these into separate buckets keyed
        // by the parent's ScopeInstanceAddress.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildParentStoredRow("B"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A1"),
            NestedTopologyBuilders.BuildChildStoredRow("A", "A2"),
            NestedTopologyBuilders.BuildChildStoredRow("B", "B1")
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
                [ParentBItemId, DocumentId, 2, "B"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2ItemId, ParentAItemId, 2, "A2"],
                [ChildB1ItemId, ParentBItemId, 1, "B1"],
            ]
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            tableStateBuilders
        );
    }

    [Test]
    public void It_indexes_visible_stored_children_under_parent_A_address_separately_from_parent_B()
    {
        // The walker's WalkChildren reads visibleStoredRowsForScope by
        // (childJsonScope, parentScopeAddress) inside its per-table loop. For the children
        // scope under two distinct parents, each parent's address must produce its own
        // bucket; the canonicalize call site receives only that partition's stored rows.
        var parentAAddress = ParentRowAddress("A");
        var parentBAddress = ParentRowAddress("B");

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey((NestedTopologyBuilders.ChildrenScope, parentAAddress));
        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey((NestedTopologyBuilders.ChildrenScope, parentBAddress));
    }

    [Test]
    public void It_does_not_pollute_parent_As_visible_stored_bucket_with_parent_Bs_children()
    {
        // The children of parent A and parent B are partitioned: a per-partition canonicalize
        // call under parent A must see ONLY A1 and A2 — never B1. If the visible-stored
        // index leaked B's children into A's bucket, canonicalize would receive a union
        // (a regression of the per-scope-not-per-partition bug this task guards against).
        var parentAAddress = ParentRowAddress("A");
        var aBucket = _walker.VisibleStoredRowsByChildScopeAndParent[
            (NestedTopologyBuilders.ChildrenScope, parentAAddress)
        ];
        aBucket.Length.Should().Be(2, "parent A has exactly two nested children (A1, A2)");
        var aIdentities = aBucket
            .Select(r => r.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>())
            .OrderBy(s => s)
            .ToList();
        aIdentities.Should().Equal(["A1", "A2"], "parent A's bucket must contain only A's children");
    }

    [Test]
    public void It_does_not_pollute_parent_Bs_visible_stored_bucket_with_parent_As_children()
    {
        var parentBAddress = ParentRowAddress("B");
        var bBucket = _walker.VisibleStoredRowsByChildScopeAndParent[
            (NestedTopologyBuilders.ChildrenScope, parentBAddress)
        ];
        bBucket.Length.Should().Be(1, "parent B has exactly one nested child (B1)");
        bBucket[0].Address.SemanticIdentityInOrder[0].Value!.GetValue<string>().Should().Be("B1");
    }

    [Test]
    public void It_indexes_current_children_under_parent_A_separately_from_parent_B()
    {
        // The walker's WalkChildren reads currentRowsForScope by
        // (childTable, parentIdentityKey) — the parent identity key is built from the
        // parent's PhysicalRowIdentity values. For the children table, parent A's bucket
        // (keyed by ParentItemId = ParentAItemId) and parent B's bucket (keyed by
        // ParentItemId = ParentBItemId) must be independent — feeding the canonicalize
        // helpers' currentRows argument per-partition.
        var keyForA = (
            _childrenPlan.TableModel.Table,
            new ParentIdentityKey([new FlattenedWriteValue.Literal(ParentAItemId)])
        );
        var keyForB = (
            _childrenPlan.TableModel.Table,
            new ParentIdentityKey([new FlattenedWriteValue.Literal(ParentBItemId)])
        );

        _walker.CurrentCollectionRowsByTableAndParentIdentity.Should().ContainKey(keyForA);
        _walker.CurrentCollectionRowsByTableAndParentIdentity.Should().ContainKey(keyForB);

        var aRows = _walker.CurrentCollectionRowsByTableAndParentIdentity[keyForA];
        var bRows = _walker.CurrentCollectionRowsByTableAndParentIdentity[keyForB];

        aRows.Count.Should().Be(2, "parent A has two current children");
        bRows.Count.Should().Be(1, "parent B has one current child");
    }

    [Test]
    public void It_count_divergence_for_canonicalization_fences_is_computed_per_partition()
    {
        // The Slice 4 URI cache fence shape — URI absent + scalar ambiguous + count
        // divergent — uses the per-partition stored count (visibleStoredRowsForScope.Length)
        // and the per-partition current count (currentRowsForScope.Length) as inputs to the
        // descriptor cache-miss fallback (see CanonicalizeDescriptorStoredRows /
        // CanonicalizeDocumentReferenceStoredRows). For two parents whose partitions diverge
        // independently (e.g., A: stored=2 / current=2 in this fixture; B: stored=1 /
        // current=1), the fence's count-divergent test fires on the offending partition
        // alone — never across the union. Pin the per-partition counts that drive that
        // arithmetic.
        var parentAAddress = ParentRowAddress("A");
        var parentBAddress = ParentRowAddress("B");

        var aStored = _walker.VisibleStoredRowsByChildScopeAndParent[
            (NestedTopologyBuilders.ChildrenScope, parentAAddress)
        ];
        var bStored = _walker.VisibleStoredRowsByChildScopeAndParent[
            (NestedTopologyBuilders.ChildrenScope, parentBAddress)
        ];

        var aCurrent = _walker.CurrentCollectionRowsByTableAndParentIdentity[
            (
                _childrenPlan.TableModel.Table,
                new ParentIdentityKey([new FlattenedWriteValue.Literal(ParentAItemId)])
            )
        ];
        var bCurrent = _walker.CurrentCollectionRowsByTableAndParentIdentity[
            (
                _childrenPlan.TableModel.Table,
                new ParentIdentityKey([new FlattenedWriteValue.Literal(ParentBItemId)])
            )
        ];

        // A's count-divergence input: stored=2, current=2 — equal (no divergence on A).
        aStored.Length.Should().Be(2);
        aCurrent.Count.Should().Be(2);

        // B's count-divergence input: stored=1, current=1 — independent of A. If the walker
        // computed count divergence from a scope-wide union (3 stored vs. 3 current across
        // both parents), the per-partition fence shape would be unobservable here. Each
        // partition's pair must be an isolated input to the helpers' arithmetic.
        bStored.Length.Should().Be(1);
        bCurrent.Count.Should().Be(1);
    }

    private static ImmutableArray<SemanticIdentityPart> Identity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static ScopeInstanceAddress ParentRowAddress(string parentSemanticIdentity) =>
        new(
            NestedTopologyBuilders.ParentsScope,
            [
                new AncestorCollectionInstance(
                    NestedTopologyBuilders.ParentsScope,
                    Identity(parentSemanticIdentity)
                ),
            ]
        );
}

/// <summary>
/// Local builders for a 3-table nested-topology test plan: a minimal root, a top-level
/// parents collection at <c>$.parents[*]</c>, and a nested children collection at
/// <c>$.parents[*].children[*]</c>. The children's <c>ParentItemId</c> binding is a
/// <see cref="WriteValueSource.ParentKeyPart"/> referring to slot 0 of the parent's
/// PhysicalRowIdentity (the parent's CollectionItemId).
/// </summary>
internal static class AlignedExtensionScopeTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("sample");
    public const string AlignedScope = "$.parents[*]._ext.aligned";

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan AlignedPlan
    ) BuildRootParentsAndAlignedScopePlan()
    {
        var (nestedPlan, parentsPlan, _) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = nestedPlan.TablePlansInDependencyOrder[0];
        var alignedPlan = BuildAlignedScopePlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "AlignedTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    alignedPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, alignedPlan]
        );

        return (resourceWritePlan, parentsPlan, alignedPlan);
    }

    public static ScopeInstanceAddress AlignedScopeAddress(string parentSemanticIdentity) =>
        new(
            AlignedScope,
            NestedTopologyBuilders.ParentRowAddress(parentSemanticIdentity).AncestorCollectionInstances
        );

    public static RequestScopeState BuildRequestScopeState(
        string parentSemanticIdentity,
        ProfileVisibilityKind visibility,
        bool creatable
    ) => new(AlignedScopeAddress(parentSemanticIdentity), visibility, creatable);

    public static StoredScopeState BuildStoredScopeState(
        string parentSemanticIdentity,
        ProfileVisibilityKind visibility
    ) => new(AlignedScopeAddress(parentSemanticIdentity), visibility, ImmutableArray<string>.Empty);

    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems,
        ImmutableArray<RequestScopeState> alignedScopeStates
    ) =>
        new(
            writableBody,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
                .. alignedScopeStates,
            ],
            visibleItems
        );

    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows,
        ImmutableArray<StoredScopeState> alignedScopeStates
    ) =>
        new(
            request,
            new JsonObject(),
            [
                new StoredScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    ImmutableArray<string>.Empty
                ),
                .. alignedScopeStates,
            ],
            storedRows
        );

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan alignedPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> alignedRows
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"),
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
                new HydratedTableRows(alignedPlan.TableModel, alignedRows),
            ],
            []
        );

    private static TableWritePlan BuildAlignedScopePlan()
    {
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var favoriteColorColumn = new DbColumnModel(
            ColumnName: new DbColumnName("FavoriteColor"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: true,
            SourceJsonPath: new JsonPathExpression(
                "$.favoriteColor",
                [new JsonPathSegment.Property("favoriteColor")]
            ),
            TargetResource: null
        );

        DbColumnModel[] columns = [parentItemIdColumn, favoriteColorColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ParentsAlignedExtension"),
            JsonScope: new JsonPathExpression(AlignedScope, []),
            Key: new TableKey(
                "PK_ParentsAlignedExtension",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.CollectionExtensionScope,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentItemId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO sample.\"ParentsAlignedExtension\" VALUES (@ParentItemId, @FavoriteColor)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    favoriteColorColumn,
                    new WriteValueSource.Scalar(
                        favoriteColorColumn.SourceJsonPath!.Value,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );
    }
}

/// <summary>
/// Slice 5 regression: when a child collection's immediate JSON parent is an inlined
/// non-collection scope (here <c>$.parents[*].detail</c>) with no backing table plan, the
/// walker must still dispatch the child collection from the nearest table-backed ancestor
/// (<c>$.parents[*]</c>) using an effective parent address whose JsonScope is the inlined
/// scope. Without that, <c>EnumerateDirectChildCollectionScopes</c>'s string-direct-child
/// check rejects the child path and the children scope is never visited; the per-(scope,
/// parent-instance) index lookup also misses because Core-emitted addresses carry the
/// inlined parent scope. This fixture proves both routes work end-to-end: matched-update
/// rows survive across two parent partitions and FK-rewrite stamps the parent's
/// PhysicalRowIdentity into each child's parent-key slot.
/// </summary>
[TestFixture]
public class Given_two_top_level_collection_rows_each_with_an_inlined_parent_nested_collection
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ParentBItemId = 200L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildB1ItemId = 2001L;

    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;
    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            NestedTopologyBuilders.BuildRootParentsAndDetailChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // parentsPlan is consumed below via NestedTopologyBuilders.BuildParentCandidate /
        // BuildCurrentState; assigning to a local keeps the deconstruction shape clear.

        // Request body: two parents, each carrying one nested child reachable through the
        // inlined `detail` non-collection scope.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["detail"] = new JsonObject
                    {
                        ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "A1" }),
                    },
                },
                new JsonObject
                {
                    ["identityField0"] = "B",
                    ["detail"] = new JsonObject
                    {
                        ["children"] = new JsonArray(new JsonObject { ["identityField0"] = "B1" }),
                    },
                }
            ),
        };

        var childA1 = NestedTopologyBuilders.BuildDetailChildCandidate(
            childrenPlan,
            "A1",
            parentArrayIndex: 0,
            requestOrder: 0
        );
        var childB1 = NestedTopologyBuilders.BuildDetailChildCandidate(
            childrenPlan,
            "B1",
            parentArrayIndex: 1,
            requestOrder: 0
        );
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            nestedChildren: [childA1]
        );
        var candidateB = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "B",
            1,
            nestedChildren: [childB1]
        );

        var requestItems = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
            NestedTopologyBuilders.BuildParentRequestItem("B", arrayIndex: 1),
            NestedTopologyBuilders.BuildDetailChildRequestItem(
                "A",
                "A1",
                parentArrayIndex: 0,
                childArrayIndex: 0
            ),
            NestedTopologyBuilders.BuildDetailChildRequestItem(
                "B",
                "B1",
                parentArrayIndex: 1,
                childArrayIndex: 0
            )
        );

        var request = NestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidateA, candidateB]);

        // Stored: parents A, B; children A1 under A and B1 under B. Both children carry
        // ParentAddress with the inlined `$.parents[*].detail` JsonScope so the visible-
        // stored index keys it under the inlined parent address.
        var storedRows = ImmutableArray.Create(
            NestedTopologyBuilders.BuildParentStoredRow("A"),
            NestedTopologyBuilders.BuildParentStoredRow("B"),
            NestedTopologyBuilders.BuildDetailChildStoredRow("A", "A1"),
            NestedTopologyBuilders.BuildDetailChildStoredRow("B", "B1")
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
                [ParentBItemId, DocumentId, 2, "B"],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildB1ItemId, ParentBItemId, 1, "B1"],
            ]
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the inlined-parent-collection walk completes without rejection");
    }

    [Test]
    public void It_partitions_current_rows_under_the_effective_inlined_parent_address_per_parent_instance()
    {
        // Ancestor descriptor / document-reference canonicalization on descendants of the
        // inlined-parent children scope reads currentRowsByJsonScopeAndParent keyed by
        // (childJsonScope, effective parent ScopeInstanceAddress). Without the inlined-
        // parent fix in ResolveParentContainingScopeAddress, this partition is empty for
        // inlined-parent children — the resolver returns null because $.parents[*].detail
        // has no backing table plan — and any descendant ancestor canonicalization that
        // requires a per-parent partition fails closed.
        var partitionA = (
            JsonScope: NestedTopologyBuilders.DetailChildrenScope,
            ParentAddress: NestedTopologyBuilders.DetailParentAddress("A")
        );
        var partitionB = (
            JsonScope: NestedTopologyBuilders.DetailChildrenScope,
            ParentAddress: NestedTopologyBuilders.DetailParentAddress("B")
        );

        _walker
            .CurrentRowsByJsonScopeAndParent.Should()
            .ContainKey(
                partitionA,
                "parent A's child A1 must live in a partition keyed by the inlined-detail address for A"
            );
        _walker
            .CurrentRowsByJsonScopeAndParent.Should()
            .ContainKey(
                partitionB,
                "parent B's child B1 must live in a partition keyed by the inlined-detail address for B"
            );

        _walker.CurrentRowsByJsonScopeAndParent[partitionA].Length.Should().Be(1);
        _walker.CurrentRowsByJsonScopeAndParent[partitionB].Length.Should().Be(1);

        // Cross-parent isolation: each partition's lone child row must carry the correct
        // ParentItemId (parent A's CollectionItemId for A1; parent B's for B1). A wrong-
        // parent partition assignment would surface as the wrong stable ID here.
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("ParentItemId")
        );
        _walker
            .CurrentRowsByJsonScopeAndParent[partitionA][0]
            .ProjectedCurrentRow.Values[parentItemIdBindingIndex]
            .Should()
            .Be(new FlattenedWriteValue.Literal(ParentAItemId));
        _walker
            .CurrentRowsByJsonScopeAndParent[partitionB][0]
            .ProjectedCurrentRow.Values[parentItemIdBindingIndex]
            .Should()
            .Be(new FlattenedWriteValue.Literal(ParentBItemId));
    }

    [Test]
    public void It_aggregates_two_merged_child_rows_into_a_single_children_table_state()
    {
        // Both children are matched-update entries (each parent's child has a stored row
        // and a request candidate), so the children table builder must carry two merged
        // rows. Without the inlined-parent fix, the walker never visits the children scope
        // and the builder is empty.
        var childrenBuilder = _tableStateBuilders[_childrenPlan.TableModel.Table];
        childrenBuilder
            .HasContent.Should()
            .BeTrue("the recursion must visit the inlined-parent children scope");
        var state = childrenBuilder.Build();
        state.MergedRows.Length.Should().Be(2);
        state.CurrentRows.Length.Should().Be(2);
    }

    [Test]
    public void It_rewrites_each_merged_child_row_parent_key_to_the_owning_parent_physical_identity()
    {
        // Cross-parent FK isolation: child A1's ParentItemId must equal parent A's
        // PhysicalRowIdentity, and child B1's ParentItemId must equal parent B's. A
        // wrong-parent dispatch (e.g. dispatching both children under the same parent
        // address) would surface here as duplicate or swapped FK values.
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("ParentItemId")
        );
        var identityBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _childrenPlan,
            new DbColumnName("IdentityField0")
        );
        var state = _tableStateBuilders[_childrenPlan.TableModel.Table].Build();
        var pairs = state
            .MergedRows.Select(r =>
                (
                    Identity: r.Values[identityBindingIndex] is FlattenedWriteValue.Literal il
                        ? il.Value
                        : null,
                    ParentItemId: r.Values[parentItemIdBindingIndex] is FlattenedWriteValue.Literal pl
                        ? pl.Value
                        : null
                )
            )
            .OrderBy(p => p.Identity as string)
            .ToList();
        pairs
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Identity: (object?)"A1", ParentItemId: (object?)ParentAItemId),
                    (Identity: (object?)"B1", ParentItemId: (object?)ParentBItemId),
                }
            );
    }
}

/// <summary>
/// Slice 5 follow-up: the inlined-parent partition fix in
/// <c>ResolveInlinedParentContainingScopeAddress</c> must also handle the case where the
/// nearest table-backed ancestor of an inlined-parent collection is a
/// <see cref="DbTableKind.RootExtension"/> scope. <c>addressByTableAndStableId</c> only
/// registers <see cref="DbTableKind.Collection"/> /
/// <see cref="DbTableKind.ExtensionCollection"/> rows, so a collection at
/// <c>$._ext.sample.detail.children[*]</c> would silently lose its
/// <c>currentRowsByJsonScopeAndParent</c> partition without a kind-aware fallback —
/// ancestor descriptor / document-reference canonicalization on any descendant of these
/// children would then fail closed.
/// </summary>
[TestFixture]
public class Given_an_inlined_parent_collection_under_a_root_extension_scope
{
    [Test]
    public void It_partitions_current_rows_under_the_inlined_detail_address_with_an_empty_ancestor_chain()
    {
        var (plan, _, childrenPlan) = RootExtensionInlinedDetailTopologyBuilders.Build();
        const long documentId = 345L;
        const long childItemId = 9001L;

        // Empty body: the partition we care about lives in the per-merge index, which is
        // built at construction time from current state. The walk itself need not run.
        var body = new JsonObject { ["_ext"] = new JsonObject { ["sample"] = new JsonObject() } };
        var request = NestedTopologyBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );
        var context = NestedTopologyBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var rootExtensionPlan = plan.TablePlansInDependencyOrder[1];
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                ContentVersion: 1L,
                IdentityVersion: 1L,
                ContentLastModifiedAt: new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero),
                IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [documentId],
                    ]
                ),
                new HydratedTableRows(
                    rootExtensionPlan.TableModel,
                    [
                        [documentId],
                    ]
                ),
                new HydratedTableRows(
                    childrenPlan.TableModel,
                    [
                        [childItemId, documentId, 1, "C1"],
                    ]
                ),
            ],
            []
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: NestedTopologyBuilders.BuildFlattenedWriteSet(
                rootPlan,
                ImmutableArray<CollectionWriteCandidate>.Empty
            ),
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            tableStateBuilders
        );

        // The expected partition key carries the inlined `detail` JsonScope and an empty
        // ancestor chain — the root extension is 1:1 with the document, so the inlined-
        // detail scope inherits its empty chain (mirroring the existing RootExtension
        // direct-parent branch in ResolveParentContainingScopeAddress).
        var partition = (
            JsonScope: RootExtensionInlinedDetailTopologyBuilders.ChildrenScope,
            ParentAddress: new ScopeInstanceAddress(
                RootExtensionInlinedDetailTopologyBuilders.DetailScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            )
        );

        walker
            .CurrentRowsByJsonScopeAndParent.Should()
            .ContainKey(
                partition,
                "the children under inlined detail below a RootExtension must register a partition entry so descendant ancestor canonicalization has a parent-instance bucket to look up"
            );
        walker.CurrentRowsByJsonScopeAndParent[partition].Length.Should().Be(1);
    }
}

internal static class RootExtensionInlinedDetailTopologyBuilders
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _sampleSchema = new("sample");
    public const string RootExtensionScope = "$._ext.sample";
    public const string DetailScope = "$._ext.sample.detail";
    public const string ChildrenScope = "$._ext.sample.detail.children[*]";

    public static (
        ResourceWritePlan Plan,
        TableWritePlan RootExtensionPlan,
        TableWritePlan ChildrenPlan
    ) Build()
    {
        var rootPlan = BuildRootPlan();
        var rootExtensionPlan = BuildRootExtensionPlan();
        var childrenPlan = BuildChildrenPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "RootExtensionInlinedDetailTest"),
                PhysicalSchema: _edfiSchema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    rootExtensionPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, rootExtensionPlan, childrenPlan]
        );
        return (resourceWritePlan, rootExtensionPlan, childrenPlan);
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
        var rootTableModel = new DbTableModel(
            Table: new DbTableName(_edfiSchema, "RootExtensionInlinedDetailTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_RootExtensionInlinedDetailTest",
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
            InsertSql: "INSERT INTO edfi.\"RootExtensionInlinedDetailTest\" DEFAULT VALUES",
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

    private static TableWritePlan BuildRootExtensionPlan()
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
            Table: new DbTableName(_sampleSchema, "RootExtensionInlinedDetailTestExtension"),
            JsonScope: new JsonPathExpression(
                RootExtensionScope,
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
            Key: new TableKey(
                "PK_RootExtensionInlinedDetailTestExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };
        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO sample.\"RootExtensionInlinedDetailTestExtension\" VALUES (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: "DELETE FROM sample.\"RootExtensionInlinedDetailTestExtension\" WHERE \"DocumentId\" = @DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.ParentKeyPart(0), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan BuildChildrenPlan()
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
            Table: new DbTableName(_sampleSchema, "RootExtensionInlinedDetailTestChildren"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_RootExtensionInlinedDetailTestChildren",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
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
            InsertSql: "INSERT INTO sample.\"RootExtensionInlinedDetailTestChildren\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(documentIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
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
                UpdateByStableRowIdentitySql: "UPDATE sample.\"RootExtensionInlinedDetailTestChildren\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM sample.\"RootExtensionInlinedDetailTestChildren\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }
}

internal static class NestedTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");
    public const string ParentsScope = "$.parents[*]";
    public const string ChildrenScope = "$.parents[*].children[*]";
    public const string DetailScope = "$.parents[*].detail";
    public const string DetailChildrenScope = "$.parents[*].detail.children[*]";

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndChildrenPlan()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildParentsCollectionPlan();
        var childrenPlan = BuildChildrenCollectionPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "NestedTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
    }

    /// <summary>
    /// Variant of <see cref="BuildRootParentsAndChildrenPlan"/> whose children plan carries
    /// an extra scalar column (<c>ScalarField1</c> bound at <c>$.scalarField1</c>) in
    /// addition to the identity scalar. Used by hidden-member-path fixtures to mark the
    /// extra scalar as hidden on a stored row, asserting that the matched-row overlay
    /// preserves the stored value at the hidden path while overlaying request values at
    /// visible paths.
    /// </summary>
    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndChildrenPlanWithExtraChildScalar()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildParentsCollectionPlan();
        var childrenPlan = BuildChildrenCollectionPlanWithExtraScalar();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "NestedTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
    }

    /// <summary>
    /// Variant of <see cref="BuildRootParentsAndChildrenPlan"/> whose children collection
    /// hangs off an inlined non-collection scope <c>$.parents[*].detail</c> rather than
    /// directly off the parent collection scope. The <c>detail</c> object has no backing
    /// table plan, so this fixture exercises the inlined-parent-collection traversal path
    /// where the nearest table-backed ancestor of <c>$.parents[*].detail.children[*]</c>
    /// is the parents collection at <c>$.parents[*]</c>.
    /// </summary>
    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndDetailChildrenPlan()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildParentsCollectionPlan();
        var childrenPlan = BuildDetailChildrenCollectionPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "NestedTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
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
            Table: new DbTableName(_schema, "NestedTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_NestedTest",
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
            InsertSql: "INSERT INTO edfi.\"NestedTest\" DEFAULT VALUES",
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

    private static TableWritePlan BuildParentsCollectionPlan()
    {
        // Layout: [ParentItemId, ParentDocumentId, Ordinal, IdentityField0].
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentDocIdColumn = new DbColumnModel(
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
        DbColumnModel[] columns = [parentItemIdColumn, parentDocIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ParentsTable"),
            JsonScope: new JsonPathExpression(ParentsScope, []),
            Key: new TableKey(
                "PK_ParentsTable",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
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
            InsertSql: "INSERT INTO edfi.\"ParentsTable\" VALUES (@ParentItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    parentDocIdColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ParentsTable\" SET X = @X WHERE \"ParentItemId\" = @ParentItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ParentsTable\" WHERE \"ParentItemId\" = @ParentItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ParentItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildChildrenCollectionPlan()
    {
        // Layout: [ChildItemId, ParentItemId, Ordinal, IdentityField0]. ParentItemId is a
        // ParentKeyPart binding referring to slot 0 of the parents table's
        // PhysicalRowIdentity (i.e., the parent's ParentItemId column).
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                // ParentKeyPart(0) → slot 0 of the parents table's PhysicalRowIdentity.
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildChildrenCollectionPlanWithExtraScalar()
    {
        // Layout: [ChildItemId, ParentItemId, Ordinal, IdentityField0, ScalarField1].
        // ScalarField1 (visible binding when not hidden) lets the matched-row overlay
        // exercise hidden-member-path preservation for a non-identity scalar. The plan is
        // otherwise identical to BuildChildrenCollectionPlan() so callers may swap planners
        // with no other arrangement change.
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        var scalarColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ScalarField1"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: true,
            SourceJsonPath: new JsonPathExpression(
                "$.scalarField1",
                [new JsonPathSegment.Property("scalarField1")]
            ),
            TargetResource: null
        );
        DbColumnModel[] columns =
        [
            childItemIdColumn,
            parentItemIdColumn,
            ordinalColumn,
            identityColumn,
            scalarColumn,
        ];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                new WriteColumnBinding(
                    scalarColumn,
                    new WriteValueSource.Scalar(
                        scalarColumn.SourceJsonPath!.Value,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "ScalarField1"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(identityColumn.SourceJsonPath!.Value, 3),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildDetailChildrenCollectionPlan()
    {
        // Same column layout/binding as BuildChildrenCollectionPlan, but the table's
        // JsonScope places the array under an inlined `detail` property of the parent
        // collection row: $.parents[*].detail.children[*]. The `detail` scope is NOT
        // table-backed (no plan in the write plan), so traversal must reach the children
        // table from the nearest table-backed ancestor ($.parents[*]) through the inlined
        // intermediate scope.
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(DetailChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    public static CollectionWriteCandidate BuildParentCandidate(
        TableWritePlan parentsPlan,
        string identityValue,
        int requestOrder,
        IEnumerable<CollectionWriteCandidate>? nestedChildren = null,
        IEnumerable<CandidateAttachedAlignedScopeData>? attachedAlignedScopeData = null
    )
    {
        var values = new FlattenedWriteValue[parentsPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        // Stamp the identity field at index 3.
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        // Note for Slice 5 CP2 Task 12 - synthesizer-level fixtures may now attach realized
        // child candidates via the optional nestedChildren parameter, after the constructor
        // fence on nested CollectionCandidates was retired.
        return new CollectionWriteCandidate(
            tableWritePlan: parentsPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue],
            attachedAlignedScopeData: attachedAlignedScopeData,
            collectionCandidates: nestedChildren,
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                parentsPlan,
                [identityValue]
            )
        );
    }

    /// <summary>
    /// Build a nested-children CollectionWriteCandidate that hangs under a parent at
    /// <see cref="ChildrenScope"/>. Layout matches BuildChildrenCollectionPlan:
    /// [ChildItemId, ParentItemId, Ordinal, IdentityField0, (optional) ScalarField1]. The
    /// ParentItemId binding is resolved by the walker via
    /// <see cref="WriteValueSource.ParentKeyPart"/> at emission time, so we leave that slot
    /// as a null literal here. <paramref name="scalarFieldValue"/> stamps the optional
    /// ScalarField1 column on the extended-children-plan variant; ignored when the plan
    /// only carries the identity scalar.
    /// </summary>
    public static CollectionWriteCandidate BuildChildCandidate(
        TableWritePlan childrenPlan,
        string identityValue,
        int requestOrder,
        object? scalarFieldValue = null
    )
    {
        var values = new FlattenedWriteValue[childrenPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(identityValue);
        if (childrenPlan.ColumnBindings.Length > 4)
        {
            values[4] = new FlattenedWriteValue.Literal(scalarFieldValue);
        }

        return new CollectionWriteCandidate(
            tableWritePlan: childrenPlan,
            ordinalPath: [0, requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                childrenPlan,
                [identityValue]
            )
        );
    }

    public static ImmutableArray<SemanticIdentityPart> Identity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    public static ScopeInstanceAddress RootAddress() =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    public static ScopeInstanceAddress ParentRowAddress(string parentSemanticIdentity) =>
        new(ParentsScope, [new AncestorCollectionInstance(ParentsScope, Identity(parentSemanticIdentity))]);

    /// <summary>
    /// Effective parent address used by the planner/index when the immediate JSON parent of
    /// a child collection is the inlined non-collection scope <see cref="DetailScope"/>.
    /// JsonScope is the inlined scope; the ancestor chain is the parent collection row's
    /// chain (the inlined scope contributes no <see cref="AncestorCollectionInstance"/>
    /// because it is not itself a collection instance).
    /// </summary>
    public static ScopeInstanceAddress DetailParentAddress(string parentSemanticIdentity) =>
        new(DetailScope, [new AncestorCollectionInstance(ParentsScope, Identity(parentSemanticIdentity))]);

    public static VisibleRequestCollectionItem BuildDetailChildRequestItem(
        string parentSemanticIdentity,
        string childIdentity,
        int parentArrayIndex,
        int childArrayIndex,
        bool creatable = true
    ) =>
        new(
            new CollectionRowAddress(
                DetailChildrenScope,
                DetailParentAddress(parentSemanticIdentity),
                Identity(childIdentity)
            ),
            creatable,
            $"$.parents[{parentArrayIndex}].detail.children[{childArrayIndex}]"
        );

    public static VisibleStoredCollectionRow BuildDetailChildStoredRow(
        string parentSemanticIdentity,
        string childIdentity,
        ImmutableArray<string>? hiddenMemberPaths = null
    ) =>
        new(
            new CollectionRowAddress(
                DetailChildrenScope,
                DetailParentAddress(parentSemanticIdentity),
                Identity(childIdentity)
            ),
            hiddenMemberPaths ?? ImmutableArray<string>.Empty
        );

    /// <summary>
    /// CollectionWriteCandidate that hangs under a parent at <see cref="DetailChildrenScope"/>.
    /// Layout matches BuildDetailChildrenCollectionPlan: [ChildItemId, ParentItemId, Ordinal,
    /// IdentityField0]. ParentItemId is resolved by the walker via
    /// <see cref="WriteValueSource.ParentKeyPart"/> at emission time, so we leave that slot
    /// as a null literal here.
    /// </summary>
    public static CollectionWriteCandidate BuildDetailChildCandidate(
        TableWritePlan childrenPlan,
        string identityValue,
        int parentArrayIndex,
        int requestOrder
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
            ordinalPath: [parentArrayIndex, requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                childrenPlan,
                [identityValue]
            )
        );
    }

    public static VisibleRequestCollectionItem BuildParentRequestItem(
        string identityValue,
        int arrayIndex,
        bool creatable = true
    ) =>
        new(
            new CollectionRowAddress(ParentsScope, RootAddress(), Identity(identityValue)),
            creatable,
            $"$.parents[{arrayIndex}]"
        );

    public static VisibleRequestCollectionItem BuildChildRequestItem(
        string parentSemanticIdentity,
        string childIdentity,
        int parentArrayIndex,
        int childArrayIndex,
        bool creatable = true
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddress(parentSemanticIdentity),
                Identity(childIdentity)
            ),
            creatable,
            $"$.parents[{parentArrayIndex}].children[{childArrayIndex}]"
        );

    public static VisibleStoredCollectionRow BuildParentStoredRow(string identityValue) =>
        new(
            new CollectionRowAddress(ParentsScope, RootAddress(), Identity(identityValue)),
            ImmutableArray<string>.Empty
        );

    public static VisibleStoredCollectionRow BuildChildStoredRow(
        string parentSemanticIdentity,
        string childIdentity,
        ImmutableArray<string>? hiddenMemberPaths = null
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddress(parentSemanticIdentity),
                Identity(childIdentity)
            ),
            hiddenMemberPaths ?? ImmutableArray<string>.Empty
        );

    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootPlan,
        ImmutableArray<CollectionWriteCandidate> parentCandidates
    )
    {
        FlattenedWriteValue[] rootValues = [new FlattenedWriteValue.Literal(345L)];
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, collectionCandidates: parentCandidates)
        );
    }

    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems
    ) =>
        new(
            writableBody,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            visibleItems
        );

    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows
    ) => new(request, new JsonObject(), ImmutableArray<StoredScopeState>.Empty, storedRows);

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> childRows
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
            ],
            []
        );
}

/// <summary>
/// CP2 Slice 5: regression for the nested-recursion lookup miss when the parent collection's
/// semantic identity is descriptor-backed. The walker builds its address-keyed indexes
/// (<c>_visibleRequestItemsByChildScopeAndParent</c>, <c>_visibleStoredRowsByChildScopeAndParent</c>)
/// once at construction from the raw Core-emitted addresses; those addresses' ancestor identities
/// carry descriptor URIs in their <c>SemanticIdentityInOrder</c>. The walker's matched/insert
/// recursion builds the child <c>ContainingScopeAddress</c> from the matched stored row's
/// <c>SemanticIdentityInOrder</c>, which has been canonicalized to backend Int64 form.
/// Without ancestor canonicalization at index build, the recursion's lookup key
/// <c>(childScope, parentAddrWithBackendId)</c> never matches the index entry
/// <c>(childScope, parentAddrWithDescriptorURI)</c>, and the nested children scope appears empty.
/// <para>
/// This fixture builds: parents collection at <c>$.parents[*]</c> with descriptor-backed
/// identity (URI <c>uri://ed-fi.org/ParentTypeDescriptor#A</c> resolves to backend id 42); a
/// nested children collection at <c>$.parents[*].children[*]</c> with scalar identity. One
/// stored parent row + two stored children under it. After driving the walker, the children
/// table state must contain two merged rows, attached to the canonicalized parent identity.
/// </para>
/// </summary>
[TestFixture]
public class Given_a_descriptor_backed_parent_collection_with_nested_children
{
    private const long DocumentId = 345L;
    private const long ParentItemId = 100L;
    private const long ParentDescriptorId = 42L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;

    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;
    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            DescriptorBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: one parent referencing a descriptor URI. No nested-children
        // CollectionCandidates are attached, matching the existing nested-walker fixtures
        // that scope the assertion to the descriptor-canonicalization path.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject { ["parentTypeDescriptor"] = DescriptorBackedNestedTopologyBuilders.ParentUri }
            ),
        };

        // Parent candidate carries the canonicalized Int64 descriptor id (as the flattener
        // produces after URI resolution).
        var candidate = DescriptorBackedNestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            ParentDescriptorId,
            requestOrder: 0
        );

        // Visible-request items: one parent item carrying the URI form (as Core emits), with
        // an ancestor-less ParentAddress (the parent is at the top level under root).
        var requestItems = ImmutableArray.Create(
            DescriptorBackedNestedTopologyBuilders.BuildParentRequestItemWithUri(
                DescriptorBackedNestedTopologyBuilders.ParentUri,
                arrayIndex: 0
            )
        );

        var request = DescriptorBackedNestedTopologyBuilders.BuildRequest(body, requestItems);

        var flattened = DescriptorBackedNestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidate]);

        // Visible-stored rows: parent (URI form, as Core emits) + two stored children whose
        // ParentAddress.AncestorCollectionInstances[0] carries the parent's URI form.
        var storedRows = ImmutableArray.Create(
            DescriptorBackedNestedTopologyBuilders.BuildParentStoredRowWithUri(
                DescriptorBackedNestedTopologyBuilders.ParentUri
            ),
            DescriptorBackedNestedTopologyBuilders.BuildChildStoredRowWithUriParent(
                DescriptorBackedNestedTopologyBuilders.ParentUri,
                "C1"
            ),
            DescriptorBackedNestedTopologyBuilders.BuildChildStoredRowWithUriParent(
                DescriptorBackedNestedTopologyBuilders.ParentUri,
                "C2"
            )
        );

        var context = DescriptorBackedNestedTopologyBuilders.BuildContext(request, storedRows);

        // Current DB state: parent stored as Int64 descriptor id; two children under it.
        var currentState = DescriptorBackedNestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentItemId, DocumentId, 1, ParentDescriptorId],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentItemId, 1, "C1"],
                [ChildA2ItemId, ParentItemId, 2, "C2"],
            ]
        );

        // Resolved reference set: maps the URI to ParentDescriptorId so the URI cache hits.
        var resolvedRefs = DescriptorBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
            DescriptorBackedNestedTopologyBuilders.ParentUri,
            ParentDescriptorId
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_routes_children_through_delete_by_absence_when_visible_stored_lookup_hits()
    {
        // Discriminator: the children scope has two visible-stored rows under the matched
        // parent address but ZERO visible-request items (this fixture supplies no nested
        // CollectionCandidates) and TWO current rows. The persister's correct outcome is
        // delete-by-absence — visible-stored rows whose visible-request items were omitted
        // from the request must be deleted. The planner achieves this by emitting NO
        // entries for those rows (Phase 2 hits the "visible slot with no merged entry to
        // consume → omitted" branch), so the children TableState carries ZERO merged
        // rows. The persister's set-difference (current − merged) then deletes both.
        //
        // Without the ancestor-canonicalization fix, the walker's recursion lookup at
        // (childScope, parentAddrWithInt64Ancestor) misses the index entry keyed by
        // (childScope, parentAddrWithUriAncestor). visibleStoredRowsForScope is empty
        // inside the planner, so Phase 2 finds NO match in visibleStoredByIdentity and
        // routes each current row through HiddenPreserveEntry — emitting TWO false-preserve
        // merged rows. The persister then sees current=merged and preserves the rows
        // unchanged. False preserves; data is not deleted as it should be.
        //
        // Discriminator: 0 merged rows (fix in place) vs 2 merged rows (bug). Both branches
        // populate the children TableState with the 2 current rows so the persister has a
        // delete-by-absence baseline; the merged side is what differs.
        var childrenBuilder = _tableStateBuilders[_childrenPlan.TableModel.Table];
        childrenBuilder.HasContent.Should().BeTrue("recursion must visit the children scope");
        var state = childrenBuilder.Build();
        state
            .CurrentRows.Length.Should()
            .Be(2, "the children's current rows feed the persister's delete-by-absence baseline");
        state
            .MergedRows.Length.Should()
            .Be(
                0,
                "delete-by-absence: visible-stored under matched parent with no request item "
                    + "means the rows are omitted from the merged sequence; the persister's "
                    + "set-difference deletes them. With the bug, the visible-stored lookup misses "
                    + "and the planner falsely routes the children through HiddenPreserveEntry, "
                    + "producing 2 spurious merged rows."
            );
    }

    [Test]
    public void It_canonicalizes_ancestor_identities_in_visible_stored_index_keys()
    {
        // Direct structural assertion on the per-merge index built at walker construction.
        // The walker MUST canonicalize ancestor identities at index-build time so the
        // recursion lookup (which constructs the parent address from the canonicalized
        // current row's semantic identity in Int64 form) hits the correct bucket. Without
        // the fix, the index key would carry the URI-form ancestor and the lookup at
        // ScopeInstanceAddress("$.parents[*]", [AncestorCollectionInstance("$.parents[*]",
        // [Int64 42])]) would miss.
        var canonicalizedParentAncestor = new AncestorCollectionInstance(
            DescriptorBackedNestedTopologyBuilders.ParentsScope,
            [
                new SemanticIdentityPart(
                    DescriptorBackedNestedTopologyBuilders.ParentDescriptorRelativePath,
                    JsonValue.Create(ParentDescriptorId),
                    IsPresent: true
                ),
            ]
        );
        var canonicalizedParentAddress = new ScopeInstanceAddress(
            DescriptorBackedNestedTopologyBuilders.ParentsScope,
            [canonicalizedParentAncestor]
        );
        var canonicalKey = (DescriptorBackedNestedTopologyBuilders.ChildrenScope, canonicalizedParentAddress);

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey(
                canonicalKey,
                "the index key must carry the canonicalized (Int64) ancestor identity so "
                    + "recursion lookups (whose parent addresses are built from canonicalized "
                    + "stored rows) can find the bucket"
            );
    }
}

/// <summary>
/// Hidden inlined-descendant path expansion under a parent collection whose semantic
/// identity is descriptor-backed. The walker canonicalizes
/// child rows' <c>ParentAddress.AncestorCollectionInstances</c> at index-build time
/// (descriptor URI → Int64 id), but the per-row hidden-path expansion reconstructs the
/// child <c>CollectionRowAddress</c> from the descendant <see cref="StoredScopeState"/>'s
/// raw ancestor chain (still URI form). If the expansion runs after parent-address
/// canonicalization, the reconstructed key never matches the canonical row key in the
/// expander's bucket dictionary, the inlined hidden member path is silently dropped, and
/// the matched-row classifier later treats the descendant member as visible writable —
/// allowing flattened-null candidates to overwrite stored values on update.
/// <para>
/// Fixture: descriptor-backed parents + scalar-identity nested children. Two stored child
/// rows (C1, C2) under one URI parent. A single stored scope state for the inlined
/// non-collection descendant <c>$.parents[*].children[*].period</c> targets the C1 child
/// (raw URI parent + raw "C1" child in its ancestor chain) and contributes hidden member
/// path <c>endDate</c>. After the walker constructs its visible-stored index, the C1
/// row in the canonical bucket must carry <c>period.endDate</c> in
/// <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>; C2 must not.
/// </para>
/// </summary>
[TestFixture]
public class Given_a_descriptor_backed_parent_with_inlined_descendant_hidden_member
{
    private const long DocumentId = 345L;
    private const long ParentItemId = 100L;
    private const long ParentDescriptorId = 42L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;

    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            DescriptorBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject { ["parentTypeDescriptor"] = DescriptorBackedNestedTopologyBuilders.ParentUri }
            ),
        };

        var candidate = DescriptorBackedNestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            ParentDescriptorId,
            requestOrder: 0
        );
        var requestItems = ImmutableArray.Create(
            DescriptorBackedNestedTopologyBuilders.BuildParentRequestItemWithUri(
                DescriptorBackedNestedTopologyBuilders.ParentUri,
                arrayIndex: 0
            )
        );
        var request = DescriptorBackedNestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = DescriptorBackedNestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [candidate]);

        var storedRows = ImmutableArray.Create(
            DescriptorBackedNestedTopologyBuilders.BuildParentStoredRowWithUri(
                DescriptorBackedNestedTopologyBuilders.ParentUri
            ),
            DescriptorBackedNestedTopologyBuilders.BuildChildStoredRowWithUriParent(
                DescriptorBackedNestedTopologyBuilders.ParentUri,
                "C1"
            ),
            DescriptorBackedNestedTopologyBuilders.BuildChildStoredRowWithUriParent(
                DescriptorBackedNestedTopologyBuilders.ParentUri,
                "C2"
            )
        );

        // Stored scope state for the inlined non-collection descendant
        // $.parents[*].children[*].period under the C1 child row. Raw ancestor chain:
        // [URI parent, "C1" child]. The expander reconstructs the child row's
        // CollectionRowAddress from this chain — both ancestors are in raw form here, so
        // the reconstructed key only compares equal to the row's stored key when the
        // expansion runs BEFORE parent-address canonicalization rewrites the URI to Int64.
        var inlinedDescendantState = new StoredScopeState(
            Address: new ScopeInstanceAddress(
                JsonScope: "$.parents[*].children[*].period",
                AncestorCollectionInstances:
                [
                    new AncestorCollectionInstance(
                        DescriptorBackedNestedTopologyBuilders.ParentsScope,
                        [
                            new SemanticIdentityPart(
                                DescriptorBackedNestedTopologyBuilders.ParentDescriptorRelativePath,
                                JsonValue.Create(DescriptorBackedNestedTopologyBuilders.ParentUri),
                                IsPresent: true
                            ),
                        ]
                    ),
                    new AncestorCollectionInstance(
                        DescriptorBackedNestedTopologyBuilders.ChildrenScope,
                        [
                            new SemanticIdentityPart(
                                "$.identityField0",
                                JsonValue.Create("C1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                ]
            ),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            HiddenMemberPaths: ["endDate"]
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray.Create(inlinedDescendantState),
            storedRows
        );

        var currentState = DescriptorBackedNestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentItemId, DocumentId, 1, ParentDescriptorId],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentItemId, 1, "C1"],
                [ChildA2ItemId, ParentItemId, 2, "C2"],
            ]
        );

        var resolvedRefs = DescriptorBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
            DescriptorBackedNestedTopologyBuilders.ParentUri,
            ParentDescriptorId
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        Dictionary<DbTableName, ProfileTableStateBuilder> tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            tableStateBuilders
        );
    }

    [Test]
    public void It_attaches_inlined_descendant_hidden_path_to_the_canonicalized_child_row()
    {
        var canonicalParentAddress = new ScopeInstanceAddress(
            DescriptorBackedNestedTopologyBuilders.ParentsScope,
            [
                new AncestorCollectionInstance(
                    DescriptorBackedNestedTopologyBuilders.ParentsScope,
                    [
                        new SemanticIdentityPart(
                            DescriptorBackedNestedTopologyBuilders.ParentDescriptorRelativePath,
                            JsonValue.Create(ParentDescriptorId),
                            IsPresent: true
                        ),
                    ]
                ),
            ]
        );
        var canonicalKey = (DescriptorBackedNestedTopologyBuilders.ChildrenScope, canonicalParentAddress);

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey(canonicalKey, "the children index must carry the canonicalized parent ancestor");

        var childBucket = _walker.VisibleStoredRowsByChildScopeAndParent[canonicalKey];

        var c1Row = childBucket.Single(r =>
            r.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>() == "C1"
        );
        c1Row
            .HiddenMemberPaths.Should()
            .Contain(
                "period.endDate",
                "the inlined descendant scope state targeting C1 must fold its hidden member path "
                    + "onto C1's canonical row in the index. The expander reconstructs C1's address "
                    + "from the descendant state's raw URI ancestor chain, so the expansion must run "
                    + "before parent-address canonicalization rewrites the URI to Int64 — otherwise "
                    + "the lookup misses and the matched-row overlay later overwrites the stored "
                    + "hidden value with a flattened null."
            );
    }

    [Test]
    public void It_does_not_fold_descendant_hidden_path_onto_a_sibling_child_row()
    {
        var canonicalParentAddress = new ScopeInstanceAddress(
            DescriptorBackedNestedTopologyBuilders.ParentsScope,
            [
                new AncestorCollectionInstance(
                    DescriptorBackedNestedTopologyBuilders.ParentsScope,
                    [
                        new SemanticIdentityPart(
                            DescriptorBackedNestedTopologyBuilders.ParentDescriptorRelativePath,
                            JsonValue.Create(ParentDescriptorId),
                            IsPresent: true
                        ),
                    ]
                ),
            ]
        );
        var canonicalKey = (DescriptorBackedNestedTopologyBuilders.ChildrenScope, canonicalParentAddress);
        var childBucket = _walker.VisibleStoredRowsByChildScopeAndParent[canonicalKey];

        var c2Row = childBucket.Single(r =>
            r.Address.SemanticIdentityInOrder[0].Value!.GetValue<string>() == "C2"
        );
        c2Row
            .HiddenMemberPaths.Should()
            .NotContain(
                "period.endDate",
                "the descendant state targets C1's identity only — the full structural address "
                    + "match must still discriminate C1 from its sibling C2"
            );
    }
}

/// <summary>
/// Local builders for a 3-table nested-topology test plan with a descriptor-backed parent
/// collection at <c>$.parents[*]</c> (URI form in Core-emitted addresses; Int64 form in the
/// canonicalized current state) and a scalar-identity nested children collection at
/// <c>$.parents[*].children[*]</c>.
/// </summary>
internal static class DescriptorBackedNestedTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");
    public const string ParentsScope = "$.parents[*]";
    public const string ChildrenScope = "$.parents[*].children[*]";
    public const string ParentDescriptorPath = "$.parents[*].parentTypeDescriptor";
    public const string ParentDescriptorRelativePath = "$.parentTypeDescriptor";
    public const string ParentUri = "uri://ed-fi.org/ParentTypeDescriptor#A";

    public static readonly QualifiedResourceName ParentTypeDescriptorResource = new(
        "Ed-Fi",
        "ParentTypeDescriptor"
    );

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndChildrenPlan()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildDescriptorBackedParentsCollectionPlan();
        var childrenPlan = BuildScalarChildrenCollectionPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "DescriptorNestedTest"),
                PhysicalSchema: _schema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: new JsonPathExpression(ParentDescriptorPath, []),
                        Table: parentsPlan.TableModel.Table,
                        FkColumn: new DbColumnName("ParentTypeDescriptor_Id"),
                        DescriptorResource: ParentTypeDescriptorResource
                    ),
                ]
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
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
            Table: new DbTableName(_schema, "DescriptorNestedTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_DescriptorNestedTest",
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
            InsertSql: "INSERT INTO edfi.\"DescriptorNestedTest\" DEFAULT VALUES",
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

    private static TableWritePlan BuildDescriptorBackedParentsCollectionPlan()
    {
        // Layout: [ParentItemId, ParentDocumentId, Ordinal, ParentTypeDescriptor_Id].
        // ParentTypeDescriptor_Id is a DescriptorFk identity column.
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentDocIdColumn = new DbColumnModel(
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
            ColumnName: new DbColumnName("ParentTypeDescriptor_Id"),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(ParentDescriptorPath, []),
            TargetResource: ParentTypeDescriptorResource
        );
        DbColumnModel[] columns = [parentItemIdColumn, parentDocIdColumn, ordinalColumn, descriptorColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ParentsTable"),
            JsonScope: new JsonPathExpression(ParentsScope, []),
            Key: new TableKey(
                "PK_ParentsTable",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(ParentDescriptorRelativePath, []),
                        descriptorColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"ParentsTable\" VALUES (@ParentItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    parentDocIdColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    descriptorColumn,
                    new WriteValueSource.DescriptorReference(
                        ParentTypeDescriptorResource,
                        new JsonPathExpression(ParentDescriptorPath, []),
                        DescriptorValuePath: null
                    ),
                    "ParentTypeDescriptor_Id"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(ParentDescriptorRelativePath, []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ParentsTable\" SET X = @X WHERE \"ParentItemId\" = @ParentItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ParentsTable\" WHERE \"ParentItemId\" = @ParentItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ParentItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildScalarChildrenCollectionPlan()
    {
        // Layout: [ChildItemId, ParentItemId, Ordinal, IdentityField0]. ParentItemId is a
        // ParentKeyPart referring to slot 0 of the parents table's PhysicalRowIdentity.
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    public static CollectionWriteCandidate BuildParentCandidate(
        TableWritePlan parentsPlan,
        long descriptorId,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[parentsPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        // Stamp the descriptor id at index 3.
        values[3] = new FlattenedWriteValue.Literal(descriptorId);

        return new CollectionWriteCandidate(
            tableWritePlan: parentsPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [descriptorId],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                parentsPlan,
                [descriptorId]
            )
        );
    }

    private static ImmutableArray<SemanticIdentityPart> ParentUriIdentity(string uri) =>
        [new SemanticIdentityPart(ParentDescriptorRelativePath, JsonValue.Create(uri), IsPresent: true)];

    private static ImmutableArray<SemanticIdentityPart> ChildIdentity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static ScopeInstanceAddress RootAddress() =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    private static ScopeInstanceAddress ParentRowAddressWithUri(string uri) =>
        new(ParentsScope, [new AncestorCollectionInstance(ParentsScope, ParentUriIdentity(uri))]);

    public static VisibleRequestCollectionItem BuildParentRequestItemWithUri(string uri, int arrayIndex) =>
        new(
            new CollectionRowAddress(ParentsScope, RootAddress(), ParentUriIdentity(uri)),
            Creatable: true,
            $"$.parents[{arrayIndex}]"
        );

    public static VisibleStoredCollectionRow BuildParentStoredRowWithUri(string uri) =>
        new(
            new CollectionRowAddress(ParentsScope, RootAddress(), ParentUriIdentity(uri)),
            ImmutableArray<string>.Empty
        );

    public static VisibleStoredCollectionRow BuildChildStoredRowWithUriParent(
        string parentUri,
        string childIdentity
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddressWithUri(parentUri),
                ChildIdentity(childIdentity)
            ),
            ImmutableArray<string>.Empty
        );

    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootPlan,
        ImmutableArray<CollectionWriteCandidate> parentCandidates
    )
    {
        FlattenedWriteValue[] rootValues = [new FlattenedWriteValue.Literal(345L)];
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, collectionCandidates: parentCandidates)
        );
    }

    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems
    ) =>
        new(
            writableBody,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            visibleItems
        );

    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows
    ) => new(request, new JsonObject(), ImmutableArray<StoredScopeState>.Empty, storedRows);

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> childRows
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
            ],
            []
        );

    public static ResolvedReferenceSet BuildResolvedReferenceSet(string uri, long descriptorId) =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath("$.parents[0].parentTypeDescriptor")] = new ResolvedDescriptorReference(
                    Reference: new DescriptorReference(
                        ResourceInfo: new BaseResourceInfo(
                            new ProjectName("Ed-Fi"),
                            new ResourceName("ParentTypeDescriptor"),
                            true
                        ),
                        DocumentIdentity: new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.descriptor"), uri),
                        ]),
                        ReferentialId: new ReferentialId(Guid.NewGuid()),
                        Path: new JsonPath("$.parents[0].parentTypeDescriptor")
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
}

/// <summary>
/// Slice 5 CP2 (Task 12.5): regression for the nested-recursion lookup miss when the parent
/// collection's semantic identity is document-reference-backed. After Task 12 retired the
/// constructor fence on nested CollectionCandidates, document-reference-backed parents with
/// nested children became reachable; ancestor canonicalization at index build must rewrite
/// the document-reference natural-key parts to the backend document id, mirroring the
/// descriptor URI fix landed in Task 11.5 (commit ec716854) but for the document-reference
/// identity kind.
/// <para>
/// Fixture: parents collection at <c>$.parents[*]</c> whose semantic identity is a
/// document-reference (one part, the referenced parent's <c>parentId</c> natural key). The
/// reference cache resolves <c>parentId="nk-p1"</c> to backend document id 999. Nested
/// children at <c>$.parents[*].children[*]</c> have scalar identity. One stored parent row
/// (FK = 999) and two stored children under it. The walker's recursion must hit the
/// children-scope visible-stored bucket keyed by the canonicalized parent ancestor (Int64
/// 999) — the index entry built at construction must therefore carry the canonicalized form.
/// </para>
/// </summary>
[TestFixture]
public class Given_a_document_reference_backed_parent_collection_with_nested_children
{
    private const long DocumentId = 345L;
    private const long ParentItemId = 100L;
    private const long ParentReferenceDocumentId = 999L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;

    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;
    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            DocumentReferenceBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: one parent referencing the natural-key parentId. No nested-children
        // CollectionCandidates: the synthesizer-visible request side does not realize nested
        // candidates under document-reference parents in this fixture (mirrors the descriptor
        // sibling above). The discriminator is the visible-stored children bucket lookup.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["parentReference"] = new JsonObject
                    {
                        ["parentId"] = DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                    },
                }
            ),
        };

        // Parent candidate carries the canonicalized Int64 reference document id at the FK
        // slot and the natural-key derived column (as the flattener produces after reference
        // resolution).
        var candidate = DocumentReferenceBackedNestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            ParentReferenceDocumentId,
            DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
            requestOrder: 0
        );

        // Visible-request items: one parent item carrying the natural-key form (as Core
        // emits), with an ancestor-less ParentAddress (the parent is at the top level).
        var requestItems = ImmutableArray.Create(
            DocumentReferenceBackedNestedTopologyBuilders.BuildParentRequestItemWithNaturalKey(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                arrayIndex: 0
            )
        );

        var request = DocumentReferenceBackedNestedTopologyBuilders.BuildRequest(body, requestItems);

        var flattened = DocumentReferenceBackedNestedTopologyBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [candidate]
        );

        // Visible-stored rows: parent (natural-key form, as Core emits) + two stored children
        // whose ParentAddress.AncestorCollectionInstances[0] carries the parent's natural-key
        // form. Without ancestor canonicalization, the index keyed by the natural-key form
        // would never match the recursion lookup keyed by the canonicalized backend id.
        var storedRows = ImmutableArray.Create(
            DocumentReferenceBackedNestedTopologyBuilders.BuildParentStoredRowWithNaturalKey(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey
            ),
            DocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowWithNaturalKeyParent(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                "C1"
            ),
            DocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowWithNaturalKeyParent(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                "C2"
            )
        );

        var context = DocumentReferenceBackedNestedTopologyBuilders.BuildContext(request, storedRows);

        // Current DB state: parent stored with FK = ParentReferenceDocumentId; two children
        // under it. Layout for parents: [ParentItemId, ParentDocumentId, Ordinal,
        // ParentReference_DocumentId, ParentReference_ParentId].
        var currentState = DocumentReferenceBackedNestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [
                    ParentItemId,
                    DocumentId,
                    1,
                    ParentReferenceDocumentId,
                    DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                ],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentItemId, 1, "C1"],
                [ChildA2ItemId, ParentItemId, 2, "C2"],
            ]
        );

        // Resolved reference set: maps the parent reference path to ParentReferenceDocumentId
        // so the request-side reference cache hits during canonicalization.
        var resolvedRefs = DocumentReferenceBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
            DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
            ParentReferenceDocumentId
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_routes_children_through_delete_by_absence_when_visible_stored_lookup_hits()
    {
        // Discriminator (mirrors the descriptor sibling): two visible-stored children under
        // the matched parent address but ZERO visible-request items, plus TWO current rows.
        // Correct outcome: delete-by-absence — children TableState carries ZERO merged rows
        // (Phase 2 emits nothing for visible slots without a merged entry).
        //
        // Without the document-reference ancestor canonicalization fix, the walker's
        // recursion lookup at (childScope, parentAddrWithInt64Ancestor=999) misses the
        // index entry keyed by (childScope, parentAddrWithNaturalKeyAncestor="nk-p1").
        // visibleStoredRowsForScope is empty inside the planner, the planner falsely routes
        // the two current rows through HiddenPreserveEntry, and TWO false-preserve merged
        // rows surface. Discriminator: 0 merged rows (fix in place) vs 2 merged rows (bug).
        var childrenBuilder = _tableStateBuilders[_childrenPlan.TableModel.Table];
        childrenBuilder.HasContent.Should().BeTrue("recursion must visit the children scope");
        var state = childrenBuilder.Build();
        state
            .CurrentRows.Length.Should()
            .Be(2, "the children's current rows feed the persister's delete-by-absence baseline");
        state
            .MergedRows.Length.Should()
            .Be(
                0,
                "delete-by-absence: visible-stored under matched parent with no request item "
                    + "means the rows are omitted from the merged sequence; the persister's "
                    + "set-difference deletes them. With the bug, the visible-stored lookup misses "
                    + "and the planner falsely routes the children through HiddenPreserveEntry, "
                    + "producing 2 spurious merged rows."
            );
    }

    [Test]
    public void It_canonicalizes_document_reference_ancestor_identities_in_visible_stored_index_keys()
    {
        // Direct structural assertion on the per-merge index built at walker construction.
        // The walker MUST canonicalize document-reference ancestor identities at index-build
        // time so the recursion lookup (which constructs the parent address from the
        // canonicalized current row's semantic identity in Int64 form) hits the correct
        // bucket. Without the fix, the index key would carry the natural-key-form ancestor
        // and the lookup at ScopeInstanceAddress("$.parents[*]",
        // [AncestorCollectionInstance("$.parents[*]", [Int64 999])]) would miss.
        var canonicalizedParentAncestor = new AncestorCollectionInstance(
            DocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [
                new SemanticIdentityPart(
                    DocumentReferenceBackedNestedTopologyBuilders.ParentReferenceParentIdRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
            ]
        );
        var canonicalizedParentAddress = new ScopeInstanceAddress(
            DocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [canonicalizedParentAncestor]
        );
        var canonicalKey = (
            DocumentReferenceBackedNestedTopologyBuilders.ChildrenScope,
            canonicalizedParentAddress
        );

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey(
                canonicalKey,
                "the index key must carry the canonicalized (Int64) document-reference "
                    + "ancestor identity so recursion lookups (whose parent addresses are "
                    + "built from canonicalized stored rows) can find the bucket"
            );
    }
}

/// <summary>
/// Slice 5 CP2 (Task 13.9): regression for the inserted-document-reference-backed parent
/// case. When the walker encounters an INSERTED parent (no current row yet) whose semantic
/// identity is document-reference-backed, the existing stored-side ancestor canonicalization
/// in <c>BuildVisibleRequestItemsIndex</c> cannot resolve via current-row scan — there are
/// no current rows for the inserted parent. The fix adds a request-side path that uses the
/// child item's <see cref="VisibleRequestCollectionItem.RequestJsonPath"/> to derive the
/// ancestor's ordinal path within the request, then looks up the resolved DocumentId via
/// <c>FlatteningResolvedReferenceLookupSet.GetDocumentId(BindingIndex, ordinalPath)</c>.
/// <para>
/// Fixture: brand-new resource (no current state). Request body contains one parent at
/// <c>$.parents[0]</c> with two nested children at <c>$.parents[0].children[0]</c> and
/// <c>[1]</c>. The parent's <c>parentReference.parentId</c> resolves to backend DocumentId
/// 999 in the request-cycle reference cache. Visible-request items: 1 parent + 2 children,
/// all creatable. The children's <c>ParentAddress.AncestorCollectionInstances[0]</c>
/// carries the parent's natural-key form (Core-emitted shape). Without the fix, the
/// ancestor canonicalization pass fails closed (no current rows to scan) — index build
/// throws an InvalidOperationException OR the natural-key form remains in the index key
/// while the walker's recursion lookup uses the canonicalized DocumentId form, missing
/// the bucket and dropping the children. With the fix, the request-side ordinal-path
/// resolver canonicalizes the ancestor identity to <c>Int64 999</c> at index-build time,
/// the walker's recursion lookup hits, and both children are emitted as inserts.
/// </para>
/// </summary>
[TestFixture]
public class Given_an_inserted_document_reference_backed_parent_with_nested_request_children
{
    private const long DocumentId = 345L;
    private const long ParentReferenceDocumentId = 999L;

    private TableWritePlan _parentsPlan = null!;
    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;
    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            DocumentReferenceBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _parentsPlan = parentsPlan;
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: one parent referencing the natural-key parentId, with two nested
        // children. The walker navigates the body via ResolveCollectionItemNode using the
        // request items' RequestJsonPath, so children must be present at the expected
        // concrete paths.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["parentReference"] = new JsonObject
                    {
                        ["parentId"] = DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                    },
                    ["children"] = new JsonArray(
                        new JsonObject { ["identityField0"] = "C1" },
                        new JsonObject { ["identityField0"] = "C2" }
                    ),
                }
            ),
        };

        // Two nested children candidates carried by the parent CollectionWriteCandidate so
        // the walker recurses into the children scope after inserting the parent.
        var childCandidate1 = DocumentReferenceBackedNestedTopologyBuilders.BuildChildCandidate(
            childrenPlan,
            "C1",
            parentArrayIndex: 0,
            childArrayIndex: 0
        );
        var childCandidate2 = DocumentReferenceBackedNestedTopologyBuilders.BuildChildCandidate(
            childrenPlan,
            "C2",
            parentArrayIndex: 0,
            childArrayIndex: 1
        );

        // Parent candidate with nested children. SemanticIdentityValues carries the
        // canonicalized Int64 reference document id (the form the planner consumes) so the
        // walker's BuildContainingScopeAddress emits the canonicalized ancestor identity
        // when it recurses into the children. The natural-key form lives only on the
        // visible-request items' ParentAddress (Core-emitted shape that the request-side
        // canonicalization rewrites at index-build time).
        var parentCandidate =
            DocumentReferenceBackedNestedTopologyBuilders.BuildParentCandidateWithNestedChildren(
                parentsPlan,
                ParentReferenceDocumentId,
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                requestOrder: 0,
                nestedChildren: ImmutableArray.Create(childCandidate1, childCandidate2)
            );

        // Visible-request items: one parent (creatable) + two children whose ParentAddress
        // carries the parent's natural-key form. The walker's BuildVisibleRequestItemsIndex
        // must canonicalize the ancestor identity to 999 (Int64) so the recursion lookup
        // at (childrenScope, ScopeInstanceAddress("$.parents[*]", [Int64 999])) finds the
        // bucket. Without Task 13.9, the canonicalization pass cannot resolve the ancestor
        // (no current row exists for the brand-new parent) and either fails closed or
        // leaves the natural-key form in place — both flavors miss the recursion lookup.
        var requestItems = ImmutableArray.Create(
            DocumentReferenceBackedNestedTopologyBuilders.BuildParentRequestItemWithNaturalKey(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                arrayIndex: 0
            ),
            DocumentReferenceBackedNestedTopologyBuilders.BuildChildRequestItemWithNaturalKeyParent(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                "C1",
                parentArrayIndex: 0,
                childArrayIndex: 0
            ),
            DocumentReferenceBackedNestedTopologyBuilders.BuildChildRequestItemWithNaturalKeyParent(
                DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
                "C2",
                parentArrayIndex: 0,
                childArrayIndex: 1
            )
        );

        var request = DocumentReferenceBackedNestedTopologyBuilders.BuildRequest(body, requestItems);
        var flattened = DocumentReferenceBackedNestedTopologyBuilders.BuildFlattenedWriteSet(
            rootPlan,
            ImmutableArray.Create(parentCandidate)
        );

        // No visible stored rows — the parent is brand new, no children existed before.
        var context = DocumentReferenceBackedNestedTopologyBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

        // Empty current state: existing-document path with zero rows in the parents and
        // children tables. The constructor requires currentState and profileAppliedContext
        // be both-null or both-non-null; we use both-non-null + empty hydrated rows so the
        // walker still constructs the per-merge indexes (they read from currentState) and
        // the request-side canonicalization is the only path that can resolve the
        // inserted-parent ancestor identity.
        var currentState = DocumentReferenceBackedNestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows: [],
            childRows: []
        );

        // Resolved reference set: parentReference at $.parents[0].parentReference resolves
        // to ParentReferenceDocumentId. The request-side canonicalization path uses the
        // child item's RequestJsonPath (e.g., $.parents[0].children[0]) to derive ordinal
        // path [0], which matches the wildcard count of the ancestor's JsonScope
        // ($.parents[*]); GetDocumentId(BindingIndex=0, [0]) returns 999.
        var resolvedRefs = DocumentReferenceBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
            DocumentReferenceBackedNestedTopologyBuilders.ParentNaturalKey,
            ParentReferenceDocumentId
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_canonicalizes_the_inserted_parent_ancestor_via_request_ordinal_path()
    {
        // Direct structural assertion on the visible-request index built at walker
        // construction. The fix routes BuildVisibleRequestItemsIndex through
        // CanonicalizeAddressAncestorsForRequestItem, which derives the ancestor's
        // ordinal path from the child item's RequestJsonPath ($.parents[0].children[*])
        // and looks up the resolved DocumentId via the reference cache. The index key
        // for the children scope must therefore carry an Int64 999 ancestor.
        var canonicalizedParentAncestor = new AncestorCollectionInstance(
            DocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [
                new SemanticIdentityPart(
                    DocumentReferenceBackedNestedTopologyBuilders.ParentReferenceParentIdRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
            ]
        );
        var canonicalizedParentAddress = new ScopeInstanceAddress(
            DocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [canonicalizedParentAncestor]
        );
        var canonicalKey = (
            DocumentReferenceBackedNestedTopologyBuilders.ChildrenScope,
            canonicalizedParentAddress
        );

        _walker
            .VisibleRequestItemsByChildScopeAndParent.Should()
            .ContainKey(
                canonicalKey,
                "the request-side canonicalization pass must resolve the inserted parent's "
                    + "document-reference natural key via the request-cycle reference cache "
                    + "using the child item's RequestJsonPath ordinal-path prefix; without "
                    + "the fix, no current row exists and the helper either fails closed or "
                    + "leaves the natural-key form in place"
            );
        _walker
            .VisibleRequestItemsByChildScopeAndParent[canonicalKey]
            .Length.Should()
            .Be(2, "both children must be indexed under the canonicalized parent ancestor");
    }

    [Test]
    public void It_emits_two_inserted_children_under_the_inserted_parent_with_correct_FK()
    {
        // End-to-end discriminator: the recursion into the inserted parent's children
        // produces two inserted child rows in the children TableState, each carrying the
        // parent's PhysicalRowIdentity at the ParentItemId slot. Without the fix, the
        // walker's recursion lookup misses the visible-request index bucket and the
        // children TableState carries zero merged rows.
        var childrenBuilder = _tableStateBuilders[_childrenPlan.TableModel.Table];
        childrenBuilder.HasContent.Should().BeTrue("recursion must visit the children scope");
        var state = childrenBuilder.Build();
        state
            .MergedRows.Length.Should()
            .Be(
                2,
                "the inserted parent's two nested children must surface as merged rows; "
                    + "without the fix, the visible-request index lookup misses and the "
                    + "children are dropped (zero merged rows)"
            );

        // Discriminator on parents: one inserted parent with the canonicalized FK in
        // its ParentReference_DocumentId slot, confirming the parent path is intact.
        var parentsBuilder = _tableStateBuilders[_parentsPlan.TableModel.Table];
        parentsBuilder.HasContent.Should().BeTrue("the parents scope must produce a merged row");
        var parentsState = parentsBuilder.Build();
        parentsState.MergedRows.Length.Should().Be(1, "exactly one parent row is inserted from the request");
    }
}

/// <summary>
/// Slice 5 CP2 (Task 12.6): regression for the unsafe <c>TryGetValue&lt;long&gt;</c>
/// short-circuit in <c>CanonicalizeAncestorDocumentReferenceParts</c>. The short-circuit
/// treated any <see cref="JsonValue"/> parseable as <see cref="long"/> as already
/// canonicalized and skipped resolver-based canonicalization. Ed-Fi document-reference
/// natural keys frequently ARE numeric long values (e.g.,
/// <c>schoolReference.schoolId = 12345</c>,
/// <c>educationOrganizationReference.educationOrganizationId = 67890</c>): these need
/// natural-key &#x2192; backend DocumentId canonicalization but were short-circuited.
/// <para>
/// Fixture mirrors <c>Given_a_document_reference_backed_parent_collection_with_nested_children</c>
/// from Task 12.5 but uses a numeric natural-key part (long-valued
/// <c>schoolId</c> column). The reference cache resolves <c>schoolId=12345</c> to backend
/// document id 67890. Without the fix, the index built at construction keeps the natural
/// key (12345) in the ancestor part; the recursion lookup (constructed from the
/// canonicalized stored row's Int64 identity = 67890) misses; nested children appear
/// false-hidden-preserve via the current-row index hitting and the planner routing through
/// <c>HiddenPreserveEntry</c>.
/// </para>
/// </summary>
[TestFixture]
public class Given_a_numeric_document_reference_backed_parent_collection_with_nested_children
{
    private const long DocumentId = 345L;
    private const long ParentItemId = 100L;
    private const long ParentReferenceDocumentId = 67890L;
    private const long ParentNumericNaturalKey = 12345L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;

    private TableWritePlan _childrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;
    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            NumericDocumentReferenceBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        _childrenPlan = childrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body: one parent referencing the numeric natural-key schoolId. As with the
        // string-natural-key sibling, no nested-children CollectionCandidates are realized on
        // the request side — the discriminator is the visible-stored children bucket lookup.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["parentReference"] = new JsonObject { ["schoolId"] = ParentNumericNaturalKey },
                }
            ),
        };

        var candidate = NumericDocumentReferenceBackedNestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            ParentReferenceDocumentId,
            ParentNumericNaturalKey,
            requestOrder: 0
        );

        var requestItems = ImmutableArray.Create(
            NumericDocumentReferenceBackedNestedTopologyBuilders.BuildParentRequestItemWithNaturalKey(
                ParentNumericNaturalKey,
                arrayIndex: 0
            )
        );

        var request = NumericDocumentReferenceBackedNestedTopologyBuilders.BuildRequest(body, requestItems);

        var flattened = NumericDocumentReferenceBackedNestedTopologyBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [candidate]
        );

        // Visible-stored rows: parent (numeric natural-key form, as Core emits) + two stored
        // children whose ParentAddress.AncestorCollectionInstances[0] carries the parent's
        // numeric natural-key form. With the unsafe short-circuit, the index keyed by the
        // numeric natural-key (Int64 12345) would never match the recursion lookup keyed by
        // the canonicalized backend id (Int64 67890).
        var storedRows = ImmutableArray.Create(
            NumericDocumentReferenceBackedNestedTopologyBuilders.BuildParentStoredRowWithNaturalKey(
                ParentNumericNaturalKey
            ),
            NumericDocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowWithNaturalKeyParent(
                ParentNumericNaturalKey,
                "C1"
            ),
            NumericDocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowWithNaturalKeyParent(
                ParentNumericNaturalKey,
                "C2"
            )
        );

        var context = NumericDocumentReferenceBackedNestedTopologyBuilders.BuildContext(request, storedRows);

        var currentState = NumericDocumentReferenceBackedNestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentItemId, DocumentId, 1, ParentReferenceDocumentId, ParentNumericNaturalKey],
            ],
            childRows:
            [
                [ChildA1ItemId, ParentItemId, 1, "C1"],
                [ChildA2ItemId, ParentItemId, 2, "C2"],
            ]
        );

        var resolvedRefs = NumericDocumentReferenceBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
            ParentNumericNaturalKey,
            ParentReferenceDocumentId
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            _tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_finds_nested_children_under_numeric_natural_key_parent()
    {
        // Discriminator: two visible-stored children under the matched parent address but
        // ZERO visible-request items, plus TWO current rows. Correct outcome:
        // delete-by-absence — children TableState carries ZERO merged rows.
        //
        // Without the fix, the unsafe TryGetValue<long> short-circuit treats the numeric
        // natural key (Int64 12345) as already canonicalized; the index keeps 12345 in the
        // ancestor part; recursion lookup keyed by ScopeInstanceAddress with Int64 67890
        // (the canonicalized backend id) misses; visibleStoredRowsForScope is empty inside
        // the planner; the planner falsely routes the two current rows through
        // HiddenPreserveEntry, and TWO false-preserve merged rows surface.
        var childrenBuilder = _tableStateBuilders[_childrenPlan.TableModel.Table];
        childrenBuilder.HasContent.Should().BeTrue("recursion must visit the children scope");
        var state = childrenBuilder.Build();
        state
            .CurrentRows.Length.Should()
            .Be(2, "the children's current rows feed the persister's delete-by-absence baseline");
        state
            .MergedRows.Length.Should()
            .Be(
                0,
                "delete-by-absence: visible-stored under matched parent with no request item "
                    + "means the rows are omitted from the merged sequence; the persister's "
                    + "set-difference deletes them. With the bug, the unsafe TryGetValue<long> "
                    + "short-circuit skips canonicalization on the numeric natural-key part, the "
                    + "visible-stored lookup misses on the recursion-built canonicalized address, "
                    + "and the planner falsely routes the children through HiddenPreserveEntry, "
                    + "producing 2 spurious merged rows."
            );
    }

    [Test]
    public void It_canonicalizes_numeric_natural_key_in_visible_stored_index_keys()
    {
        // Direct structural assertion on the per-merge index built at walker construction.
        // The walker MUST canonicalize numeric document-reference natural-key ancestor
        // identities at index-build time, replacing the natural-key value (Int64 12345)
        // with the resolved backend document id (Int64 67890). Without the fix, the
        // TryGetValue<long> short-circuit treats the numeric natural key as already
        // canonicalized and skips the resolver, leaving the index key carrying 12345 and
        // the recursion lookup at ScopeInstanceAddress("$.parents[*]",
        // [AncestorCollectionInstance("$.parents[*]", [Int64 67890])]) misses.
        var canonicalizedParentAncestor = new AncestorCollectionInstance(
            NumericDocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [
                new SemanticIdentityPart(
                    NumericDocumentReferenceBackedNestedTopologyBuilders.ParentReferenceSchoolIdRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
            ]
        );
        var canonicalizedParentAddress = new ScopeInstanceAddress(
            NumericDocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [canonicalizedParentAncestor]
        );
        var canonicalKey = (
            NumericDocumentReferenceBackedNestedTopologyBuilders.ChildrenScope,
            canonicalizedParentAddress
        );

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey(
                canonicalKey,
                "the index key must carry the canonicalized (Int64 backend document id) "
                    + "ancestor identity so recursion lookups (whose parent addresses are "
                    + "built from canonicalized stored rows) can find the bucket; the "
                    + "TryGetValue<long> short-circuit is unsafe because numeric natural "
                    + "keys parse as long but still require resolver-based canonicalization"
            );
    }
}

/// <summary>
/// Local builders for a 3-table nested-topology test plan with a document-reference-backed
/// parent collection at <c>$.parents[*]</c> whose natural-key part is NUMERIC (Int64
/// schoolId-style). Mirrors <see cref="DocumentReferenceBackedNestedTopologyBuilders"/> but
/// the natural-key column is <see cref="ScalarKind.Int64"/> rather than
/// <see cref="ScalarKind.String"/>.
/// </summary>
internal static class NumericDocumentReferenceBackedNestedTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");
    public const string ParentsScope = "$.parents[*]";
    public const string ChildrenScope = "$.parents[*].children[*]";
    public const string ParentReferenceObjectPath = "$.parents[*].parentReference";
    public const string ParentReferenceSchoolIdPath = "$.parents[*].parentReference.schoolId";
    public const string ParentReferenceSchoolIdRelativePath = "$.parentReference.schoolId";
    public const string ParentReferenceConcretePath = "$.parents[0].parentReference";

    public static readonly QualifiedResourceName ParentResource = new("Ed-Fi", "School");

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndChildrenPlan()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildDocumentReferenceBackedParentsCollectionPlan();
        var childrenPlan = BuildScalarChildrenCollectionPlan();

        var documentReferenceBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
            Table: parentsPlan.TableModel.Table,
            FkColumn: new DbColumnName("ParentReference_DocumentId"),
            TargetResource: ParentResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    new JsonPathExpression("$.schoolId", []),
                    new JsonPathExpression(ParentReferenceSchoolIdPath, []),
                    new DbColumnName("ParentReference_SchoolId")
                ),
            ]
        );

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "DocRefNumericNestedTest"),
                PhysicalSchema: _schema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [documentReferenceBinding],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
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
            Table: new DbTableName(_schema, "DocRefNumericNestedTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_DocRefNumericNestedTest",
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
            InsertSql: "INSERT INTO edfi.\"DocRefNumericNestedTest\" DEFAULT VALUES",
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

    private static TableWritePlan BuildDocumentReferenceBackedParentsCollectionPlan()
    {
        // Layout: [ParentItemId, ParentDocumentId, Ordinal, ParentReference_DocumentId,
        // ParentReference_SchoolId]. ParentReference_SchoolId is an Int64 scalar — the numeric
        // natural-key form distinguishes this fixture from the string-natural-key sibling.
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentDocIdColumn = new DbColumnModel(
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
            ColumnName: new DbColumnName("ParentReference_DocumentId"),
            Kind: ColumnKind.DocumentFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: ParentResource
        );
        var schoolIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentReference_SchoolId"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(ParentReferenceSchoolIdPath, []),
            TargetResource: null
        );
        DbColumnModel[] columns =
        [
            parentItemIdColumn,
            parentDocIdColumn,
            ordinalColumn,
            fkColumn,
            schoolIdColumn,
        ];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ParentsTable"),
            JsonScope: new JsonPathExpression(ParentsScope, []),
            Key: new TableKey(
                "PK_ParentsTable",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceSchoolIdRelativePath, []),
                        fkColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"ParentsTable\" VALUES (@ParentItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    parentDocIdColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    fkColumn,
                    new WriteValueSource.DocumentReference(0),
                    "ParentReference_DocumentId"
                ),
                new WriteColumnBinding(
                    schoolIdColumn,
                    new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
                            IdentityJsonPath: new JsonPathExpression("$.schoolId", []),
                            ReferenceJsonPath: new JsonPathExpression(ParentReferenceSchoolIdPath, [])
                        )
                    ),
                    "ParentReference_SchoolId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceSchoolIdRelativePath, []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ParentsTable\" SET X = @X WHERE \"ParentItemId\" = @ParentItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ParentsTable\" WHERE \"ParentItemId\" = @ParentItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ParentItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildScalarChildrenCollectionPlan()
    {
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    public static CollectionWriteCandidate BuildParentCandidate(
        TableWritePlan parentsPlan,
        long referenceDocumentId,
        long numericNaturalKey,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[parentsPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        // Stamp the reference document id at the FK slot (index 3) and the numeric natural-key
        // value at the derived ParentReference_SchoolId slot (index 4).
        values[3] = new FlattenedWriteValue.Literal(referenceDocumentId);
        values[4] = new FlattenedWriteValue.Literal(numericNaturalKey);

        return new CollectionWriteCandidate(
            tableWritePlan: parentsPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [referenceDocumentId],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                parentsPlan,
                [referenceDocumentId]
            )
        );
    }

    private static ImmutableArray<SemanticIdentityPart> ParentNumericNaturalKeyIdentity(
        long numericNaturalKey
    ) =>
        [
            new SemanticIdentityPart(
                ParentReferenceSchoolIdRelativePath,
                JsonValue.Create(numericNaturalKey),
                IsPresent: true
            ),
        ];

    private static ImmutableArray<SemanticIdentityPart> ChildIdentity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static ScopeInstanceAddress RootAddress() =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    private static ScopeInstanceAddress ParentRowAddressWithNaturalKey(long numericNaturalKey) =>
        new(
            ParentsScope,
            [new AncestorCollectionInstance(ParentsScope, ParentNumericNaturalKeyIdentity(numericNaturalKey))]
        );

    public static VisibleRequestCollectionItem BuildParentRequestItemWithNaturalKey(
        long numericNaturalKey,
        int arrayIndex
    ) =>
        new(
            new CollectionRowAddress(
                ParentsScope,
                RootAddress(),
                ParentNumericNaturalKeyIdentity(numericNaturalKey)
            ),
            Creatable: true,
            $"$.parents[{arrayIndex}]"
        );

    public static VisibleStoredCollectionRow BuildParentStoredRowWithNaturalKey(long numericNaturalKey) =>
        new(
            new CollectionRowAddress(
                ParentsScope,
                RootAddress(),
                ParentNumericNaturalKeyIdentity(numericNaturalKey)
            ),
            ImmutableArray<string>.Empty
        );

    public static VisibleStoredCollectionRow BuildChildStoredRowWithNaturalKeyParent(
        long parentNumericNaturalKey,
        string childIdentity
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddressWithNaturalKey(parentNumericNaturalKey),
                ChildIdentity(childIdentity)
            ),
            ImmutableArray<string>.Empty
        );

    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootPlan,
        ImmutableArray<CollectionWriteCandidate> parentCandidates
    )
    {
        FlattenedWriteValue[] rootValues = [new FlattenedWriteValue.Literal(345L)];
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, collectionCandidates: parentCandidates)
        );
    }

    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems
    ) =>
        new(
            writableBody,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            visibleItems
        );

    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows
    ) => new(request, new JsonObject(), ImmutableArray<StoredScopeState>.Empty, storedRows);

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> childRows
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-2222-3333-4444-cccccccccccc"),
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
            ],
            []
        );

    public static ResolvedReferenceSet BuildResolvedReferenceSet(
        long numericNaturalKey,
        long referenceDocumentId
    ) =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath(ParentReferenceConcretePath)] = new ResolvedDocumentReference(
                    Reference: new DocumentReference(
                        ResourceInfo: new BaseResourceInfo(
                            new ProjectName("Ed-Fi"),
                            new ResourceName("School"),
                            false
                        ),
                        DocumentIdentity: new DocumentIdentity([
                            new DocumentIdentityElement(
                                new JsonPath("$.schoolId"),
                                numericNaturalKey.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            ),
                        ]),
                        ReferentialId: new ReferentialId(Guid.NewGuid()),
                        Path: new JsonPath(ParentReferenceConcretePath)
                    ),
                    DocumentId: referenceDocumentId,
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
}

/// <summary>
/// Local builders for a 3-table nested-topology test plan with a document-reference-backed
/// parent collection at <c>$.parents[*]</c> (natural-key form in Core-emitted addresses;
/// Int64 backend reference-document-id in the canonicalized current state) and a
/// scalar-identity nested children collection at <c>$.parents[*].children[*]</c>.
/// </summary>
internal static class DocumentReferenceBackedNestedTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");
    public const string ParentsScope = "$.parents[*]";
    public const string ChildrenScope = "$.parents[*].children[*]";
    public const string ParentReferenceObjectPath = "$.parents[*].parentReference";
    public const string ParentReferenceParentIdPath = "$.parents[*].parentReference.parentId";
    public const string ParentReferenceParentIdRelativePath = "$.parentReference.parentId";
    public const string ParentReferenceConcretePath = "$.parents[0].parentReference";
    public const string ParentNaturalKey = "nk-p1";

    public static readonly QualifiedResourceName ParentResource = new("Ed-Fi", "Parent");

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndChildrenPlan()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildDocumentReferenceBackedParentsCollectionPlan();
        var childrenPlan = BuildScalarChildrenCollectionPlan();

        var documentReferenceBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
            Table: parentsPlan.TableModel.Table,
            FkColumn: new DbColumnName("ParentReference_DocumentId"),
            TargetResource: ParentResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    new JsonPathExpression("$.parentId", []),
                    new JsonPathExpression(ParentReferenceParentIdPath, []),
                    new DbColumnName("ParentReference_ParentId")
                ),
            ]
        );

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "DocRefNestedTest"),
                PhysicalSchema: _schema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [documentReferenceBinding],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
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
            Table: new DbTableName(_schema, "DocRefNestedTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_DocRefNestedTest",
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
            InsertSql: "INSERT INTO edfi.\"DocRefNestedTest\" DEFAULT VALUES",
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

    private static TableWritePlan BuildDocumentReferenceBackedParentsCollectionPlan()
    {
        // Layout: [ParentItemId, ParentDocumentId, Ordinal, ParentReference_DocumentId,
        // ParentReference_ParentId]. ParentReference_DocumentId is a DocumentFk column that
        // backs the table's single semantic identity binding (matching the Slice 4
        // reference-backed top-level fixture's shape, simplified to one identity part).
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentDocIdColumn = new DbColumnModel(
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
            ColumnName: new DbColumnName("ParentReference_DocumentId"),
            Kind: ColumnKind.DocumentFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: ParentResource
        );
        var parentIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentReference_ParentId"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(ParentReferenceParentIdPath, []),
            TargetResource: null
        );
        DbColumnModel[] columns =
        [
            parentItemIdColumn,
            parentDocIdColumn,
            ordinalColumn,
            fkColumn,
            parentIdColumn,
        ];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ParentsTable"),
            JsonScope: new JsonPathExpression(ParentsScope, []),
            Key: new TableKey(
                "PK_ParentsTable",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceParentIdRelativePath, []),
                        fkColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"ParentsTable\" VALUES (@ParentItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    parentDocIdColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    fkColumn,
                    new WriteValueSource.DocumentReference(0),
                    "ParentReference_DocumentId"
                ),
                new WriteColumnBinding(
                    parentIdColumn,
                    new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
                            IdentityJsonPath: new JsonPathExpression("$.parentId", []),
                            ReferenceJsonPath: new JsonPathExpression(ParentReferenceParentIdPath, [])
                        )
                    ),
                    "ParentReference_ParentId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceParentIdRelativePath, []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ParentsTable\" SET X = @X WHERE \"ParentItemId\" = @ParentItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ParentsTable\" WHERE \"ParentItemId\" = @ParentItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ParentItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildScalarChildrenCollectionPlan()
    {
        // Layout: [ChildItemId, ParentItemId, Ordinal, IdentityField0]. ParentItemId is a
        // ParentKeyPart referring to slot 0 of the parents table's PhysicalRowIdentity.
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    public static CollectionWriteCandidate BuildParentCandidate(
        TableWritePlan parentsPlan,
        long referenceDocumentId,
        string parentNaturalKey,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[parentsPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        // Stamp the reference document id at the FK slot (index 3) and the natural key
        // value at the derived ParentReference_ParentId slot (index 4).
        values[3] = new FlattenedWriteValue.Literal(referenceDocumentId);
        values[4] = new FlattenedWriteValue.Literal(parentNaturalKey);

        return new CollectionWriteCandidate(
            tableWritePlan: parentsPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [referenceDocumentId],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                parentsPlan,
                [referenceDocumentId]
            )
        );
    }

    private static ImmutableArray<SemanticIdentityPart> ParentNaturalKeyIdentity(string naturalKey) =>
        [
            new SemanticIdentityPart(
                ParentReferenceParentIdRelativePath,
                JsonValue.Create(naturalKey),
                IsPresent: true
            ),
        ];

    private static ImmutableArray<SemanticIdentityPart> ChildIdentity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static ScopeInstanceAddress RootAddress() =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    private static ScopeInstanceAddress ParentRowAddressWithNaturalKey(string naturalKey) =>
        new(
            ParentsScope,
            [new AncestorCollectionInstance(ParentsScope, ParentNaturalKeyIdentity(naturalKey))]
        );

    public static VisibleRequestCollectionItem BuildParentRequestItemWithNaturalKey(
        string naturalKey,
        int arrayIndex
    ) =>
        new(
            new CollectionRowAddress(ParentsScope, RootAddress(), ParentNaturalKeyIdentity(naturalKey)),
            Creatable: true,
            $"$.parents[{arrayIndex}]"
        );

    public static VisibleStoredCollectionRow BuildParentStoredRowWithNaturalKey(string naturalKey) =>
        new(
            new CollectionRowAddress(ParentsScope, RootAddress(), ParentNaturalKeyIdentity(naturalKey)),
            ImmutableArray<string>.Empty
        );

    public static VisibleStoredCollectionRow BuildChildStoredRowWithNaturalKeyParent(
        string parentNaturalKey,
        string childIdentity
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddressWithNaturalKey(parentNaturalKey),
                ChildIdentity(childIdentity)
            ),
            ImmutableArray<string>.Empty
        );

    /// <summary>
    /// Slice 5 CP2 (Task 13.9): builds a children visible-request item whose ParentAddress
    /// carries the parent's natural-key form (Core-emitted shape), simulating a nested child
    /// under a document-reference-backed parent. The walker must canonicalize the ancestor
    /// identity to the resolved DocumentId (Int64) so the recursion lookup hits.
    /// </summary>
    public static VisibleRequestCollectionItem BuildChildRequestItemWithNaturalKeyParent(
        string parentNaturalKey,
        string childIdentity,
        int parentArrayIndex,
        int childArrayIndex
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddressWithNaturalKey(parentNaturalKey),
                ChildIdentity(childIdentity)
            ),
            Creatable: true,
            $"$.parents[{parentArrayIndex}].children[{childArrayIndex}]"
        );

    /// <summary>
    /// Builds a CollectionWriteCandidate for a nested child row whose parent is a
    /// document-reference-backed parent. The child row's ParentKeyPart slot is left as a
    /// null Literal — the walker resolves it at emission time from the parent's
    /// PhysicalRowIdentity (mirrors <c>NestedTopologyBuilders.BuildChildCandidate</c>).
    /// </summary>
    public static CollectionWriteCandidate BuildChildCandidate(
        TableWritePlan childrenPlan,
        string childIdentity,
        int parentArrayIndex,
        int childArrayIndex
    )
    {
        var values = new FlattenedWriteValue[childrenPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(childIdentity);

        return new CollectionWriteCandidate(
            tableWritePlan: childrenPlan,
            ordinalPath: [parentArrayIndex, childArrayIndex],
            requestOrder: childArrayIndex,
            values: values,
            semanticIdentityValues: [childIdentity],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                childrenPlan,
                [childIdentity]
            )
        );
    }

    /// <summary>
    /// Slice 5 CP2 (Task 13.9): builds a parent CollectionWriteCandidate that carries nested
    /// children CollectionCandidates so the walker recurses into the children scope after
    /// inserting the parent. Mirrors <see cref="BuildParentCandidate"/> but accepts nested
    /// candidates explicitly.
    /// </summary>
    public static CollectionWriteCandidate BuildParentCandidateWithNestedChildren(
        TableWritePlan parentsPlan,
        long referenceDocumentId,
        string parentNaturalKey,
        int requestOrder,
        ImmutableArray<CollectionWriteCandidate> nestedChildren
    )
    {
        var values = new FlattenedWriteValue[parentsPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        // Stamp the reference document id at the FK slot (index 3) and the natural key
        // value at the derived ParentReference_ParentId slot (index 4).
        values[3] = new FlattenedWriteValue.Literal(referenceDocumentId);
        values[4] = new FlattenedWriteValue.Literal(parentNaturalKey);

        return new CollectionWriteCandidate(
            tableWritePlan: parentsPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [referenceDocumentId],
            collectionCandidates: nestedChildren,
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                parentsPlan,
                [referenceDocumentId]
            )
        );
    }

    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootPlan,
        ImmutableArray<CollectionWriteCandidate> parentCandidates
    )
    {
        FlattenedWriteValue[] rootValues = [new FlattenedWriteValue.Literal(345L)];
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, collectionCandidates: parentCandidates)
        );
    }

    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems
    ) =>
        new(
            writableBody,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            visibleItems
        );

    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows
    ) => new(request, new JsonObject(), ImmutableArray<StoredScopeState>.Empty, storedRows);

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> childRows
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-2222-3333-4444-bbbbbbbbbbbb"),
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
            ],
            []
        );

    public static ResolvedReferenceSet BuildResolvedReferenceSet(
        string parentNaturalKey,
        long referenceDocumentId
    ) =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath(ParentReferenceConcretePath)] = new ResolvedDocumentReference(
                    Reference: new DocumentReference(
                        ResourceInfo: new BaseResourceInfo(
                            new ProjectName("Ed-Fi"),
                            new ResourceName("Parent"),
                            false
                        ),
                        DocumentIdentity: new DocumentIdentity([
                            new DocumentIdentityElement(new JsonPath("$.parentId"), parentNaturalKey),
                        ]),
                        ReferentialId: new ReferentialId(Guid.NewGuid()),
                        Path: new JsonPath(ParentReferenceConcretePath)
                    ),
                    DocumentId: referenceDocumentId,
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
}

/// <summary>
/// Slice 5 CP2 (Task 12.7): regression for the descriptor-blind raw-JSON equality in
/// <c>TryResolveAncestorDocumentReferenceIdFromCurrentRows</c>'s <c>sameReferenceParts</c>
/// loop. When a document-reference natural key is COMPOSITE — combining a scalar numeric
/// part (e.g., <c>programReference.programId</c>) AND a descriptor URI part (e.g.,
/// <c>programReference.programTypeDescriptor</c>) — the stored ancestor identity carries
/// the URI string at the descriptor slot while the current row carries the canonical
/// Int64 descriptor id. Raw JSON equality cannot match the URI string against the Int64,
/// and the helper fell through to the fail-closed
/// <see cref="InvalidOperationException"/> even when the URI was resolvable via the
/// request-cycle URI cache.
/// <para>
/// Fixture mirrors <see cref="Given_a_document_reference_backed_parent_collection_with_nested_children"/>
/// but adds a descriptor URI part to the parent reference's natural key. With the fix, the
/// canonicalization scan invokes the existing
/// <c>DocumentReferenceIdentityPartsMatch</c> helper which converts the stored URI to its
/// canonical Int64 via the URI cache before comparing to the current row's column value;
/// the parent natural key resolves uniquely to the matched current row's
/// <c>ParentReference_DocumentId</c>, the ancestor identity is rewritten, and the
/// recursion-side index lookup hits.
/// </para>
/// </summary>
[TestFixture]
public class Given_a_composite_descriptor_and_scalar_document_reference_backed_parent_collection_with_nested_children
{
    private const long DocumentId = 345L;
    private const long ParentItemId = 100L;
    private const long ParentReferenceDocumentId = 555L;
    private const long ProgramId = 42L;
    private const long RegularDescriptorId = 77L;
    private const string RegularUri = "uri://ed-fi.org/ProgramTypeDescriptor#Regular";
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;

    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Request body has no programs — delete-by-absence shape mirrors the sibling
        // document-reference fixtures. The bug fires during walker construction (ancestor
        // canonicalization at index-build time), so the request shape is incidental; what
        // matters is that the visible-stored children's ParentAddress carries the COMPOSITE
        // (URI + scalar) ancestor identity that the canonicalization pass must resolve.
        var body = new JsonObject { ["parents"] = new JsonArray() };

        var flattened =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildFlattenedWriteSet(
                rootPlan,
                ImmutableArray<CollectionWriteCandidate>.Empty
            );

        var request = CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        // Visible-stored rows: parent (composite natural-key form: scalar Int64 + URI) and
        // two stored children whose ParentAddress.AncestorCollectionInstances[0] carries
        // the parent's COMPOSITE natural-key form. Without descriptor-aware comparison in
        // the ancestor canonicalization scan, the URI part fails raw-equality against the
        // current row's Int64 descriptor id and the helper throws fail-closed.
        var storedRows = ImmutableArray.Create(
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildParentStoredRow(
                ProgramId,
                RegularUri
            ),
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowUnderParent(
                ProgramId,
                RegularUri,
                "C1"
            ),
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowUnderParent(
                ProgramId,
                RegularUri,
                "C2"
            )
        );

        var context = CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildContext(
            request,
            storedRows
        );

        // Current DB rows for parents: the descriptor column carries the canonical Int64
        // descriptor id (RegularDescriptorId), NOT the URI. This is the asymmetry the bug
        // hinges on — stored ancestor identity has URI; current row has Int64.
        var currentState =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildCurrentState(
                rootPlan,
                parentsPlan,
                childrenPlan,
                DocumentId,
                parentRows:
                [
                    [ParentItemId, DocumentId, 1, ParentReferenceDocumentId, ProgramId, RegularDescriptorId],
                ],
                childRows:
                [
                    [ChildA1ItemId, ParentItemId, 1, "C1"],
                    [ChildA2ItemId, ParentItemId, 2, "C2"],
                ]
            );

        // Resolved-reference set seeds the URI cache so the canonicalization scan can
        // convert the URI to its Int64 id during DocumentReferenceIdentityPartsMatch.
        var resolvedRefs =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildResolvedReferenceSet(
                ProgramId,
                RegularUri,
                RegularDescriptorId,
                ParentReferenceDocumentId
            );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_resolves_the_composite_natural_key_to_DocumentId_via_descriptor_aware_comparison()
    {
        // Without the fix: TryResolveAncestorDocumentReferenceIdFromCurrentRows throws
        // InvalidOperationException ("Cannot canonicalize document-reference ancestor
        // identity...") during walker construction because the raw-JSON equality scan
        // cannot match the stored URI string against the current row's Int64 descriptor
        // id, even though the URI is present in the cache. The exception fires from
        // CanonicalizeAncestorDocumentReferenceParts via CanonicalizeAddressAncestors
        // during walker construction, so the [SetUp] does not complete and Setup throws.
        //
        // With the fix: the scan delegates per-part comparison to
        // DocumentReferenceIdentityPartsMatch, which canonicalizes the descriptor-FK
        // part's stored URI to Int64 via the URI cache; the scalar part still uses raw
        // equality; the unique current row is matched, the ParentReference_DocumentId
        // value (555) is returned, and the ancestor identity is rewritten to the
        // canonical DocumentId form. Walker construction completes; WalkChildren returns
        // a null outcome (no creatability rejection).
        _walker
            .Should()
            .NotBeNull("walker construction must complete without fail-closed throw");
    }

    [Test]
    public void It_finds_nested_children_under_composite_descriptor_scalar_parent()
    {
        // Direct structural assertion: the per-merge visible-stored index built at walker
        // construction must carry a bucket keyed by the canonicalized
        // (Int64 backend document id) ancestor identity. The two stored children of the
        // composite-key parent must land in that bucket.
        //
        // The CanonicalizeAncestorDocumentReferenceParts pass rewrites EVERY
        // documentReferenceParts slot to the resolved DocumentId — both the programId slot
        // and the programTypeDescriptor slot land at ParentReference_DocumentId (555),
        // which mirrors how the existing single-slot fixtures key their canonical index.
        var canonicalizedParentAncestor = new AncestorCollectionInstance(
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [
                new SemanticIdentityPart(
                    CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentReferenceProgramIdRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
                new SemanticIdentityPart(
                    CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentReferenceProgramTypeDescriptorRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
            ]
        );
        var canonicalizedParentAddress = new ScopeInstanceAddress(
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [canonicalizedParentAncestor]
        );
        var canonicalKey = (
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ChildrenScope,
            canonicalizedParentAddress
        );

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey(
                canonicalKey,
                "with the descriptor-aware ancestor canonicalization, the index key carries "
                    + "the canonicalized (Int64 backend document id) form for both parts "
                    + "of the composite reference; the two stored children land in this bucket"
            );

        _walker
            .VisibleStoredRowsByChildScopeAndParent[canonicalKey]
            .Length.Should()
            .Be(
                2,
                "both stored children under the composite-key parent must be indexed in the "
                    + "canonicalized parent's bucket — the descriptor-aware match resolved "
                    + "the URI part of the natural key against the current row's Int64 column"
            );
    }
}

/// <summary>
/// Slice 5 CP2 closeout: regression for the scalar-only fallback gap in
/// <c>TryResolveAncestorDocumentReferenceIdFromCurrentRows</c>. The top-level path already
/// resolves a composite document-reference natural key by matching only non-descriptor
/// parts when the descriptor URI is absent from the request-cycle cache and those scalar
/// parts uniquely identify the current row. Ancestor canonicalization must do the same so
/// nested children of reference-backed parents are not lost when descriptor references are
/// absent from <see cref="ResolvedReferenceSet"/>.
/// </summary>
[TestFixture]
public class Given_a_composite_descriptor_and_scalar_document_reference_backed_parent_collection_with_nested_children_when_descriptor_cache_misses
{
    private const long DocumentId = 345L;
    private const long HiddenParentItemId = 90L;
    private const long ParentItemId = 100L;
    private const long HiddenParentReferenceDocumentId = 444L;
    private const long ParentReferenceDocumentId = 555L;
    private const long ProgramId = 42L;
    private const long HiddenProgramId = 99L;
    private const long RegularDescriptorId = 77L;
    private const long HiddenDescriptorId = 88L;
    private const string RegularUri = "uri://ed-fi.org/ProgramTypeDescriptor#Regular";
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;

    private ProfileCollectionWalker _walker = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject { ["parents"] = new JsonArray() };

        var flattened =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildFlattenedWriteSet(
                rootPlan,
                ImmutableArray<CollectionWriteCandidate>.Empty
            );

        var request = CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        var storedRows = ImmutableArray.Create(
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildParentStoredRow(
                ProgramId,
                RegularUri
            ),
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowUnderParent(
                ProgramId,
                RegularUri,
                "C1"
            ),
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildChildStoredRowUnderParent(
                ProgramId,
                RegularUri,
                "C2"
            )
        );

        var context = CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildContext(
            request,
            storedRows
        );

        // Descriptor cache is empty. Full natural-key matching cannot convert RegularUri to
        // RegularDescriptorId. The hidden current parent row makes positional fallback
        // insufficient, but the scalar ProgramId part still uniquely identifies ParentItemId.
        var resolvedRefs = EmptyResolvedReferenceSet();

        var currentState =
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.BuildCurrentState(
                rootPlan,
                parentsPlan,
                childrenPlan,
                DocumentId,
                parentRows:
                [
                    [
                        HiddenParentItemId,
                        DocumentId,
                        1,
                        HiddenParentReferenceDocumentId,
                        HiddenProgramId,
                        HiddenDescriptorId,
                    ],
                    [ParentItemId, DocumentId, 2, ParentReferenceDocumentId, ProgramId, RegularDescriptorId],
                ],
                childRows:
                [
                    [ChildA1ItemId, ParentItemId, 1, "C1"],
                    [ChildA2ItemId, ParentItemId, 2, "C2"],
                ]
            );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: resolvedRefs
        );

        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(
            mergeRequest,
            FlatteningResolvedReferenceLookupSet.Create(plan, resolvedRefs),
            tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        var outcome = _walker.WalkChildren(rootContext, WalkMode.Normal);
        outcome.Should().BeNull("the walk completes successfully (no creatability rejection)");
    }

    [Test]
    public void It_constructs_the_walker_and_walks_children_successfully() =>
        _walker
            .Should()
            .NotBeNull(
                "ancestor document-reference canonicalization should use the unique scalar natural-key part when descriptor URI resolution misses"
            );

    [Test]
    public void It_indexes_nested_children_under_the_scalar_fallback_canonicalized_ancestor_key()
    {
        var canonicalizedParentAncestor = new AncestorCollectionInstance(
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [
                new SemanticIdentityPart(
                    CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentReferenceProgramIdRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
                new SemanticIdentityPart(
                    CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentReferenceProgramTypeDescriptorRelativePath,
                    JsonValue.Create(ParentReferenceDocumentId),
                    IsPresent: true
                ),
            ]
        );
        var canonicalizedParentAddress = new ScopeInstanceAddress(
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ParentsScope,
            [canonicalizedParentAncestor]
        );
        var canonicalKey = (
            CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders.ChildrenScope,
            canonicalizedParentAddress
        );

        _walker
            .VisibleStoredRowsByChildScopeAndParent.Should()
            .ContainKey(
                canonicalKey,
                "the scalar-only fallback should resolve the parent reference to the backend document id even when the descriptor URI is absent from the cache"
            );

        _walker.VisibleStoredRowsByChildScopeAndParent[canonicalKey].Length.Should().Be(2);
    }
}

/// <summary>
/// Local builders for a 3-table nested-topology test plan with a document-reference-backed
/// parent collection at <c>$.parents[*]</c> whose natural key is COMPOSITE: a scalar Int64
/// part (<c>programReference.programId</c>) and a descriptor-URI part
/// (<c>programReference.programTypeDescriptor</c> backed by a
/// <see cref="ColumnKind.DescriptorFk"/> column). Mirrors the SchoolProgram top-level
/// fixture in <c>RelationalWriteProfileMergeSynthesizerTests.cs</c> but as a nested-topology
/// plan with a children scope to exercise ancestor canonicalization.
/// </summary>
internal static class CompositeDescriptorScalarDocumentReferenceBackedNestedTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");
    public const string ParentsScope = "$.parents[*]";
    public const string ChildrenScope = "$.parents[*].children[*]";
    public const string ParentReferenceObjectPath = "$.parents[*].parentReference";
    public const string ParentReferenceProgramIdPath = "$.parents[*].parentReference.programId";
    public const string ParentReferenceProgramTypeDescriptorPath =
        "$.parents[*].parentReference.programTypeDescriptor";
    public const string ParentReferenceProgramIdRelativePath = "$.parentReference.programId";
    public const string ParentReferenceProgramTypeDescriptorRelativePath =
        "$.parentReference.programTypeDescriptor";
    public const string ParentReferenceConcretePath = "$.parents[0].parentReference";
    public const string ParentReferenceProgramTypeDescriptorConcretePath =
        "$.parents[0].parentReference.programTypeDescriptor";

    public static readonly QualifiedResourceName ParentResource = new("Ed-Fi", "Program");
    public static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "ProgramTypeDescriptor");

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndChildrenPlan()
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildCompositeReferenceBackedParentsCollectionPlan();
        var childrenPlan = BuildScalarChildrenCollectionPlan();

        var documentReferenceBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
            Table: parentsPlan.TableModel.Table,
            FkColumn: new DbColumnName("ParentReference_DocumentId"),
            TargetResource: ParentResource,
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    new JsonPathExpression("$.programId", []),
                    new JsonPathExpression(ParentReferenceProgramIdPath, []),
                    new DbColumnName("ParentReference_ProgramId")
                ),
                new ReferenceIdentityBinding(
                    new JsonPathExpression("$.programTypeDescriptor", []),
                    new JsonPathExpression(ParentReferenceProgramTypeDescriptorPath, []),
                    new DbColumnName("ParentReference_ProgramTypeDescriptor_Id")
                ),
            ]
        );

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "DocRefCompositeNestedTest"),
                PhysicalSchema: _schema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    childrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [documentReferenceBinding],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: new JsonPathExpression(
                            ParentReferenceProgramTypeDescriptorPath,
                            []
                        ),
                        Table: parentsPlan.TableModel.Table,
                        FkColumn: new DbColumnName("ParentReference_ProgramTypeDescriptor_Id"),
                        DescriptorResource: DescriptorResource
                    ),
                ]
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );
        return (resourceWritePlan, parentsPlan, childrenPlan);
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
            Table: new DbTableName(_schema, "DocRefCompositeNestedTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_DocRefCompositeNestedTest",
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
            InsertSql: "INSERT INTO edfi.\"DocRefCompositeNestedTest\" DEFAULT VALUES",
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

    private static TableWritePlan BuildCompositeReferenceBackedParentsCollectionPlan()
    {
        // Layout: [ParentItemId, ParentDocumentId, Ordinal, ParentReference_DocumentId,
        // ParentReference_ProgramId, ParentReference_ProgramTypeDescriptor_Id].
        // ProgramId is a scalar Int64; ProgramTypeDescriptor_Id is a DescriptorFk Int64
        // — the canonical id resolved from the URI. Both feed the table's two semantic
        // identity bindings (the COMPOSITE natural key).
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentDocIdColumn = new DbColumnModel(
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
            ColumnName: new DbColumnName("ParentReference_DocumentId"),
            Kind: ColumnKind.DocumentFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: ParentResource
        );
        var programIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentReference_ProgramId"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(ParentReferenceProgramIdPath, []),
            TargetResource: null
        );
        var programTypeDescriptorColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentReference_ProgramTypeDescriptor_Id"),
            Kind: ColumnKind.DescriptorFk,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression(ParentReferenceProgramTypeDescriptorPath, []),
            TargetResource: DescriptorResource
        );
        DbColumnModel[] columns =
        [
            parentItemIdColumn,
            parentDocIdColumn,
            ordinalColumn,
            fkColumn,
            programIdColumn,
            programTypeDescriptorColumn,
        ];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ParentsTable"),
            JsonScope: new JsonPathExpression(ParentsScope, []),
            Key: new TableKey(
                "PK_ParentsTable",
                [new DbKeyColumn(new DbColumnName("ParentItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ParentItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceProgramIdRelativePath, []),
                        fkColumn.ColumnName
                    ),
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceProgramTypeDescriptorRelativePath, []),
                        fkColumn.ColumnName
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.\"ParentsTable\" VALUES (@ParentItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "ParentItemId"
                ),
                new WriteColumnBinding(
                    parentDocIdColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    fkColumn,
                    new WriteValueSource.DocumentReference(0),
                    "ParentReference_DocumentId"
                ),
                new WriteColumnBinding(
                    programIdColumn,
                    new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
                            IdentityJsonPath: new JsonPathExpression("$.programId", []),
                            ReferenceJsonPath: new JsonPathExpression(ParentReferenceProgramIdPath, [])
                        )
                    ),
                    "ParentReference_ProgramId"
                ),
                new WriteColumnBinding(
                    programTypeDescriptorColumn,
                    new WriteValueSource.ReferenceDerived(
                        new ReferenceDerivedValueSourceMetadata(
                            BindingIndex: 0,
                            ReferenceObjectPath: new JsonPathExpression(ParentReferenceObjectPath, []),
                            IdentityJsonPath: new JsonPathExpression("$.programTypeDescriptor", []),
                            ReferenceJsonPath: new JsonPathExpression(
                                ParentReferenceProgramTypeDescriptorPath,
                                []
                            )
                        )
                    ),
                    "ParentReference_ProgramTypeDescriptor_Id"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceProgramIdRelativePath, []),
                        3
                    ),
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(ParentReferenceProgramTypeDescriptorRelativePath, []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ParentsTable\" SET X = @X WHERE \"ParentItemId\" = @ParentItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ParentsTable\" WHERE \"ParentItemId\" = @ParentItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 5, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ParentItemId"),
                0
            )
        );
    }

    private static TableWritePlan BuildScalarChildrenCollectionPlan()
    {
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "ChildrenTable"),
            JsonScope: new JsonPathExpression(ChildrenScope, []),
            Key: new TableKey(
                "PK_ChildrenTable",
                [new DbKeyColumn(new DbColumnName("ChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO edfi.\"ChildrenTable\" VALUES (@ChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(childItemIdColumn, new WriteValueSource.Precomputed(), "ChildItemId"),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"ChildrenTable\" SET X = @X WHERE \"ChildItemId\" = @ChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"ChildrenTable\" WHERE \"ChildItemId\" = @ChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("ChildItemId"),
                0
            )
        );
    }

    private static ImmutableArray<SemanticIdentityPart> ParentCompositeNaturalKeyIdentity(
        long programId,
        string programTypeUri
    ) =>
        [
            new SemanticIdentityPart(
                ParentReferenceProgramIdRelativePath,
                JsonValue.Create(programId),
                IsPresent: true
            ),
            new SemanticIdentityPart(
                ParentReferenceProgramTypeDescriptorRelativePath,
                JsonValue.Create(programTypeUri),
                IsPresent: true
            ),
        ];

    private static ImmutableArray<SemanticIdentityPart> ChildIdentity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static ScopeInstanceAddress RootAddress() =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    private static ScopeInstanceAddress ParentRowAddress(long programId, string programTypeUri) =>
        new(
            ParentsScope,
            [
                new AncestorCollectionInstance(
                    ParentsScope,
                    ParentCompositeNaturalKeyIdentity(programId, programTypeUri)
                ),
            ]
        );

    public static VisibleStoredCollectionRow BuildParentStoredRow(long programId, string programTypeUri) =>
        new(
            new CollectionRowAddress(
                ParentsScope,
                RootAddress(),
                ParentCompositeNaturalKeyIdentity(programId, programTypeUri)
            ),
            ImmutableArray<string>.Empty
        );

    public static VisibleStoredCollectionRow BuildChildStoredRowUnderParent(
        long parentProgramId,
        string parentProgramTypeUri,
        string childIdentity
    ) =>
        new(
            new CollectionRowAddress(
                ChildrenScope,
                ParentRowAddress(parentProgramId, parentProgramTypeUri),
                ChildIdentity(childIdentity)
            ),
            ImmutableArray<string>.Empty
        );

    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootPlan,
        ImmutableArray<CollectionWriteCandidate> parentCandidates
    )
    {
        FlattenedWriteValue[] rootValues = [new FlattenedWriteValue.Literal(345L)];
        return new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, rootValues, collectionCandidates: parentCandidates)
        );
    }

    public static ProfileAppliedWriteRequest BuildRequest(
        JsonNode writableBody,
        ImmutableArray<VisibleRequestCollectionItem> visibleItems
    ) =>
        new(
            writableBody,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            visibleItems
        );

    public static ProfileAppliedWriteContext BuildContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<VisibleStoredCollectionRow> storedRows
    ) => new(request, new JsonObject(), ImmutableArray<StoredScopeState>.Empty, storedRows);

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> childRows
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("aaaaaaaa-3333-4444-5555-dddddddddddd"),
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
            ],
            []
        );

    public static ResolvedReferenceSet BuildResolvedReferenceSet(
        long programId,
        string programTypeUri,
        long programTypeDescriptorId,
        long parentReferenceDocumentId
    ) =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
            {
                [new JsonPath(ParentReferenceConcretePath)] = new ResolvedDocumentReference(
                    Reference: new DocumentReference(
                        ResourceInfo: new BaseResourceInfo(
                            new ProjectName("Ed-Fi"),
                            new ResourceName("Program"),
                            false
                        ),
                        DocumentIdentity: new DocumentIdentity([
                            new DocumentIdentityElement(
                                new JsonPath("$.programId"),
                                programId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            ),
                            new DocumentIdentityElement(
                                new JsonPath("$.programTypeDescriptor"),
                                programTypeUri
                            ),
                        ]),
                        ReferentialId: new ReferentialId(Guid.NewGuid()),
                        Path: new JsonPath(ParentReferenceConcretePath)
                    ),
                    DocumentId: parentReferenceDocumentId,
                    ResourceKeyId: 11
                ),
            },
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [new JsonPath(ParentReferenceProgramTypeDescriptorConcretePath)] =
                    new ResolvedDescriptorReference(
                        new DescriptorReference(
                            new BaseResourceInfo(
                                new ProjectName("Ed-Fi"),
                                new ResourceName("ProgramTypeDescriptor"),
                                false
                            ),
                            new DocumentIdentity([
                                new DocumentIdentityElement(
                                    DocumentIdentity.DescriptorIdentityJsonPath,
                                    programTypeUri
                                ),
                            ]),
                            new ReferentialId(Guid.NewGuid()),
                            new JsonPath(ParentReferenceProgramTypeDescriptorConcretePath)
                        ),
                        programTypeDescriptorId,
                        13
                    ),
            },
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );
}

/// <summary>
/// Slice 5 CP4 Task 25: a collection-aligned 1:1 extension scope can host its own nested
/// child collection at <c>$.parents[*]._ext.aligned.children[*]</c>. CP3 wired the walker's
/// recursion shape (after the aligned dispatch returns Update/Insert, the walker re-enters
/// <see cref="ProfileCollectionWalker.WalkChildren"/> with
/// <c>RequestSubstructure: CandidateAttachedAlignedScopeData</c>) but the recursion's
/// child-candidate enumeration was a no-op for that substructure shape. CP4 fills it in
/// via <c>ResolveCollectionCandidatesForParent</c>'s
/// <see cref="CandidateAttachedAlignedScopeData"/> case so children attached under the
/// aligned scope's <c>CollectionCandidates</c> participate in the per-(scope,parent-instance)
/// merge.
///
/// <para>Topology: two parents (A, B) each with a <c>VisiblePresent</c> aligned scope. Each
/// aligned scope owns two child rows in storage. The profile request keeps both children
/// visible under each aligned scope and supplies matching candidates. The mock aligned
/// dispatch returns <c>Update</c> with a single merged row whose values carry the parent's
/// <c>ParentItemId</c> at slot 0. Expected: the children table state aggregates four merged
/// rows - two under <c>ParentAItemId</c> and two under <c>ParentBItemId</c>.</para>
/// </summary>
[TestFixture]
public class Given_a_collection_row_with_an_aligned_extension_scope_containing_a_child_collection
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;
    private const long ParentBItemId = 200L;
    private const long ChildA1ItemId = 1001L;
    private const long ChildA2ItemId = 1002L;
    private const long ChildB1ItemId = 2001L;
    private const long ChildB2ItemId = 2002L;

    private TableWritePlan _alignedChildrenPlan = null!;
    private Dictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, alignedPlan, alignedChildrenPlan) =
            AlignedExtensionScopeWithChildrenTopologyBuilders.BuildPlan();
        _alignedChildrenPlan = alignedChildrenPlan;
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = "A",
                    ["_ext"] = new JsonObject
                    {
                        ["aligned"] = new JsonObject
                        {
                            ["favoriteColor"] = "Blue",
                            ["children"] = new JsonArray(
                                new JsonObject { ["identityField0"] = "A1" },
                                new JsonObject { ["identityField0"] = "A2" }
                            ),
                        },
                    },
                },
                new JsonObject
                {
                    ["identityField0"] = "B",
                    ["_ext"] = new JsonObject
                    {
                        ["aligned"] = new JsonObject
                        {
                            ["favoriteColor"] = "Green",
                            ["children"] = new JsonArray(
                                new JsonObject { ["identityField0"] = "B1" },
                                new JsonObject { ["identityField0"] = "B2" }
                            ),
                        },
                    },
                }
            ),
        };

        // Each aligned scope carries a CandidateAttachedAlignedScopeData whose
        // CollectionCandidates supplies the two child candidates. CP3 Task 21 retired the
        // constructor fence on AttachedAlignedScopeData.CollectionCandidates so this is now
        // legal; CP4 Task 25 wires the walker recursion to consume them.
        var attachedA = new CandidateAttachedAlignedScopeData(
            alignedPlan,
            [new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Blue")],
            collectionCandidates:
            [
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildCandidate(
                    alignedChildrenPlan,
                    "A1",
                    0
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildCandidate(
                    alignedChildrenPlan,
                    "A2",
                    1
                ),
            ]
        );
        var attachedB = new CandidateAttachedAlignedScopeData(
            alignedPlan,
            [new FlattenedWriteValue.Literal(null), new FlattenedWriteValue.Literal("Green")],
            collectionCandidates:
            [
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildCandidate(
                    alignedChildrenPlan,
                    "B1",
                    0
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildCandidate(
                    alignedChildrenPlan,
                    "B2",
                    1
                ),
            ]
        );
        var candidateA = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "A",
            0,
            attachedAlignedScopeData: [attachedA]
        );
        var candidateB = NestedTopologyBuilders.BuildParentCandidate(
            parentsPlan,
            "B",
            1,
            attachedAlignedScopeData: [attachedB]
        );

        var request = AlignedExtensionScopeTopologyBuilders.BuildRequest(
            body,
            [
                NestedTopologyBuilders.BuildParentRequestItem("A", arrayIndex: 0),
                NestedTopologyBuilders.BuildParentRequestItem("B", arrayIndex: 1),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildRequestItem(
                    parentSemanticIdentity: "A",
                    childIdentity: "A1",
                    parentArrayIndex: 0,
                    childArrayIndex: 0
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildRequestItem(
                    parentSemanticIdentity: "A",
                    childIdentity: "A2",
                    parentArrayIndex: 0,
                    childArrayIndex: 1
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildRequestItem(
                    parentSemanticIdentity: "B",
                    childIdentity: "B1",
                    parentArrayIndex: 1,
                    childArrayIndex: 0
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildRequestItem(
                    parentSemanticIdentity: "B",
                    childIdentity: "B2",
                    parentArrayIndex: 1,
                    childArrayIndex: 1
                ),
            ],
            [
                AlignedExtensionScopeTopologyBuilders.BuildRequestScopeState(
                    "A",
                    ProfileVisibilityKind.VisiblePresent,
                    creatable: true
                ),
                AlignedExtensionScopeTopologyBuilders.BuildRequestScopeState(
                    "B",
                    ProfileVisibilityKind.VisiblePresent,
                    creatable: true
                ),
            ]
        );

        var context = AlignedExtensionScopeTopologyBuilders.BuildContext(
            request,
            [
                NestedTopologyBuilders.BuildParentStoredRow("A"),
                NestedTopologyBuilders.BuildParentStoredRow("B"),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildStoredRow(
                    parentSemanticIdentity: "A",
                    childIdentity: "A1"
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildStoredRow(
                    parentSemanticIdentity: "A",
                    childIdentity: "A2"
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildStoredRow(
                    parentSemanticIdentity: "B",
                    childIdentity: "B1"
                ),
                AlignedExtensionScopeWithChildrenTopologyBuilders.BuildAlignedChildStoredRow(
                    parentSemanticIdentity: "B",
                    childIdentity: "B2"
                ),
            ],
            [
                AlignedExtensionScopeTopologyBuilders.BuildStoredScopeState(
                    "A",
                    ProfileVisibilityKind.VisiblePresent
                ),
                AlignedExtensionScopeTopologyBuilders.BuildStoredScopeState(
                    "B",
                    ProfileVisibilityKind.VisiblePresent
                ),
            ]
        );

        var currentState = AlignedExtensionScopeWithChildrenTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            alignedPlan,
            alignedChildrenPlan,
            DocumentId,
            parentRows:
            [
                [ParentAItemId, DocumentId, 1, "A"],
                [ParentBItemId, DocumentId, 2, "B"],
            ],
            alignedRows:
            [
                [ParentAItemId, "Red"],
                [ParentBItemId, "Yellow"],
            ],
            alignedChildRows:
            [
                [ChildA1ItemId, ParentAItemId, 1, "A1"],
                [ChildA2ItemId, ParentAItemId, 2, "A2"],
                [ChildB1ItemId, ParentBItemId, 1, "B1"],
                [ChildB2ItemId, ParentBItemId, 2, "B2"],
            ]
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: NestedTopologyBuilders.BuildFlattenedWriteSet(
                rootPlan,
                [candidateA, candidateB]
            ),
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        _tableStateBuilders = [];
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            _tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            _tableStateBuilders,
            SynthesizeAlignedScope
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: mergeRequest.FlattenedWriteSet.RootRow,
            ParentRequestNode: body
        );

        var outcome = walker.WalkChildren(rootContext, WalkMode.Normal);

        outcome.Should().BeNull("the aligned scope dispatch and child recursion complete without rejection");
    }

    [Test]
    public void It_plans_the_aligned_scope_child_collection_per_aligned_scope_instance()
    {
        // The four child rows (A1, A2 under aligned-A; B1, B2 under aligned-B) are present
        // in storage and matched by visible request items + candidates. Each match produces a
        // matched-update merged row, so the consolidated aligned-children TableState carries
        // exactly four merged rows. Without the CP4 fix, ResolveCollectionCandidatesForParent
        // returned Empty for CandidateAttachedAlignedScopeData and the children scope was
        // never visited under the aligned parent.
        var builder = _tableStateBuilders[_alignedChildrenPlan.TableModel.Table];
        builder
            .HasContent.Should()
            .BeTrue("the recursion must visit the children scope under the aligned parent");
        var state = builder.Build();
        state.MergedRows.Length.Should().Be(4);
    }

    [Test]
    public void It_attaches_each_aligned_scope_child_row_to_its_parent_aligned_scope_physical_identity()
    {
        // Across the consolidated aligned-children TableState, each merged row's ParentItemId
        // (slot 1) must match its parent's aligned-scope PhysicalRowIdentity slot 0 - which is
        // the same ParentItemId as the parent collection row, since the aligned scope shares
        // the parent's primary key. Two rows under ParentAItemId, two under ParentBItemId.
        var parentItemIdBindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
            _alignedChildrenPlan,
            new DbColumnName("ParentItemId")
        );
        var state = _tableStateBuilders[_alignedChildrenPlan.TableModel.Table].Build();
        var parentItemIdValues = state
            .MergedRows.Select(r =>
                r.Values[parentItemIdBindingIndex] is FlattenedWriteValue.Literal lit ? lit.Value : null
            )
            .OrderBy(v => v is null ? long.MaxValue : Convert.ToInt64(v))
            .ToList();
        parentItemIdValues
            .Should()
            .BeEquivalentTo(new object?[] { ParentAItemId, ParentAItemId, ParentBItemId, ParentBItemId });
    }

    private static SeparateScopeSynthesisResult SynthesizeAlignedScope(
        TableWritePlan tablePlan,
        ScopeInstanceAddress scopeAddress,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
        SeparateScopeBuffer? buffer,
        JsonNode? scopedRequestNode,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        CurrentSeparateScopeRowProjection? currentRowProjection
    )
    {
        currentRowProjection.Should().NotBeNull("each aligned scope has a stored row");
        var currentRow = currentRowProjection!.ProjectedRow;

        // Update outcome: synthesize a merged row whose values carry the parent's
        // ParentItemId at slot 0 (same as currentRow.Values[0]) and the request's
        // FavoriteColor at slot 1. The walker uses ExtractPhysicalRowIdentityValues
        // on this merged row to seed the recursion's parent-identity context.
        var mergedValues = currentRow.Values.ToArray();
        if (scopedRequestNode is JsonObject obj && obj["favoriteColor"] is JsonValue colorValue)
        {
            mergedValues[1] = new FlattenedWriteValue.Literal(colorValue.GetValue<string>());
        }

        var mergedRow = new RelationalWriteMergedTableRow(
            mergedValues,
            RelationalWriteMergeSupport.ProjectComparableValues(tablePlan, mergedValues)
        );

        return SeparateScopeSynthesisResult.Table(
            ProfileSeparateTableMergeOutcome.Update,
            new RelationalWriteMergedTableState(tablePlan, [currentRow], [mergedRow])
        );
    }
}

/// <summary>
/// Local builders for a 4-table aligned-with-children topology: root,
/// <c>$.parents[*]</c> (collection), <c>$.parents[*]._ext.aligned</c> (CollectionExtensionScope),
/// and <c>$.parents[*]._ext.aligned.children[*]</c> (collection nested under the aligned
/// scope). Reuses <see cref="AlignedExtensionScopeTopologyBuilders.BuildRootParentsAndAlignedScopePlan"/>
/// to keep parent / aligned plans identical to the existing CP3 fixtures and only adds the
/// children plan on top.
/// </summary>
internal static class AlignedExtensionScopeWithChildrenTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("sample");
    public const string AlignedChildrenScope = "$.parents[*]._ext.aligned.children[*]";

    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan AlignedPlan,
        TableWritePlan AlignedChildrenPlan
    ) BuildPlan()
    {
        var (basePlan, parentsPlan, alignedPlan) =
            AlignedExtensionScopeTopologyBuilders.BuildRootParentsAndAlignedScopePlan();
        var rootPlan = basePlan.TablePlansInDependencyOrder[0];
        var alignedChildrenPlan = BuildAlignedChildrenCollectionPlan();

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "AlignedScopeChildrenTest"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder:
                [
                    rootPlan.TableModel,
                    parentsPlan.TableModel,
                    alignedPlan.TableModel,
                    alignedChildrenPlan.TableModel,
                ],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, alignedPlan, alignedChildrenPlan]
        );

        return (resourceWritePlan, parentsPlan, alignedPlan, alignedChildrenPlan);
    }

    public static CollectionWriteCandidate BuildAlignedChildCandidate(
        TableWritePlan alignedChildrenPlan,
        string identityValue,
        int requestOrder
    )
    {
        // Layout: [AlignedChildItemId, ParentItemId, Ordinal, IdentityField0]. Mirror the
        // nested-children candidate shape from NestedTopologyBuilders.BuildChildCandidate:
        // null literals everywhere except slot 3 (semantic identity scalar).
        var values = new FlattenedWriteValue[alignedChildrenPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        return new CollectionWriteCandidate(
            tableWritePlan: alignedChildrenPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                alignedChildrenPlan,
                [identityValue]
            )
        );
    }

    public static VisibleRequestCollectionItem BuildAlignedChildRequestItem(
        string parentSemanticIdentity,
        string childIdentity,
        int parentArrayIndex,
        int childArrayIndex
    ) =>
        new(
            new CollectionRowAddress(
                AlignedChildrenScope,
                AlignedExtensionScopeTopologyBuilders.AlignedScopeAddress(parentSemanticIdentity),
                Identity(childIdentity)
            ),
            Creatable: true,
            $"$.parents[{parentArrayIndex}]._ext.aligned.children[{childArrayIndex}]"
        );

    public static VisibleStoredCollectionRow BuildAlignedChildStoredRow(
        string parentSemanticIdentity,
        string childIdentity
    ) =>
        new(
            new CollectionRowAddress(
                AlignedChildrenScope,
                AlignedExtensionScopeTopologyBuilders.AlignedScopeAddress(parentSemanticIdentity),
                Identity(childIdentity)
            ),
            ImmutableArray<string>.Empty
        );

    public static RelationalWriteCurrentState BuildCurrentState(
        TableWritePlan rootPlan,
        TableWritePlan parentsPlan,
        TableWritePlan alignedPlan,
        TableWritePlan alignedChildrenPlan,
        long documentId,
        IReadOnlyList<object?[]> parentRows,
        IReadOnlyList<object?[]> alignedRows,
        IReadOnlyList<object?[]> alignedChildRows
    ) =>
        new(
            new DocumentMetadataRow(
                DocumentId: documentId,
                DocumentUuid: Guid.Parse("dddddddd-2222-3333-4444-eeeeeeeeeeee"),
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
                new HydratedTableRows(alignedPlan.TableModel, alignedRows),
                new HydratedTableRows(alignedChildrenPlan.TableModel, alignedChildRows),
            ],
            []
        );

    private static ImmutableArray<SemanticIdentityPart> Identity(string value) =>
        [new SemanticIdentityPart("$.identityField0", JsonValue.Create(value), IsPresent: true)];

    private static TableWritePlan BuildAlignedChildrenCollectionPlan()
    {
        // Layout: [AlignedChildItemId, ParentItemId, Ordinal, IdentityField0]. The
        // ParentItemId binding is a ParentKeyPart referring to slot 0 of the aligned scope's
        // PhysicalRowIdentity (which itself contains exactly [ParentItemId]).
        var childItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("AlignedChildItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentItemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentItemId"),
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
        DbColumnModel[] columns = [childItemIdColumn, parentItemIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "AlignedChildrenTable"),
            JsonScope: new JsonPathExpression(AlignedChildrenScope, []),
            Key: new TableKey(
                "PK_AlignedChildrenTable",
                [new DbKeyColumn(new DbColumnName("AlignedChildItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("AlignedChildItemId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentItemId")],
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
            InsertSql: "INSERT INTO sample.\"AlignedChildrenTable\" VALUES (@AlignedChildItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    childItemIdColumn,
                    new WriteValueSource.Precomputed(),
                    "AlignedChildItemId"
                ),
                new WriteColumnBinding(
                    parentItemIdColumn,
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentItemId"
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
                UpdateByStableRowIdentitySql: "UPDATE sample.\"AlignedChildrenTable\" SET X = @X WHERE \"AlignedChildItemId\" = @AlignedChildItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM sample.\"AlignedChildrenTable\" WHERE \"AlignedChildItemId\" = @AlignedChildItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("AlignedChildItemId"),
                0
            )
        );
    }
}

/// <summary>
/// Regression fixture for the nullable semantic-identity presence mismatch between
/// the walker's DB-projected <see cref="CurrentCollectionRowProjection"/> and Core's
/// stored-side <see cref="VisibleStoredCollectionRow"/> projection.
///
/// <para>
/// The Core stored side runs <see cref="DocumentReconstituter.EmitScalars"/>, which
/// omits SQL <c>NULL</c> columns from the reconstituted JSON, then
/// <see cref="AddressDerivationEngine.ReadSemanticIdentity"/> walks that JSON and
/// reports <c>IsPresent=false</c> for any identity path whose property is missing.
/// The walker only has the DB-row column value, so a SQL <c>NULL</c> identity column
/// must produce <c>IsPresent=false</c> to keep the walker's identity key consistent
/// with the Core <see cref="VisibleStoredCollectionRow"/> key under the
/// presence-aware <see cref="SemanticIdentityKeys.BuildKey"/>.
/// </para>
///
/// <para>
/// Before the fix the walker hardcoded <c>IsPresent=true</c>; the planner's
/// <c>SemanticIdentityKeys.BuildKey</c> includes presence, so a stored row with a
/// <c>NULL</c> identity column would not reverse-map to its corresponding current
/// row and would fail the reverse stored coverage invariant.
/// </para>
/// </summary>
[TestFixture]
public class Given_a_walker_with_a_current_row_whose_semantic_identity_column_is_null
{
    private ProfileCollectionWalker _walker = null!;
    private DbTableName _collectionTable;
    private ImmutableArray<FlattenedWriteValue> _expectedParentValues;

    [SetUp]
    public void Setup()
    {
        var (plan, collectionPlan) = CollectionSynthesizerBuilders.BuildRootAndCollectionPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        const long documentId = 345L;

        // Request body has no addresses array — request side is empty for this nullable
        // identity row. The merge candidate carries a literal null for the identity column,
        // matching what the flattener produces when the request omits the field.
        var body = new JsonObject();

        var nullCandidate = new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values:
            [
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(documentId),
                new FlattenedWriteValue.Literal(1),
                new FlattenedWriteValue.Literal(null),
            ],
            semanticIdentityValues: [null],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                collectionPlan,
                [null]
            )
        );

        // No visible request item: the request omits the identity field entirely. The walker
        // should still index the stored row's NULL identity correctly, which is what the
        // planner relies on for reverse stored coverage on nullable identity columns.
        var request = CollectionSynthesizerBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );
        var flattened = CollectionSynthesizerBuilders.BuildFlattenedWriteSet(
            rootPlan,
            [nullCandidate],
            documentId
        );

        // Single current collection row whose identity column is SQL NULL. Layout matches
        // MinimalCollectionTableWritePlan: [CollectionItemId, ParentDocumentId, Ordinal, IdentityField0].
        object?[] dbRowNullIdentity = [10L, documentId, 1, null];
        var currentState = CollectionSynthesizerBuilders.BuildCurrentState(
            rootPlan,
            collectionPlan,
            documentId,
            [dbRowNullIdentity]
        );

        var context = CollectionSynthesizerBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: request,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        var resolvedReferenceLookups = EmptyResolvedReferenceLookups(plan);

        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var p in plan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[p.TableModel.Table] = new ProfileTableStateBuilder(p);
        }

        _walker = new ProfileCollectionWalker(mergeRequest, resolvedReferenceLookups, tableStateBuilders);

        _collectionTable = collectionPlan.TableModel.Table;
        _expectedParentValues = [new FlattenedWriteValue.Literal(documentId)];
    }

    [Test]
    public void It_marks_the_db_projected_semantic_identity_part_as_not_present_for_a_null_column()
    {
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];

        bucket[0].SemanticIdentityInOrder[0].IsPresent.Should().BeFalse();
    }

    [Test]
    public void It_emits_a_null_value_for_the_db_projected_semantic_identity_part()
    {
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];

        bucket[0].SemanticIdentityInOrder[0].Value.Should().BeNull();
    }

    [Test]
    public void It_keys_the_db_row_identity_to_match_a_core_stored_row_built_from_a_reconstituted_document_with_an_omitted_property()
    {
        // Core's stored side: DocumentReconstituter.EmitScalars omits SQL NULL columns from
        // the reconstituted JSON, then AddressDerivationEngine.ReadSemanticIdentity walks
        // that JSON and reports IsPresent=false for the missing identity property. Build the
        // equivalent SemanticIdentityPart by hand here — using the walker's emitted
        // canonical RelativePath so the comparison isolates the IsPresent/Value contract
        // rather than path normalization — and confirm both sides produce the same
        // SemanticIdentityKeys.BuildKey output, which is what the planner's
        // ValidateReverseStoredCoverage relies on.
        var key = (_collectionTable, new ParentIdentityKey(_expectedParentValues));
        var bucket = _walker.CurrentCollectionRowsByTableAndParentIdentity[key];
        var walkerIdentity = bucket[0].SemanticIdentityInOrder;
        var walkerKey = SemanticIdentityKeys.BuildKey(walkerIdentity);

        ImmutableArray<SemanticIdentityPart> coreStoredIdentity =
        [
            new SemanticIdentityPart(walkerIdentity[0].RelativePath, Value: null, IsPresent: false),
        ];
        var coreStoredKey = SemanticIdentityKeys.BuildKey(coreStoredIdentity);

        walkerKey.Should().Be(coreStoredKey);
    }
}
