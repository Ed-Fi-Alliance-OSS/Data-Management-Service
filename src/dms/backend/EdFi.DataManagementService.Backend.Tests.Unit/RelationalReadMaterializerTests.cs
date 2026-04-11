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

    [Test]
    public void It_injects_api_metadata_for_external_response_reads_using_stored_document_stamps()
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
        result["_etag"]!.GetValue<string>().Should().Be("\"91\"");
        result["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-04-03T14:10:11Z");
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

    private static DocumentMetadataRow CreateDocumentMetadataRow(
        long contentVersion,
        DateTimeOffset contentLastModifiedAt
    )
    {
        return new DocumentMetadataRow(
            DocumentId: 345L,
            DocumentUuid: _documentUuid,
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
}
