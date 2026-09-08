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
/// <see cref="ProfileCollectionWalker"/>'s <c>ResolveParentKeyPartSlotsForChild</c> maps
/// each <c>ImmediateParentScopeLocatorColumn</c> binding to a slot in the parent's
/// <c>PhysicalRowIdentity</c> buffer. Only two source kinds make sense as parent locators:
/// <see cref="WriteValueSource.ParentKeyPart"/> (declared parent slot) and
/// <see cref="WriteValueSource.DocumentId"/> in the root-anchored single-slot case
/// (parent is the resource root, whose physical identity is the document id).
///
/// <para>Any other source kind — <see cref="WriteValueSource.Scalar"/>,
/// <see cref="WriteValueSource.DocumentReference"/>,
/// <see cref="WriteValueSource.ReferenceDerived"/>,
/// <see cref="WriteValueSource.DescriptorReference"/>,
/// <see cref="WriteValueSource.Ordinal"/>,
/// <see cref="WriteValueSource.Precomputed"/> — bound to a parent locator column would
/// silently be bucketed under positional slot <c>i</c>, mis-aligning row lookups against
/// the parent identity buffer and corrupting the merge. The walker must fail closed so
/// compiled-plan drift surfaces deterministically.</para>
/// </summary>
[TestFixture]
public class Given_a_walker_with_a_scalar_bound_immediate_parent_locator_column
{
    private const long DocumentId = 345L;

    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, corruptedParentsPlan) = ParentLocatorSourceBuilders.BuildRootAndCorruptedParentsPlan(
            replacementSource: new WriteValueSource.Scalar(
                new JsonPathExpression(
                    "$.parentDocumentId",
                    [new JsonPathSegment.Property("parentDocumentId")]
                ),
                new RelationalScalarType(ScalarKind.String, MaxLength: 60)
            )
        );
        _ = corruptedParentsPlan;

        _act = ParentLocatorSourceBuilders.BuildWalkActionForRoot(plan, DocumentId);
    }

    [Test]
    public void It_fails_closed_with_an_invalid_operation_exception()
    {
        _act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_names_the_corrupted_child_table_in_the_message()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("ParentsTable");
    }

    [Test]
    public void It_names_the_locator_column_in_the_message()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("ParentDocumentId");
    }

    [Test]
    public void It_names_the_unsupported_source_kind_in_the_message()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("Scalar");
    }

    [Test]
    public void It_includes_the_binding_index_in_the_message()
    {
        // ParentDocumentId is binding index 1 in BuildCorruptedParentsPlan's layout
        // [ParentItemId, ParentDocumentId, Ordinal, IdentityField0].
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("binding index 1");
    }

    [Test]
    public void It_explains_only_parentkeypart_or_documentid_locators_are_supported()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("ParentKeyPart");
        thrown.Message.Should().Contain("DocumentId");
    }
}

/// <summary>
/// Counterpart to the unsupported-source fixture: the existing root-anchored case where
/// a child collection's <c>ImmediateParentScopeLocatorColumn</c> is bound to
/// <see cref="WriteValueSource.DocumentId"/> (parent is the resource root, single-slot
/// physical identity). The walker must continue to dispatch this case successfully — the
/// fail-closed guard rejects only unsupported source kinds.
/// </summary>
[TestFixture]
public class Given_a_walker_with_a_valid_documentid_immediate_parent_locator
{
    private const long DocumentId = 345L;

    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan) = ParentLocatorSourceBuilders.BuildRootAndCorruptedParentsPlan(
            replacementSource: new WriteValueSource.DocumentId()
        );
        _ = parentsPlan;

        _act = ParentLocatorSourceBuilders.BuildWalkActionForRoot(plan, DocumentId);
    }

    [Test]
    public void It_dispatches_the_walk_without_throwing()
    {
        _act.Should().NotThrow();
    }
}

