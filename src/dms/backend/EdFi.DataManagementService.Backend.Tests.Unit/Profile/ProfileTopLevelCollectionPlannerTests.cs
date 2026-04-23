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

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Local test-double factories for Slice-4 planner tests.
/// Task 3.2 will promote these to ProfileTestDoubles.cs.
/// </summary>
internal static class Slice4Builders
{
    private static readonly DbSchemaName _schema = new("edfi");

    public static ImmutableArray<SemanticIdentityPart> BuildSemanticIdentity(
        string relativePath,
        string value
    ) => [new SemanticIdentityPart(relativePath, JsonValue.Create(value), IsPresent: true)];

    public static ScopeInstanceAddress RootScopeAddress() =>
        new("$", ImmutableArray<AncestorCollectionInstance>.Empty);

    public static VisibleStoredCollectionRow BuildVisibleStoredCollectionRow(
        string jsonScope,
        ImmutableArray<SemanticIdentityPart> identity,
        ImmutableArray<string>? hiddenMemberPaths = null
    ) =>
        new(
            new CollectionRowAddress(jsonScope, RootScopeAddress(), identity),
            hiddenMemberPaths ?? ImmutableArray<string>.Empty
        );

    public static VisibleRequestCollectionItem BuildVisibleRequestCollectionItem(
        string jsonScope,
        ImmutableArray<SemanticIdentityPart> identity,
        bool creatable,
        string requestJsonPath
    ) => new(new CollectionRowAddress(jsonScope, RootScopeAddress(), identity), creatable, requestJsonPath);

    public static CurrentCollectionRowSnapshot BuildCurrentCollectionRowSnapshot(
        ImmutableArray<SemanticIdentityPart> identity,
        int storedOrdinal,
        long stableRowIdentity = 1L
    ) =>
        new(
            stableRowIdentity,
            identity,
            storedOrdinal,
            new RelationalWriteMergedTableRow(
                ImmutableArray<FlattenedWriteValue>.Empty,
                ImmutableArray<FlattenedWriteValue>.Empty
            )
        );

    /// <summary>
    /// Builds a minimal <see cref="CollectionWriteCandidate"/> whose semantic identity is
    /// derived from the supplied <see cref="SemanticIdentityPart"/> array. The candidate's
    /// <see cref="CollectionWriteCandidate.SemanticIdentityValues"/> stores each part's
    /// underlying CLR value directly (e.g. the raw <c>string</c> from
    /// <c>JsonValue.GetValue&lt;string&gt;()</c>), matching the real flattener output from
    /// <c>RelationalWriteFlattener</c>. The planner's <c>BuildCandidateIdentityKey</c> wraps
    /// these via <c>JsonValue.Create</c> to normalize them before key comparison.
    /// </summary>
    public static CollectionWriteCandidate BuildCollectionWriteCandidate(
        string jsonScope,
        ImmutableArray<SemanticIdentityPart> identity,
        int requestOrder = 0
    )
    {
        var identityCount = identity.Length;
        var tableWritePlan = MinimalCollectionTableWritePlan(jsonScope, identityCount);

        // Pass raw CLR values as the flattener does: GetValue<T>() extracts the underlying
        // CLR object from each JsonValue without re-serializing to JSON text.
        var semanticIdentityValues = identity
            .Select(p => p.Value is JsonValue jv ? (object?)jv.GetValue<object>() : null)
            .ToArray();

        return new CollectionWriteCandidate(
            tableWritePlan: tableWritePlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: Enumerable.Repeat<FlattenedWriteValue>(
                new FlattenedWriteValue.Literal(null),
                tableWritePlan.ColumnBindings.Length
            ),
            semanticIdentityValues: semanticIdentityValues
        );
    }

