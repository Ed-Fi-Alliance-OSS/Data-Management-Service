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
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.OverlayTestFixtures;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

// ── Shared fixture builders for overlay tests ─────────────────────────────────

/// <summary>
/// Fixture builders for <see cref="ProfileCollectionMatchedRowOverlay"/> tests.
/// Uses the existing <see cref="AdapterFactoryTestFixtures.BuildCollectionTableWritePlan"/> plan
/// (schema edfi, table SchoolAddress, scope $.addresses[*]) as the primary collection plan and
/// provides helpers for building stored rows, request candidates, and profile requests.
/// </summary>
internal static class OverlayTestFixtures
{
    // Column binding layout for the collection table (matches AdapterFactoryTestFixtures):
    //   [0] CollectionItemId — Precomputed    → StorageManaged (stable row identity)
    //   [1] School_DocumentId — DocumentId    → StorageManaged
    //   [2] Ordinal — Ordinal                 → StorageManaged (stamped by StampOrdinal)
    //   [3] AddressType — Scalar $.addressType → VisibleWritable or HiddenPreserved

    /// <summary>Index of the AddressType scalar binding (the only governable binding).</summary>
    public const int AddressTypeBindingIndex = 3;

    /// <summary>Index of the Ordinal binding.</summary>
    public const int OrdinalBindingIndex = 2;

    /// <summary>Index of the CollectionItemId binding (stable row identity).</summary>
    public const int StableRowIdentityBindingIndex = 0;

    /// <summary>
    /// Builds the standard collection plan wrapping it in a ResourceWritePlan. Uses the existing
    /// <see cref="AdapterFactoryTestFixtures"/> builders so the shape is consistent with the
    /// rest of the test suite.
    /// </summary>
    public static (ResourceWritePlan ResourcePlan, TableWritePlan CollectionPlan) BuildCollectionPlan()
    {
        var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
        var collectionPlan = plan.TablePlansInDependencyOrder[1];
        return (plan, collectionPlan);
    }

    /// <summary>
    /// Builds a <see cref="ProfileAppliedWriteRequest"/> with no collection RequestScopeState.
    /// Production shape: Core never emits collection scopes in RequestScopeStates. The
    /// classifier uses the table's own JsonScope as the containing scope directly.
    /// </summary>
    public static ProfileAppliedWriteRequest BuildCollectionRequest() => CreateRequest();

    /// <summary>
    /// Builds a <see cref="ProfileAppliedWriteRequest"/> with a VisibleAbsent scope at
    /// <c>$.addresses[*]</c> so the classifier can produce <see cref="RootBindingDisposition.ClearOnVisibleAbsent"/>
    /// for the AddressType binding.
    /// </summary>
    public static ProfileAppliedWriteRequest BuildCollectionRequestWithVisibleAbsentScope() =>
        CreateRequest(scopeStates: RequestVisibleAbsentScope("$.addresses[*]"));

    /// <summary>
    /// Builds a stored row snapshot with the given values. The values array must have
    /// exactly 4 entries (matching the collection table binding count). The per-column-name
    /// dictionary covers the same binding-backed values so tests that exercise hidden
    /// key-unification on bindable columns can resolve them; tests targeting UnifiedAlias
    /// columns supply their own snapshot via <see cref="CurrentCollectionRowSnapshot"/>
    /// constructor directly.
    /// </summary>
    public static CurrentCollectionRowSnapshot BuildStoredRow(
        object?[] values,
        long stableRowIdentity = 100L
    ) => BuildStoredRow(values, collectionPlan: null, stableRowIdentity);

    /// <summary>
    /// Overload that, when supplied with a collection plan, populates the per-column-name
    /// dictionary for every binding's column. Use when a test needs hidden k-u member lookup
    /// to find a binding-backed column by name.
    /// </summary>
    public static CurrentCollectionRowSnapshot BuildStoredRow(
        object?[] values,
        TableWritePlan? collectionPlan,
        long stableRowIdentity = 100L
    )
    {
        var flatValues = values
            .Select(v => (FlattenedWriteValue)new FlattenedWriteValue.Literal(v))
            .ToImmutableArray();

        var projectedRow = new RelationalWriteMergedTableRow(
            flatValues,
            ImmutableArray<FlattenedWriteValue>.Empty
        );

        IReadOnlyDictionary<DbColumnName, object?> currentRowByColumnName = collectionPlan is null
            ? new Dictionary<DbColumnName, object?>()
            : BuildBindingBackedColumnNameDict(collectionPlan, values);

        return new CurrentCollectionRowSnapshot(
            stableRowIdentity,
            ImmutableArray<SemanticIdentityPart>.Empty,
            1,
            projectedRow,
            currentRowByColumnName
        );
    }

    private static IReadOnlyDictionary<DbColumnName, object?> BuildBindingBackedColumnNameDict(
        TableWritePlan collectionPlan,
        object?[] values
    )
    {
        var dict = new Dictionary<DbColumnName, object?>(collectionPlan.ColumnBindings.Length);
        for (var i = 0; i < collectionPlan.ColumnBindings.Length && i < values.Length; i++)
        {
            dict[collectionPlan.ColumnBindings[i].Column.ColumnName] = values[i];
        }
        return dict;
    }

