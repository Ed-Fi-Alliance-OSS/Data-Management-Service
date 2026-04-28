// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalReadMaterializer
{
    private static readonly Guid _documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly Guid _secondDocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc");

    [Test]
    public void It_injects_a_canonical_etag_and_stored_last_modified_date_for_external_response_reads()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = CreateReadPlan();

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 9, 10, 11, TimeSpan.FromHours(-5))
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
        );

        result.Should().BeOfType<JsonObject>();
        result["name"]!.GetValue<string>().Should().Be("Lincoln High");
        result["id"]!.GetValue<string>().Should().Be(_documentUuid.ToString());
        result["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(JsonNode.Parse("""{"name":"Lincoln High"}""")!));
        result["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-04-03T14:10:11Z");
    }

    [Test]
    public void It_does_not_derive_the_external_etag_or_a_public_change_version_from_content_version()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = CreateReadPlan();

        var firstResult = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
        );
        var secondResult = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 907L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.ExternalResponse
            )
        );

        firstResult["_etag"]!.GetValue<string>().Should().Be(secondResult["_etag"]!.GetValue<string>());
        firstResult["ChangeVersion"].Should().BeNull();
        secondResult["ChangeVersion"].Should().BeNull();
    }

    [Test]
    public void It_leaves_api_metadata_out_of_stored_document_reads()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = CreateReadPlan();

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedTableRows(readPlan, (345L, "Lincoln High")),
                [],
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().BeOfType<JsonObject>();
        result["name"]!.GetValue<string>().Should().Be("Lincoln High");
        result["id"].Should().BeNull();
        result["_etag"].Should().BeNull();
        result["_lastModifiedDate"].Should().BeNull();
    }

    [Test]
    public void It_projects_descriptor_uris_from_hydrated_descriptor_rows()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = CreateReadPlanWithDescriptor();

        var result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                CreateDocumentMetadataRow(
                    contentVersion: 91L,
                    contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
                ),
                CreateHydratedDescriptorTableRows(readPlan, (345L, 601L)),
                CreateHydratedDescriptorRows((601L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade")),
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().BeOfType<JsonObject>();
        result["entryGradeLevelDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be("uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade");
    }

    [Test]
    public void It_materializes_page_documents_in_metadata_order_for_external_response_reads()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = CreateReadPlan();
        var firstDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 345L,
            documentUuid: _documentUuid,
            contentVersion: 91L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
        );
        var secondDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 678L,
            documentUuid: _secondDocumentUuid,
            contentVersion: 92L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 4, 15, 11, 12, TimeSpan.Zero)
        );

        var result = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                CreateHydratedPage(
                    [firstDocumentMetadata, secondDocumentMetadata],
                    CreateHydratedTableRows(readPlan, (678L, "Cedar High"), (345L, "Lincoln High")),
                    []
                ),
                RelationalGetRequestReadMode.ExternalResponse
            )
        );

        result.Should().HaveCount(2);
        result.Select(static document => document.DocumentMetadata.DocumentId).Should().Equal(345L, 678L);
        result
            .Select(document => document.Document["name"]!.GetValue<string>())
            .Should()
            .Equal("Lincoln High", "Cedar High");
        result
            .Select(document => document.Document["id"]!.GetValue<string>())
            .Should()
            .Equal(_documentUuid.ToString(), _secondDocumentUuid.ToString());
        result
            .Select(document => document.Document["_lastModifiedDate"]!.GetValue<string>())
            .Should()
            .Equal("2026-04-03T14:10:11Z", "2026-04-04T15:11:12Z");
    }

    [Test]
    public void It_materializes_page_documents_from_shared_descriptor_rows_for_stored_document_reads()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = CreateReadPlanWithDescriptor();
        var firstDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 345L,
            documentUuid: _documentUuid,
            contentVersion: 91L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 3, 14, 10, 11, TimeSpan.Zero)
        );
        var secondDocumentMetadata = CreateDocumentMetadataRow(
            documentId: 678L,
            documentUuid: _secondDocumentUuid,
            contentVersion: 92L,
            contentLastModifiedAt: new DateTimeOffset(2026, 4, 4, 15, 11, 12, TimeSpan.Zero)
        );

        var result = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                CreateHydratedPage(
                    [firstDocumentMetadata, secondDocumentMetadata],
                    CreateHydratedDescriptorTableRows(readPlan, (678L, 601L), (345L, 601L)),
                    CreateHydratedDescriptorRows(
                        (601L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade")
                    )
                ),
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().HaveCount(2);
        result.Select(static document => document.DocumentMetadata.DocumentId).Should().Equal(345L, 678L);
        result
            .Select(document => document.Document["entryGradeLevelDescriptor"]!.GetValue<string>())
            .Should()
            .Equal(
                "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade",
                "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
            );

        foreach (var document in result)
        {
            document.Document["id"].Should().BeNull();
            document.Document["_etag"].Should().BeNull();
            document.Document["_lastModifiedDate"].Should().BeNull();
        }
    }

    private static ResourceReadPlan CreateReadPlan()
    {
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
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
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
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

        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(rootTable, "select \"DocumentId\", \"Name\" from edfi.\"School\"")],
            [],
            []
        );
    }

    private static ResourceReadPlan CreateReadPlanWithDescriptor()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
        var descriptorValuePath = new JsonPathExpression(
            "$.entryGradeLevelDescriptor",
            [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
        );
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
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
                    new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    descriptorResource
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

        return new ResourceReadPlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "School"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: descriptorValuePath,
                        Table: rootTable.Table,
                        FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                        DescriptorResource: descriptorResource
                    ),
                ]
            ),
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [
                new TableReadPlan(
                    rootTable,
                    "select \"DocumentId\", \"EntryGradeLevelDescriptor_DescriptorId\" from edfi.\"School\""
                ),
            ],
            [],
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "select \"DescriptorId\", \"Uri\" from dms.\"Descriptor\"",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: descriptorValuePath,
                            Table: rootTable.Table,
                            DescriptorResource: descriptorResource,
                            DescriptorIdColumnOrdinal: 1
                        ),
                    ]
                ),
            ]
        );
    }

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    ) => CreateDocumentMetadataRow(345L, _documentUuid, contentVersion, contentLastModifiedAt);

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        long documentId,
        Guid documentUuid,
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    )
    {
        return new DocumentMetadataRow(
            DocumentId: documentId,
            DocumentUuid: documentUuid,
            ContentVersion: contentVersion,
            IdentityVersion: 92L,
            ContentLastModifiedAt: contentLastModifiedAt,
            IdentityLastModifiedAt: contentLastModifiedAt
        );
    }

    private static IReadOnlyList<HydratedTableRows> CreateHydratedTableRows(
        ResourceReadPlan readPlan,
        params (long DocumentId, string Name)[] rows
    )
    {
        return
        [
            new HydratedTableRows(
                readPlan.Model.Root,
                rows.Select(row => new object?[] { row.DocumentId, row.Name }).ToArray()
            ),
        ];
    }

    private static IReadOnlyList<HydratedTableRows> CreateHydratedDescriptorTableRows(
        ResourceReadPlan readPlan,
        params (long DocumentId, long DescriptorId)[] rows
    )
    {
        return
        [
            new HydratedTableRows(
                readPlan.Model.Root,
                rows.Select(row => new object?[] { row.DocumentId, row.DescriptorId }).ToArray()
            ),
        ];
    }

    private static IReadOnlyList<HydratedDescriptorRows> CreateHydratedDescriptorRows(
        params (long DescriptorId, string Uri)[] rows
    )
    {
        return
        [
            new HydratedDescriptorRows(
                rows.Select(row => new DescriptorUriRow(row.DescriptorId, row.Uri)).ToArray()
            ),
        ];
    }

    private static HydratedPage CreateHydratedPage(
        IReadOnlyList<DocumentMetadataRow> documentMetadata,
        IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
        IReadOnlyList<HydratedDescriptorRows> descriptorRowsInPlanOrder
    ) => new(null, documentMetadata, tableRowsInDependencyOrder, descriptorRowsInPlanOrder);
}

/// <summary>
/// Scenario 2: Optional descriptor FK is null → the descriptor JSON property is absent from the output.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_RelationalReadMaterializer_With_Null_Optional_Descriptor_FK
{
    private JsonNode _result = null!;

    private static readonly Guid _documentUuid = Guid.Parse("bbbbbbbb-2222-3333-4444-cccccccccccc");

    [SetUp]
    public void SetUp()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = BuildDescriptorReadPlan();

        // FK column value is null (optional descriptor not set on this document)
        var tableRows = new List<HydratedTableRows>
        {
            new(readPlan.Model.Root, [new object?[] { 345L, null }]),
        };

        _result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                new DocumentMetadataRow(
                    DocumentId: 345L,
                    DocumentUuid: _documentUuid,
                    ContentVersion: 1L,
                    IdentityVersion: 1L,
                    ContentLastModifiedAt: DateTimeOffset.UtcNow,
                    IdentityLastModifiedAt: DateTimeOffset.UtcNow
                ),
                tableRows,
                [],
                RelationalGetRequestReadMode.StoredDocument
            )
        );
    }

    [Test]
    public void It_does_not_emit_the_descriptor_property_key()
    {
        ((JsonObject)_result).ContainsKey("entryGradeLevelDescriptor").Should().BeFalse();
    }

    [Test]
    public void It_does_not_emit_json_null_at_the_descriptor_path()
    {
        _result["entryGradeLevelDescriptor"].Should().BeNull();
    }

    private static ResourceReadPlan BuildDescriptorReadPlan()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
        var descriptorValuePath = new JsonPathExpression(
            "$.entryGradeLevelDescriptor",
            [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
        );
        var fkColumnName = new DbColumnName("EntryGradeLevelDescriptor_DescriptorId");

        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
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
                    fkColumnName,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    null,
                    descriptorResource
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

        return new ResourceReadPlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "School"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: descriptorValuePath,
                        Table: rootTable.Table,
                        FkColumn: fkColumnName,
                        DescriptorResource: descriptorResource
                    ),
                ]
            ),
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [
                new TableReadPlan(
                    rootTable,
                    "select \"DocumentId\", \"EntryGradeLevelDescriptor_DescriptorId\" from edfi.\"School\""
                ),
            ],
            [],
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "select \"DescriptorId\", \"Uri\" from dms.\"Descriptor\"",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: descriptorValuePath,
                            Table: rootTable.Table,
                            DescriptorResource: descriptorResource,
                            DescriptorIdColumnOrdinal: 1
                        ),
                    ]
                ),
            ]
        );
    }
}

/// <summary>
/// Scenario 3: Document with two descriptor FK columns of different types → both URIs appear at their declared paths.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_RelationalReadMaterializer_With_Multiple_Descriptors
{
    private JsonNode _result = null!;

    private static readonly Guid _documentUuid = Guid.Parse("cccccccc-3333-4444-5555-dddddddddddd");
    private const string EntryUri = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string ExitUri = "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade";

    [SetUp]
    public void SetUp()
    {
        var sut = new RelationalReadMaterializer();
        var (readPlan, tableModel) = BuildReadPlanWithTwoDescriptors();

        // Both FK columns populated
        var tableRows = new List<HydratedTableRows> { new(tableModel, [new object?[] { 345L, 601L, 602L }]) };

        // Two descriptor projection plans, one result set each
        var descriptorRows = new List<HydratedDescriptorRows>
        {
            new([new DescriptorUriRow(601L, EntryUri)]),
            new([new DescriptorUriRow(602L, ExitUri)]),
        };

        _result = sut.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                new DocumentMetadataRow(
                    DocumentId: 345L,
                    DocumentUuid: _documentUuid,
                    ContentVersion: 1L,
                    IdentityVersion: 1L,
                    ContentLastModifiedAt: DateTimeOffset.UtcNow,
                    IdentityLastModifiedAt: DateTimeOffset.UtcNow
                ),
                tableRows,
                descriptorRows,
                RelationalGetRequestReadMode.StoredDocument
            )
        );
    }

    [Test]
    public void It_emits_the_entry_grade_level_descriptor_uri_at_its_declared_path()
    {
        _result["entryGradeLevelDescriptor"]!.GetValue<string>().Should().Be(EntryUri);
    }

    [Test]
    public void It_emits_the_exit_grade_level_descriptor_uri_at_its_declared_path()
    {
        _result["exitGradeLevelDescriptor"]!.GetValue<string>().Should().Be(ExitUri);
    }

    private static (ResourceReadPlan ReadPlan, DbTableModel TableModel) BuildReadPlanWithTwoDescriptors()
    {
        var gradeLevelResource = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
        var entryPath = new JsonPathExpression(
            "$.entryGradeLevelDescriptor",
            [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
        );
        var exitPath = new JsonPathExpression(
            "$.exitGradeLevelDescriptor",
            [new JsonPathSegment.Property("exitGradeLevelDescriptor")]
        );
        var entryFkColumn = new DbColumnName("EntryGradeLevelDescriptor_DescriptorId");
        var exitFkColumn = new DbColumnName("ExitWithdrawGradeLevelDescriptor_DescriptorId");

        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_StudentSchoolAssociation",
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
                    entryFkColumn,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    gradeLevelResource
                ),
                new DbColumnModel(
                    exitFkColumn,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    true,
                    null,
                    gradeLevelResource
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

        var readPlan = new ResourceReadPlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: entryPath,
                        Table: rootTable.Table,
                        FkColumn: entryFkColumn,
                        DescriptorResource: gradeLevelResource
                    ),
                    new DescriptorEdgeSource(
                        IsIdentityComponent: false,
                        DescriptorValuePath: exitPath,
                        Table: rootTable.Table,
                        FkColumn: exitFkColumn,
                        DescriptorResource: gradeLevelResource
                    ),
                ]
            ),
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [
                new TableReadPlan(
                    rootTable,
                    "select \"DocumentId\", \"EntryGradeLevelDescriptor_DescriptorId\", \"ExitWithdrawGradeLevelDescriptor_DescriptorId\" from edfi.\"StudentSchoolAssociation\""
                ),
            ],
            [],
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "select \"DescriptorId\", \"Uri\" from dms.\"Descriptor\" where \"DescriptorId\" in (select \"EntryGradeLevelDescriptor_DescriptorId\" from edfi.\"StudentSchoolAssociation\")",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: entryPath,
                            Table: rootTable.Table,
                            DescriptorResource: gradeLevelResource,
                            DescriptorIdColumnOrdinal: 1
                        ),
                    ]
                ),
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "select \"DescriptorId\", \"Uri\" from dms.\"Descriptor\" where \"DescriptorId\" in (select \"ExitWithdrawGradeLevelDescriptor_DescriptorId\" from edfi.\"StudentSchoolAssociation\")",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: exitPath,
                            Table: rootTable.Table,
                            DescriptorResource: gradeLevelResource,
                            DescriptorIdColumnOrdinal: 2
                        ),
                    ]
                ),
            ]
        );

        return (readPlan, rootTable);
    }
}

/// <summary>
/// Scenario 4: Multiple documents in the same page → each document receives its own descriptor URI with no cross-document contamination.
/// The materializer is called once per document; the shared descriptor lookup must map the correct URI to each document's FK.
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable]
public class Given_RelationalReadMaterializer_With_Multiple_Documents_And_Distinct_Descriptor_URIs
{
    private JsonNode _resultForDoc345 = null!;
    private JsonNode _resultForDoc789 = null!;

    private static readonly Guid _uuidDoc345 = Guid.Parse("dddddddd-4444-5555-6666-eeeeeeeeeeee");
    private static readonly Guid _uuidDoc789 = Guid.Parse("eeeeeeee-5555-6666-7777-ffffffffffff");
    private const string UriFor345 = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string UriFor789 = "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade";

    [SetUp]
    public void SetUp()
    {
        var sut = new RelationalReadMaterializer();
        var readPlan = BuildSingleDescriptorPlan();

        // Two document rows in the same page keyset
        IReadOnlyList<HydratedTableRows> tableRows =
        [
            new(readPlan.Model.Root, [new object?[] { 345L, 601L }, new object?[] { 789L, 602L }]),
        ];

        // Combined descriptor lookup for the entire page: both descriptor IDs resolved
        IReadOnlyList<HydratedDescriptorRows> descriptorRows =
        [
            new([new DescriptorUriRow(601L, UriFor345), new DescriptorUriRow(602L, UriFor789)]),
        ];

        var commonMetadataParts = (ContentVersion: 1L, IdentityVersion: 1L, Ts: DateTimeOffset.UtcNow);

        var doc345Metadata = new DocumentMetadataRow(
            DocumentId: 345L,
            DocumentUuid: _uuidDoc345,
            ContentVersion: commonMetadataParts.ContentVersion,
            IdentityVersion: commonMetadataParts.IdentityVersion,
            ContentLastModifiedAt: commonMetadataParts.Ts,
            IdentityLastModifiedAt: commonMetadataParts.Ts
        );
        var doc789Metadata = new DocumentMetadataRow(
            DocumentId: 789L,
            DocumentUuid: _uuidDoc789,
            ContentVersion: commonMetadataParts.ContentVersion,
            IdentityVersion: commonMetadataParts.IdentityVersion,
            ContentLastModifiedAt: commonMetadataParts.Ts,
            IdentityLastModifiedAt: commonMetadataParts.Ts
        );

        var results = sut.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                new HydratedPage(null, [doc345Metadata, doc789Metadata], tableRows, descriptorRows),
                RelationalGetRequestReadMode.StoredDocument
            )
        );

        _resultForDoc345 = results.First(d => d.DocumentMetadata.DocumentId == 345L).Document;
        _resultForDoc789 = results.First(d => d.DocumentMetadata.DocumentId == 789L).Document;
    }

    [Test]
    public void It_resolves_the_correct_descriptor_uri_for_document_345()
    {
        _resultForDoc345["entryGradeLevelDescriptor"]!.GetValue<string>().Should().Be(UriFor345);
    }

    [Test]
    public void It_resolves_the_correct_descriptor_uri_for_document_789()
    {
        _resultForDoc789["entryGradeLevelDescriptor"]!.GetValue<string>().Should().Be(UriFor789);
    }

    [Test]
    public void It_does_not_contaminate_document_345_with_the_uri_for_document_789()
    {
        _resultForDoc345["entryGradeLevelDescriptor"]!.GetValue<string>().Should().NotBe(UriFor789);
    }

    [Test]
    public void It_does_not_contaminate_document_789_with_the_uri_for_document_345()
    {
        _resultForDoc789["entryGradeLevelDescriptor"]!.GetValue<string>().Should().NotBe(UriFor345);
    }

    private static ResourceReadPlan BuildSingleDescriptorPlan()
    {
        var gradeLevelResource = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
        var descriptorValuePath = new JsonPathExpression(
            "$.entryGradeLevelDescriptor",
            [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
        );
        var fkColumnName = new DbColumnName("EntryGradeLevelDescriptor_DescriptorId");

        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
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
                    fkColumnName,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    gradeLevelResource
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

        return new ResourceReadPlan(
            new RelationalResourceModel(
                Resource: new QualifiedResourceName("Ed-Fi", "School"),
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: descriptorValuePath,
                        Table: rootTable.Table,
                        FkColumn: fkColumnName,
                        DescriptorResource: gradeLevelResource
                    ),
                ]
            ),
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [
                new TableReadPlan(
                    rootTable,
                    "select \"DocumentId\", \"EntryGradeLevelDescriptor_DescriptorId\" from edfi.\"School\""
                ),
            ],
            [],
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "select \"DescriptorId\", \"Uri\" from dms.\"Descriptor\"",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: descriptorValuePath,
                            Table: rootTable.Table,
                            DescriptorResource: gradeLevelResource,
                            DescriptorIdColumnOrdinal: 1
                        ),
                    ]
                ),
            ]
        );
    }
}