    /// <summary>
    /// Builds a minimal <see cref="TableWritePlan"/> for a collection table with
    /// <paramref name="semanticIdentityCount"/> identity columns. Layout:
    /// [0] CollectionItemId (Precomputed), [1] ParentDocumentId (DocumentId),
    /// [2] Ordinal (Ordinal), [3..] identity Scalar columns.
    /// </summary>
    public static TableWritePlan MinimalCollectionTableWritePlan(string jsonScope, int semanticIdentityCount)
    {
        // Fixed columns: CollectionItemId, ParentDocumentId, Ordinal
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

        // Identity columns: one Scalar per identity member
        var identityColumns = Enumerable
            .Range(0, semanticIdentityCount)
            .Select(i => new DbColumnModel(
                ColumnName: new DbColumnName($"IdentityField{i}"),
                Kind: ColumnKind.Scalar,
                ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                IsNullable: false,
                SourceJsonPath: new JsonPathExpression(
                    $"$.identityField{i}",
                    [new JsonPathSegment.Property($"identityField{i}")]
                ),
                TargetResource: null
            ))
            .ToArray();

        var allColumns = new DbColumnModel[] { collectionKeyColumn, parentKeyColumn, ordinalColumn }
            .Concat(identityColumns)
            .ToArray();

        // Identity metadata: one SemanticIdentityBinding per identity column
        var semanticIdentityBindings = identityColumns
            .Select(
                (col, i) => new CollectionSemanticIdentityBinding(col.SourceJsonPath!.Value, col.ColumnName)
            )
            .ToArray();

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, "CollectionTable"),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_CollectionTable",
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
                SemanticIdentityBindings: semanticIdentityBindings
            ),
        };

        // SemanticIdentityBindings in CollectionMergePlan reference binding indexes 3..N+2
        var mergeSemanticIdentityBindings = Enumerable
            .Range(0, semanticIdentityCount)
            .Select(i => new CollectionMergeSemanticIdentityBinding(
                identityColumns[i].SourceJsonPath!.Value,
                3 + i
            ))
            .ToArray();

        var columnBindings = new List<WriteColumnBinding>
        {
            new(collectionKeyColumn, new WriteValueSource.Precomputed(), "CollectionItemId"),
            new(parentKeyColumn, new WriteValueSource.DocumentId(), "ParentDocumentId"),
            new(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
        };
        columnBindings.AddRange(
            identityColumns.Select(col => new WriteColumnBinding(
                col,
                new WriteValueSource.Scalar(
                    col.SourceJsonPath!.Value,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                ),
                col.ColumnName.Value
            ))
        );

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"CollectionTable\" VALUES (@CollectionItemId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, allColumns.Length, 65535),
            ColumnBindings: columnBindings,
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: mergeSemanticIdentityBindings,
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "UPDATE edfi.\"CollectionTable\" SET X = @X WHERE \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "DELETE FROM edfi.\"CollectionTable\" WHERE \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [.. Enumerable.Range(3, semanticIdentityCount), 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 1 — Reverse stored coverage
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_visible_stored_row_lacking_current_row_counterpart
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: [Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", identity)],
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
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
    public void It_names_the_json_scope_in_the_message() =>
        _thrown!.Message.Should().Contain("$.addresses[*]");

    [Test]
    public void It_names_the_invariant_category() =>
        _thrown!.Message.Should().Contain("reverse stored coverage");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 2 — Request-side coverage
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_visible_request_item_lacking_request_candidate_counterpart
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems:
            [
                Slice4Builders.BuildVisibleRequestCollectionItem(
                    "$.addresses[*]",
                    identity,
                    creatable: true,
                    requestJsonPath: "$.addresses[0]"
                ),
            ],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
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
    public void It_names_request_coverage_violation() =>
        _thrown!.Message.Should().Contain("request-side coverage");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 3 — Duplicate VisibleRequestCollectionItem
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_duplicate_visible_request_items
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var candidate = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            identity,
            requestOrder: 0
        );
        var visible1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            identity,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var visible2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            identity,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate],
            VisibleRequestItems: [visible1, visible2],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_duplicate_visible_request_items() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("duplicate visible request item");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 4 — Duplicate VisibleStoredCollectionRow
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_duplicate_visible_stored_rows
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var stored1 = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", identity);
        var stored2 = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", identity);
        var current = Slice4Builders.BuildCurrentCollectionRowSnapshot(identity, storedOrdinal: 1);

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: [stored1, stored2],
            CurrentRows: [current]
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_duplicate_visible_stored_rows() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("duplicate visible stored row");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 5 — Duplicate CurrentCollectionRowSnapshot semantic identity
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_duplicate_current_row_identities
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var current1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            identity,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var current2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            identity,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: [current1, current2]
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_duplicate_current_row_identities() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("current row identity uniqueness");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 6 — Pre-scoped input: JsonScope mismatch on a candidate
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_candidate_in_wrong_jsonscope
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("phoneId", "P1");
        // Candidate's TableWritePlan uses "$.phones[*]" scope — different from input's JsonScope.
        var wrongScopeCandidate = Slice4Builders.BuildCollectionWriteCandidate(
            "$.phones[*]",
            identity,
            requestOrder: 0
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [wrongScopeCandidate],
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_jsonscope_mismatch() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("pre-scoped input: JsonScope mismatch");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 6 — Pre-scoped input: parent scope mismatch on a visible request item
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_address_under_wrong_parent_scope
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        // Item uses a non-root parent scope address — different from input's ParentScopeAddress.
        var wrongParent = new ScopeInstanceAddress(
            "$.nested",
            ImmutableArray<AncestorCollectionInstance>.Empty
        );
        var wrongParentItem = new VisibleRequestCollectionItem(
            new CollectionRowAddress("$.addresses[*]", wrongParent, identity),
            Creatable: true,
            RequestJsonPath: "$.addresses[0]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems: [wrongParentItem],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_parent_scope_mismatch() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("pre-scoped input: parent scope mismatch");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 7 — Order consistency: stored
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_stored_rows_out_of_ordinal_order
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var id1 = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var id2 = Slice4Builders.BuildSemanticIdentity("addressId", "A2");

        // Current rows have ordinals 1 and 2, but VisibleStoredRows presents them in
        // reverse order (A2 at ordinal 2 before A1 at ordinal 1) — decreasing ordinals.
        var current1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            id1,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var current2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            id2,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );

        var stored1 = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", id2); // ordinal 2
        var stored2 = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", id1); // ordinal 1

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: [stored1, stored2],
            CurrentRows: [current1, current2]
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_stored_ordinal_order_violation() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("order consistency: stored");
}

