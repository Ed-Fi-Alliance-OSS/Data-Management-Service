// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
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

            CREATE TABLE hydtest.SchoolAddressPeriod (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL,
                ParentCollectionItemId bigint NOT NULL REFERENCES hydtest.SchoolAddress(CollectionItemId),
                Ordinal int NOT NULL,
                BeginDate varchar(10) NOT NULL
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

            INSERT INTO hydtest.SchoolAddressPeriod (CollectionItemId, School_DocumentId, ParentCollectionItemId, Ordinal, BeginDate)
            VALUES
                (5001, 101, 1001, 0, '2020-01-01'),
                (5002, 101, 1001, 1, '2021-06-15'),
                (5003, 101, 1002, 0, '2022-09-01'),
                (5004, 102, 1003, 0, '2023-03-01');
            """
        );

        // Build read plan
        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql);

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
        _result.TableRowsInDependencyOrder.Should().HaveCount(3);

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
    public void It_returns_nested_child_rows_ordered_by_root_scope_parent_scope_and_ordinal()
    {
        var nestedRows = _result.TableRowsInDependencyOrder[2];
        nestedRows.Rows.Should().HaveCount(4);

        // Columns: CollectionItemId, School_DocumentId, ParentCollectionItemId, Ordinal, BeginDate
        // Ordered by School_DocumentId ASC, ParentCollectionItemId ASC, Ordinal ASC

        // Row 0: doc 101, parent 1001, ordinal 0
        ((long)nestedRows.Rows[0][1]!)
            .Should()
            .Be(101);
        ((long)nestedRows.Rows[0][2]!).Should().Be(1001);
        ((int)nestedRows.Rows[0][3]!).Should().Be(0);
        ((string)nestedRows.Rows[0][4]!).Should().Be("2020-01-01");

        // Row 1: doc 101, parent 1001, ordinal 1
        ((long)nestedRows.Rows[1][2]!)
            .Should()
            .Be(1001);
        ((int)nestedRows.Rows[1][3]!).Should().Be(1);
        ((string)nestedRows.Rows[1][4]!).Should().Be("2021-06-15");

        // Row 2: doc 101, parent 1002, ordinal 0
        ((long)nestedRows.Rows[2][2]!)
            .Should()
            .Be(1002);
        ((string)nestedRows.Rows[2][4]!).Should().Be("2022-09-01");

        // Row 3: doc 102, parent 1003, ordinal 0
        ((long)nestedRows.Rows[3][1]!)
            .Should()
            .Be(102);
        ((long)nestedRows.Rows[3][2]!).Should().Be(1003);
        ((string)nestedRows.Rows[3][4]!).Should().Be("2023-03-01");
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

            CREATE TABLE hydsingle.SchoolAddressPeriod (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL,
                ParentCollectionItemId bigint NOT NULL REFERENCES hydsingle.SchoolAddress(CollectionItemId),
                Ordinal int NOT NULL,
                BeginDate varchar(10) NOT NULL
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

            INSERT INTO hydsingle.SchoolAddressPeriod (CollectionItemId, School_DocumentId, ParentCollectionItemId, Ordinal, BeginDate)
            VALUES (6001, 201, 2001, 0, '2020-01-01'), (6002, 202, 2002, 0, '2023-03-01');
            """
        );

        // Build plan using the hydsingle schema
        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql);
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

    [Test]
    public void It_returns_nested_child_rows_only_for_that_document()
    {
        var nestedRows = _result.TableRowsInDependencyOrder[2];
        nestedRows.Rows.Should().HaveCount(1);
        ((long)nestedRows.Rows[0][1]!).Should().Be(201);
        ((long)nestedRows.Rows[0][2]!).Should().Be(2001);
        ((string)nestedRows.Rows[0][4]!).Should().Be("2020-01-01");
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
public class Given_A_Query_With_TotalCount_Requested_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private HydratedPage _result = null!;

    private const string TestSchema = "hydcount";

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
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hydcount') EXEC('CREATE SCHEMA [hydcount]');

            CREATE TABLE dms.Document (
                DocumentId bigint PRIMARY KEY,
                DocumentUuid uniqueidentifier NOT NULL,
                ContentVersion bigint NOT NULL DEFAULT 1,
                IdentityVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                IdentityLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE hydcount.School (
                DocumentId bigint PRIMARY KEY,
                SchoolId int NOT NULL
            );

            CREATE TABLE hydcount.SchoolAddress (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL REFERENCES hydcount.School(DocumentId),
                Ordinal int NOT NULL,
                City varchar(100) NOT NULL
            );

            CREATE TABLE hydcount.SchoolAddressPeriod (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL,
                ParentCollectionItemId bigint NOT NULL REFERENCES hydcount.SchoolAddress(CollectionItemId),
                Ordinal int NOT NULL,
                BeginDate varchar(10) NOT NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            INSERT INTO dms.Document (DocumentId, DocumentUuid)
            VALUES
                (301, 'eeeeeeee-5555-5555-5555-eeeeeeeeeeee'),
                (302, 'ffffffff-6666-6666-6666-ffffffffffff'),
                (303, '11111111-7777-7777-7777-111111111111');

            INSERT INTO hydcount.School (DocumentId, SchoolId)
            VALUES (301, 900001), (302, 900002), (303, 900003);
            """
        );

        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql);

        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT DocumentId FROM hydcount.School
                ORDER BY DocumentId
                OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
                """,
                TotalCountSql: "SELECT COUNT(1) FROM hydcount.School",
                PageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                TotalCountParametersInOrder: []
            ),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 2L }
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
    public void It_returns_total_count()
    {
        _result.TotalCount.Should().Be(3);
    }

    [Test]
    public void It_returns_only_the_paged_documents()
    {
        _result.DocumentMetadata.Should().HaveCount(2);
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
public class Given_A_Reference_Bearing_Resource_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private HydratedPage _result = null!;
    private ResourceReadPlan _plan = null!;

    private const string TestSchema = "hydref";

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
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hydref') EXEC('CREATE SCHEMA [hydref]');

            CREATE TABLE dms.Document (
                DocumentId bigint PRIMARY KEY,
                DocumentUuid uniqueidentifier NOT NULL,
                ContentVersion bigint NOT NULL DEFAULT 1,
                IdentityVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                IdentityLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE hydref.StudentSchoolAssociation (
                DocumentId bigint PRIMARY KEY,
                School_DocumentId bigint NULL,
                School_SchoolId bigint NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            INSERT INTO dms.Document (DocumentId, DocumentUuid)
            VALUES
                (401, 'aaaa0001-0001-0001-0001-aaaa00000001'),
                (402, 'aaaa0002-0002-0002-0002-aaaa00000002'),
                (403, 'aaaa0003-0003-0003-0003-aaaa00000003');

            INSERT INTO hydref.StudentSchoolAssociation (DocumentId, School_DocumentId, School_SchoolId)
            VALUES
                (401, 10, 255901),
                (402, NULL, NULL),
                (403, 20, 255902);
            """
        );

        _plan = HydrationTestHelper.BuildStudentSchoolAssociationReadPlan(TestSchema, SqlDialect.Mssql);

        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT DocumentId FROM hydref.StudentSchoolAssociation
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
            _plan,
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
    public void It_returns_all_three_documents()
    {
        _result.DocumentMetadata.Should().HaveCount(3);
    }

    [Test]
    public void It_returns_root_rows_with_nullable_reference_columns()
    {
        var rootRows = _result.TableRowsInDependencyOrder[0];
        rootRows.Rows.Should().HaveCount(3);

        // Doc 401: School_DocumentId=10, School_SchoolId=255901
        rootRows.Rows[0][1].Should().NotBeNull();
        ((long)rootRows.Rows[0][1]!).Should().Be(10);
        ((long)rootRows.Rows[0][2]!).Should().Be(255901);

        // Doc 402: School_DocumentId=NULL, School_SchoolId=NULL
        rootRows.Rows[1][1].Should().BeNull();
        rootRows.Rows[1][2].Should().BeNull();

        // Doc 403: School_DocumentId=20, School_SchoolId=255902
        ((long)rootRows.Rows[2][1]!)
            .Should()
            .Be(20);
        ((long)rootRows.Rows[2][2]!).Should().Be(255902);
    }

    [Test]
    public void It_projects_reference_for_populated_documents()
    {
        var projectionPlan = _plan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var hydratedRows = _result.TableRowsInDependencyOrder[0];

        var projections = ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);

        projections.Should().ContainKey(401L);
        projections.Should().ContainKey(403L);

        var doc401 = projections[401L];
        doc401.Should().HaveCount(1);
        doc401[0]
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255901L);

        var doc403 = projections[403L];
        doc403.Should().HaveCount(1);
        doc403[0]
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255902L);
    }

    [Test]
    public void It_does_not_project_reference_for_null_fk()
    {
        var projectionPlan = _plan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var hydratedRows = _result.TableRowsInDependencyOrder[0];

        var projections = ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);

        projections.Should().NotContainKey(402L);
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
