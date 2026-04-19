// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Multi_Document_Page
{
    private PageReconstitutionContext _context = null!;
    private DbTableName _rootTable = default;
    private DbTableName _addressTable = default;
    private DbTableName _periodTable = default;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();

        _context = PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage);
        _rootTable = pageData.RootTable;
        _addressTable = pageData.AddressTable;
        _periodTable = pageData.PeriodTable;
    }

    [Test]
    public void It_should_preserve_document_metadata_order_even_when_root_rows_are_hydrated_in_a_different_order()
    {
        _context.DocumentsInOrder.Select(document => document.DocumentId).Should().Equal(101L, 202L);
    }

    [Test]
    public void It_should_build_a_single_descriptor_lookup_for_the_page()
    {
        _context.DescriptorUrisById.Should().HaveCount(2);
        _context
            .GetDescriptorUriOrThrow(601L)
            .Should()
            .Be("uri://ed-fi.org/SchoolCategoryDescriptor#Alternative");
        _context
            .GetDescriptorUriOrThrow(602L)
            .Should()
            .Be("uri://ed-fi.org/SchoolCategoryDescriptor#Charter");
    }

    [Test]
    public void It_should_attach_child_rows_to_their_immediate_parents()
    {
        var firstDocument = _context.DocumentsInOrder[0];
        var firstAddress = _context.GetRowOrThrow(_addressTable, new ScopeKey([101L]));
        var nestedPeriod = _context.GetRowOrThrow(_periodTable, new ScopeKey([101L]));

        firstDocument.RootRow.GetImmediateChildren(_addressTable).Should().HaveCount(2);
        firstAddress.Parent.Should().BeSameAs(firstDocument.RootRow);
        nestedPeriod.Parent.Should().BeSameAs(firstAddress);
    }

    [Test]
    public void It_should_order_root_collection_children_by_ordinal_for_each_parent()
    {
        var firstDocument = _context.DocumentsInOrder[0];
        var secondDocument = _context.DocumentsInOrder[1];

        firstDocument
            .RootRow.GetImmediateChildren(_addressTable)
            .Select(child => child.Row[3])
            .Should()
            .Equal("East City", "North City");

        secondDocument
            .RootRow.GetImmediateChildren(_addressTable)
            .Select(child => child.Row[3])
            .Should()
            .Equal("South City");
    }

    [Test]
    public void It_should_order_nested_collection_children_by_ordinal_for_each_parent()
    {
        var eastCityAddress = _context.GetRowOrThrow(_addressTable, new ScopeKey([102L]));

        eastCityAddress
            .GetImmediateChildren(_periodTable)
            .Select(child => child.Row[4])
            .Should()
            .Equal("2023-09-01", "2024-01-05");
    }

    [Test]
    public void It_should_use_table_qualified_physical_identity_lookups()
    {
        var rootRow = _context.GetRowOrThrow(_rootTable, new ScopeKey([101L]));
        var addressRow = _context.GetRowOrThrow(_addressTable, new ScopeKey([101L]));
        var periodRow = _context.GetRowOrThrow(_periodTable, new ScopeKey([101L]));

        rootRow.Table.Should().Be(_rootTable);
        addressRow.Table.Should().Be(_addressTable);
        periodRow.Table.Should().Be(_periodTable);
        rootRow.Should().NotBeSameAs(addressRow);
        addressRow.Should().NotBeSameAs(periodRow);
    }
}

[TestFixture]
public class Given_PageOrderedChildRowsCache_With_A_Multi_Document_Page
{
    private PageOrderedChildRows _firstLookup = null!;
    private PageOrderedChildRows _secondLookup = null!;
    private DbTableName _addressTable = default;
    private DbTableName _periodTable = default;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(pageData.ReadPlan);

        _firstLookup = PageOrderedChildRowsCache.GetOrBuild(
            compiledPlan,
            pageData.HydratedPage.TableRowsInDependencyOrder
        );
        _secondLookup = PageOrderedChildRowsCache.GetOrBuild(
            compiledPlan,
            pageData.HydratedPage.TableRowsInDependencyOrder
        );
        _addressTable = pageData.AddressTable;
        _periodTable = pageData.PeriodTable;
    }

    [Test]
    public void It_should_reuse_the_page_scoped_lookup_for_the_same_hydrated_page()
    {
        _secondLookup.Should().BeSameAs(_firstLookup);
    }

    [Test]
    public void It_should_order_root_collection_rows_once_per_parent()
    {
        _firstLookup
            .GetRowsByParentLocator(_addressTable, 101L)
            .Select(row => row[3])
            .Should()
            .Equal("East City", "North City");
    }

    [Test]
    public void It_should_order_nested_collection_rows_once_per_parent()
    {
        _firstLookup
            .GetRowsByParentLocator(_periodTable, 102L)
            .Select(row => row[4])
            .Should()
            .Equal("2023-09-01", "2024-01-05");
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Root_Rows
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDuplicateRootRows();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception.Message.Should().Contain("duplicate root row");
        _exception.Message.Should().Contain("DocumentId 101");
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Physical_Row_Identity
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDuplicateChildPhysicalIdentity();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception.Message.Should().Contain("duplicate physical row identity");
        _exception.Message.Should().Contain("SchoolAddress");
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_An_Orphaned_Child_Row
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithOrphanedChildRow();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception.Message.Should().Contain("orphaned row");
        _exception.Message.Should().Contain("SchoolAddressPeriod");
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Metadata_Row_Without_A_Root_Row
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithMetadataRowWithoutRoot();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception.Message.Should().Contain("document metadata row");
        _exception.Message.Should().Contain("DocumentId 202");
        _exception.Message.Should().Contain("no matching root row");
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Extra_Root_Rows_Not_In_Metadata
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithExtraRootRow();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception.Message.Should().Contain("not present in page metadata");
        _exception.Message.Should().Contain("[303]");
    }
}