// ────────────────────────────────────────────────────────────────────────────────
// Invariant 8 — Order consistency: request
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_matched_candidates_out_of_request_order
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var id1 = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var id2 = Slice4Builders.BuildSemanticIdentity("addressId", "A2");

        // Candidates: A1 has requestOrder=1, A2 has requestOrder=0.
        // VisibleRequestItems in array order: A1 first then A2 — maps to requestOrders [1, 0],
        // which is decreasing (invariant violated).
        var candidate1 = Slice4Builders.BuildCollectionWriteCandidate("$.addresses[*]", id1, requestOrder: 1);
        var candidate2 = Slice4Builders.BuildCollectionWriteCandidate("$.addresses[*]", id2, requestOrder: 0);

        var item1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            id1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var item2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            id2,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate1, candidate2],
            VisibleRequestItems: [item1, item2],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_for_request_order_violation() =>
        _thrown
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("order consistency: request");
}

// ────────────────────────────────────────────────────────────────────────────────
// New invariant — Duplicate visible request candidate
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_duplicate_request_candidates
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");

        // Two candidates with the same semantic identity — requestOrder differs to prevent
        // other invariants from firing first.
        var candidate1 = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            identity,
            requestOrder: 0
        );
        var candidate2 = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            identity,
            requestOrder: 1
        );

        // VisibleRequestItems and CurrentRows/VisibleStoredRows are empty so only the
        // duplicate-candidate invariant fires.
        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate1, candidate2],
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
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
    public void It_names_the_invariant_category() =>
        _thrown!.Message.Should().Contain("duplicate visible request candidate");
}

// ────────────────────────────────────────────────────────────────────────────────
// New invariant — Orphan candidate (reverse request-side coverage)
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_candidate_lacking_matching_visible_request_item
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");

        // One candidate but no corresponding VisibleRequestCollectionItem.
        // VisibleStoredRows and CurrentRows are empty to avoid other invariants.
        var candidate = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            identity,
            requestOrder: 0
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate],
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        try
        {
            ProfileTopLevelCollectionPlanner.Plan(input);
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
    public void It_names_the_invariant_category() => _thrown!.Message.Should().Contain("orphan candidate");
}