    /// <summary>
    /// Builds a <see cref="CollectionWriteCandidate"/> with the given values. The values
    /// array must have exactly 4 entries (matching the collection table binding count).
    /// </summary>
    public static CollectionWriteCandidate BuildRequestCandidate(
        TableWritePlan collectionPlan,
        object?[] values
    )
    {
        var flatValues = values
            .Select(v => (FlattenedWriteValue)new FlattenedWriteValue.Literal(v))
            .ToImmutableArray();

        return new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values: flatValues,
            semanticIdentityValues: [values[AddressTypeBindingIndex]],
            semanticIdentityInOrder: CollectionWriteCandidate.InferSemanticIdentityInOrderForTests(
                collectionPlan,
                [values[AddressTypeBindingIndex]]
            )
        );
    }

    /// <summary>
    /// Calls <see cref="ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission"/>
    /// with the standard wiring: empty key-unification plans, no hidden paths, ordinal 1.
    /// Callers override only what their specific fixture needs.
    /// </summary>
    public static RelationalWriteMergedTableRow CallOverlay(
        ResourceWritePlan resourcePlan,
        TableWritePlan collectionPlan,
        ProfileAppliedWriteRequest request,
        CurrentCollectionRowSnapshot storedRow,
        CollectionWriteCandidate requestCandidate,
        ImmutableArray<string> hiddenMemberPaths,
        int finalOrdinal = 1,
        IReadOnlyList<FlattenedWriteValue>? parentKeyValues = null,
        JsonNode? requestItemNode = null
    ) =>
        ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths,
            finalOrdinal,
            parentKeyValues ?? [new FlattenedWriteValue.Literal(42L)],
            requestItemNode ?? new JsonObject(),
            EmptyResolvedReferenceLookups(resourcePlan)
        );

    /// <summary>
    /// Builds a minimal <see cref="DbColumnModel"/> using the stored-column overload so no
    /// explicit <see cref="ColumnStorage"/> argument is required.
    /// </summary>
    public static DbColumnModel StoredColumn(
        string name,
        ColumnKind kind,
        RelationalScalarType? scalarType = null,
        bool isNullable = true,
        JsonPathExpression? sourceJsonPath = null
    ) => new(new DbColumnName(name), kind, scalarType, isNullable, sourceJsonPath, null);

    /// <summary>
    /// Builds a <see cref="CollectionWriteCandidate"/> from a values array for custom plans.
    /// </summary>
    public static CollectionWriteCandidate BuildCustomRequestCandidate(
        TableWritePlan collectionPlan,
        object?[] values
    )
    {
        var flatValues = values
            .Select(v => (FlattenedWriteValue)new FlattenedWriteValue.Literal(v))
            .ToImmutableArray();
        return new CollectionWriteCandidate(
            tableWritePlan: collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            values: flatValues,
            semanticIdentityValues: [],
            semanticIdentityInOrder: CollectionWriteCandidate.InferSemanticIdentityInOrderForTests(
                collectionPlan,
                []
            )
        );
    }
}

// ── Fixture 1: Hidden scalar preserves stored value ──────────────────────────

/// <summary>
/// Verifies that when the AddressType binding is hidden (in <c>hiddenMemberPaths</c>),
/// the emitted row carries the stored value, not the request's different value.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_with_hidden_scalar_preserves_stored_value
{
    private RelationalWriteMergedTableRow _result = null!;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildCollectionPlan();
        var request = BuildCollectionRequest();

        // Stored: AddressType = "Home"
        var storedRow = BuildStoredRow([null, null, 1, "Home"]);

        // Request: AddressType = "Work" (should be ignored because hidden)
        var requestCandidate = BuildRequestCandidate(collectionPlan, [null, null, 1, "Work"]);

        // Hide "addressType" so the stored value is preserved
        _result = CallOverlay(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ["addressType"]
        );
    }

    [Test]
    public void It_preserves_stored_value_for_hidden_binding() =>
        ((FlattenedWriteValue.Literal)_result.Values[AddressTypeBindingIndex]).Value.Should().Be("Home");
}

// ── Fixture 2: Visible scalar takes request value ─────────────────────────────

/// <summary>
/// Verifies that when the AddressType binding is visible (not in <c>hiddenMemberPaths</c>),
/// the emitted row carries the request value, not the stored value.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_with_visible_scalar_takes_request_value
{
    private RelationalWriteMergedTableRow _result = null!;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildCollectionPlan();
        var request = BuildCollectionRequest();

        // Stored: AddressType = "Home"
        var storedRow = BuildStoredRow([null, null, 1, "Home"]);

        // Request: AddressType = "Work"
        var requestCandidate = BuildRequestCandidate(collectionPlan, [null, null, 1, "Work"]);

        // No hidden paths → AddressType is VisibleWritable
        _result = CallOverlay(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ImmutableArray<string>.Empty
        );
    }

    [Test]
    public void It_takes_request_value_for_visible_binding() =>
        ((FlattenedWriteValue.Literal)_result.Values[AddressTypeBindingIndex]).Value.Should().Be("Work");
}

// ── Fixture 3: Hidden FK preserves stored FK ──────────────────────────────────

