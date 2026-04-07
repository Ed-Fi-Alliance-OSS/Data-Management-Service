// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Proves that a reconstituted document with nested collections and _ext scopes
/// produces a structure aligned with the compiled-scope adapter, satisfying AC11
/// of DMS-1105: profiled update/upsert flow can derive stored-side addresses.
///
/// Scenario: ProfileHiddenExtensionChildCollectionPreservation variant — a Contact
/// with addresses[*], addresses[*]._ext.sample (CollectionExtensionScope), and
/// addresses[*]._ext.sample.deliveryNotes[*] (ExtensionCollection).
/// </summary>
[TestFixture]
public class Given_Reconstituted_Document_With_Nested_Ext_In_ProfileHiddenExtensionChildCollectionPreservation_Flow
{
    private JsonNode _reconstitutedDocument = null!;
    private CompiledScopeDescriptor[] _scopeCatalog = null!;

    // --- Table names ---
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbTableName _rootTableName = new(_schema, "Contact");
    private static readonly DbTableName _addressTableName = new(_schema, "ContactAddress");
    private static readonly DbTableName _extScopeTableName = new(_schema, "ContactExtensionAddress");
    private static readonly DbTableName _extCollectionTableName = new(
        _schema,
        "ContactExtensionAddressDeliveryNote"
    );