// ────────────────────────────────────────────────────────────────────────────────
// Happy path — valid empty input returns stub Success
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_valid_empty_input
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: ImmutableArray<CollectionWriteCandidate>.Empty,
            VisibleRequestItems: ImmutableArray<VisibleRequestCollectionItem>.Empty,
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_an_empty_sequence()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().BeEmpty();
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.2 — Fixture 1: one matched visible row produces MatchedUpdateEntry
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_one_matched_visible_row_produces_MatchedUpdate
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "A1");
        var current = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            identity,
            storedOrdinal: 1,
            stableRowIdentity: 42L
        );
        var stored = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", identity);
        var candidate = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            identity,
            requestOrder: 0
        );
        var requestItem = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            identity,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate],
            VisibleRequestItems: [requestItem],
            VisibleStoredRows: [stored],
            CurrentRows: [current]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_one_entry()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(1);
    }

    [Test]
    public void It_returns_a_MatchedUpdateEntry()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence[0].Should().BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>();
    }

    [Test]
    public void It_returns_MatchedUpdateEntry_with_correct_StableRowIdentity()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = (ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry)success.Plan.Sequence[0];
        entry.StoredRow.StableRowIdentity.Should().Be(42L);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.2 — Fixture 2: unmatched creatable request item produces VisibleInsertEntry
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_unmatched_creatable_visible_request_produces_VisibleInsert
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var identity = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");
        var candidate = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            identity,
            requestOrder: 0
        );
        var requestItem = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            identity,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate],
            VisibleRequestItems: [requestItem],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_one_entry()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(1);
    }

    [Test]
    public void It_returns_a_VisibleInsertEntry()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence[0].Should().BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>();
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.2 — Fixture 3: unmatched non-creatable request item returns CreatabilityRejection
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_unmatched_non_creatable_visible_request_returns_CreatabilityRejection
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    private ImmutableArray<SemanticIdentityPart> _identity;

    [SetUp]
    public void Setup()
    {
        _identity = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");
        var candidate = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            _identity,
            requestOrder: 0
        );
        var requestItem = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            _identity,
            creatable: false,
            requestJsonPath: "$.addresses[0]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidate],
            VisibleRequestItems: [requestItem],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_CreatabilityRejection() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.CreatabilityRejection>();

    [Test]
    public void It_returns_rejection_with_matching_identity()
    {
        var rejection = (ProfileTopLevelCollectionPlanResult.CreatabilityRejection)_result;
        rejection.OffendingAddress.SemanticIdentityInOrder.Should().BeEquivalentTo(_identity);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.2 — Fixture 4: matched update + non-creatable insert returns CreatabilityRejection
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_matched_update_and_non_creatable_insert_returns_CreatabilityRejection
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    private ImmutableArray<SemanticIdentityPart> _new1Identity;

    [SetUp]
    public void Setup()
    {
        var v1Identity = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        _new1Identity = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");

        var current = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            v1Identity,
            storedOrdinal: 1,
            stableRowIdentity: 10L
        );
        var stored = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", v1Identity);

        var candidateV1 = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            v1Identity,
            requestOrder: 0
        );
        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            _new1Identity,
            requestOrder: 1
        );

        var requestV1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            v1Identity,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            _new1Identity,
            creatable: false,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV1, candidateNew1],
            VisibleRequestItems: [requestV1, requestNew1],
            VisibleStoredRows: [stored],
            CurrentRows: [current]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_CreatabilityRejection() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.CreatabilityRejection>();

    [Test]
    public void It_returns_rejection_pointing_at_NEW1_not_V1()
    {
        var rejection = (ProfileTopLevelCollectionPlanResult.CreatabilityRejection)_result;
        rejection.OffendingAddress.SemanticIdentityInOrder.Should().BeEquivalentTo(_new1Identity);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.2 — Fixture 5: two matched rows in request order (reversed from stored)
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_with_two_matched_rows_in_request_order_reordered_from_stored
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var id1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var id2 = Slice4Builders.BuildSemanticIdentity("addressId", "V2");

        // Stored ordinals: V1=1, V2=2 (ascending in DB)
        var current1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            id1,
            storedOrdinal: 1,
            stableRowIdentity: 11L
        );
        var current2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            id2,
            storedOrdinal: 2,
            stableRowIdentity: 22L
        );

        // VisibleStoredRows in stored ordinal order (required by invariant 7)
        var stored1 = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", id1);
        var stored2 = Slice4Builders.BuildVisibleStoredCollectionRow("$.addresses[*]", id2);

        // Request order: V2 first (requestOrder=0), V1 second (requestOrder=1)
        var candidateV2 = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            id2,
            requestOrder: 0
        );
        var candidateV1 = Slice4Builders.BuildCollectionWriteCandidate(
            "$.addresses[*]",
            id1,
            requestOrder: 1
        );

        // VisibleRequestItems in request array order (V2 first, V1 second)
        var requestV2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            id2,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestV1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            "$.addresses[*]",
            id1,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: "$.addresses[*]",
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV2, candidateV1],
            VisibleRequestItems: [requestV2, requestV1],
            VisibleStoredRows: [stored1, stored2],
            CurrentRows: [current1, current2]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_two_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(2);
    }

    [Test]
    public void It_returns_V2_first_in_request_order()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = (ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry)success.Plan.Sequence[0];
        entry.StoredRow.StableRowIdentity.Should().Be(22L);
    }

    [Test]
    public void It_returns_V1_second_in_request_order()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = (ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry)success.Plan.Sequence[1];
        entry.StoredRow.StableRowIdentity.Should().Be(11L);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 1: Example 1 — reorder, delete, insert with hidden rows