/// <summary>
/// Regression for a tighter guard than "single-slot at position 0":
/// <see cref="WriteValueSource.DocumentId"/> on a parent locator must be valid only when
/// the immediate parent IS the resource root, not merely when the child's locator
/// happens to be a single column. A nested child whose
/// <c>ImmediateParentScopeLocatorColumn</c> is e.g. <c>ParentItemId</c> (the parent
/// collection's <c>PhysicalRowIdentity</c> slot) also has <c>Count == 1</c>; if its
/// binding drifts to <see cref="WriteValueSource.DocumentId"/>, returning slot 0 would
/// mis-match the parent context's <c>ParentPhysicalIdentityValues</c> (which holds the
/// parent's <c>ParentItemId</c>, not the document id) against rows storing the document
/// id. <see cref="RelationalWriteRowHelpers"/> rewrites only <c>ParentKeyPart</c> sources
/// at insert time, so the drift is not corrected later. Fail closed at lookup-shape
/// resolution.
/// </summary>
[TestFixture]
public class Given_a_walker_with_a_documentid_bound_nested_immediate_parent_locator
{
    private const long DocumentId = 345L;
    private const long ParentAItemId = 100L;

    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, parentsPlan, corruptedChildrenPlan) =
            ParentLocatorSourceBuilders.BuildRootParentsAndCorruptedChildrenPlan(
                childLocatorSource: new WriteValueSource.DocumentId()
            );

        _act = ParentLocatorSourceBuilders.BuildWalkActionForParentsContext(
            plan,
            parentsPlan,
            corruptedChildrenPlan,
            DocumentId,
            ParentAItemId
        );
    }

    [Test]
    public void It_fails_closed_with_an_invalid_operation_exception()
    {
        _act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_names_the_corrupted_child_table_in_the_message()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("ChildrenTable");
    }

    [Test]
    public void It_names_the_locator_column_in_the_message()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("ParentItemId");
    }

    [Test]
    public void It_names_the_unsupported_source_kind_in_the_message()
    {
        var thrown = _act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("DocumentId");
    }
}

/// <summary>
/// Builders for the parent-locator-source fail-closed fixtures. Construct a minimal
/// root-and-parents topology where the parents collection's
/// <c>ImmediateParentScopeLocatorColumn</c> is bound to a parameterized source kind, so
/// the same walker dispatch exercises the helper for both valid and invalid source kinds.
/// </summary>
internal static class ParentLocatorSourceBuilders
{
    private static readonly DbSchemaName _schema = new("edfi");
    public const string ParentsScope = "$.parents[*]";
    public const string ChildrenScope = "$.parents[*].children[*]";

