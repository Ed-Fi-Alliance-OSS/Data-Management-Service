// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

// ── Classification outcome tests ───────────────────────────────────────────

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_root_only_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan()
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_RootTableOnly()
    {
        _result.Should().Be(RequiredSliceFamily.RootTableOnly);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_separate_table_non_collection_scope_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_top_level_collection_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleCollectionScope("$.addresses[*]")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_TopLevelCollection()
    {
        _result.Should().Be(RequiredSliceFamily.TopLevelCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_nested_collection_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*].periods[*]",
                "AddressPeriods",
                DbTableKind.Collection
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleCollectionScope("$.addresses[*].periods[*]")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_request_root_only_with_hidden_separate_table_in_stored
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        // Request only touches root scope
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$")
        );
        // Hidden stored separate-table scopes still require the separate-table slice
        // so later slices do not accidentally relax this fence too early.
        var context = ProfileSliceFenceClassifierTestHelpers.CreateContext(
            request,
            ProfileSliceFenceClassifierTestHelpers.HiddenStoredScope("$._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForExistingDocument(context, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

// ── Input mode tests ────────────────────────────────────────────────────────

[TestFixture]
public class Given_ProfileSliceFenceClassifier_ClassifyForCreateNew_ignores_stored_side
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            )
        );
        // Request only touches root
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$")
        );
        // ClassifyForCreateNew does not accept a context, so stored side is never consulted.
        // Verifying request-only classification returns RootTableOnly despite a top-level
        // collection scope existing in the topology index.
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_RootTableOnly_without_escalating_to_stored_side()
    {
        _result.Should().Be(RequiredSliceFamily.RootTableOnly);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_ClassifyForExistingDocument_uses_union_of_request_and_stored
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*].periods[*]",
                "AddressPeriods",
                DbTableKind.Collection
            )
        );
        // Request only touches a top-level collection → TopLevelCollection on its own
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleCollectionScope("$.addresses[*]")
        );
        // Stored side adds a visible-absent nested collection → escalates to NestedAndExtensionCollections
        var context = ProfileSliceFenceClassifierTestHelpers.CreateContext(
            request,
            ProfileSliceFenceClassifierTestHelpers.VisibleAbsentStoredScope("$.addresses[*].periods[*]")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForExistingDocument(context, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections_after_escalation_from_stored_side()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

// ── Extension collection variant tests ─────────────────────────────────────

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_root_level_extension_child_collection
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$._ext.sample.contacts[*]",
                "Contacts",
                DbTableKind.ExtensionCollection
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleCollectionScope("$._ext.sample.contacts[*]")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_collection_aligned_extension_child_collection
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$.addresses[*]._ext.sample",
                "AddressExtension",
                DbTableKind.CollectionExtensionScope
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]._ext.sample.deliveryNotes[*]",
                "DeliveryNotes",
                DbTableKind.ExtensionCollection
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleCollectionScope(
                "$.addresses[*]._ext.sample.deliveryNotes[*]"
            )
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_nested_extension_child_collection_under_extension_child_parent
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        // A nested extension collection under another extension collection (deep nesting)
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$._ext.sample.contacts[*]",
                "Contacts",
                DbTableKind.ExtensionCollection
            ),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$._ext.sample.contacts[*].phones[*]",
                "ContactPhones",
                DbTableKind.ExtensionCollection
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleCollectionScope(
                "$._ext.sample.contacts[*].phones[*]"
            )
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_request_root_only_with_hidden_separate_table_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.HiddenRequestScope("$._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_RootTableOnly()
    {
        _result.Should().Be(RequiredSliceFamily.RootTableOnly);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_request_root_only_with_visible_absent_separate_table_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleAbsentRequestScope("$._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_request_root_only_with_visible_present_separate_table_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_visible_absent_stored_separate_table_escalates
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$._ext.sample",
                "RootExtension",
                DbTableKind.RootExtension
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$")
        );
        var context = ProfileSliceFenceClassifierTestHelpers.CreateContext(
            request,
            ProfileSliceFenceClassifierTestHelpers.VisibleAbsentStoredScope("$._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForExistingDocument(context, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_hidden_stored_collection_aligned_extension_scope
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(
            ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
            ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                "$.addresses[*]",
                "Addresses",
                DbTableKind.Collection
            ),
            // CollectionExtensionScope: non-collection extension whose parent is a collection.
            // ScopeTopologyIndex maps this to SeparateTableNonCollection.
            ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                "$.addresses[*]._ext.sample",
                "AddressesExtSample",
                DbTableKind.CollectionExtensionScope
            )
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$")
        );
        var context = ProfileSliceFenceClassifierTestHelpers.CreateContext(
            request,
            ProfileSliceFenceClassifierTestHelpers.HiddenStoredScope("$.addresses[*]._ext.sample")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForExistingDocument(context, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

// ── Inlined descendant scope tests ─────────────────────────────────────────

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_visible_inlined_scope_under_top_level_collection_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndexWithInlined(
            [
                ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
                ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*]",
                    "Addresses",
                    DbTableKind.Collection
                ),
            ],
            ("$.addresses[*].mileInfo", ScopeKind.NonCollection)
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$.addresses[*].mileInfo")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_TopLevelCollection_from_inlined_scope_under_collection_ancestor()
    {
        _result.Should().Be(RequiredSliceFamily.TopLevelCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_visible_inlined_scope_under_nested_collection_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndexWithInlined(
            [
                ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
                ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*]",
                    "Addresses",
                    DbTableKind.Collection
                ),
                ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                    "$.addresses[*].periods[*]",
                    "AddressPeriods",
                    DbTableKind.Collection
                ),
            ],
            ("$.addresses[*].periods[*].notes", ScopeKind.NonCollection)
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$.addresses[*].periods[*].notes")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections_from_inlined_scope_under_nested_collection()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_visible_inlined_scope_under_root_extension_in_request
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndexWithInlined(
            [
                ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
                ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                    "$._ext.sample",
                    "RootExtension",
                    DbTableKind.RootExtension
                ),
            ],
            ("$._ext.sample.locator", ScopeKind.NonCollection)
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$"),
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$._ext.sample.locator")
        );
        _result = ProfileSliceFenceClassifier.ClassifyForCreateNew(request, index);
    }

    [Test]
    public void It_returns_SeparateTableNonCollection_from_inlined_scope_under_root_extension()
    {
        _result.Should().Be(RequiredSliceFamily.SeparateTableNonCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_with_visible_inlined_stored_scope_under_extension_collection
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndexWithInlined(
            [
                ProfileSliceFenceClassifierTestHelpers.RootTablePlan(),
                ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
                    "$._ext.sample",
                    "RootExtension",
                    DbTableKind.RootExtension
                ),
                ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
                    "$._ext.sample.contacts[*]",
                    "Contacts",
                    DbTableKind.ExtensionCollection
                ),
            ],
            ("$._ext.sample.contacts[*].phone", ScopeKind.NonCollection)
        );
        var request = ProfileSliceFenceClassifierTestHelpers.CreateRequest(
            ProfileSliceFenceClassifierTestHelpers.VisibleScope("$")
        );
        var context = ProfileSliceFenceClassifierTestHelpers.CreateContext(
            request,
            new StoredScopeState(
                Address: ProfileSliceFenceClassifierTestHelpers.ScopeAddress(
                    "$._ext.sample.contacts[*].phone"
                ),
                Visibility: ProfileVisibilityKind.VisiblePresent,
                HiddenMemberPaths: []
            )
        );
        _result = ProfileSliceFenceClassifier.ClassifyForExistingDocument(context, index);
    }

    [Test]
    public void It_returns_NestedAndExtensionCollections_from_inlined_scope_under_extension_collection()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

