// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Shared schema, model, and data constants for PostgreSQL integration tests that cover
/// descriptor FK projection on a child (collection-item) table.
/// The resource model is a "School" with a child "SchoolAddress" table
/// that has an <c>AddressType_DescriptorId</c> nullable descriptor FK column.
/// </summary>
internal static class CollectionDescriptorProjectionFixture
{
    internal const string TestSchema = "colldescrprojtest";
    internal const long SchoolDocumentId1 = 820L;
    internal const long AddressCollectionItemId1 = 5001L;
    internal const long AddressCollectionItemId2 = 5002L;
    internal const long DescriptorId920 = 920L;
    internal const long DescriptorId921 = 921L;
    internal const string Uri920 = "uri://ed-fi.org/AddressTypeDescriptor#Physical";
    internal const string Uri921 = "uri://ed-fi.org/AddressTypeDescriptor#Mailing";

    internal static readonly DbSchemaName Schema = new(TestSchema);
    internal static readonly DbTableName RootTableName = new(Schema, "School");
    internal static readonly DbTableName ChildTableName = new(Schema, "SchoolAddress");

    internal static readonly JsonPathExpression AddressTypeDescriptorPath = new(
        "$.addresses[*].addressTypeDescriptor",
        [
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("addressTypeDescriptor"),
        ]
    );

    internal static readonly QualifiedResourceName AddressTypeDescriptorResource = new(
        "Ed-Fi",
        "AddressTypeDescriptor"
    );

    internal static readonly DbColumnName AddressTypeFkColumn = new("AddressType_DescriptorId");

    internal static DbTableModel BuildRootTableModel() =>
        new(
            Table: RootTableName,
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                $"PK_{TestSchema}_School",
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
                    ColumnName: new DbColumnName("SchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolId",
                        [new JsonPathSegment.Property("schoolId")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

    internal static DbTableModel BuildChildTableModel() =>
        new(
            Table: ChildTableName,
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                $"PK_{TestSchema}_SchoolAddress",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("City"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].city",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("city"),
                        ]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: AddressTypeFkColumn,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: AddressTypeDescriptorPath,
                    TargetResource: AddressTypeDescriptorResource
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

    internal static RelationalResourceModel BuildResourceModel()
    {
        var rootTable = BuildRootTableModel();
        var childTable = BuildChildTableModel();
        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: AddressTypeDescriptorPath,
                    Table: ChildTableName,
                    FkColumn: AddressTypeFkColumn,
                    DescriptorResource: AddressTypeDescriptorResource
                ),
            ]
        );
    }

    internal static async Task ProvisionSchemaAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            CREATE SCHEMA IF NOT EXISTS dms;
            DROP SCHEMA IF EXISTS "{TestSchema}" CASCADE;
            CREATE SCHEMA "{TestSchema}";

