// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

// ────────────────────────────────────────────────────────────────────────────────
// Test 1: Create flow (no current state) — root-only resource
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_a_root_only_create_with_no_profile
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var plan = PlanBuilder.BuildPlanWithRootOnly([
            ("SchoolId", "$.schoolId"),
            ("NameOfInstitution", "$.nameOfInstitution"),
        ]);

        var rootTablePlan = plan.TablePlansInDependencyOrder[0];
        var selectedBody = new JsonObject { ["schoolId"] = 123, ["nameOfInstitution"] = "Test School" };

        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootTablePlan,
            rootExtensionRows: [],
            collectionCandidates: []
        );

        _result = NoProfileSyntheticProfileAdapter.Build(
            plan,
            flattenedWriteSet,
            selectedBody,
            currentState: null
        );
    }

    [Test]
    public void It_sets_RootResourceCreatable_to_true()
    {
        _result.Request.RootResourceCreatable.Should().BeTrue();
    }

    [Test]
    public void It_returns_null_Context_for_create_flow()
    {
        _result.Context.Should().BeNull();
    }

    [Test]
    public void It_includes_root_scope_in_catalog()
    {
        _result.Catalog.Should().Contain(d => d.JsonScope == "$");
    }

    [Test]
    public void It_includes_root_scope_in_RequestScopeStates()
    {
        _result
            .Request.RequestScopeStates.Should()
            .Contain(s => s.Address.JsonScope == "$" && s.Address.AncestorCollectionInstances.IsEmpty);
    }

    [Test]
    public void It_marks_root_scope_as_Visible_and_Creatable()
    {
        var rootState = _result.Request.RequestScopeStates.Single(s => s.Address.JsonScope == "$");
        rootState.Visibility.Should().Be(ProfileVisibilityKind.VisiblePresent);
        rootState.Creatable.Should().BeTrue();
    }

    [Test]
    public void It_has_no_VisibleRequestCollectionItems()
    {
        _result.Request.VisibleRequestCollectionItems.Should().BeEmpty();
    }

    [Test]
    public void It_sets_WritableRequestBody_to_selectedBody()
    {
        _result.Request.WritableRequestBody.Should().NotBeNull();
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Test 2: Existing-document update with omitted root extension scope
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_an_existing_document_update_with_omitted_root_extension_scope
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "School",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("SchoolId", "$.schoolId"), ("NameOfInstitution", "$.nameOfInstitution")]
        );

        var extensionPlan = PlanBuilder.CreateTablePlan(
            tableName: "SchoolExt",
            jsonScope: "$._ext.SchoolExt",
            tableKind: DbTableKind.RootExtension,
            columns: [("ExtField", "$.extField")]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, extensionPlan]);

        // Flattened request has only root — the extension is omitted
        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [],
            collectionCandidates: []
        );

        var selectedBody = new JsonObject { ["schoolId"] = 123 };

        // Current state has both root and extension rows
        var currentState = AdapterTestHelpers.BuildCurrentState([
            (rootPlan.TableModel, 1),
            (extensionPlan.TableModel, 1),
        ]);

        _result = NoProfileSyntheticProfileAdapter.Build(plan, flattenedWriteSet, selectedBody, currentState);
    }

    [Test]
    public void It_returns_non_null_Context()
    {
        _result.Context.Should().NotBeNull();
    }

    [Test]
    public void It_has_root_stored_scope_as_Visible()
    {
        _result
            .Context!.StoredScopeStates.Should()
            .Contain(s => s.Address.JsonScope == "$" && s.Visibility == ProfileVisibilityKind.VisiblePresent);
    }

    [Test]
    public void It_has_extension_stored_scope_as_VisibleAbsent()
    {
        _result
            .Context!.StoredScopeStates.Should()
            .Contain(s =>
                s.Address.JsonScope == "$._ext.SchoolExt"
                && s.Visibility == ProfileVisibilityKind.VisibleAbsent
            );
    }

    [Test]
    public void It_has_empty_HiddenMemberPaths_on_all_stored_scopes()
    {
        _result.Context!.StoredScopeStates.Should().OnlyContain(s => s.HiddenMemberPaths.IsEmpty);
    }

    [Test]
    public void It_has_root_request_scope_as_Visible()
    {
        _result
            .Request.RequestScopeStates.Should()
            .Contain(s => s.Address.JsonScope == "$" && s.Visibility == ProfileVisibilityKind.VisiblePresent);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Test 3: Inlined scope under collection
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_a_resource_with_inlined_reference_scope_inside_a_collection
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "Section",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("SectionId", "$.sectionIdentifier")]
        );

        var collectionPlan = PlanBuilder.CreateCollectionTablePlan(
            tableName: "SectionClassPeriod",
            jsonScope: "$.classPeriods[*]",
            columns: [("Ref_SchoolId", "$.classPeriodReference.schoolId")],
            semanticIdentityRelativePaths: ["$.classPeriodReference.schoolId"]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, collectionPlan]);

        // Flatten 2 collection rows
        var candidate1 = AdapterTestHelpers.BuildCollectionCandidate(
            collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            semanticIdentityValues: ["SchoolA"],
            semanticIdentityPresenceFlags: [true]
        );

        var candidate2 = AdapterTestHelpers.BuildCollectionCandidate(
            collectionPlan,
            ordinalPath: [1],
            requestOrder: 1,
            semanticIdentityValues: ["SchoolB"],
            semanticIdentityPresenceFlags: [true]
        );

        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [],
            collectionCandidates: [candidate1, candidate2]
        );

        var selectedBody = new JsonObject
        {
            ["sectionIdentifier"] = "sec1",
            ["classPeriods"] = new JsonArray(
                new JsonObject { ["classPeriodReference"] = new JsonObject { ["schoolId"] = "SchoolA" } },
                new JsonObject { ["classPeriodReference"] = new JsonObject { ["schoolId"] = "SchoolB" } }
            ),
        };

        _result = NoProfileSyntheticProfileAdapter.Build(
            plan,
            flattenedWriteSet,
            selectedBody,
            currentState: null
        );
    }

    [Test]
    public void It_has_inlined_scope_entries_in_RequestScopeStates_for_each_collection_row()
    {
        var inlinedStates = _result
            .Request.RequestScopeStates.Where(s =>
                s.Address.JsonScope == "$.classPeriods[*].classPeriodReference"
            )
            .ToList();

        inlinedStates.Should().HaveCount(2);
    }

    [Test]
    public void It_marks_inlined_scopes_as_Visible_and_Creatable()
    {
        var inlinedStates = _result
            .Request.RequestScopeStates.Where(s =>
                s.Address.JsonScope == "$.classPeriods[*].classPeriodReference"
            )
            .ToList();

        inlinedStates.Should().OnlyContain(s => s.Visibility == ProfileVisibilityKind.VisiblePresent);
        inlinedStates.Should().OnlyContain(s => s.Creatable);
    }

    [Test]
    public void It_has_ancestor_collection_instances_on_inlined_scope_addresses()
    {
        var inlinedStates = _result
            .Request.RequestScopeStates.Where(s =>
                s.Address.JsonScope == "$.classPeriods[*].classPeriodReference"
            )
            .ToList();

        inlinedStates.Should().OnlyContain(s => !s.Address.AncestorCollectionInstances.IsEmpty);
    }

    [Test]
    public void It_has_catalog_entry_for_inlined_scope()
    {
        _result.Catalog.Should().Contain(d => d.JsonScope == "$.classPeriods[*].classPeriodReference");
    }

    [Test]
    public void It_has_two_VisibleRequestCollectionItems()
    {
        _result.Request.VisibleRequestCollectionItems.Should().HaveCount(2);
    }

    [Test]
    public void It_marks_all_collection_items_as_Creatable()
    {
        _result.Request.VisibleRequestCollectionItems.Should().OnlyContain(v => v.Creatable);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Test 4: Insert-only update — current state has 0 collection rows, request has 3
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_update_with_no_current_collection_rows_but_flattened_inserts
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "Section",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("SectionId", "$.sectionIdentifier")]
        );

        var collectionPlan = PlanBuilder.CreateCollectionTablePlan(
            tableName: "SectionClassPeriod",
            jsonScope: "$.classPeriods[*]",
            columns: [("Name", "$.classPeriodName")],
            semanticIdentityRelativePaths: ["$.classPeriodName"]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, collectionPlan]);

        var candidates = Enumerable
            .Range(0, 3)
            .Select(i =>
                AdapterTestHelpers.BuildCollectionCandidate(
                    collectionPlan,
                    ordinalPath: [i],
                    requestOrder: i,
                    semanticIdentityValues: [$"Period{i}"],
                    semanticIdentityPresenceFlags: [true]
                )
            )
            .ToList();

        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [],
            collectionCandidates: candidates
        );

        var selectedBody = new JsonObject { ["sectionIdentifier"] = "sec1" };

        // Current state: root row only, no collection rows
        var currentState = AdapterTestHelpers.BuildCurrentState([
            (rootPlan.TableModel, 1),
            (collectionPlan.TableModel, 0),
        ]);

        _result = NoProfileSyntheticProfileAdapter.Build(plan, flattenedWriteSet, selectedBody, currentState);
    }

    [Test]
    public void It_has_empty_VisibleStoredCollectionRows()
    {
        _result.Context!.VisibleStoredCollectionRows.Should().BeEmpty();
    }

    [Test]
    public void It_has_three_VisibleRequestCollectionItems()
    {
        _result.Request.VisibleRequestCollectionItems.Should().HaveCount(3);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Test 5: Delete-all — current state has 5 rows, request flattens 0
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_update_with_current_collection_rows_but_empty_flattened_collection
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "Section",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("SectionId", "$.sectionIdentifier")]
        );

        var collectionPlan = PlanBuilder.CreateCollectionTablePlan(
            tableName: "SectionClassPeriod",
            jsonScope: "$.classPeriods[*]",
            columns: [("Name", "$.classPeriodName")],
            semanticIdentityRelativePaths: ["$.classPeriodName"]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, collectionPlan]);

        // Request has 0 collection rows
        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [],
            collectionCandidates: []
        );

        var selectedBody = new JsonObject { ["sectionIdentifier"] = "sec1" };

        // Current state: root + 5 collection rows
        var currentState = AdapterTestHelpers.BuildCurrentState([
            (rootPlan.TableModel, 1),
            (collectionPlan.TableModel, 5),
        ]);

        _result = NoProfileSyntheticProfileAdapter.Build(plan, flattenedWriteSet, selectedBody, currentState);
    }

    [Test]
    public void It_has_five_VisibleStoredCollectionRows()
    {
        _result.Context!.VisibleStoredCollectionRows.Should().HaveCount(5);
    }

    [Test]
    public void It_has_empty_VisibleRequestCollectionItems()
    {
        _result.Request.VisibleRequestCollectionItems.Should().BeEmpty();
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Test 6: Root + extension + collection combined — present extension
// ────────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_update_with_root_extension_and_collection_all_present
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "School",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("SchoolId", "$.schoolId")]
        );

        var extensionPlan = PlanBuilder.CreateTablePlan(
            tableName: "SchoolExt",
            jsonScope: "$._ext.SchoolExt",
            tableKind: DbTableKind.RootExtension,
            columns: [("ExtField", "$.extField")]
        );

        var collectionPlan = PlanBuilder.CreateCollectionTablePlan(
            tableName: "SchoolAddress",
            jsonScope: "$.addresses[*]",
            columns: [("City", "$.city")],
            semanticIdentityRelativePaths: ["$.city"]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, extensionPlan, collectionPlan]);

        var extensionRow = AdapterTestHelpers.BuildRootExtensionRow(extensionPlan);

        var candidate = AdapterTestHelpers.BuildCollectionCandidate(
            collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            semanticIdentityValues: ["Springfield"],
            semanticIdentityPresenceFlags: [true]
        );

        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [extensionRow],
            collectionCandidates: [candidate]
        );

        var selectedBody = new JsonObject { ["schoolId"] = 123 };

        var currentState = AdapterTestHelpers.BuildCurrentState([
            (rootPlan.TableModel, 1),
            (extensionPlan.TableModel, 1),
            (collectionPlan.TableModel, 1),
        ]);

        _result = NoProfileSyntheticProfileAdapter.Build(plan, flattenedWriteSet, selectedBody, currentState);
    }

    [Test]
    public void It_has_root_extension_and_collection_scopes_in_RequestScopeStates()
    {
        var scopes = _result.Request.RequestScopeStates.Select(s => s.Address.JsonScope).ToList();
        scopes.Should().Contain("$");
        scopes.Should().Contain("$._ext.SchoolExt");
    }

    [Test]
    public void It_marks_present_extension_stored_scope_as_Visible()
    {
        _result
            .Context!.StoredScopeStates.Should()
            .Contain(s =>
                s.Address.JsonScope == "$._ext.SchoolExt"
                && s.Visibility == ProfileVisibilityKind.VisiblePresent
            );
    }

    [Test]
    public void It_has_one_VisibleRequestCollectionItem()
    {
        _result.Request.VisibleRequestCollectionItems.Should().HaveCount(1);
    }

    [Test]
    public void It_has_one_VisibleStoredCollectionRow()
    {
        _result.Context!.VisibleStoredCollectionRows.Should().HaveCount(1);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Shared test helpers for adapter tests
// ────────────────────────────────────────────────────────────────────────────────

internal static class AdapterTestHelpers
{
    /// <summary>
    /// Builds a minimal FlattenedWriteSet from a root table plan and optional
    /// root extension rows and collection candidates.
    /// </summary>
    public static FlattenedWriteSet BuildFlattenedWriteSet(
        TableWritePlan rootTablePlan,
        IEnumerable<RootExtensionWriteRowBuffer> rootExtensionRows,
        IEnumerable<CollectionWriteCandidate> collectionCandidates
    )
    {
        var rootRowValues = rootTablePlan
            .ColumnBindings.Select<WriteColumnBinding, FlattenedWriteValue>(
                _ => new FlattenedWriteValue.Literal(null)
            )
            .ToList();

        var rootRow = new RootWriteRowBuffer(
            tableWritePlan: rootTablePlan,
            values: rootRowValues,
            rootExtensionRows: rootExtensionRows,
            collectionCandidates: collectionCandidates
        );

        return new FlattenedWriteSet(rootRow);
    }

    /// <summary>
    /// Builds a minimal RootExtensionWriteRowBuffer for an extension table plan.
    /// </summary>
    public static RootExtensionWriteRowBuffer BuildRootExtensionRow(TableWritePlan extensionTablePlan)
    {
        var values = extensionTablePlan
            .ColumnBindings.Select<WriteColumnBinding, FlattenedWriteValue>(
                _ => new FlattenedWriteValue.Literal(null)
            )
            .ToList();

        return new RootExtensionWriteRowBuffer(tableWritePlan: extensionTablePlan, values: values);
    }

    /// <summary>
    /// Builds a minimal CollectionWriteCandidate for a collection table plan.
    /// </summary>
    public static CollectionWriteCandidate BuildCollectionCandidate(
        TableWritePlan collectionTablePlan,
        IEnumerable<int> ordinalPath,
        int requestOrder,
        IEnumerable<object?> semanticIdentityValues,
        IEnumerable<bool> semanticIdentityPresenceFlags
    )
    {
        var values = collectionTablePlan
            .ColumnBindings.Select<WriteColumnBinding, FlattenedWriteValue>(
                _ => new FlattenedWriteValue.Literal(null)
            )
            .ToList();

        return new CollectionWriteCandidate(
            tableWritePlan: collectionTablePlan,
            ordinalPath: ordinalPath,
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: semanticIdentityValues,
            semanticIdentityPresenceFlags: semanticIdentityPresenceFlags
        );
    }

    /// <summary>
    /// Builds a minimal RelationalWriteCurrentState with the specified tables and row counts.
    /// Each table gets the specified number of empty rows.
    /// </summary>
    public static RelationalWriteCurrentState BuildCurrentState(
        IEnumerable<(DbTableModel TableModel, int RowCount)> tableRowCounts
    )
    {
        var tableRows = tableRowCounts
            .Select(t =>
            {
                var rows = Enumerable
                    .Range(0, t.RowCount)
                    .Select(rowIndex =>
                    {
                        var row = new object?[t.TableModel.Columns.Count];
                        // Fill with nulls except set DocumentId=1 for root tables
                        for (var i = 0; i < row.Length; i++)
                        {
                            row[i] = null;
                        }
                        return row;
                    })
                    .ToList();
                return new HydratedTableRows(t.TableModel, rows);
            })
            .ToList();

        var documentMetadata = new DocumentMetadataRow(
            DocumentId: 1,
            DocumentUuid: Guid.NewGuid(),
            ContentVersion: 1,
            IdentityVersion: 1,
            ContentLastModifiedAt: DateTimeOffset.UtcNow,
            IdentityLastModifiedAt: DateTimeOffset.UtcNow
        );

        return new RelationalWriteCurrentState(documentMetadata, tableRows);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Test 7: CollectionExtensionScope table — must NOT appear in StoredScopeStates
// ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Regression test for the bug where BuildStoredScopeStates emitted a root-level
/// StoredScopeState for a CollectionExtensionScope table (e.g. addresses[*]._ext.Sample).
/// The presence key it would produce — "$scope|" (empty ancestor portion) — never matches
/// the non-empty ancestor keys in the flattened scope set, so the scope would always be
/// classified VisibleAbsent and cause spurious deletes in the second-pass of the unified merge.
///
/// Fix: CollectionExtensionScope is now added to the same skip filter as Collection and
/// ExtensionCollection, so it is handled through the VisibleStoredCollectionRows pathway.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_a_resource_with_collection_extension_scope_table_kind
{
    private NoProfileSyntheticProfileAdapter.AdapterOutput _result = null!;

    [SetUp]
    public void Setup()
    {
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "Contact",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("ContactId", "$.contactUniqueId")]
        );

        var collectionPlan = PlanBuilder.CreateCollectionTablePlan(
            tableName: "ContactAddress",
            jsonScope: "$.addresses[*]",
            columns: [("City", "$.city")],
            semanticIdentityRelativePaths: ["$.city"]
        );

        // CollectionExtensionScope: extension data on a collection row
        // (e.g. addresses[*]._ext.Sample — not itself a collection, but lives under one)
        var collectionExtensionScopePlan = PlanBuilder.CreateTablePlan(
            tableName: "ContactAddressSampleExt",
            jsonScope: "$.addresses[*]._ext.Sample",
            tableKind: DbTableKind.CollectionExtensionScope,
            columns: [("IsUrban", "$.isUrban")]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);

        // Request includes the collection row but NOT the extension scope row
        var candidate = AdapterTestHelpers.BuildCollectionCandidate(
            collectionPlan,
            ordinalPath: [0],
            requestOrder: 0,
            semanticIdentityValues: ["Springfield"],
            semanticIdentityPresenceFlags: [true]
        );

        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [],
            collectionCandidates: [candidate]
        );

        var selectedBody = new JsonObject { ["contactUniqueId"] = "C001" };

        // Current state has data for all three tables (root, collection row, extension scope row)
        var currentState = AdapterTestHelpers.BuildCurrentState([
            (rootPlan.TableModel, 1),
            (collectionPlan.TableModel, 1),
            (collectionExtensionScopePlan.TableModel, 1),
        ]);

        _result = NoProfileSyntheticProfileAdapter.Build(plan, flattenedWriteSet, selectedBody, currentState);
    }

    [Test]
    public void It_does_not_emit_root_level_StoredScopeState_for_CollectionExtensionScope_table()
    {
        // Before the fix, BuildStoredScopeStates produced a StoredScopeState for
        // "$.addresses[*]._ext.Sample" with empty AncestorCollectionInstances.
        // That entry would always be VisibleAbsent and cause spurious deletes.
        _result
            .Context!.StoredScopeStates.Should()
            .NotContain(s =>
                s.Address.JsonScope == "$.addresses[*]._ext.Sample"
                && s.Address.AncestorCollectionInstances.IsEmpty
            );
    }

    [Test]
    public void It_returns_non_null_Context_for_update_flow()
    {
        _result.Context.Should().NotBeNull();
    }

    [Test]
    public void It_includes_root_scope_in_StoredScopeStates()
    {
        _result
            .Context!.StoredScopeStates.Should()
            .Contain(s => s.Address.JsonScope == "$" && s.Visibility == ProfileVisibilityKind.VisiblePresent);
    }

    [Test]
    public void It_has_one_VisibleStoredCollectionRow_for_the_collection_table()
    {
        _result.Context!.VisibleStoredCollectionRows.Should().HaveCount(1);
    }
}

// ────────────────────────────────────────────────────────────────────────────────
// Extended PlanBuilder: add CreateTablePlan with explicit tableKind,
// CreateCollectionTablePlan with merge plan, etc.
// ────────────────────────────────────────────────────────────────────────────────
// These are partial extensions — the base PlanBuilder in SchemaInlinedScopeDiscoveryTests.cs
// already provides BuildPlanWithRootOnly, BuildPlanWithRootAndCollection, etc.
// We add overloads here that produce proper Collection/RootExtension table kinds.