/// <summary>
/// Verifies that when the governing path of a reference-derived binding is hidden,
/// the stored FK value is preserved. Uses a plan built with a DocumentReference +
/// ReferenceDerived pair so the classifier routes through the reference-derived governance path.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_with_hidden_fk_preserves_stored_fk
{
    private RelationalWriteMergedTableRow _result = null!;

    // Binding layout for the FK collection plan:
    //   [0] CollectionItemId (Precomputed) — stable row identity
    //   [1] ParentDocumentId (DocumentId)
    //   [2] Ordinal (Ordinal)
    //   [3] SchoolRef_DocumentId (DocumentReference)
    //   [4] SchoolRef_SchoolId (ReferenceDerived)
    private const int FkBindingIndex = 3;
    private const int DerivedBindingIndex = 4;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildFkCollectionPlan();
        // Production-shape request: no collection RequestScopeState.
        var request = CreateRequest();

        // Stored: FK = 99L, Derived = 1001
        var storedRow = BuildStoredRow([null, null, 1, 99L, 1001], stableRowIdentity: 200L);

        // Request: FK = 77L, Derived = 2002 (should be ignored because schoolReference is hidden)
        var requestCandidate = BuildCustomRequestCandidate(collectionPlan, [null, null, 1, 77L, 2002]);

        // Hide the reference root so both FK and derived bindings are HiddenPreserved
        _result = ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ["schoolReference"],
            finalOrdinal: 1,
            parentPhysicalRowIdentityValues: [new FlattenedWriteValue.Literal(42L)],
            concreteRequestItemNode: new JsonObject(),
            resolvedReferenceLookups: EmptyResolvedReferenceLookups(resourcePlan)
        );
    }

    [Test]
    public void It_preserves_stored_fk() =>
        ((FlattenedWriteValue.Literal)_result.Values[FkBindingIndex]).Value.Should().Be(99L);

    [Test]
    public void It_preserves_stored_derived_value() =>
        ((FlattenedWriteValue.Literal)_result.Values[DerivedBindingIndex]).Value.Should().Be(1001);

    private static (ResourceWritePlan, TableWritePlan) BuildFkCollectionPlan()
    {
        var schema = new DbSchemaName("edfi");
        var referenceObjectPath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );

        var collectionKeyColumn = StoredColumn(
            "CollectionItemId",
            ColumnKind.CollectionKey,
            isNullable: false
        );
        var parentKeyColumn = StoredColumn("ParentDocumentId", ColumnKind.ParentKeyPart, isNullable: false);
        var ordinalColumn = StoredColumn("Ordinal", ColumnKind.Ordinal, isNullable: false);
        var fkColumn = StoredColumn(
            "SchoolRef_DocumentId",
            ColumnKind.DocumentFk,
            new RelationalScalarType(ScalarKind.Int64)
        );
        var derivedColumn = StoredColumn(
            "SchoolRef_SchoolId",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32),
            sourceJsonPath: new JsonPathExpression(
                "$.schoolRefs[*].schoolReference.schoolId",
                [new JsonPathSegment.Property("schoolId")]
            )
        );

        var allColumns = new[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            fkColumn,
            derivedColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(schema, "SchoolRef"),
            JsonScope: new JsonPathExpression(
                "$.schoolRefs[*]",
                [new JsonPathSegment.Property("schoolRefs"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_SchoolRef",
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
                SemanticIdentityBindings: []
            ),
        };

        var referenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: referenceObjectPath,
            IdentityJsonPath: new JsonPathExpression(
                "$.schoolId",
                [new JsonPathSegment.Property("schoolId")]
            ),
            ReferenceJsonPath: new JsonPathExpression(
                "$.schoolRefs[*].schoolReference.schoolId",
                [new JsonPathSegment.Property("schoolId")]
            )
        );

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.SchoolRef VALUES (...)",
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
                    "SchoolRef_DocumentId"
                ),
                new WriteColumnBinding(
                    derivedColumn,
                    new WriteValueSource.ReferenceDerived(referenceSource),
                    "SchoolRef_SchoolId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.SchoolRef SET x = @x WHERE CollectionItemId = @CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.SchoolRef WHERE CollectionItemId = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var docRefBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: referenceObjectPath,
            Table: tableModel.Table,
            FkColumn: fkColumn.ColumnName,
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings: []
        );

        var rootTableModel = AdapterFactoryTestFixtures.BuildRootTableModel();
        var rootPlan = AdapterFactoryTestFixtures.BuildRootTableWritePlan(rootTableModel);

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, tableModel],
            DocumentReferenceBindings: [docRefBinding],
            DescriptorEdgeSources: []
        );

        return (new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]), collectionPlan);
    }
}

// ── Fixture 4: Canonical key-unification storage preserved ────────────────────