// current: [V1@1, H@2, V2@3, V3@4, H'@5]; request: [V3', V2', NEW1]; V1 omitted
// expected: [MatchedUpdate(V3,id=4), HiddenPreserve(H,id=2), MatchedUpdate(V2,id=3),
//            VisibleInsert(NEW1), HiddenPreserve(H',id=5)]
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_example_1_reorder_delete_insert
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idV1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var idH = Slice4Builders.BuildSemanticIdentity("addressId", "H");
        var idV2 = Slice4Builders.BuildSemanticIdentity("addressId", "V2");
        var idV3 = Slice4Builders.BuildSemanticIdentity("addressId", "V3");
        var idHprime = Slice4Builders.BuildSemanticIdentity("addressId", "Hprime");
        var idNew1 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");

        // Current rows ordered by StoredOrdinal: V1@1, H@2, V2@3, V3@4, H'@5
        var currentV1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV1,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var currentH = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idH,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );
        var currentV2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV2,
            storedOrdinal: 3,
            stableRowIdentity: 3L
        );
        var currentV3 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV3,
            storedOrdinal: 4,
            stableRowIdentity: 4L
        );
        var currentHprime = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idHprime,
            storedOrdinal: 5,
            stableRowIdentity: 5L
        );

        // VisibleStoredRows: V1, V2, V3 (in stored ordinal order, required by invariant 7)
        var storedV1 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV1);
        var storedV2 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV2);
        var storedV3 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV3);

        // Request: [V3', V2', NEW1] — requestOrders 0, 1, 2
        var candidateV3 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV3, requestOrder: 0);
        var candidateV2 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV2, requestOrder: 1);
        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew1, requestOrder: 2);

        var requestV3 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV3,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestV2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV2,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );
        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew1,
            creatable: true,
            requestJsonPath: "$.addresses[2]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV3, candidateV2, candidateNew1],
            VisibleRequestItems: [requestV3, requestV2, requestNew1],
            VisibleStoredRows: [storedV1, storedV2, storedV3],
            CurrentRows: [currentV1, currentH, currentV2, currentV3, currentHprime]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_five_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(5);
    }

    [Test]
    public void It_places_MatchedUpdate_V3_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(4L);
    }

    [Test]
    public void It_places_HiddenPreserve_H_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(2L);
    }

    [Test]
    public void It_places_MatchedUpdate_V2_at_index_2()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[2]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(3L);
    }

    [Test]
    public void It_places_VisibleInsert_NEW1_at_index_3()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[3]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW1");
    }

    [Test]
    public void It_places_HiddenPreserve_Hprime_at_index_4()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[4]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(5L);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 2: Example 2 — delete all visibles, one insert