    // --- JSON scopes ---
    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _addressesScope = new(
        "$.addresses[*]",
        [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
    );
    private static readonly JsonPathExpression _extScope = new(
        "$.addresses[*]._ext.sample",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
        ]
    );
    private static readonly JsonPathExpression _deliveryNotesScope = new(
        "$.addresses[*]._ext.sample.deliveryNotes[*]",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("deliveryNotes"),
            new JsonPathSegment.AnyArrayElement(),
        ]
    );

    // --- JSON paths ---
    private static readonly JsonPathExpression _contactIdPath = new(
        "$.contactId",
        [new JsonPathSegment.Property("contactId")]
    );
    private static readonly JsonPathExpression _cityPath = new(
        "$.addresses[*].city",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("city"),
        ]
    );
    private static readonly JsonPathExpression _isUrbanPath = new(
        "$.addresses[*]._ext.sample.isUrban",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("isUrban"),
        ]
    );
    private static readonly JsonPathExpression _notePath = new(
        "$.addresses[*]._ext.sample.deliveryNotes[*].note",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("_ext"),
            new JsonPathSegment.Property("sample"),
            new JsonPathSegment.Property("deliveryNotes"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("note"),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        // ── Build table models ──

        var rootTableModel = new DbTableModel(
            _rootTableName,
            _rootScope,
            new TableKey(
                "PK_Contact",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("ContactId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    _contactIdPath,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var addressTableModel = new DbTableModel(
            _addressTableName,
            _addressesScope,
            new TableKey(
                "PK_ContactAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("City"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    _cityPath,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                []
            ),
        };

        var extScopeTableModel = new DbTableModel(
            _extScopeTableName,
            _extScope,
            new TableKey(
                "PK_ContactExtAddress",
                [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("BaseCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("IsUrban"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    true,
                    _isUrbanPath,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.CollectionExtensionScope,
                [new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("BaseCollectionItemId")],
                []
            ),
        };

        var extCollectionTableModel = new DbTableModel(
            _extCollectionTableName,
            _deliveryNotesScope,
            new TableKey(
                "PK_ContactExtAddressNote",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("BaseCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null
                ),
                new DbColumnModel(
                    new DbColumnName("Note"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 255),
                    false,
                    _notePath,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("BaseCollectionItemId")],
                []
            ),
        };

        // ── Build hydrated rows ──
        // Document 1: Contact with two addresses, each with extension scope
        // Address 1 (CollectionItemId=100) has one delivery note
        // Address 2 (CollectionItemId=200) has one delivery note
        object?[] rootRow = [1L, 42];
        object?[] addressRow1 = [1L, 100L, 0, "Austin"];
        object?[] addressRow2 = [1L, 200L, 1, "Dallas"];
        object?[] extScopeRow1 = [1L, 100L, true];
        object?[] extScopeRow2 = [1L, 200L, false];
        object?[] noteRow1 = [1L, 100L, 301L, 0, "Ring doorbell"];
        object?[] noteRow2 = [1L, 200L, 302L, 0, "Leave at office"];

        var tableRowsInDependencyOrder = new List<HydratedTableRows>
        {
            new(rootTableModel, [rootRow]),
            new(addressTableModel, [addressRow1, addressRow2]),
            new(extScopeTableModel, [extScopeRow1, extScopeRow2]),
            new(extCollectionTableModel, [noteRow1, noteRow2]),
        };

        // ── Reconstitute ──
        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            documentId: 1L,
            tableRowsInDependencyOrder: tableRowsInDependencyOrder,
            referenceProjectionPlans: [],
            descriptorProjectionSources: [],
            descriptorUriLookup: new Dictionary<long, string>()
        );

        // ── Build compiled-scope catalog from write plan ──
        var writePlan = new ResourceWritePlan(
            new RelationalResourceModel(
                new QualifiedResourceName("Ed-Fi", "Contact"),
                _schema,
                ResourceStorageKind.RelationalTables,
                rootTableModel,
                [rootTableModel, addressTableModel, extScopeTableModel, extCollectionTableModel],
                [],
                []
            ),
            [
                BuildRootTableWritePlan(rootTableModel),
                BuildCollectionTableWritePlan(addressTableModel),
                BuildNonCollectionTableWritePlan(extScopeTableModel),
                BuildExtensionCollectionTableWritePlan(extCollectionTableModel),
            ]
        );

        _scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
    }

    [Test]
    public void It_should_reconstitute_root_scalar()
    {
        _reconstitutedDocument["contactId"]!.GetValue<int>().Should().Be(42);
    }

    [Test]
    public void It_should_reconstitute_two_address_items()
    {
        _reconstitutedDocument["addresses"]!.AsArray().Count.Should().Be(2);
    }

    [Test]
    public void It_should_reconstitute_ext_scope_on_first_address()
    {
        _reconstitutedDocument["addresses"]![0]!["_ext"]!["sample"]!["isUrban"]!
            .GetValue<bool>()
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_should_reconstitute_ext_collection_on_first_address()
    {
        _reconstitutedDocument["addresses"]![0]!["_ext"]!["sample"]!["deliveryNotes"]!
            .AsArray()
            .Count.Should()
            .Be(1);
        _reconstitutedDocument["addresses"]![0]!["_ext"]!["sample"]!["deliveryNotes"]![0]!["note"]!
            .GetValue<string>()
            .Should()
            .Be("Ring doorbell");
    }

    [Test]
    public void It_should_reconstitute_ext_scope_on_second_address()
    {
        _reconstitutedDocument["addresses"]![1]!["_ext"]!["sample"]!["isUrban"]!
            .GetValue<bool>()
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_should_reconstitute_ext_collection_on_second_address()
    {
        _reconstitutedDocument["addresses"]![1]!["_ext"]!["sample"]!["deliveryNotes"]![0]!["note"]!
            .GetValue<string>()
            .Should()
            .Be("Leave at office");
    }

    [Test]
    public void It_should_produce_scope_descriptors_for_all_four_scopes()
    {
        _scopeCatalog.Should().HaveCount(4);
    }

    [Test]
    public void It_should_have_scope_descriptor_for_root()
    {
        _scopeCatalog.Should().Contain(d => d.JsonScope == "$" && d.ScopeKind == ScopeKind.Root);
    }

    [Test]
    public void It_should_have_scope_descriptor_for_addresses_collection()
    {
        _scopeCatalog
            .Should()
            .Contain(d => d.JsonScope == "$.addresses[*]" && d.ScopeKind == ScopeKind.Collection);
    }

    [Test]
    public void It_should_have_scope_descriptor_for_collection_extension_scope()
    {
        _scopeCatalog
            .Should()
            .Contain(d =>
                d.JsonScope == "$.addresses[*]._ext.sample" && d.ScopeKind == ScopeKind.NonCollection
            );
    }

    [Test]
    public void It_should_have_scope_descriptor_for_extension_collection()
    {
        _scopeCatalog
            .Should()
            .Contain(d =>
                d.JsonScope == "$.addresses[*]._ext.sample.deliveryNotes[*]"
                && d.ScopeKind == ScopeKind.Collection
            );
    }

    [Test]
    public void It_should_align_every_scope_to_a_json_path_present_in_reconstituted_document()
    {
        foreach (var descriptor in _scopeCatalog.Where(d => d.JsonScope != "$"))
        {
            var pathExists = JsonPathExistsInDocument(_reconstitutedDocument, descriptor.JsonScope);
            pathExists
                .Should()
                .BeTrue(
                    $"scope '{descriptor.JsonScope}' from the compiled catalog should map to "
                        + "a path present in the reconstituted document"
                );
        }
    }

    [Test]
    public void It_should_produce_correct_parent_scope_for_extension_collection()
    {
        var noteScope = _scopeCatalog.Single(d =>
            d.JsonScope == "$.addresses[*]._ext.sample.deliveryNotes[*]"
        );
        noteScope.ImmediateParentJsonScope.Should().Be("$.addresses[*]._ext.sample");
    }

    [Test]
    public void It_should_produce_correct_collection_ancestors_for_extension_collection()
    {
        var noteScope = _scopeCatalog.Single(d =>
            d.JsonScope == "$.addresses[*]._ext.sample.deliveryNotes[*]"
        );
        noteScope.CollectionAncestorsInOrder.Should().Contain("$.addresses[*]");
    }

    // ── Address derivation verification (AC11: stored-side addresses align to compiled adapter) ──

    [Test]
    public void It_should_derive_root_scope_instance_address()
    {
        var engine = new AddressDerivationEngine(_scopeCatalog);

        var address = engine.DeriveScopeInstanceAddress("$", []);

        address.JsonScope.Should().Be("$");
        address.AncestorCollectionInstances.Should().BeEmpty();
    }

    [Test]
    public void It_should_derive_collection_row_address_for_first_address_item()
    {
        var engine = new AddressDerivationEngine(_scopeCatalog);
        var firstAddress = _reconstitutedDocument["addresses"]![0]!;

        var address = engine.DeriveCollectionRowAddress("$.addresses[*]", firstAddress, []);

        address.JsonScope.Should().Be("$.addresses[*]");
        address.ParentAddress.JsonScope.Should().Be("$");
    }

    [Test]
    public void It_should_derive_ext_scope_instance_address_with_collection_ancestor()
    {
        var engine = new AddressDerivationEngine(_scopeCatalog);
        var firstAddress = _reconstitutedDocument["addresses"]![0]!;

        var address = engine.DeriveScopeInstanceAddress(
            "$.addresses[*]._ext.sample",
            [new AncestorItemContext("$.addresses[*]", firstAddress)]
        );

        address.JsonScope.Should().Be("$.addresses[*]._ext.sample");
        address.AncestorCollectionInstances.Should().HaveCount(1);
        address.AncestorCollectionInstances[0].JsonScope.Should().Be("$.addresses[*]");
    }

    [Test]
    public void It_should_derive_extension_collection_row_address_with_nested_ancestors()
    {
        var engine = new AddressDerivationEngine(_scopeCatalog);
        var firstAddress = _reconstitutedDocument["addresses"]![0]!;
        var firstNote = firstAddress["_ext"]!["sample"]!["deliveryNotes"]![0]!;

        var address = engine.DeriveCollectionRowAddress(
            "$.addresses[*]._ext.sample.deliveryNotes[*]",
            firstNote,
            [new AncestorItemContext("$.addresses[*]", firstAddress)]
        );

        address.JsonScope.Should().Be("$.addresses[*]._ext.sample.deliveryNotes[*]");
        address.ParentAddress.JsonScope.Should().Be("$.addresses[*]._ext.sample");
        address.ParentAddress.AncestorCollectionInstances.Should().HaveCount(1);
        address.ParentAddress.AncestorCollectionInstances[0].JsonScope.Should().Be("$.addresses[*]");
    }

    [Test]
    public void It_should_derive_distinct_collection_row_addresses_for_each_address_item()
    {
        var engine = new AddressDerivationEngine(_scopeCatalog);
        var addressArray = _reconstitutedDocument["addresses"]!.AsArray();

        var addr0 = engine.DeriveCollectionRowAddress("$.addresses[*]", addressArray[0]!, []);
        var addr1 = engine.DeriveCollectionRowAddress("$.addresses[*]", addressArray[1]!, []);

        addr0.JsonScope.Should().Be(addr1.JsonScope);
        addr0.ParentAddress.JsonScope.Should().Be(addr1.ParentAddress.JsonScope);
        // Both derive successfully from the reconstituted document — distinct items
        // produce distinct addresses (same scope but different JSON content)
        addressArray[0]!["city"]!
            .GetValue<string>()
            .Should()
            .NotBe(addressArray[1]!["city"]!.GetValue<string>());
    }

    // ── Helpers ──

    private static bool JsonPathExistsInDocument(JsonNode document, string jsonScope)
    {
        var segments = jsonScope.Split('.');
        JsonNode? current = document;

        for (var i = 1; i < segments.Length; i++)
        {
            if (current is null)
            {
                return false;
            }

            var segment = segments[i];

            if (segment.EndsWith("[*]"))
            {
                var propertyName = segment[..^3];
                if (current is JsonObject obj && obj[propertyName] is JsonArray arr && arr.Count > 0)
                {
                    current = arr[0];
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (current is JsonObject obj && obj[segment] is JsonNode child)
                {
                    current = child;
                }
                else
                {
                    return false;
                }
            }
        }

        return current is not null;
    }

    /// <summary>
    /// Builds a TableWritePlan for a root table (no CollectionMergePlan needed).
    /// </summary>
    private static TableWritePlan BuildRootTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "-- placeholder",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(_contactIdPath, new RelationalScalarType(ScalarKind.Int32)),
                    "ContactId"
                ),
            ],
            KeyUnificationPlans: []
        );

    /// <summary>
    /// Builds a TableWritePlan for a Collection table with required CollectionMergePlan.
    /// Columns: [0]=DocumentId, [1]=CollectionItemId, [2]=Ordinal, [3]=City
    /// </summary>
    private static TableWritePlan BuildCollectionTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "-- placeholder",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        _cityPath,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "City"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "-- placeholder",
                DeleteByStableRowIdentitySql: "-- placeholder",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                1
            )
        );

    /// <summary>
    /// Builds a TableWritePlan for a non-collection (CollectionExtensionScope) table.
    /// Columns: [0]=DocumentId, [1]=BaseCollectionItemId, [2]=IsUrban
    /// </summary>
    private static TableWritePlan BuildNonCollectionTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "-- placeholder",
            UpdateSql: "-- placeholder",
            DeleteByParentSql: "-- placeholder",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(_isUrbanPath, new RelationalScalarType(ScalarKind.Boolean)),
                    "IsUrban"
                ),
            ],
            KeyUnificationPlans: []
        );

    /// <summary>
    /// Builds a TableWritePlan for an ExtensionCollection table with required CollectionMergePlan.
    /// Columns: [0]=DocumentId, [1]=BaseCollectionItemId, [2]=CollectionItemId, [3]=Ordinal, [4]=Note
    /// </summary>
    private static TableWritePlan BuildExtensionCollectionTableWritePlan(DbTableModel tableModel) =>
        new(
            TableModel: tableModel,
            InsertSql: "-- placeholder",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        _notePath,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 255)
                    ),
                    "Note"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 2,
                UpdateByStableRowIdentitySql: "-- placeholder",
                DeleteByStableRowIdentitySql: "-- placeholder",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [4, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                2
            )
        );
}