file static class PageReconstitutionContextTestData
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");

    public static PageData CreateHappyPathPage()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata:
                [
                    CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111"),
                    CreateDocumentMetadataRow(202L, "bbbbbbbb-2222-2222-2222-222222222222"),
                ],
                rootRows:
                [
                    [202, "Second School"],
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, 2, "North City"],
                    [202, 202L, 1, "South City"],
                    [102, 101L, 1, "East City"],
                ],
                periodRows:
                [
                    [101, 101, 101, 2, "2024-08-15"],
                    [201, 202, 202, 1, "2025-01-10"],
                    [102, 102, 101L, 1, "2024-01-05"],
                    [103, 102, 101L, 0, "2023-09-01"],
                ],
                descriptorRowsInPlanOrder:
                [
                    new HydratedDescriptorRows([
                        new DescriptorUriRow(601L, "uri://ed-fi.org/SchoolCategoryDescriptor#Alternative"),
                    ]),
                    new HydratedDescriptorRows([
                        new DescriptorUriRow(601L, "uri://ed-fi.org/SchoolCategoryDescriptor#Alternative"),
                        new DescriptorUriRow(602L, "uri://ed-fi.org/SchoolCategoryDescriptor#Charter"),
                    ]),
                ]
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithDuplicateRootRows()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                    [101L, "Duplicate Root"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithDuplicateChildPhysicalIdentity()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, 1, "North City"],
                    [101L, 101L, 2, "Duplicate Address Identity"],
                ],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithOrphanedChildRow()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, 1, "North City"],
                ],
                periodRows:
                [
                    [201, 999, 101, 1, "2024-08-15"],
                ]
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithMetadataRowWithoutRoot()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata:
                [
                    CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111"),
                    CreateDocumentMetadataRow(202L, "bbbbbbbb-2222-2222-2222-222222222222"),
                ],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithExtraRootRow()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                    [303, "Extra Root"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    private static ResourceReadPlan CreateReadPlan()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(_schema, "School"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_School",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn(
                    "NameOfInstitution",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    false,
                    CreatePath("$.nameOfInstitution", new JsonPathSegment.Property("nameOfInstitution"))
                ),
            ],
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

        var addressTable = new DbTableModel(
            Table: new DbTableName(_schema, "SchoolAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddress",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, ScalarKind.Int64, false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, ScalarKind.Int32, false),
                CreateColumn(
                    "City",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    false,
                    CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var periodTable = new DbTableModel(
            Table: new DbTableName(_schema, "SchoolAddressPeriod"),
            JsonScope: CreatePath(
                "$.addresses[*].periods[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("periods"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddressPeriod",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, ScalarKind.Int64, false),
                CreateColumn("ParentCollectionItemId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, ScalarKind.Int32, false),
                CreateColumn(
                    "BeginDate",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    false,
                    CreatePath(
                        "$.addresses[*].periods[*].beginDate",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periods"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("beginDate")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, addressTable, periodTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(rootTable, "SELECT 1"),
                new TableReadPlan(addressTable, "SELECT 1"),
                new TableReadPlan(periodTable, "SELECT 1"),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }

    private static HydratedPage CreateHydratedPage(
        IReadOnlyList<DocumentMetadataRow> documentMetadata,
        IReadOnlyList<object?[]> rootRows,
        IReadOnlyList<object?[]> addressRows,
        IReadOnlyList<object?[]> periodRows,
        IReadOnlyList<HydratedDescriptorRows>? descriptorRowsInPlanOrder = null
    )
    {
        var readPlan = CreateReadPlan();

        return new HydratedPage(
            TotalCount: null,
            DocumentMetadata: documentMetadata,
            TableRowsInDependencyOrder:
            [
                new HydratedTableRows(readPlan.Model.Root, rootRows),
                new HydratedTableRows(readPlan.Model.TablesInDependencyOrder[1], addressRows),
                new HydratedTableRows(readPlan.Model.TablesInDependencyOrder[2], periodRows),
            ],
            DescriptorRowsInPlanOrder: descriptorRowsInPlanOrder ?? []
        );
    }

    private static DocumentMetadataRow CreateDocumentMetadataRow(long documentId, string documentUuid) =>
        new(
            DocumentId: documentId,
            DocumentUuid: Guid.Parse(documentUuid),
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero)
        );

    private static DbColumnModel CreateColumn(
        string name,
        ColumnKind kind,
        ScalarKind scalarKind,
        bool isNullable,
        JsonPathExpression? sourceJsonPath = null
    ) =>
        new(
            ColumnName: new DbColumnName(name),
            Kind: kind,
            ScalarType: scalarKind switch
            {
                ScalarKind.String => new RelationalScalarType(scalarKind, MaxLength: 255),
                _ => new RelationalScalarType(scalarKind),
            },
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);

    internal sealed record PageData(
        ResourceReadPlan ReadPlan,
        HydratedPage HydratedPage,
        DbTableName RootTable,
        DbTableName AddressTable,
        DbTableName PeriodTable
    );
}