// current: [V1@1, H@2, V2@3]; request: [NEW1]
// expected: [VisibleInsert(NEW1), HiddenPreserve(H)]
// V1 slot consumes NEW1; H passes through; V2 slot has no merged left → omitted.
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_example_2_delete_all_visibles_one_insert
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idV1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var idH = Slice4Builders.BuildSemanticIdentity("addressId", "H");
        var idV2 = Slice4Builders.BuildSemanticIdentity("addressId", "V2");
        var idNew1 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");

        var currentV1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV1,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var currentH = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idH,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );
        var currentV2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV2,
            storedOrdinal: 3,
            stableRowIdentity: 3L
        );

        var storedV1 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV1);
        var storedV2 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV2);

        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew1, requestOrder: 0);
        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateNew1],
            VisibleRequestItems: [requestNew1],
            VisibleStoredRows: [storedV1, storedV2],
            CurrentRows: [currentV1, currentH, currentV2]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_two_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(2);
    }

    [Test]
    public void It_places_VisibleInsert_NEW1_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence[0].Should().BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>();
    }

    [Test]
    public void It_places_HiddenPreserve_H_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(2L);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 3: Example 3 — empty current, inserts only
// current: []; request: [NEW1, NEW2]
// expected: [VisibleInsert(NEW1), VisibleInsert(NEW2)]
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_example_3_empty_current_inserts_only
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idNew1 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");
        var idNew2 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW2");

        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew1, requestOrder: 0);
        var candidateNew2 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew2, requestOrder: 1);

        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestNew2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew2,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateNew1, candidateNew2],
            VisibleRequestItems: [requestNew1, requestNew2],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: ImmutableArray<CurrentCollectionRowSnapshot>.Empty
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_two_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(2);
    }

    [Test]
    public void It_places_VisibleInsert_NEW1_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW1");
    }

    [Test]
    public void It_places_VisibleInsert_NEW2_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW2");
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 4: Example 4 — single hidden, one insert
// current: [H@1]; request: [NEW1]
// expected: [HiddenPreserve(H), VisibleInsert(NEW1)]
// No visible slot exists; NEW1 appends as leftover at the end.
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_example_4_single_hidden_one_insert
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idH = Slice4Builders.BuildSemanticIdentity("addressId", "H");
        var idNew1 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");

        var currentH = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idH,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );

        // No visible stored rows — H is hidden
        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew1, requestOrder: 0);
        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateNew1],
            VisibleRequestItems: [requestNew1],
            VisibleStoredRows: ImmutableArray<VisibleStoredCollectionRow>.Empty,
            CurrentRows: [currentH]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_two_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(2);
    }

    [Test]
    public void It_places_HiddenPreserve_H_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(1L);
    }

    [Test]
    public void It_places_VisibleInsert_NEW1_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence[1].Should().BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>();
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 5: Example 5 — matched middle, trailing hidden, insert
// current: [H@1, V1@2, H'@3]; request: [V1', NEW1]
// expected: [HiddenPreserve(H), MatchedUpdate(V1), HiddenPreserve(H'), VisibleInsert(NEW1)]
// NEW1 lands AFTER trailing H', not between V1_updated and H'.
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_example_5_matched_middle_trailing_hidden_insert
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idH = Slice4Builders.BuildSemanticIdentity("addressId", "H");
        var idV1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var idHprime = Slice4Builders.BuildSemanticIdentity("addressId", "Hprime");
        var idNew1 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");

        var currentH = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idH,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var currentV1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV1,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );
        var currentHprime = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idHprime,
            storedOrdinal: 3,
            stableRowIdentity: 3L
        );

        var storedV1 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV1);

        var candidateV1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV1, requestOrder: 0);
        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew1, requestOrder: 1);

        var requestV1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew1,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV1, candidateNew1],
            VisibleRequestItems: [requestV1, requestNew1],
            VisibleStoredRows: [storedV1],
            CurrentRows: [currentH, currentV1, currentHprime]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_four_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(4);
    }

    [Test]
    public void It_places_HiddenPreserve_H_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(1L);
    }

    [Test]
    public void It_places_MatchedUpdate_V1_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(2L);
    }

    [Test]
    public void It_places_HiddenPreserve_Hprime_at_index_2()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[2]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(3L);
    }

    [Test]
    public void It_places_VisibleInsert_NEW1_at_index_3()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence[3].Should().BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>();
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 6: matched only, no reorder
// current: [V1@1, V2@2]; request: [V1', V2']
// expected: [MatchedUpdate(V1), MatchedUpdate(V2)]
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_matched_only_no_reorder
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idV1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var idV2 = Slice4Builders.BuildSemanticIdentity("addressId", "V2");

        var currentV1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV1,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var currentV2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV2,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );

        var storedV1 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV1);
        var storedV2 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV2);

        var candidateV1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV1, requestOrder: 0);
        var candidateV2 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV2, requestOrder: 1);

        var requestV1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestV2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV2,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV1, candidateV2],
            VisibleRequestItems: [requestV1, requestV2],
            VisibleStoredRows: [storedV1, storedV2],
            CurrentRows: [currentV1, currentV2]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_two_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(2);
    }

    [Test]
    public void It_places_MatchedUpdate_V1_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(1L);
    }

    [Test]
    public void It_places_MatchedUpdate_V2_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(2L);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 7: kitchen sink — reorder, delete, inserts, hidden interleave