// ── ClassifyFromCatalog tests ──────────────────────────────────────────────

[TestFixture]
public class Given_ProfileSliceFenceClassifier_ClassifyFromCatalog_with_only_non_collection_scopes
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var rootPlan = ProfileSliceFenceClassifierTestHelpers.RootTablePlan();
        var extensionPlan = ProfileSliceFenceClassifierTestHelpers.CreateTablePlan(
            "$._ext.sample",
            "RootExtension",
            DbTableKind.RootExtension
        );
        var catalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            ProfileSliceFenceClassifierTestHelpers.CreateWritePlan(rootPlan, extensionPlan)
        );
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(rootPlan, extensionPlan);

        _result = ProfileSliceFenceClassifier.ClassifyFromCatalog(catalog, index);
    }

    [Test]
    public void It_returns_RootTableOnly()
    {
        _result.Should().Be(RequiredSliceFamily.RootTableOnly);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_ClassifyFromCatalog_with_top_level_collection_scope
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var rootPlan = ProfileSliceFenceClassifierTestHelpers.RootTablePlan();
        var collectionPlan = ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var catalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            ProfileSliceFenceClassifierTestHelpers.CreateWritePlan(rootPlan, collectionPlan)
        );
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(rootPlan, collectionPlan);

        _result = ProfileSliceFenceClassifier.ClassifyFromCatalog(catalog, index);
    }

    [Test]
    public void It_escalates_to_TopLevelCollection()
    {
        _result.Should().Be(RequiredSliceFamily.TopLevelCollection);
    }
}

[TestFixture]
public class Given_ProfileSliceFenceClassifier_ClassifyFromCatalog_with_nested_collection_scope
{
    private RequiredSliceFamily _result;

    [SetUp]
    public void Setup()
    {
        var rootPlan = ProfileSliceFenceClassifierTestHelpers.RootTablePlan();
        var addressesPlan = ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var periodsPlan = ProfileSliceFenceClassifierTestHelpers.CreateCollectionTablePlan(
            "$.addresses[*].periods[*]",
            "AddressPeriods",
            DbTableKind.Collection
        );
        var catalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            ProfileSliceFenceClassifierTestHelpers.CreateWritePlan(rootPlan, addressesPlan, periodsPlan)
        );
        var index = ProfileSliceFenceClassifierTestHelpers.BuildIndex(rootPlan, addressesPlan, periodsPlan);