/// <summary>
/// Verifies that when a key-unification member is hidden, the emitted row's canonical
/// column carries the stored value rather than the request value.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_preserves_canonical_key_unification_storage
{
    private RelationalWriteMergedTableRow _result = null!;
    private int _canonicalIndex;

    // Layout: [0]=CollectionItemId, [1]=ParentDocId, [2]=Ordinal,
    //         [3]=KU_Canonical (resolver-owned), [4]=PeriodName (scalar member)
    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan, canonicalIdx) = BuildKuCollectionPlan();
        _canonicalIndex = canonicalIdx;

        // Production-shape request: no collection RequestScopeState.
        var request = CreateRequest();

        // Stored: member = "STORED_MEMBER" (canonical is also "STORED_MEMBER" via k-u resolution).
        // Pass the collection plan so the column-name-keyed projection covers PeriodName,
        // which the hidden key-unification member resolver looks up by physical column name.
        var storedRow = BuildStoredRow(
            [null, null, 1, "STORED_MEMBER", "STORED_MEMBER"],
            collectionPlan,
            stableRowIdentity: 300L
        );

        // Request: member = "REQUEST_MEMBER" (should not affect because member is hidden)
        var requestCandidate = BuildCustomRequestCandidate(
            collectionPlan,
            [null, null, 1, null, "REQUEST_MEMBER"]
        );

        // Hide the periodName member so canonical must come from stored row
        _result = ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ["periodName"],
            finalOrdinal: 1,
            parentPhysicalRowIdentityValues: [new FlattenedWriteValue.Literal(42L)],
            concreteRequestItemNode: new JsonObject { ["periodName"] = "REQUEST_MEMBER" },
            resolvedReferenceLookups: EmptyResolvedReferenceLookups(resourcePlan)
        );
    }

    [Test]
    public void It_preserves_stored_canonical_value() =>
        ((FlattenedWriteValue.Literal)_result.Values[_canonicalIndex]).Value.Should().Be("STORED_MEMBER");

    private static (ResourceWritePlan, TableWritePlan, int CanonicalIndex) BuildKuCollectionPlan()
    {
        var schema = new DbSchemaName("edfi");
        var collectionKeyColumn = StoredColumn(
            "CollectionItemId",
            ColumnKind.CollectionKey,
            isNullable: false
        );
        var parentDocColumn = StoredColumn("ParentDocId", ColumnKind.ParentKeyPart, isNullable: false);
        var ordinalColumn = StoredColumn("Ordinal", ColumnKind.Ordinal, isNullable: false);
        var canonicalColumn = StoredColumn(
            "KU_Canonical",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60)
        );
        var memberColumn = StoredColumn(
            "PeriodName",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            sourceJsonPath: new JsonPathExpression(
                "$.periods[*].periodName",
                [new JsonPathSegment.Property("periodName")]
            )
        );

        var allColumns = new[]
        {
            collectionKeyColumn,
            parentDocColumn,
            ordinalColumn,
            canonicalColumn,
            memberColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(schema, "Period"),
            JsonScope: new JsonPathExpression(
                "$.periods[*]",
                [new JsonPathSegment.Property("periods"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_Period",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocId")],
                SemanticIdentityBindings: []
            ),
        };

        const int canonicalBindingIndex = 3;

        var kuPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: canonicalBindingIndex,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: memberColumn.ColumnName,
                    RelativePath: new JsonPathExpression(
                        "$.periods[*].periodName",
                        [new JsonPathSegment.Property("periodName")]
                    ),
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                ),
            ]
        );

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.Period VALUES (...)",
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
                new WriteColumnBinding(parentDocColumn, new WriteValueSource.DocumentId(), "ParentDocId"),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(canonicalColumn, new WriteValueSource.Precomputed(), "KU_Canonical"),
                new WriteColumnBinding(
                    memberColumn,
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.periods[*].periodName",
                            [new JsonPathSegment.Property("periodName")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "PeriodName"
                ),
            ],
            KeyUnificationPlans: [kuPlan],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.Period SET x=@x WHERE CollectionItemId=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.Period WHERE CollectionItemId=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var rootTableModel = AdapterFactoryTestFixtures.BuildRootTableModel();
        var rootPlan = AdapterFactoryTestFixtures.BuildRootTableWritePlan(rootTableModel);

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return (
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            collectionPlan,
            canonicalBindingIndex
        );
    }
}

// ── Fixture 5: Synthetic presence preserved for hidden member ─────────────────

/// <summary>
/// Verifies that when a key-unification member is hidden and has a synthetic presence
/// column, the emitted row's synthetic presence binding carries the stored presence value.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_preserves_synthetic_presence_for_hidden_member
{
    private RelationalWriteMergedTableRow _result = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    // Layout: [0]=CollectionItemId, [1]=ParentDocId, [2]=Ordinal,
    //         [3]=KU_Canonical (resolver-owned), [4]=KU_Presence (resolver-owned), [5]=PeriodName

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan, canonicalIdx, presenceIdx) = BuildKuCollectionPlanWithPresence();
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIdx;

        // Production-shape request: no collection RequestScopeState.
        var request = CreateRequest();

        // Stored row: presence = true (non-null), member = "STORED". Pass the collection plan so
        // the column-name-keyed projection covers PeriodName for hidden k-u member lookup.
        var storedRow = BuildStoredRow(
            [null, null, 1, "STORED", true, "STORED"],
            collectionPlan,
            stableRowIdentity: 400L
        );

        // Request: member = "REQUEST" (should not affect hidden member)
        var requestCandidate = BuildCustomRequestCandidate(
            collectionPlan,
            [null, null, 1, null, null, "REQUEST"]
        );

        _result = ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ["periodName"],
            finalOrdinal: 1,
            parentPhysicalRowIdentityValues: [new FlattenedWriteValue.Literal(42L)],
            concreteRequestItemNode: new JsonObject { ["periodName"] = "REQUEST" },
            resolvedReferenceLookups: EmptyResolvedReferenceLookups(resourcePlan)
        );
    }

    [Test]
    public void It_writes_synthetic_presence_true_for_stored_present_member() =>
        ((FlattenedWriteValue.Literal)_result.Values[_presenceIndex]).Value.Should().Be(true);

    [Test]
    public void It_writes_canonical_from_stored_value() =>
        ((FlattenedWriteValue.Literal)_result.Values[_canonicalIndex]).Value.Should().Be("STORED");

    private static (
        ResourceWritePlan,
        TableWritePlan,
        int CanonicalIndex,
        int PresenceIndex
    ) BuildKuCollectionPlanWithPresence()
    {
        var schema = new DbSchemaName("edfi");
        var collectionKeyColumn = StoredColumn(
            "CollectionItemId",
            ColumnKind.CollectionKey,
            isNullable: false
        );
        var parentDocColumn = StoredColumn("ParentDocId", ColumnKind.ParentKeyPart, isNullable: false);
        var ordinalColumn = StoredColumn("Ordinal", ColumnKind.Ordinal, isNullable: false);
        var canonicalColumn = StoredColumn(
            "KU_Canonical",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60)
        );
        var presenceColumn = StoredColumn(
            "KU_Presence",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Boolean)
        );
        var memberColumn = StoredColumn(
            "PeriodName",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            sourceJsonPath: new JsonPathExpression(
                "$.periods[*].periodName",
                [new JsonPathSegment.Property("periodName")]
            )
        );

        var allColumns = new[]
        {
            collectionKeyColumn,
            parentDocColumn,
            ordinalColumn,
            canonicalColumn,
            presenceColumn,
            memberColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(schema, "Period"),
            JsonScope: new JsonPathExpression(
                "$.periods[*]",
                [new JsonPathSegment.Property("periods"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_Period",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocId")],
                SemanticIdentityBindings: []
            ),
        };

        const int canonicalBindingIndex = 3;
        const int presenceBindingIndex = 4;

        var kuPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: canonicalBindingIndex,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: memberColumn.ColumnName,
                    RelativePath: new JsonPathExpression(
                        "$.periods[*].periodName",
                        [new JsonPathSegment.Property("periodName")]
                    ),
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    PresenceColumn: presenceColumn.ColumnName,
                    PresenceBindingIndex: presenceBindingIndex,
                    PresenceIsSynthetic: true
                ),
            ]
        );

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.Period VALUES (...)",
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
                new WriteColumnBinding(parentDocColumn, new WriteValueSource.DocumentId(), "ParentDocId"),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(canonicalColumn, new WriteValueSource.Precomputed(), "KU_Canonical"),
                new WriteColumnBinding(presenceColumn, new WriteValueSource.Precomputed(), "KU_Presence"),
                new WriteColumnBinding(
                    memberColumn,
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.periods[*].periodName",
                            [new JsonPathSegment.Property("periodName")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "PeriodName"
                ),
            ],
            KeyUnificationPlans: [kuPlan],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.Period SET x=@x WHERE CollectionItemId=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.Period WHERE CollectionItemId=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var rootTableModel = AdapterFactoryTestFixtures.BuildRootTableModel();
        var rootPlan = AdapterFactoryTestFixtures.BuildRootTableWritePlan(rootTableModel);

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return (
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            collectionPlan,
            canonicalBindingIndex,
            presenceBindingIndex
        );
    }
}