// current: [V1@1, H1@2, V2@3, V3@4, H2@5]; request: [V3', NEW_A, V1', NEW_B]; V2 omitted
// Phase 1: mergedVisible = [V3_upd, NEW_A, V1_upd, NEW_B]
// Phase 2 walk:
//   V1 slot (visible) → V3_upd (cursor=1)
//   H1 (hidden) → HiddenPreserve
//   V2 slot (visible) → NEW_A (cursor=2)
//   V3 slot (visible) → V1_upd (cursor=3)
//   H2 (hidden) → HiddenPreserve
//   Leftover: NEW_B appended at end
// expected: [V3_upd(id=4), H1(id=2), NEW_A, V1_upd(id=1), H2(id=5), NEW_B]
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_kitchen_sink_reorder_delete_inserts_hidden_interleave
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idV1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var idH1 = Slice4Builders.BuildSemanticIdentity("addressId", "H1");
        var idV2 = Slice4Builders.BuildSemanticIdentity("addressId", "V2");
        var idV3 = Slice4Builders.BuildSemanticIdentity("addressId", "V3");
        var idH2 = Slice4Builders.BuildSemanticIdentity("addressId", "H2");
        var idNewA = Slice4Builders.BuildSemanticIdentity("addressId", "NEW_A");
        var idNewB = Slice4Builders.BuildSemanticIdentity("addressId", "NEW_B");

        var currentV1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV1,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var currentH1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idH1,
            storedOrdinal: 2,
            stableRowIdentity: 2L
        );
        var currentV2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV2,
            storedOrdinal: 3,
            stableRowIdentity: 3L
        );
        var currentV3 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV3,
            storedOrdinal: 4,
            stableRowIdentity: 4L
        );
        var currentH2 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idH2,
            storedOrdinal: 5,
            stableRowIdentity: 5L
        );

        // VisibleStoredRows: V1, V2, V3 in ordinal order
        var storedV1 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV1);
        var storedV2 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV2);
        var storedV3 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV3);

        // Request: [V3', NEW_A, V1', NEW_B]
        var candidateV3 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV3, requestOrder: 0);
        var candidateNewA = Slice4Builders.BuildCollectionWriteCandidate(scope, idNewA, requestOrder: 1);
        var candidateV1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV1, requestOrder: 2);
        var candidateNewB = Slice4Builders.BuildCollectionWriteCandidate(scope, idNewB, requestOrder: 3);

        var requestV3 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV3,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestNewA = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNewA,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );
        var requestV1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV1,
            creatable: true,
            requestJsonPath: "$.addresses[2]"
        );
        var requestNewB = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNewB,
            creatable: true,
            requestJsonPath: "$.addresses[3]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV3, candidateNewA, candidateV1, candidateNewB],
            VisibleRequestItems: [requestV3, requestNewA, requestV1, requestNewB],
            VisibleStoredRows: [storedV1, storedV2, storedV3],
            CurrentRows: [currentV1, currentH1, currentV2, currentV3, currentH2]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_six_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(6);
    }

    [Test]
    public void It_places_MatchedUpdate_V3_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(4L);
    }

    [Test]
    public void It_places_HiddenPreserve_H1_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(2L);
    }

    [Test]
    public void It_places_VisibleInsert_NEW_A_at_index_2()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[2]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW_A");
    }

    [Test]
    public void It_places_MatchedUpdate_V1_at_index_3()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[3]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(1L);
    }

    [Test]
    public void It_places_HiddenPreserve_H2_at_index_4()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[4]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(5L);
    }

    [Test]
    public void It_places_VisibleInsert_NEW_B_at_index_5()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[5]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW_B");
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Task 2.3 — Fixture 8: multi-insert preserves request order among inserts
// current: [V1@1]; request: [V1', NEW1, NEW2, NEW3]
// expected: [MatchedUpdate(V1), VisibleInsert(NEW1), VisibleInsert(NEW2), VisibleInsert(NEW3)]
// V1 slot consumes V1_upd; walk ends; leftovers NEW1, NEW2, NEW3 append in request order.
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class Given_Planner_multi_insert_preserves_request_order_among_inserts
{
    private ProfileTopLevelCollectionPlanResult _result = null!;

    [SetUp]
    public void Setup()
    {
        const string scope = "$.addresses[*]";
        var idV1 = Slice4Builders.BuildSemanticIdentity("addressId", "V1");
        var idNew1 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW1");
        var idNew2 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW2");
        var idNew3 = Slice4Builders.BuildSemanticIdentity("addressId", "NEW3");

        var currentV1 = Slice4Builders.BuildCurrentCollectionRowSnapshot(
            idV1,
            storedOrdinal: 1,
            stableRowIdentity: 1L
        );
        var storedV1 = Slice4Builders.BuildVisibleStoredCollectionRow(scope, idV1);

        var candidateV1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idV1, requestOrder: 0);
        var candidateNew1 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew1, requestOrder: 1);
        var candidateNew2 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew2, requestOrder: 2);
        var candidateNew3 = Slice4Builders.BuildCollectionWriteCandidate(scope, idNew3, requestOrder: 3);

        var requestV1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idV1,
            creatable: true,
            requestJsonPath: "$.addresses[0]"
        );
        var requestNew1 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew1,
            creatable: true,
            requestJsonPath: "$.addresses[1]"
        );
        var requestNew2 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew2,
            creatable: true,
            requestJsonPath: "$.addresses[2]"
        );
        var requestNew3 = Slice4Builders.BuildVisibleRequestCollectionItem(
            scope,
            idNew3,
            creatable: true,
            requestJsonPath: "$.addresses[3]"
        );

        var input = new ProfileTopLevelCollectionScopeInput(
            JsonScope: scope,
            ParentScopeAddress: Slice4Builders.RootScopeAddress(),
            RequestCandidates: [candidateV1, candidateNew1, candidateNew2, candidateNew3],
            VisibleRequestItems: [requestV1, requestNew1, requestNew2, requestNew3],
            VisibleStoredRows: [storedV1],
            CurrentRows: [currentV1]
        );

        _result = ProfileTopLevelCollectionPlanner.Plan(input);
    }

    [Test]
    public void It_returns_Success() =>
        _result.Should().BeOfType<ProfileTopLevelCollectionPlanResult.Success>();

    [Test]
    public void It_returns_sequence_with_four_entries()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        success.Plan.Sequence.Should().HaveCount(4);
    }

    [Test]
    public void It_places_MatchedUpdate_V1_at_index_0()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[0]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry>()
            .Subject;
        entry.StoredRow.StableRowIdentity.Should().Be(1L);
    }

    [Test]
    public void It_places_VisibleInsert_NEW1_at_index_1()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[1]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW1");
    }

    [Test]
    public void It_places_VisibleInsert_NEW2_at_index_2()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[2]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW2");
    }

    [Test]
    public void It_places_VisibleInsert_NEW3_at_index_3()
    {
        var success = (ProfileTopLevelCollectionPlanResult.Success)_result;
        var entry = success
            .Plan.Sequence[3]
            .Should()
            .BeOfType<ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry>()
            .Subject;
        entry.RequestCandidate.SemanticIdentityValues.Should().ContainSingle().Which.Should().Be("NEW3");
    }
}
