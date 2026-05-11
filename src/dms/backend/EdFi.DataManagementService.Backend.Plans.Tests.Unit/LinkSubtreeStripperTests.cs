// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_LinkSubtreeStripper
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "StudentSchoolAssociation");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly DbTableName _rootTableName = new(
        new DbSchemaName("edfi"),
        "StudentSchoolAssociation"
    );

    private static readonly JsonPathExpression _rootScope = new("$", []);
    private static readonly JsonPathExpression _schoolReferencePath = new(
        "$.schoolReference",
        [new JsonPathSegment.Property("schoolReference")]
    );
    private static readonly JsonPathExpression _schoolReferenceSchoolIdPath = new(
        "$.schoolReference.schoolId",
        [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
    );

    [Test]
    public void It_passes_through_unchanged_when_options_Enabled_is_true()
    {
        var document = BuildDocumentWithLink();
        var snapshot = document.DeepClone();

        DocumentReconstituter.StripReferenceLinks(
            document,
            BuildReadPlan(),
            new ResourceLinksOptions { Enabled = true }
        );

        document.ToJsonString().Should().Be(snapshot.ToJsonString());
    }

    [Test]
    public void It_removes_link_from_root_reference_when_options_Enabled_is_false()
    {
        var document = BuildDocumentWithLink();

        DocumentReconstituter.StripReferenceLinks(
            document,
            BuildReadPlan(),
            new ResourceLinksOptions { Enabled = false }
        );

        var schoolReference = document["schoolReference"]!.AsObject();
        schoolReference["link"].Should().BeNull();
        schoolReference["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public void It_leaves_identity_etag_and_lastModifiedDate_untouched_when_stripping()
    {
        var document = BuildDocumentWithLink();
        document["id"] = "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb";
        document["_etag"] = "computed-etag-value";
        document["_lastModifiedDate"] = "2026-05-11T00:00:00Z";

        DocumentReconstituter.StripReferenceLinks(
            document,
            BuildReadPlan(),
            new ResourceLinksOptions { Enabled = false }
        );

        document["id"]!.GetValue<string>().Should().Be("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        document["_etag"]!.GetValue<string>().Should().Be("computed-etag-value");
        document["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-11T00:00:00Z");
    }

    [Test]
    public void It_is_a_no_op_when_document_is_null()
    {
        Action act = () =>
            DocumentReconstituter.StripReferenceLinks(
                null,
                BuildReadPlan(),
                new ResourceLinksOptions { Enabled = false }
            );

        act.Should().NotThrow();
    }

    [Test]
    public void It_is_a_no_op_when_reference_object_has_no_link_property()
    {
        var document = new JsonObject { ["schoolReference"] = new JsonObject { ["schoolId"] = 255901 } };

        DocumentReconstituter.StripReferenceLinks(
            document,
            BuildReadPlan(),
            new ResourceLinksOptions { Enabled = false }
        );

        document["schoolReference"]!["schoolId"]!.GetValue<int>().Should().Be(255901);
        document["schoolReference"]!.AsObject().Should().NotContainKey("link");
    }

    private static JsonObject BuildDocumentWithLink() =>
        new()
        {
            ["schoolReference"] = new JsonObject
            {
                ["schoolId"] = 255901,
                ["link"] = new JsonObject
                {
                    ["rel"] = "School",
                    ["href"] = "/ed-fi/schools/11112222-3333-4444-5555-666677778888",
                },
            },
        };

    private static ResourceReadPlan BuildReadPlan()
    {
        var rootTable = new DbTableModel(
            Table: _rootTableName,
            JsonScope: _rootScope,
            Key: new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: _schoolReferencePath,
                    TargetResource: _schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolReference_SchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: _schoolReferenceSchoolIdPath,
                    TargetResource: null
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

        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: rootTable.Table.Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: _schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: _schoolReferenceSchoolIdPath,
                            ReferenceJsonPath: _schoolReferenceSchoolIdPath,
                            Column: new DbColumnName("SchoolReference_SchoolId")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );

        var refProjection = new ReferenceIdentityProjectionTablePlan(
            Table: rootTable.Table,
            BindingsInOrder:
            [
                new ReferenceIdentityProjectionBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: _schoolReferencePath,
                    TargetResource: _schoolResource,
                    FkColumnOrdinal: 1,
                    IdentityFieldOrdinalsInOrder:
                    [
                        new ReferenceIdentityProjectionFieldOrdinal(
                            ReferenceJsonPath: _schoolReferenceSchoolIdPath,
                            ColumnOrdinal: 2
                        ),
                    ]
                ),
            ]
        );

        var lookupPlan = new DocumentReferenceLookupPlan(
            SelectByKeysetSql: "SELECT 1;",
            ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: new KeysetTableContract(
                Table: new SqlRelationRef.TempTable("page"),
                DocumentIdColumnName: new DbColumnName("DocumentId")
            ),
            TablePlansInDependencyOrder: [new TableReadPlan(rootTable, "SELECT 1;")],
            ReferenceIdentityProjectionPlansInDependencyOrder: [refProjection],
            DescriptorProjectionPlansInOrder: [],
            DocumentReferenceLookup: lookupPlan
        );
    }
}

[TestFixture]
public class Given_ResourceLinksOptions
{
    [Test]
    public void It_defaults_Enabled_to_true_when_no_configuration_is_bound()
    {
        var services = new ServiceCollection();
        services.AddOptions<ResourceLinksOptions>();
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ResourceLinksOptions>>().Value;

        options.Enabled.Should().BeTrue();
    }

    [Test]
    public void It_parses_Enabled_false_from_DataManagement_ResourceLinks_section()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataManagement:ResourceLinks:Enabled"] = "false" }
            )
            .Build();

        var services = new ServiceCollection();
        services.Configure<ResourceLinksOptions>(config.GetSection("DataManagement:ResourceLinks"));
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ResourceLinksOptions>>().Value;

        options.Enabled.Should().BeFalse();
    }

    [Test]
    public void It_defaults_Enabled_to_true_when_configuration_section_is_absent()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.Configure<ResourceLinksOptions>(config.GetSection("DataManagement:ResourceLinks"));
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ResourceLinksOptions>>().Value;

        options.Enabled.Should().BeTrue();
    }
}