    public static (ResourceWritePlan Plan, TableWritePlan ParentsPlan) BuildRootAndCorruptedParentsPlan(
        WriteValueSource replacementSource
    )
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildParentsCollectionPlanWithSourceForLocator(replacementSource);

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "ParentLocatorSourceTest"),
                PhysicalSchema: _schema,
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, parentsPlan.TableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan]
        );

        return (resourceWritePlan, parentsPlan);
    }

    public static Action BuildWalkActionForRoot(ResourceWritePlan plan, long documentId)
    {
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var parentsPlan = plan.TablePlansInDependencyOrder[1];

        var body = new JsonObject();
        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(documentId)],
                collectionCandidates: ImmutableArray<CollectionWriteCandidate>.Empty
            )
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

        var currentState = new RelationalWriteCurrentState(
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
                new HydratedTableRows(parentsPlan.TableModel, []),
            ],
            []
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
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(documentId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        return () => walker.WalkChildren(rootContext, WalkMode.Normal);
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
            Table: new DbTableName(_schema, "ParentLocatorSourceTest"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_ParentLocatorSourceTest",
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
            InsertSql: "INSERT INTO edfi.\"ParentLocatorSourceTest\" DEFAULT VALUES",
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
    /// Build a parents-style collection plan whose <c>ImmediateParentScopeLocatorColumn</c>
    /// (<c>ParentDocumentId</c>) is bound to <paramref name="replacementSource"/>. Layout is
    /// <c>[ParentItemId, ParentDocumentId, Ordinal, IdentityField0]</c>; the column shape
    /// is identical to the canonical NestedTopologyBuilders parents plan, only the source
    /// kind on the locator binding varies.
    /// </summary>
    private static TableWritePlan BuildParentsCollectionPlanWithSourceForLocator(
        WriteValueSource replacementSource
    )
    {
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
                new WriteColumnBinding(parentDocIdColumn, replacementSource, "ParentDocumentId"),
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

    /// <summary>
    /// Build a 3-table topology (root, parents, nested children) where the children
    /// collection's <c>ImmediateParentScopeLocatorColumn</c> (<c>ParentItemId</c>) is bound
    /// to <paramref name="childLocatorSource"/>. The parents plan stays valid with a
    /// root-anchored <see cref="WriteValueSource.DocumentId"/> locator so the walker
    /// dispatches into the children plan; only the nested locator binding varies.
    /// </summary>
    public static (
        ResourceWritePlan Plan,
        TableWritePlan ParentsPlan,
        TableWritePlan ChildrenPlan
    ) BuildRootParentsAndCorruptedChildrenPlan(WriteValueSource childLocatorSource)
    {
        var rootPlan = BuildMinimalRootPlan();
        var parentsPlan = BuildParentsCollectionPlanWithSourceForLocator(new WriteValueSource.DocumentId());
        var childrenPlan = BuildChildrenCollectionPlanWithSourceForLocator(childLocatorSource);

        var resourceWritePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "ParentLocatorSourceTest"),
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
                DescriptorEdgeSources: []
            ),
            [rootPlan, parentsPlan, childrenPlan]
        );

        return (resourceWritePlan, parentsPlan, childrenPlan);
    }

    /// <summary>
    /// Drive <see cref="ProfileCollectionWalker.WalkChildren"/> from a non-root parent
    /// context whose <c>JsonScope</c> is the parents collection scope, so the walker
    /// enumerates the nested children plan as a direct child and exercises the helper for
    /// the children's parent-locator binding directly. The parent context's
    /// <c>ParentPhysicalIdentityValues</c> is the parents collection's
    /// <c>PhysicalRowIdentity</c> shape (<c>[ParentItemId]</c>) — distinct from the
    /// resource root's document id, which is the whole point of the
    /// <see cref="WriteValueSource.DocumentId"/> guard.
    /// </summary>
    public static Action BuildWalkActionForParentsContext(
        ResourceWritePlan plan,
        TableWritePlan parentsPlan,
        TableWritePlan childrenPlan,
        long documentId,
        long parentItemId
    )
    {
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var body = new JsonObject();
        var request = new ProfileAppliedWriteRequest(
            body,
            RootResourceCreatable: true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            ImmutableArray<VisibleRequestCollectionItem>.Empty
        );

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [new FlattenedWriteValue.Literal(documentId)],
                collectionCandidates: ImmutableArray<CollectionWriteCandidate>.Empty
            )
        );

        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            ImmutableArray<StoredScopeState>.Empty,
            ImmutableArray<VisibleStoredCollectionRow>.Empty
        );

        var currentState = new RelationalWriteCurrentState(
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
                new HydratedTableRows(parentsPlan.TableModel, []),
                new HydratedTableRows(childrenPlan.TableModel, []),
            ],
            []
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

        // Synthesize a parent context whose JsonScope is the parents collection scope.
        // EnumerateDirectChildCollectionScopes keys by JsonScope, so this surfaces the
        // children plan as a direct child and the helper is invoked with the corrupted
        // children binding shape.
        var parentSemanticIdentity = ImmutableArray.Create(
            new SemanticIdentityPart(
                "$.identityField0",
                System.Text.Json.Nodes.JsonValue.Create("A"),
                IsPresent: true
            )
        );
        var parentsContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress(
                ParentsScope,
                [new AncestorCollectionInstance(ParentsScope, parentSemanticIdentity)]
            ),
            ParentPhysicalIdentityValues: [new FlattenedWriteValue.Literal(parentItemId)],
            RequestSubstructure: flattened.RootRow,
            ParentRequestNode: body
        );

        return () => walker.WalkChildren(parentsContext, WalkMode.Normal);
    }

    /// <summary>
    /// Build a children-style nested collection plan whose
    /// <c>ImmediateParentScopeLocatorColumn</c> (<c>ParentItemId</c>) is bound to
    /// <paramref name="locatorSource"/>. Layout
    /// <c>[ChildItemId, ParentItemId, Ordinal, IdentityField0]</c> mirrors the canonical
    /// nested children plan; only the source kind on the locator binding varies.
    /// </summary>
    private static TableWritePlan BuildChildrenCollectionPlanWithSourceForLocator(
        WriteValueSource locatorSource
    )
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
                // Nested child: no root-scope locator. ImmediateParent is the parents
                // collection's PhysicalRowIdentity, which is distinct from the root's
                // identity. Asserting RootScopeLocator does not match ImmediateParent is
                // the signal a tightened DocumentId guard relies on to reject a drifted
                // ParentItemId binding.
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
                new WriteColumnBinding(parentItemIdColumn, locatorSource, "ParentItemId"),
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
}
