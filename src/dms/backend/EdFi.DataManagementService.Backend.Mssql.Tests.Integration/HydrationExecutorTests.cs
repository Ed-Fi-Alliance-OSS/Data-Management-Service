// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
public class Given_A_Page_With_Multiple_Documents_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private HydratedPage _result = null!;

    private const string TestSchema = "hydtest";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("MSSQL connection string not configured.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Provision schemas and tables
        await ExecuteSql(
            connection,
            """
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hydtest') EXEC('CREATE SCHEMA [hydtest]');

            CREATE TABLE dms.Document (
                DocumentId bigint PRIMARY KEY,
                DocumentUuid uniqueidentifier NOT NULL,
                ContentVersion bigint NOT NULL DEFAULT 1,
                IdentityVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                IdentityLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE hydtest.School (
                DocumentId bigint PRIMARY KEY,
                SchoolId int NOT NULL
            );

            CREATE TABLE hydtest.SchoolAddress (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL REFERENCES hydtest.School(DocumentId),
                Ordinal int NOT NULL,
                City varchar(100) NOT NULL
            );
            """
        );

        // Insert test data
        await ExecuteSql(
            connection,
            """
            INSERT INTO dms.Document (DocumentId, DocumentUuid, ContentVersion, IdentityVersion)
            VALUES
                (101, 'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa', 10, 10),
                (102, 'bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb', 20, 20);

            INSERT INTO hydtest.School (DocumentId, SchoolId)
            VALUES
                (101, 255901),
                (102, 255902);

            INSERT INTO hydtest.SchoolAddress (CollectionItemId, School_DocumentId, Ordinal, City)
            VALUES
                (1001, 101, 0, 'Springfield'),
                (1002, 101, 1, 'Shelbyville'),
                (1003, 102, 0, 'Centerville');
            """
        );

        // Build read plan
        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema);

        // Execute hydration
        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT DocumentId FROM hydtest.School
                ORDER BY DocumentId
                OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
                """,
                TotalCountSql: null,
                PageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );

        await using var hydrationConnection = new SqlConnection(_connectionString);
        await hydrationConnection.OpenAsync();

        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            keyset,
            SqlDialect.Mssql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_document_metadata_for_all_documents()
    {
        _result.DocumentMetadata.Should().HaveCount(2);
        _result.DocumentMetadata[0].DocumentId.Should().Be(101);
        _result.DocumentMetadata[1].DocumentId.Should().Be(102);
    }

    [Test]
    public void It_returns_document_uuids()
    {
        _result
            .DocumentMetadata[0]
            .DocumentUuid.Should()
            .Be(Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa"));
        _result
            .DocumentMetadata[1]
            .DocumentUuid.Should()
            .Be(Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb"));
    }

    [Test]
    public void It_returns_version_stamps()
    {
        _result.DocumentMetadata[0].ContentVersion.Should().Be(10);
        _result.DocumentMetadata[1].ContentVersion.Should().Be(20);
    }

    [Test]
    public void It_returns_root_rows_ordered_by_DocumentId()
    {
        _result.TableRowsInDependencyOrder.Should().HaveCount(2);

        var rootRows = _result.TableRowsInDependencyOrder[0];
        rootRows.Rows.Should().HaveCount(2);

        // First column is DocumentId
        ((long)rootRows.Rows[0][0]!)
            .Should()
            .Be(101);
        ((long)rootRows.Rows[1][0]!).Should().Be(102);

        // Second column is SchoolId
        ((int)rootRows.Rows[0][1]!)
            .Should()
            .Be(255901);
        ((int)rootRows.Rows[1][1]!).Should().Be(255902);
    }

    [Test]
    public void It_returns_child_rows_ordered_by_root_scope_and_ordinal()
    {
        var childRows = _result.TableRowsInDependencyOrder[1];
        childRows.Rows.Should().HaveCount(3);

        // Child rows should be ordered by School_DocumentId, then Ordinal
        // Row 0: CollectionItemId=1001, School_DocumentId=101, Ordinal=0, City=Springfield
        ((long)childRows.Rows[0][0]!)
            .Should()
            .Be(1001);
        ((long)childRows.Rows[0][1]!).Should().Be(101);
        ((int)childRows.Rows[0][2]!).Should().Be(0);
        ((string)childRows.Rows[0][3]!).Should().Be("Springfield");

        // Row 1: CollectionItemId=1002, School_DocumentId=101, Ordinal=1, City=Shelbyville
        ((long)childRows.Rows[1][0]!)
            .Should()
            .Be(1002);
        ((int)childRows.Rows[1][2]!).Should().Be(1);

        // Row 2: CollectionItemId=1003, School_DocumentId=102, Ordinal=0, City=Centerville
        ((long)childRows.Rows[2][0]!)
            .Should()
            .Be(1003);
        ((long)childRows.Rows[2][1]!).Should().Be(102);
    }

    [Test]
    public void It_returns_no_total_count_when_not_requested()
    {
        _result.TotalCount.Should().BeNull();
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
public class Given_A_Single_DocumentId_Keyset_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private HydratedPage _result = null!;

    private const string TestSchema = "hydsingle";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("MSSQL connection string not configured.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await ExecuteSql(
            connection,
            """
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hydsingle') EXEC('CREATE SCHEMA [hydsingle]');

            CREATE TABLE dms.Document (
                DocumentId bigint PRIMARY KEY,
                DocumentUuid uniqueidentifier NOT NULL,
                ContentVersion bigint NOT NULL DEFAULT 1,
                IdentityVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                IdentityLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE hydsingle.School (
                DocumentId bigint PRIMARY KEY,
                SchoolId int NOT NULL
            );

            CREATE TABLE hydsingle.SchoolAddress (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL REFERENCES hydsingle.School(DocumentId),
                Ordinal int NOT NULL,
                City varchar(100) NOT NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            INSERT INTO dms.Document (DocumentId, DocumentUuid)
            VALUES
                (201, 'cccccccc-3333-3333-3333-cccccccccccc'),
                (202, 'dddddddd-4444-4444-4444-dddddddddddd');

            INSERT INTO hydsingle.School (DocumentId, SchoolId)
            VALUES (201, 100001), (202, 100002);

            INSERT INTO hydsingle.SchoolAddress (CollectionItemId, School_DocumentId, Ordinal, City)
            VALUES (2001, 201, 0, 'Alpha'), (2002, 202, 0, 'Beta');
            """
        );

        // Build plan using the hydsingle schema
        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema);
        var keyset = new PageKeysetSpec.Single(201L);

        await using var hydrationConnection = new SqlConnection(_connectionString);
        await hydrationConnection.OpenAsync();

        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            keyset,
            SqlDialect.Mssql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_exactly_one_document()
    {
        _result.DocumentMetadata.Should().HaveCount(1);
        _result.DocumentMetadata[0].DocumentId.Should().Be(201);
        _result
            .DocumentMetadata[0]
            .DocumentUuid.Should()
            .Be(Guid.Parse("cccccccc-3333-3333-3333-cccccccccccc"));
    }

    [Test]
    public void It_returns_root_rows_only_for_that_document()
    {
        var rootRows = _result.TableRowsInDependencyOrder[0];
        rootRows.Rows.Should().HaveCount(1);
        ((long)rootRows.Rows[0][0]!).Should().Be(201);
    }

    [Test]
    public void It_returns_child_rows_only_for_that_document()
    {
        var childRows = _result.TableRowsInDependencyOrder[1];
        childRows.Rows.Should().HaveCount(1);
        ((long)childRows.Rows[0][1]!).Should().Be(201);
        ((string)childRows.Rows[0][3]!).Should().Be("Alpha");
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Shared test model builder for hydration executor integration tests.
/// </summary>
internal static class HydrationTestHelper
{
    /// <summary>
    /// Builds a <see cref="ResourceReadPlan"/> for a School resource with an Address child table
    /// in the given schema, using the <see cref="ReadPlanCompiler"/> to generate real SQL.
    /// </summary>
    public static ResourceReadPlan BuildSchoolReadPlan(string schemaName)
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName(schemaName), "School"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_School",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
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
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName(schemaName), "SchoolAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.Scalar,
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
            ],
            Constraints: []
        );

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName(schemaName),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ReadPlanCompiler(SqlDialect.Mssql).Compile(model);
    }
}