// ── Fixture 6: Final ordinal stamped ─────────────────────────────────────────

/// <summary>
/// Verifies that the emitted row's ordinal column contains the <c>finalOrdinal</c>
/// parameter, regardless of the stored ordinal or the request ordinal.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_stamps_final_ordinal
{
    private RelationalWriteMergedTableRow _result = null!;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildCollectionPlan();
        var request = BuildCollectionRequest();

        // Stored ordinal = 1, request ordinal = 1 — we will pass finalOrdinal = 5
        var storedRow = BuildStoredRow([null, null, 1, "Home"]);
        var requestCandidate = BuildRequestCandidate(collectionPlan, [null, null, 1, "Home"]);

        _result = CallOverlay(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ImmutableArray<string>.Empty,
            finalOrdinal: 5
        );
    }

    [Test]
    public void It_stamps_the_final_ordinal() =>
        ((FlattenedWriteValue.Literal)_result.Values[OrdinalBindingIndex]).Value.Should().Be(5);
}

// ── Fixture 7: Stable row identity preserved ──────────────────────────────────

/// <summary>
/// Verifies that the emitted row's stable-row-identity binding equals the stored row's
/// stable-row-identity value, not a fresh value from the request candidate.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_preserves_stable_row_identity
{
    private RelationalWriteMergedTableRow _result = null!;
    private const long StoredStableId = 999L;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildCollectionPlan();
        var request = BuildCollectionRequest();

        // Stored stable row identity at binding [0] = 999L
        var storedRow = BuildStoredRow([StoredStableId, null, 1, "Home"], stableRowIdentity: StoredStableId);

        // Request candidate has a different value at [0]
        var requestCandidate = BuildRequestCandidate(collectionPlan, [888L, null, 1, "Home"]);

        _result = CallOverlay(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ImmutableArray<string>.Empty
        );
    }

    [Test]
    public void It_preserves_stable_row_identity_from_stored_row() =>
        ((FlattenedWriteValue.Literal)_result.Values[StableRowIdentityBindingIndex])
            .Value.Should()
            .Be(StoredStableId);
}

// ── Fixture 9: Production-shape regression — no collection RequestScopeState ──

/// <summary>
/// Regression fixture: verifies that when the profile request has NO RequestScopeState entry
/// for the collection scope (production shape — Core never emits collection scopes in
/// RequestScopeStates), hidden scalars in hiddenMemberPaths are still preserved.
///
/// Previously the classifier built candidateScopes from RequestScopeStates, which yielded an
/// empty set for collection-row calls, causing TryMatchLongestScope to return null and every
/// binding to fall through to VisibleWritable — ignoring the hiddenMemberPaths entirely.
/// </summary>
[TestFixture]
public class Given_overlay_with_no_collection_RequestScopeState_preserves_hidden_scalar
{
    private RelationalWriteMergedTableRow _result = null!;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildCollectionPlan();

        // Production-shape request: NO RequestScopeState for "$.addresses[*]".
        // Core only emits root-level scope states; collection scope visibility is
        // inferred from VisibleRequestCollectionItems and hiddenMemberPaths.
        var request = CreateRequest();