            CREATE TABLE IF NOT EXISTS dms."Document" (
                "DocumentId" bigint PRIMARY KEY,
                "DocumentUuid" uuid NOT NULL,
                "ResourceKeyId" smallint NOT NULL DEFAULT 0,
                "ContentVersion" bigint NOT NULL DEFAULT 1,
                "IdentityVersion" bigint NOT NULL DEFAULT 1,
                "ContentLastModifiedAt" timestamptz NOT NULL DEFAULT now(),
                "IdentityLastModifiedAt" timestamptz NOT NULL DEFAULT now(),
                "CreatedAt" timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS dms."Descriptor" (
                "DocumentId" bigint PRIMARY KEY,
                "Namespace" varchar(255) NOT NULL DEFAULT '',
                "CodeValue" varchar(50) NOT NULL DEFAULT '',
                "ShortDescription" varchar(75) NOT NULL DEFAULT '',
                "Description" varchar(1024) NULL,
                "EffectiveBeginDate" date NULL,
                "EffectiveEndDate" date NULL,
                "Discriminator" varchar(128) NOT NULL DEFAULT '',
                "Uri" varchar(306) NOT NULL
            );

            CREATE TABLE "{TestSchema}"."School" (
                "DocumentId" bigint PRIMARY KEY,
                "SchoolId" int NOT NULL
            );

            CREATE TABLE "{TestSchema}"."SchoolAddress" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL,
                "Ordinal" int NOT NULL,
                "City" varchar(100) NOT NULL,
                "AddressType_DescriptorId" bigint NULL
            );
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task InsertTestDataAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM dms."Descriptor" WHERE "DocumentId" IN ({DescriptorId920}, {DescriptorId921});
            DELETE FROM dms."Document" WHERE "DocumentId" IN ({SchoolDocumentId1});

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "IdentityVersion") VALUES
                ({SchoolDocumentId1}, '82000000-0000-0000-0000-000000000820', 0, 1, 1);

            INSERT INTO dms."Descriptor" ("DocumentId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri") VALUES
                ({DescriptorId920}, 'uri://ed-fi.org/AddressTypeDescriptor', 'Physical', 'Physical', 'edfi.AddressTypeDescriptor', '{Uri920}'),
                ({DescriptorId921}, 'uri://ed-fi.org/AddressTypeDescriptor', 'Mailing', 'Mailing', 'edfi.AddressTypeDescriptor', '{Uri921}');
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task DropSchemaAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DROP SCHEMA IF EXISTS "{TestSchema}" CASCADE;
            DELETE FROM dms."Descriptor" WHERE "DocumentId" IN ({DescriptorId920}, {DescriptorId921});
            DELETE FROM dms."Document" WHERE "DocumentId" IN ({SchoolDocumentId1});
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Verifies that a descriptor FK on a collection-item (child table) row is resolved to a URI
/// and projected into the reconstituted JSON document at the correct array-element path,
/// exercising the full database-backed hydrate + project + reconstitute pipeline.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Collection_Item_Descriptor_FK_Resolves_To_URI_Postgresql
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await CollectionDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await CollectionDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Insert root document
        await using var insertRootCmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{CollectionDescriptorProjectionFixture.TestSchema}"."School"
                ("DocumentId", "SchoolId")
            VALUES
                ({CollectionDescriptorProjectionFixture.SchoolDocumentId1}, 255901);
            """,
            setupConn
        );
        await insertRootCmd.ExecuteNonQueryAsync();

        // Insert two collection items with descriptor FKs
        await using var insertChildCmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{CollectionDescriptorProjectionFixture.TestSchema}"."SchoolAddress"
                ("CollectionItemId", "School_DocumentId", "Ordinal", "City", "AddressType_DescriptorId")
            VALUES
                ({CollectionDescriptorProjectionFixture.AddressCollectionItemId1},
                 {CollectionDescriptorProjectionFixture.SchoolDocumentId1}, 0, 'Grand Bend',
                 {CollectionDescriptorProjectionFixture.DescriptorId920}),
                ({CollectionDescriptorProjectionFixture.AddressCollectionItemId2},
                 {CollectionDescriptorProjectionFixture.SchoolDocumentId1}, 1, 'Austin',
                 {CollectionDescriptorProjectionFixture.DescriptorId921});
            """,
            setupConn
        );
        await insertChildCmd.ExecuteNonQueryAsync();

        var resourceModel = CollectionDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(resourceModel);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(CollectionDescriptorProjectionFixture.SchoolDocumentId1),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            CollectionDescriptorProjectionFixture.SchoolDocumentId1,
            hydratedPage.TableRowsInDependencyOrder,
            readPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await CollectionDescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_a_reconstituted_json_object()
    {
        _reconstitutedDocument.Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_emits_the_addresses_array()
    {
        _reconstitutedDocument["addresses"].Should().NotBeNull();
        _reconstitutedDocument["addresses"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public void It_emits_the_first_address_descriptor_uri()
    {
        _reconstitutedDocument["addresses"]![0]!["addressTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(CollectionDescriptorProjectionFixture.Uri920);
    }

    [Test]
    public void It_emits_the_second_address_descriptor_uri()
    {
        _reconstitutedDocument["addresses"]![1]!["addressTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(CollectionDescriptorProjectionFixture.Uri921);
    }

    [Test]
    public void It_emits_the_scalar_city_on_the_first_address()
    {
        _reconstitutedDocument["addresses"]![0]!["city"]!.GetValue<string>().Should().Be("Grand Bend");
    }

    [Test]
    public void It_does_not_expose_the_raw_descriptor_fk_column()
    {
        _reconstitutedDocument["addresses"]![0]!["AddressType_DescriptorId"].Should().BeNull();
        _reconstitutedDocument["addresses"]![1]!["AddressType_DescriptorId"].Should().BeNull();
    }
}

/// <summary>
/// Verifies that a null descriptor FK on a collection-item row causes the descriptor property
/// to be omitted from that array element in the reconstituted JSON.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Collection_Item_Null_Descriptor_FK_Omits_Property_Postgresql
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await CollectionDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await CollectionDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        await using var insertRootCmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{CollectionDescriptorProjectionFixture.TestSchema}"."School"
                ("DocumentId", "SchoolId")
            VALUES
                ({CollectionDescriptorProjectionFixture.SchoolDocumentId1}, 255901);
            """,
            setupConn
        );
        await insertRootCmd.ExecuteNonQueryAsync();

        // Insert one collection item with a non-null descriptor and one with NULL
        await using var insertChildCmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{CollectionDescriptorProjectionFixture.TestSchema}"."SchoolAddress"
                ("CollectionItemId", "School_DocumentId", "Ordinal", "City", "AddressType_DescriptorId")
            VALUES
                ({CollectionDescriptorProjectionFixture.AddressCollectionItemId1},
                 {CollectionDescriptorProjectionFixture.SchoolDocumentId1}, 0, 'Grand Bend',
                 {CollectionDescriptorProjectionFixture.DescriptorId920}),
                ({CollectionDescriptorProjectionFixture.AddressCollectionItemId2},
                 {CollectionDescriptorProjectionFixture.SchoolDocumentId1}, 1, 'Austin',
                 NULL);
            """,
            setupConn
        );
        await insertChildCmd.ExecuteNonQueryAsync();

        var resourceModel = CollectionDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(resourceModel);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(CollectionDescriptorProjectionFixture.SchoolDocumentId1),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            CollectionDescriptorProjectionFixture.SchoolDocumentId1,
            hydratedPage.TableRowsInDependencyOrder,
            readPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await CollectionDescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_emits_the_descriptor_uri_on_the_non_null_collection_item()
    {
        _reconstitutedDocument["addresses"]![0]!["addressTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(CollectionDescriptorProjectionFixture.Uri920);
    }

    [Test]
    public void It_omits_the_descriptor_property_on_the_null_collection_item()
    {
        _reconstitutedDocument["addresses"]![1]!["addressTypeDescriptor"].Should().BeNull();
    }

    [Test]
    public void It_still_emits_the_scalar_city_on_both_items()
    {
        _reconstitutedDocument["addresses"]![0]!["city"]!.GetValue<string>().Should().Be("Grand Bend");
        _reconstitutedDocument["addresses"]![1]!["city"]!.GetValue<string>().Should().Be("Austin");
    }
}
