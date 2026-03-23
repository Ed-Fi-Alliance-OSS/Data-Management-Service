// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for DbTableModel identity metadata default behavior.
/// </summary>
[TestFixture]
public class Given_DbTableModel_Identity_Metadata_Defaults
{
    private DbTableModel _table = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _table = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            CreateJsonPath("$"),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart)]
            ),
            [],
            []
        );
    }

    /// <summary>
    /// It should default to empty identity metadata.
    /// </summary>
    [Test]
    public void It_should_default_to_empty_identity_metadata()
    {
        _table.IdentityMetadata.Should().Be(DbTableIdentityMetadata.Empty);
    }

    private static JsonPathExpression CreateJsonPath(string canonical, params JsonPathSegment[] segments)
    {
        return new JsonPathExpression(canonical, segments);
    }
}

/// <summary>
/// Test fixture for explicit DbTableModel identity metadata.
/// </summary>
[TestFixture]
public class Given_DbTableModel_Identity_Metadata
{
    private DbTableModel _collectionTable = default!;
    private DbTableModel _collectionAlignedExtensionScopeTable = default!;
    private DbTableModel _extensionCollectionTable = default!;
    private DbTableModel _rootExtensionTable = default!;
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rootIdentityColumn = RelationalNameConventions.DocumentIdColumnName;
        var rootLocatorColumn = RelationalNameConventions.RootDocumentIdColumnName("School");
        var collectionIdentityColumn = RelationalNameConventions.CollectionItemIdColumnName;
        var baseCollectionIdentityColumn = RelationalNameConventions.BaseCollectionItemIdColumnName;

        var semanticIdentityBindings = new CollectionSemanticIdentityBinding[]
        {
            new(
                CreateJsonPath("$.beginDate", new JsonPathSegment.Property("beginDate")),
                new DbColumnName("BeginDate")
            ),
            new(
                CreateJsonPath("$.endDate", new JsonPathSegment.Property("endDate")),
                new DbColumnName("EndDate")
            ),
        };

        _rootTable = CreateTable(
            tableName: "School",
            keyColumns: [new DbKeyColumn(rootIdentityColumn, ColumnKind.ParentKeyPart)],
            identityMetadata: new DbTableIdentityMetadata(
                DbTableKind.Root,
                [rootIdentityColumn],
                [rootIdentityColumn],
                [],
                []
            )
        );

        _collectionTable = CreateTable(
            tableName: "SchoolAddress",
            jsonScope: "$.addresses[*]",
            keyColumns: [new DbKeyColumn(collectionIdentityColumn, ColumnKind.CollectionKey)],
            identityMetadata: new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [collectionIdentityColumn],
                [rootLocatorColumn],
                [rootLocatorColumn],
                semanticIdentityBindings
            )
        );

        _rootExtensionTable = CreateTable(
            tableName: "SchoolExtension",
            jsonScope: "$._ext.sample",
            keyColumns: [new DbKeyColumn(rootIdentityColumn, ColumnKind.ParentKeyPart)],
            identityMetadata: new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [rootIdentityColumn],
                [rootIdentityColumn],
                [rootIdentityColumn],
                []
            )
        );

        _collectionAlignedExtensionScopeTable = CreateTable(
            tableName: "SchoolAddressExtension",
            jsonScope: "$.addresses[*]._ext.sample",
            keyColumns: [new DbKeyColumn(baseCollectionIdentityColumn, ColumnKind.ParentKeyPart)],
            identityMetadata: new DbTableIdentityMetadata(
                DbTableKind.CollectionExtensionScope,
                [baseCollectionIdentityColumn],
                [rootLocatorColumn],
                [baseCollectionIdentityColumn],
                []
            )
        );

        _extensionCollectionTable = CreateTable(
            tableName: "SchoolExtensionIntervention",
            jsonScope: "$._ext.sample.interventions[*]",
            keyColumns: [new DbKeyColumn(collectionIdentityColumn, ColumnKind.CollectionKey)],
            identityMetadata: new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [collectionIdentityColumn],
                [rootLocatorColumn],
                [rootLocatorColumn],
                semanticIdentityBindings
            )
        );
    }

    /// <summary>
    /// It should distinguish table roles explicitly even when locator shape overlaps.
    /// </summary>
    [Test]
    public void It_should_distinguish_table_roles_explicitly_even_when_locator_shape_overlaps()
    {
        _collectionTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.Collection);
        _extensionCollectionTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.ExtensionCollection);
        _rootTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.Root);
        _rootExtensionTable.IdentityMetadata.TableKind.Should().Be(DbTableKind.RootExtension);
        _collectionAlignedExtensionScopeTable
            .IdentityMetadata.TableKind.Should()
            .Be(DbTableKind.CollectionExtensionScope);

        _collectionTable
            .IdentityMetadata.PhysicalRowIdentityColumns.Should()
            .Equal(_extensionCollectionTable.IdentityMetadata.PhysicalRowIdentityColumns);
        _collectionTable
            .IdentityMetadata.RootScopeLocatorColumns.Should()
            .Equal(_extensionCollectionTable.IdentityMetadata.RootScopeLocatorColumns);
        _collectionTable
            .IdentityMetadata.ImmediateParentScopeLocatorColumns.Should()
            .Equal(_extensionCollectionTable.IdentityMetadata.ImmediateParentScopeLocatorColumns);
    }

    /// <summary>
    /// It should preserve semantic identity bindings in compiled order.
    /// </summary>
    [Test]
    public void It_should_preserve_semantic_identity_bindings_in_compiled_order()
    {
        _collectionTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.RelativePath.Canonical)
            .Should()
            .Equal("$.beginDate", "$.endDate");
    }

    private static DbTableModel CreateTable(
        string tableName,
        IReadOnlyList<DbKeyColumn> keyColumns,
        DbTableIdentityMetadata identityMetadata,
        string jsonScope = "$"
    )
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            CreateJsonPath(jsonScope),
            new TableKey($"PK_{tableName}", keyColumns),
            [],
            []
        ) with
        {
            IdentityMetadata = identityMetadata,
        };
    }

    private static JsonPathExpression CreateJsonPath(string canonical, params JsonPathSegment[] segments)
    {
        return new JsonPathExpression(canonical, segments);
    }
}