        // Stored: AddressType = "STORED_HIDDEN"
        var storedRow = BuildStoredRow([null, null, 1, "STORED_HIDDEN"]);

        // Request: AddressType = "REQUEST_VISIBLE" — would overwrite if bug is present
        var requestCandidate = BuildRequestCandidate(collectionPlan, [null, null, 1, "REQUEST_VISIBLE"]);

        // addressType is hidden — stored value must be preserved regardless of request scope states
        _result = CallOverlay(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ["addressType"]
        );
    }

    [Test]
    public void It_preserves_stored_hidden_scalar_value() =>
        ((FlattenedWriteValue.Literal)_result.Values[AddressTypeBindingIndex])
            .Value.Should()
            .Be("STORED_HIDDEN");
}

// ── Fixture 8: ClearOnVisibleAbsent disposition rejected ──────────────────────

/// <summary>
/// Verifies that when binding classification produces
/// <see cref="RootBindingDisposition.ClearOnVisibleAbsent"/> for a collection row binding,
/// the overlay throws <see cref="InvalidOperationException"/> naming the disposition.
/// ClearOnVisibleAbsent is not meaningful for collection rows — row omission is a delete,
/// not a per-column clear.
/// </summary>
[TestFixture]
public class Given_overlay_rejects_ClearOnVisibleAbsent_disposition_if_produced
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan) = BuildCollectionPlan();

        // Use a VisibleAbsent scope so the classifier produces ClearOnVisibleAbsent
        // for the AddressType binding (it's visible in the scope but absent from the request).
        var request = BuildCollectionRequestWithVisibleAbsentScope();

        var storedRow = BuildStoredRow([null, null, 1, "Home"]);
        var requestCandidate = BuildRequestCandidate(collectionPlan, [null, null, 1, "Home"]);

        try
        {
            CallOverlay(
                resourcePlan,
                collectionPlan,
                request,
                storedRow,
                requestCandidate,
                hiddenMemberPaths: ImmutableArray<string>.Empty
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_InvalidOperationException() =>
        _thrown.Should().BeOfType<InvalidOperationException>();

    [Test]
    public void It_names_ClearOnVisibleAbsent_in_message() =>
        _thrown!.Message.Should().Contain(nameof(RootBindingDisposition.ClearOnVisibleAbsent));
}

// ── Fixture 10: Reference-derived k-u member with descendant hidden path ──────

/// <summary>
/// Regression test for the <c>ClassifyMemberVisibilityFromHiddenSet</c> bug where the
/// row-level classifier used exact-match <c>hiddenSet.Contains</c> instead of
/// <see cref="ProfileMemberGovernanceRules.IsHiddenGoverned"/> with
/// <see cref="ProfileMemberGovernanceRules.HiddenPathMatchKind.ReferenceRooted"/> for
/// <see cref="KeyUnificationMemberWritePlan.ReferenceDerivedMember"/>. With the bug, a
/// hidden descendant path like <c>schoolReference.schoolId</c> fails to govern the
/// reference-derived member whose governing path is the reference root
/// <c>schoolReference</c>, so the k-u resolver evaluates the member from the request
/// instead of preserving the stored canonical value.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_with_reference_derived_ku_member_and_descendant_hidden_path_preserves_stored_canonical
{
    private RelationalWriteMergedTableRow _result = null!;
    private int _canonicalIndex;

    // Layout: [0]=CollectionItemId, [1]=ParentDocId, [2]=Ordinal,
    //         [3]=KU_Canonical (resolver-owned), [4]=SchoolRef_DocumentId (FK),
    //         [5]=SchoolRef_SchoolId (reference-derived k-u member)
    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan, canonicalIdx) = BuildReferenceDerivedKuPlan();
        _canonicalIndex = canonicalIdx;

        var request = CreateRequest();

        // Stored: canonical = 1001, FK = 99L, derived = 1001. Pass the collection plan so the
        // column-name-keyed projection covers SchoolRef_SchoolId for hidden k-u member lookup.
        var storedRow = BuildStoredRow(
            [null, null, 1, 1001, 99L, 1001],
            collectionPlan,
            stableRowIdentity: 500L
        );

        // Request: derived = 2002 (must be ignored — whole reference family is
        // governed via a descendant hidden path)
        var requestCandidate = BuildCustomRequestCandidate(collectionPlan, [null, null, 1, null, 77L, 2002]);

        // Hide a descendant of the reference root ("schoolReference"): the classifier
        // must treat the ReferenceDerivedMember whose governing path is "schoolReference"
        // as HiddenGoverned (ReferenceRooted match), so canonical comes from stored row.
        _result = ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            hiddenMemberPaths: ["schoolReference.schoolId"],
            finalOrdinal: 1,
            parentPhysicalRowIdentityValues: [new FlattenedWriteValue.Literal(42L)],
            concreteRequestItemNode: new JsonObject(),
            resolvedReferenceLookups: EmptyResolvedReferenceLookups(resourcePlan)
        );
    }

    [Test]
    public void It_preserves_stored_canonical_value() =>
        ((FlattenedWriteValue.Literal)_result.Values[_canonicalIndex]).Value.Should().Be(1001);

    private static (ResourceWritePlan, TableWritePlan, int CanonicalIndex) BuildReferenceDerivedKuPlan()
    {
        var schema = new DbSchemaName("edfi");
        var referenceObjectPath = new JsonPathExpression(
            "$.schoolRefs[*].schoolReference",
            [
                new JsonPathSegment.Property("schoolRefs"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("schoolReference"),
            ]
        );

        var collectionKeyColumn = StoredColumn(
            "CollectionItemId",
            ColumnKind.CollectionKey,
            isNullable: false
        );
        var parentKeyColumn = StoredColumn("ParentDocumentId", ColumnKind.ParentKeyPart, isNullable: false);
        var ordinalColumn = StoredColumn("Ordinal", ColumnKind.Ordinal, isNullable: false);
        var canonicalColumn = StoredColumn(
            "KU_Canonical",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32)
        );
        var fkColumn = StoredColumn(
            "SchoolRef_DocumentId",
            ColumnKind.DocumentFk,
            new RelationalScalarType(ScalarKind.Int64)
        );
        var derivedColumn = StoredColumn(
            "SchoolRef_SchoolId",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32),
            sourceJsonPath: new JsonPathExpression(
                "$.schoolRefs[*].schoolReference.schoolId",
                [new JsonPathSegment.Property("schoolId")]
            )
        );

        var allColumns = new[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            canonicalColumn,
            fkColumn,
            derivedColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(schema, "SchoolRef"),
            JsonScope: new JsonPathExpression(
                "$.schoolRefs[*]",
                [new JsonPathSegment.Property("schoolRefs"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_SchoolRef",
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
                SemanticIdentityBindings: []
            ),
        };

        var referenceSource = new ReferenceDerivedValueSourceMetadata(
            BindingIndex: 0,
            ReferenceObjectPath: referenceObjectPath,
            IdentityJsonPath: new JsonPathExpression(
                "$.schoolId",
                [new JsonPathSegment.Property("schoolId")]
            ),
            ReferenceJsonPath: new JsonPathExpression(
                "$.schoolRefs[*].schoolReference.schoolId",
                [new JsonPathSegment.Property("schoolId")]
            )
        );

        const int canonicalBindingIndex = 3;

        var kuPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: canonicalBindingIndex,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
                    MemberPathColumn: derivedColumn.ColumnName,
                    RelativePath: new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    ReferenceSource: referenceSource,
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                ),
            ]
        );

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.SchoolRef VALUES (...)",
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
                new WriteColumnBinding(canonicalColumn, new WriteValueSource.Precomputed(), "KU_Canonical"),
                new WriteColumnBinding(
                    fkColumn,
                    new WriteValueSource.DocumentReference(0),
                    "SchoolRef_DocumentId"
                ),
                new WriteColumnBinding(
                    derivedColumn,
                    new WriteValueSource.ReferenceDerived(referenceSource),
                    "SchoolRef_SchoolId"
                ),
            ],
            KeyUnificationPlans: [kuPlan],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.SchoolRef SET x = @x WHERE CollectionItemId = @CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.SchoolRef WHERE CollectionItemId = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var docRefBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: referenceObjectPath,
            Table: tableModel.Table,
            FkColumn: fkColumn.ColumnName,
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings: []
        );

        var rootTableModel = AdapterFactoryTestFixtures.BuildRootTableModel();
        var rootPlan = AdapterFactoryTestFixtures.BuildRootTableWritePlan(rootTableModel);

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, tableModel],
            DocumentReferenceBindings: [docRefBinding],
            DescriptorEdgeSources: []
        );

        return (
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            collectionPlan,
            canonicalBindingIndex
        );
    }
}

