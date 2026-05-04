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

/// <summary>
/// <see cref="ProfileCollectionWalker.WalkChildren"/> groups
/// <see cref="CollectionWriteCandidate"/>s by <c>JsonScope</c> at the start of child
/// traversal, but only scopes returned by <c>EnumerateDirectChildCollectionScopes</c> for
/// the current parent context are processed. A flattener bug, a Core/backend topology
/// mismatch, or a malformed compiled plan could leave a candidate's scope grouped in the
/// dictionary yet never visited; without a post-loop assertion that bug would silently
/// drop the candidate from the merge.
///
/// <para>This fixture wires a child-scope candidate (<c>$.parents[*].children[*]</c>)
/// into the ROOT <see cref="RootWriteRowBuffer"/>'s <c>CollectionCandidates</c>.
/// At root, <c>EnumerateDirectChildCollectionScopes</c> yields only <c>$.parents[*]</c>;
/// the children scope is grouped but never reached. The walker must fail closed with a
/// <see cref="ProfilePlannerContractMismatchException"/> identifying the parent context,
/// the unconsumed scope, and enough candidate detail to debug the contract mismatch.</para>
/// </summary>
[TestFixture]
public class Given_a_walker_at_root_with_a_grandchild_collection_candidate_attached_directly_to_root
{
    private const long DocumentId = 345L;

    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Inject a CHILDREN candidate (scope: $.parents[*].children[*]) directly into the
        // root buffer's CollectionCandidates. This simulates a flattener that put a
        // grandchild scope under the wrong parent — at root the walker enumerates only
        // $.parents[*] as a direct child, so the children scope is never consumed.
        var childCandidate = NestedTopologyBuilders.BuildChildCandidate(
            childrenPlan,
            identityValue: "X1",
            requestOrder: 0
        );

        var body = new JsonObject();
        var request = NestedTopologyBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [childCandidate]);

        var context = NestedTopologyBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows: [],
            childRows: []
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

        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        _act = () => walker.WalkChildren(rootContext, WalkMode.Normal);
    }

    [Test]
    public void It_fails_closed_with_a_planner_contract_mismatch_exception()
    {
        _act.Should().Throw<ProfilePlannerContractMismatchException>();
    }

    [Test]
    public void It_carries_the_parent_context_jsonscope_for_diagnostic_shaping()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.JsonScope.Should().Be("$");
    }

    [Test]
    public void It_names_the_unconsumed_candidate_invariant()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.InvariantName.Should().Contain("unconsumed");
    }

    [Test]
    public void It_includes_the_unconsumed_child_scope_in_the_message()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.Message.Should().Contain("children");
    }

    [Test]
    public void It_includes_the_parent_context_scope_in_the_message()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.Message.Should().Contain("'$'");
    }

    [Test]
    public void It_reports_the_leftover_candidate_count_for_the_unconsumed_scope()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.Message.Should().Contain("1 candidate");
    }
}

/// <summary>
/// Companion to the wrong-parent fixture: a candidate whose <c>TableWritePlan.JsonScope</c>
/// is not in the resource topology at all. The walker's
/// <c>_directChildCollectionPlansByParentScope</c> is built from
/// <c>_request.WritePlan.TablePlansInDependencyOrder</c>, so the unknown scope is invisible
/// to <c>EnumerateDirectChildCollectionScopes</c>. The candidate's grouped entry stays in
/// <c>candidatesByScope</c> after the loop and must trigger the same fail-closed path.
/// </summary>
[TestFixture]
public class Given_a_walker_at_root_with_a_collection_candidate_for_an_unsupported_scope
{
    private const long DocumentId = 345L;
    private const string UnsupportedScope = "$.unsupported[*]";

    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, childrenPlan) = NestedTopologyBuilders.BuildRootParentsAndChildrenPlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        // Fabricate a collection plan whose JsonScope is NOT present in the resource
        // topology. Construct a CollectionWriteCandidate referencing that plan and inject
        // it at root. _directChildCollectionPlansByParentScope is keyed by the resource
        // plan's table-backed scopes, so the unsupported scope is never enumerated.
        var unsupportedPlan = UnsupportedTopologyBuilders.BuildOrphanCollectionPlan(UnsupportedScope);
        var unsupportedCandidate = UnsupportedTopologyBuilders.BuildOrphanCandidate(
            unsupportedPlan,
            identityValue: "Y1",
            requestOrder: 0
        );

        var body = new JsonObject();
        var request = NestedTopologyBuilders.BuildRequest(
            body,
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(rootPlan, [unsupportedCandidate]);

        var context = NestedTopologyBuilders.BuildContext(
            request,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );
        var currentState = NestedTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            childrenPlan,
            DocumentId,
            parentRows: [],
            childRows: []
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

        var walker = new ProfileCollectionWalker(
            mergeRequest,
            EmptyResolvedReferenceLookups(plan),
            tableStateBuilders
        );

        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(DocumentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        _act = () => walker.WalkChildren(rootContext, WalkMode.Normal);
    }

    [Test]
    public void It_fails_closed_with_a_planner_contract_mismatch_exception()
    {
        _act.Should().Throw<ProfilePlannerContractMismatchException>();
    }

    [Test]
    public void It_carries_the_parent_context_jsonscope_for_diagnostic_shaping()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.JsonScope.Should().Be("$");
    }

    [Test]
    public void It_includes_the_unsupported_scope_in_the_message()
    {
        var thrown = _act.Should().Throw<ProfilePlannerContractMismatchException>().Which;
        thrown.Message.Should().Contain("unsupported");
    }
}

/// <summary>
/// Builders that fabricate a collection table plan whose JsonScope is not present in the
/// resource write plan, so the walker's direct-child enumeration cannot reach it. Used by
/// fail-closed tests for the unconsumed-candidate post-loop assertion.
/// </summary>
internal static class UnsupportedTopologyBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");

    public static TableWritePlan BuildOrphanCollectionPlan(string jsonScope)
    {
        var itemIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var docIdColumn = new DbColumnModel(
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
        DbColumnModel[] columns = [itemIdColumn, docIdColumn, ordinalColumn, identityColumn];

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "OrphanTable"),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_OrphanTable",
                [new DbKeyColumn(new DbColumnName("ItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("ItemId")],
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
            InsertSql: "INSERT INTO edfi.\"OrphanTable\" VALUES (@ItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(itemIdColumn, new WriteValueSource.Precomputed(), "ItemId"),
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
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
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"OrphanTable\" SET X = @X WHERE \"ItemId\" = @ItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"OrphanTable\" WHERE \"ItemId\" = @ItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(new DbColumnName("ItemId"), 0)
        );
    }

    public static CollectionWriteCandidate BuildOrphanCandidate(
        TableWritePlan orphanPlan,
        string identityValue,
        int requestOrder
    )
    {
        var values = new FlattenedWriteValue[orphanPlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(null);
        }
        values[3] = new FlattenedWriteValue.Literal(identityValue);

        return new CollectionWriteCandidate(
            tableWritePlan: orphanPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: [identityValue],
            semanticIdentityInOrder: SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(
                orphanPlan,
                [identityValue]
            )
        );
    }
}