        _result = ProfileSliceFenceClassifier.ClassifyFromCatalog(catalog, index);
    }

    [Test]
    public void It_escalates_to_NestedAndExtensionCollections()
    {
        _result.Should().Be(RequiredSliceFamily.NestedAndExtensionCollections);
    }
}

// ── Shared test helpers ────────────────────────────────────────────────────

file static class ProfileSliceFenceClassifierTestHelpers
{
    private static readonly QualifiedResourceName Resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName Schema = new("edfi");

    // ── Plan construction ──────────────────────────────────────────────────

    public static TableWritePlan RootTablePlan() => CreateTablePlan("$", "Root", DbTableKind.Root);

    public static TableWritePlan CreateTablePlan(string jsonScope, string tableName, DbTableKind tableKind)
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
            Table: new DbTableName(Schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@DocumentId)",
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

    public static TableWritePlan CreateCollectionTablePlan(
        string jsonScope,
        string tableName,
        DbTableKind tableKind
    )
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
        var nameColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Name"),
            Kind: ColumnKind.Scalar,
            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
            TargetResource: null
        );

        var columns = new DbColumnModel[] { collectionKeyColumn, parentKeyColumn, ordinalColumn, nameColumn };

        var tableModel = new DbTableModel(
            Table: new DbTableName(Schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new DbColumnName("Name")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@CollectionItemId, @ParentDocumentId, @Ordinal, @Name)",
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
                    nameColumn,
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: $"UPDATE edfi.\"{tableName}\" SET \"Name\" = @Name WHERE \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: $"DELETE FROM edfi.\"{tableName}\" WHERE \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    public static ResourceWritePlan CreateWritePlan(params TableWritePlan[] tablePlans)
    {
        var rootModel = tablePlans[0].TableModel;

        var model = new RelationalResourceModel(
            Resource: Resource,
            PhysicalSchema: Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootModel,
            TablesInDependencyOrder: tablePlans.Select(tp => tp.TableModel).ToList(),
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(model, tablePlans);
    }

    public static ScopeTopologyIndex BuildIndex(params TableWritePlan[] tablePlans) =>
        ScopeTopologyIndex.BuildFromWritePlan(CreateWritePlan(tablePlans));

    public static ScopeTopologyIndex BuildIndexWithInlined(
        TableWritePlan[] tablePlans,
        params (string JsonScope, ScopeKind Kind)[] additionalScopes
    ) => ScopeTopologyIndex.BuildFromWritePlan(CreateWritePlan(tablePlans), additionalScopes);

    // ── Profile metadata factories ─────────────────────────────────────────

    public static ScopeInstanceAddress ScopeAddress(string jsonScope) => new(jsonScope, []);

    public static CollectionRowAddress CollectionAddress(string jsonScope) =>
        new(jsonScope, new ScopeInstanceAddress("$", []), []);

    public static RequestScopeState VisibleScope(string jsonScope, bool creatable = true) =>
        new(ScopeAddress(jsonScope), ProfileVisibilityKind.VisiblePresent, creatable);

    public static VisibleRequestCollectionItem VisibleCollectionScope(
        string jsonScope,
        bool creatable = true
    ) => new(CollectionAddress(jsonScope), creatable, jsonScope);

    public static StoredScopeState HiddenStoredScope(string jsonScope) =>
        new(ScopeAddress(jsonScope), ProfileVisibilityKind.Hidden, []);

    public static RequestScopeState HiddenRequestScope(string jsonScope, bool creatable = false) =>
        new(ScopeAddress(jsonScope), ProfileVisibilityKind.Hidden, creatable);

    public static RequestScopeState VisibleAbsentRequestScope(string jsonScope, bool creatable = true) =>
        new(ScopeAddress(jsonScope), ProfileVisibilityKind.VisibleAbsent, creatable);

    public static StoredScopeState VisibleAbsentStoredScope(string jsonScope) =>
        new(ScopeAddress(jsonScope), ProfileVisibilityKind.VisibleAbsent, []);

    public static ProfileAppliedWriteRequest CreateRequest(params object[] scopesAndItems)
    {
        var scopes = scopesAndItems.OfType<RequestScopeState>().ToImmutableArray();
        var items = scopesAndItems.OfType<VisibleRequestCollectionItem>().ToImmutableArray();
        return new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: scopes,
            VisibleRequestCollectionItems: items
        );
    }

    public static ProfileAppliedWriteContext CreateContext(
        ProfileAppliedWriteRequest request,
        params StoredScopeState[] storedScopes
    ) =>
        new(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates: [.. storedScopes],
            VisibleStoredCollectionRows: []
        );
}