// ── Fixture 7: Hidden k-u member path column lives outside ColumnBindings ─────

/// <summary>
/// Verifies that the matched-row overlay preserves the stored canonical value when the
/// hidden key-unification member's <see cref="KeyUnificationMemberWritePlan.ScalarMember.MemberPathColumn"/>
/// is an alias-only column on the table model — present in <see cref="DbTableModel.Columns"/>
/// but absent from <see cref="TableWritePlan.ColumnBindings"/>. This mirrors the production
/// invariant from <c>KeyUnificationWritePlanCompiler</c> (member path columns must use
/// <see cref="ColumnStorage.UnifiedAlias"/> storage, which is non-writable and therefore
/// not bound). The lookup must come from the snapshot's <see cref="CurrentCollectionRowSnapshot.CurrentRowByColumnName"/>
/// dictionary; a binding-only projection would not contain the alias column and the
/// resolver would throw "requires stored column ... was not present" — silently breaking
/// hidden k-u preservation in production.
/// </summary>
[TestFixture]
public class Given_overlay_matched_row_with_alias_only_member_column_preserves_stored_canonical
{
    private RelationalWriteMergedTableRow _result = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (resourcePlan, collectionPlan, canonicalIdx, aliasMemberColumnName) =
            BuildAliasOnlyMemberKuPlan();
        _canonicalIndex = canonicalIdx;

        var request = CreateRequest();

        // Stored canonical: 1001. The alias-only member column carries 1001 in the raw
        // hydrated current state; the overlay must read it via CurrentRowByColumnName.
        var storedRow = BuildAliasOnlyStoredRow(
            collectionPlan: collectionPlan,
            aliasColumnName: aliasMemberColumnName,
            aliasColumnValue: 1001,
            bindingValues: [null, null, 1, 1001],
            stableRowIdentity: 700L
        );

        var requestCandidate = BuildCustomRequestCandidate(collectionPlan, [null, null, 1, null]);

        _result = ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
            resourcePlan,
            collectionPlan,
            request,
            storedRow,
            requestCandidate,
            // Mark the entire member's relative path hidden so the resolver routes through
            // EvaluateHiddenMember and consults CurrentRowByColumnName.
            hiddenMemberPaths: ["aliasMember"],
            finalOrdinal: 1,
            parentPhysicalRowIdentityValues: [new FlattenedWriteValue.Literal(42L)],
            concreteRequestItemNode: new JsonObject(),
            resolvedReferenceLookups: EmptyResolvedReferenceLookups(resourcePlan)
        );
    }

    [Test]
    public void It_preserves_stored_canonical_value_via_alias_lookup() =>
        ((FlattenedWriteValue.Literal)_result.Values[_canonicalIndex]).Value.Should().Be(1001);

    /// <summary>
    /// Builds a snapshot where the per-column dictionary contains the alias-only member
    /// column even though it is absent from ColumnBindings. The binding-indexed values
    /// only carry the bound columns; the dict carries every column on the table model.
    /// </summary>
    private static CurrentCollectionRowSnapshot BuildAliasOnlyStoredRow(
        TableWritePlan collectionPlan,
        DbColumnName aliasColumnName,
        object aliasColumnValue,
        object?[] bindingValues,
        long stableRowIdentity
    )
    {
        var flatValues = bindingValues
            .Select(v => (FlattenedWriteValue)new FlattenedWriteValue.Literal(v))
            .ToImmutableArray();

        var dict = new Dictionary<DbColumnName, object?>(collectionPlan.ColumnBindings.Length + 1);
        for (var i = 0; i < collectionPlan.ColumnBindings.Length && i < bindingValues.Length; i++)
        {
            dict[collectionPlan.ColumnBindings[i].Column.ColumnName] = bindingValues[i];
        }
        dict[aliasColumnName] = aliasColumnValue;

        return new CurrentCollectionRowSnapshot(
            stableRowIdentity,
            ImmutableArray<SemanticIdentityPart>.Empty,
            1,
            new RelationalWriteMergedTableRow(flatValues, ImmutableArray<FlattenedWriteValue>.Empty),
            dict
        );
    }

    private static (
        ResourceWritePlan,
        TableWritePlan,
        int CanonicalIndex,
        DbColumnName AliasMemberColumnName
    ) BuildAliasOnlyMemberKuPlan()
    {
        var schema = new DbSchemaName("edfi");

        var collectionKeyColumn = StoredColumn(
            "CollectionItemId",
            ColumnKind.CollectionKey,
            isNullable: false
        );
        var parentDocColumn = StoredColumn("ParentDocId", ColumnKind.ParentKeyPart, isNullable: false);
        var ordinalColumn = StoredColumn("Ordinal", ColumnKind.Ordinal, isNullable: false);
        var canonicalColumn = StoredColumn(
            "KU_Canonical",
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32)
        );

        // Alias-only member column. Constructed with ColumnStorage.UnifiedAlias so IsWritable
        // is false; deliberately NOT bound in ColumnBindings to mirror production shape.
        var aliasMemberColumn = new DbColumnModel(
            ColumnName: new DbColumnName("AliasMember"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.Int32),
            IsNullable: true,
            SourceJsonPath: new JsonPathExpression(
                "$.aliasItems[*].aliasMember",
                [new JsonPathSegment.Property("aliasMember")]
            ),
            TargetResource: null,
            Storage: new ColumnStorage.UnifiedAlias(
                CanonicalColumn: canonicalColumn.ColumnName,
                PresenceColumn: null
            )
        );

        var allColumns = new[]
        {
            collectionKeyColumn,
            parentDocColumn,
            ordinalColumn,
            canonicalColumn,
            aliasMemberColumn,
        };

        var tableModel = new DbTableModel(
            Table: new DbTableName(schema, "AliasItem"),
            JsonScope: new JsonPathExpression(
                "$.aliasItems[*]",
                [new JsonPathSegment.Property("aliasItems"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_AliasItem",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: allColumns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocId")],
                SemanticIdentityBindings: []
            ),
        };

        const int canonicalBindingIndex = 3;

        var kuPlan = new KeyUnificationWritePlan(
            CanonicalColumn: canonicalColumn.ColumnName,
            CanonicalBindingIndex: canonicalBindingIndex,
            MembersInOrder:
            [
                new KeyUnificationMemberWritePlan.ScalarMember(
                    MemberPathColumn: aliasMemberColumn.ColumnName,
                    RelativePath: new JsonPathExpression(
                        "$.aliasItems[*].aliasMember",
                        [new JsonPathSegment.Property("aliasMember")]
                    ),
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    PresenceColumn: null,
                    PresenceBindingIndex: null,
                    PresenceIsSynthetic: false
                ),
            ]
        );

        var collectionPlan = new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "INSERT INTO edfi.AliasItem VALUES (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, allColumns.Length, 65535),
            // Bind only the four "real" columns; aliasMemberColumn is intentionally absent.
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(parentDocColumn, new WriteValueSource.DocumentId(), "ParentDocId"),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(canonicalColumn, new WriteValueSource.Precomputed(), "KU_Canonical"),
            ],
            KeyUnificationPlans: [kuPlan],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.AliasItem SET x=@x WHERE CollectionItemId=@CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.AliasItem WHERE CollectionItemId=@CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );

        var rootTableModel = AdapterFactoryTestFixtures.BuildRootTableModel();
        var rootPlan = AdapterFactoryTestFixtures.BuildRootTableWritePlan(rootTableModel);

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTableModel,
            TablesInDependencyOrder: [rootTableModel, tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return (
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            collectionPlan,
            canonicalBindingIndex,
            aliasMemberColumn.ColumnName
        );
    }
}
