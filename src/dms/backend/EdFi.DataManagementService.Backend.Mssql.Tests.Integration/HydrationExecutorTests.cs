// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
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
                ResourceKeyId smallint NOT NULL DEFAULT 0,
                ContentVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
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
            INSERT INTO dms.Document (DocumentId, DocumentUuid, ContentVersion)
            VALUES
                (101, 'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa', 10),
                (102, 'bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb', 20);

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
[Category(MssqlCiShards.Shard4)]
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
                ResourceKeyId smallint NOT NULL DEFAULT 0,
                ContentVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
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
[Category(MssqlCiShards.Shard4)]
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
                ResourceKeyId smallint NOT NULL DEFAULT 0,
                ContentVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
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
[Category(MssqlCiShards.Shard3)]
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
                ResourceKeyId smallint NOT NULL DEFAULT 0,
                ContentVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE hydref.StudentSchoolAssociation (
                DocumentId bigint PRIMARY KEY,
                School_DocumentId bigint NULL,
                School_SchoolId bigint NULL,
                Calendar_DocumentId bigint NULL,
                Calendar_CalendarCode varchar(60) NULL
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

            INSERT INTO hydref.StudentSchoolAssociation (DocumentId, School_DocumentId, School_SchoolId, Calendar_DocumentId, Calendar_CalendarCode)
            VALUES
                (401, 10, 255901, 50, 'CAL-101'),
                (402, NULL, NULL, NULL, NULL),
                (403, 20, 255902, 60, 'CAL-202');
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

        // Doc 401: School_DocumentId=10, School_SchoolId=255901, Calendar_DocumentId=50, Calendar_CalendarCode='CAL-101'
        rootRows.Rows[0][1].Should().NotBeNull();
        ((long)rootRows.Rows[0][1]!).Should().Be(10);
        ((long)rootRows.Rows[0][2]!).Should().Be(255901);
        ((long)rootRows.Rows[0][3]!).Should().Be(50);
        ((string)rootRows.Rows[0][4]!).Should().Be("CAL-101");

        // Doc 402: all reference columns NULL
        rootRows.Rows[1][1].Should().BeNull();
        rootRows.Rows[1][2].Should().BeNull();
        rootRows.Rows[1][3].Should().BeNull();
        rootRows.Rows[1][4].Should().BeNull();

        // Doc 403: School_DocumentId=20, School_SchoolId=255902, Calendar_DocumentId=60, Calendar_CalendarCode='CAL-202'
        ((long)rootRows.Rows[2][1]!)
            .Should()
            .Be(20);
        ((long)rootRows.Rows[2][2]!).Should().Be(255902);
        ((long)rootRows.Rows[2][3]!).Should().Be(60);
        ((string)rootRows.Rows[2][4]!).Should().Be("CAL-202");
    }

    [Test]
    public void It_projects_identity_component_reference_for_populated_documents()
    {
        var projectionPlan = _plan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var hydratedRows = _result.TableRowsInDependencyOrder[0];

        var projections = ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);

        projections.Should().ContainKey(401L);
        projections.Should().ContainKey(403L);

        var doc401School = projections[401L]
            .Single(p => p.ReferenceObjectPath.Canonical == "$.schoolReference");
        doc401School.IsIdentityComponent.Should().BeTrue();
        doc401School.TargetResource.Should().Be(new QualifiedResourceName("Ed-Fi", "School"));
        doc401School
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255901L);

        var doc403School = projections[403L]
            .Single(p => p.ReferenceObjectPath.Canonical == "$.schoolReference");
        doc403School
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.schoolReference.schoolId")
            .Value.Should()
            .Be(255902L);
    }

    [Test]
    public void It_projects_non_identity_reference_for_populated_documents()
    {
        var projectionPlan = _plan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var hydratedRows = _result.TableRowsInDependencyOrder[0];

        var projections = ReferenceIdentityProjector.ProjectTable(hydratedRows, projectionPlan);

        var doc401Calendar = projections[401L]
            .Single(p => p.ReferenceObjectPath.Canonical == "$.calendarReference");
        doc401Calendar.IsIdentityComponent.Should().BeFalse();
        doc401Calendar.TargetResource.Should().Be(new QualifiedResourceName("Ed-Fi", "Calendar"));
        doc401Calendar
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.calendarReference.calendarCode")
            .Value.Should()
            .Be("CAL-101");

        var doc403Calendar = projections[403L]
            .Single(p => p.ReferenceObjectPath.Canonical == "$.calendarReference");
        doc403Calendar
            .FieldsInOrder.Single(f => f.ReferenceJsonPath.Canonical == "$.calendarReference.calendarCode")
            .Value.Should()
            .Be("CAL-202");
    }

    [Test]
    public void It_does_not_project_any_reference_for_null_fk()
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

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_HydrationExecutor_Single_Document_Fast_Path_With_DescriptorProjection_And_DocumentReferenceLookup_Mssql
{
    private const string TestSchema = "hydfastpath";
    private const long ResourceDocumentId = 10001L;
    private static readonly PageKeysetSpec.Single _keyset = new(ResourceDocumentId);
    private static readonly HydrationExecutionOptions _keysetOptions = new(
        IncludeDescriptorProjection: true,
        IncludeDocumentReferenceLookup: true,
        UseSingleDocumentFastPath: false
    );
    private static readonly HydrationExecutionOptions _fastPathOptions = new(
        IncludeDescriptorProjection: true,
        IncludeDocumentReferenceLookup: true,
        UseSingleDocumentFastPath: true
    );

    private string _databaseName = null!;
    private string _connectionString = null!;
    private ResourceReadPlan _plan = null!;
    private HydratedPage _keysetResult = null!;
    private HydratedPage _fastPathResult = null!;
    private string _keysetBatchSql = null!;
    private string _fastPathBatchSql = null!;

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
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hydfastpath') EXEC('CREATE SCHEMA [hydfastpath]');

            CREATE TABLE dms.Document (
                DocumentId bigint PRIMARY KEY,
                DocumentUuid uniqueidentifier NOT NULL,
                ResourceKeyId smallint NOT NULL DEFAULT 0,
                ContentVersion bigint NOT NULL DEFAULT 1,
                IdentityVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                IdentityLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE dms.Descriptor (
                DocumentId bigint PRIMARY KEY,
                Namespace varchar(255) NOT NULL DEFAULT '',
                CodeValue varchar(50) NOT NULL DEFAULT '',
                ShortDescription varchar(75) NOT NULL DEFAULT '',
                Description varchar(1024) NULL,
                EffectiveBeginDate date NULL,
                EffectiveEndDate date NULL,
                Discriminator varchar(128) NOT NULL DEFAULT '',
                Uri varchar(306) NOT NULL
            );

            CREATE TABLE hydfastpath.StudentSchoolAssociation (
                DocumentId bigint PRIMARY KEY,
                School_DocumentId bigint NULL,
                School_SchoolId bigint NULL,
                EntryGradeLevelDescriptor_DescriptorId bigint NULL
            );

            CREATE TABLE hydfastpath.StudentSchoolAssociationProgram (
                CollectionItemId bigint PRIMARY KEY,
                StudentSchoolAssociation_DocumentId bigint NOT NULL REFERENCES hydfastpath.StudentSchoolAssociation(DocumentId),
                Ordinal int NOT NULL,
                Program_DocumentId bigint NULL,
                Program_ProgramName varchar(100) NULL,
                ProgramTypeDescriptor_DescriptorId bigint NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            INSERT INTO dms.Document (DocumentId, DocumentUuid, ResourceKeyId, ContentVersion, IdentityVersion)
            VALUES
                (10001, '00000000-0000-0000-0000-000000010001', 1, 11, 11),
                (10002, '00000000-0000-0000-0000-000000010002', 1, 12, 12),
                (11001, '00000000-0000-0000-0000-000000011001', 2, 1, 1),
                (11002, '00000000-0000-0000-0000-000000011002', 3, 1, 1),
                (11003, '00000000-0000-0000-0000-000000011003', 4, 1, 1),
                (12001, '00000000-0000-0000-0000-000000012001', 5, 1, 1),
                (12002, '00000000-0000-0000-0000-000000012002', 6, 1, 1),
                (12003, '00000000-0000-0000-0000-000000012003', 7, 1, 1);

            INSERT INTO dms.Descriptor (DocumentId, Namespace, CodeValue, ShortDescription, Discriminator, Uri)
            VALUES
                (12001, 'uri://ed-fi.org/GradeLevelDescriptor', 'Ninth grade', 'Ninth grade', 'edfi.GradeLevelDescriptor', 'uri://ed-fi.org/GradeLevelDescriptor#Ninth grade'),
                (12002, 'uri://ed-fi.org/ProgramTypeDescriptor', 'Gifted', 'Gifted', 'edfi.ProgramTypeDescriptor', 'uri://ed-fi.org/ProgramTypeDescriptor#Gifted'),
                (12003, 'uri://ed-fi.org/GradeLevelDescriptor', 'Tenth grade', 'Tenth grade', 'edfi.GradeLevelDescriptor', 'uri://ed-fi.org/GradeLevelDescriptor#Tenth grade');

            INSERT INTO hydfastpath.StudentSchoolAssociation
                (DocumentId, School_DocumentId, School_SchoolId, EntryGradeLevelDescriptor_DescriptorId)
            VALUES
                (10001, 11001, 255901, 12001),
                (10002, 11003, 255902, 12003);

            INSERT INTO hydfastpath.StudentSchoolAssociationProgram
                (CollectionItemId, StudentSchoolAssociation_DocumentId, Ordinal, Program_DocumentId, Program_ProgramName, ProgramTypeDescriptor_DescriptorId)
            VALUES
                (20001, 10001, 0, 11002, 'Gifted', 12002),
                (20002, 10001, 1, NULL, NULL, NULL),
                (20003, 10002, 0, 11003, 'Other', 12003);
            """
        );

        _plan = BuildReadPlan();
        _keysetBatchSql = HydrationBatchBuilder.Build(_plan, _keyset, SqlDialect.Mssql, _keysetOptions);
        _fastPathBatchSql = HydrationBatchBuilder.Build(_plan, _keyset, SqlDialect.Mssql, _fastPathOptions);

        await using var keysetConnection = new SqlConnection(_connectionString);
        await keysetConnection.OpenAsync();
        _keysetResult = await HydrationExecutor.ExecuteAsync(
            keysetConnection,
            _plan,
            _keyset,
            SqlDialect.Mssql,
            _keysetOptions,
            CancellationToken.None
        );

        await using var fastPathConnection = new SqlConnection(_connectionString);
        await fastPathConnection.OpenAsync();
        _fastPathResult = await HydrationExecutor.ExecuteAsync(
            fastPathConnection,
            _plan,
            _keyset,
            SqlDialect.Mssql,
            _fastPathOptions,
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
    public void It_generates_a_batch_without_the_page_temp_table()
    {
        _fastPathBatchSql.Should().NotContain("[#page]");
        _fastPathBatchSql.Should().NotContain("CREATE TABLE");
        _fastPathBatchSql.Should().NotContain("DROP TABLE");
    }

    [Test]
    public void It_generates_a_keyset_batch_that_does_use_the_page_temp_table()
    {
        _keysetBatchSql.Should().Contain("[#page]");
    }

    [Test]
    public void It_generates_different_batch_sql_for_the_keyset_and_fast_paths()
    {
        _fastPathBatchSql.Should().NotBe(_keysetBatchSql);
    }

    [Test]
    public void It_matches_the_existing_keyset_hydration_result()
    {
        AssertHydratedPagesMatch(_keysetResult, _fastPathResult);
    }

    [Test]
    public void It_filters_child_descriptor_and_lookup_rows_to_the_single_document()
    {
        _fastPathResult.DocumentMetadata.Should().ContainSingle();
        _fastPathResult.DocumentMetadata[0].DocumentId.Should().Be(ResourceDocumentId);

        var childRows = _fastPathResult.TableRowsInDependencyOrder[1].Rows;
        childRows.Should().HaveCount(2);
        childRows.Select(row => (long)row[1]!).Should().Equal(ResourceDocumentId, ResourceDocumentId);
        childRows.Select(row => row[5]).Should().Equal(12002L, null);

        _fastPathResult
            .DescriptorRowsInPlanOrder.Should()
            .ContainSingle()
            .Which.Rows.Select(row => row.DescriptorId)
            .Should()
            .Equal(12001L, 12002L);

        var documentReferenceLookup = _fastPathResult.DocumentReferenceLookup;

        documentReferenceLookup.Should().NotBeNull();
        documentReferenceLookup!.Rows.Select(row => row.DocumentId).Should().Equal(11001L, 11002L);
    }

    private static ResourceReadPlan BuildReadPlan()
    {
        var schema = new DbSchemaName(TestSchema);
        var rootTableName = new DbTableName(schema, "StudentSchoolAssociation");
        var childTableName = new DbTableName(schema, "StudentSchoolAssociationProgram");

        var schoolReferencePath = new JsonPathExpression(
            "$.schoolReference",
            [new JsonPathSegment.Property("schoolReference")]
        );
        var schoolIdPath = new JsonPathExpression(
            "$.schoolReference.schoolId",
            [new JsonPathSegment.Property("schoolReference"), new JsonPathSegment.Property("schoolId")]
        );
        var entryGradePath = new JsonPathExpression(
            "$.entryGradeLevelDescriptor",
            [new JsonPathSegment.Property("entryGradeLevelDescriptor")]
        );
        var programsPath = new JsonPathExpression(
            "$.programs[*]",
            [new JsonPathSegment.Property("programs"), new JsonPathSegment.AnyArrayElement()]
        );
        var programReferencePath = new JsonPathExpression(
            "$.programs[*].programReference",
            [
                new JsonPathSegment.Property("programs"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("programReference"),
            ]
        );
        var programNamePath = new JsonPathExpression(
            "$.programs[*].programReference.programName",
            [
                new JsonPathSegment.Property("programs"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("programReference"),
                new JsonPathSegment.Property("programName"),
            ]
        );
        var programTypeDescriptorPath = new JsonPathExpression(
            "$.programs[*].programTypeDescriptor",
            [
                new JsonPathSegment.Property("programs"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("programTypeDescriptor"),
            ]
        );

        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var programResource = new QualifiedResourceName("Ed-Fi", "Program");
        var gradeLevelDescriptorResource = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
        var programTypeDescriptorResource = new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor");

        var rootTable = new DbTableModel(
            Table: rootTableName,
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_StudentSchoolAssociation",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false, null, null),
                CreateColumn(
                    "School_DocumentId",
                    ColumnKind.DocumentFk,
                    ScalarKind.Int64,
                    true,
                    schoolReferencePath,
                    schoolResource
                ),
                CreateColumn(
                    "School_SchoolId",
                    ColumnKind.Scalar,
                    ScalarKind.Int64,
                    true,
                    schoolIdPath,
                    null
                ),
                CreateColumn(
                    "EntryGradeLevelDescriptor_DescriptorId",
                    ColumnKind.DescriptorFk,
                    ScalarKind.Int64,
                    true,
                    entryGradePath,
                    gradeLevelDescriptorResource
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

        var childTable = new DbTableModel(
            Table: childTableName,
            JsonScope: programsPath,
            Key: new TableKey(
                ConstraintName: "PK_StudentSchoolAssociationProgram",
                Columns:
                [
                    new DbKeyColumn(
                        new DbColumnName("StudentSchoolAssociation_DocumentId"),
                        ColumnKind.ParentKeyPart
                    ),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                CreateColumn(
                    "CollectionItemId",
                    ColumnKind.CollectionKey,
                    ScalarKind.Int64,
                    false,
                    null,
                    null
                ),
                CreateColumn(
                    "StudentSchoolAssociation_DocumentId",
                    ColumnKind.ParentKeyPart,
                    ScalarKind.Int64,
                    false,
                    null,
                    null
                ),
                CreateColumn("Ordinal", ColumnKind.Ordinal, ScalarKind.Int32, false, null, null),
                CreateColumn(
                    "Program_DocumentId",
                    ColumnKind.DocumentFk,
                    ScalarKind.Int64,
                    true,
                    programReferencePath,
                    programResource
                ),
                CreateColumn(
                    "Program_ProgramName",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    true,
                    programNamePath,
                    null
                ),
                CreateColumn(
                    "ProgramTypeDescriptor_DescriptorId",
                    ColumnKind.DescriptorFk,
                    ScalarKind.Int64,
                    true,
                    programTypeDescriptorPath,
                    programTypeDescriptorResource
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
            PhysicalSchema: schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: rootTableName,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: schoolIdPath,
                            ReferenceJsonPath: schoolIdPath,
                            Column: new DbColumnName("School_SchoolId")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: programReferencePath,
                    Table: childTableName,
                    FkColumn: new DbColumnName("Program_DocumentId"),
                    TargetResource: programResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            IdentityJsonPath: programNamePath,
                            ReferenceJsonPath: programNamePath,
                            Column: new DbColumnName("Program_ProgramName")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: entryGradePath,
                    Table: rootTableName,
                    FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                    DescriptorResource: gradeLevelDescriptorResource
                ),
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: programTypeDescriptorPath,
                    Table: childTableName,
                    FkColumn: new DbColumnName("ProgramTypeDescriptor_DescriptorId"),
                    DescriptorResource: programTypeDescriptorResource
                ),
            ]
        );

        return new ReadPlanCompiler(SqlDialect.Mssql).Compile(model);
    }

    private static DbColumnModel CreateColumn(
        string name,
        ColumnKind kind,
        ScalarKind scalarKind,
        bool isNullable,
        JsonPathExpression? sourceJsonPath,
        QualifiedResourceName? targetResource
    ) =>
        new(
            ColumnName: new DbColumnName(name),
            Kind: kind,
            ScalarType: scalarKind is ScalarKind.String
                ? new RelationalScalarType(scalarKind, MaxLength: 100)
                : new RelationalScalarType(scalarKind),
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: targetResource
        );

    private static void AssertHydratedPagesMatch(HydratedPage expected, HydratedPage actual)
    {
        actual.TotalCount.Should().Be(expected.TotalCount);
        actual.DocumentMetadata.Should().Equal(expected.DocumentMetadata);
        actual.TableRowsInDependencyOrder.Should().HaveCount(expected.TableRowsInDependencyOrder.Count);

        for (var tableIndex = 0; tableIndex < expected.TableRowsInDependencyOrder.Count; tableIndex++)
        {
            var expectedRows = expected.TableRowsInDependencyOrder[tableIndex].Rows;
            var actualRows = actual.TableRowsInDependencyOrder[tableIndex].Rows;

            actualRows.Should().HaveCount(expectedRows.Count);

            for (var rowIndex = 0; rowIndex < expectedRows.Count; rowIndex++)
            {
                actualRows[rowIndex].Should().Equal(expectedRows[rowIndex]);
            }
        }

        actual.DescriptorRowsInPlanOrder.Should().HaveCount(expected.DescriptorRowsInPlanOrder.Count);

        for (var planIndex = 0; planIndex < expected.DescriptorRowsInPlanOrder.Count; planIndex++)
        {
            actual
                .DescriptorRowsInPlanOrder[planIndex]
                .Rows.Should()
                .Equal(expected.DescriptorRowsInPlanOrder[planIndex].Rows);
        }

        actual.DocumentReferenceLookup.Should().NotBeNull();
        expected.DocumentReferenceLookup.Should().NotBeNull();
        actual.DocumentReferenceLookup!.Rows.Should().Equal(expected.DocumentReferenceLookup!.Rows);
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The keyset materialization's <c>OUTPUT INSERTED</c> clause carries the selected page keyset out of
/// hydration on the same command that hydrates it. These cases run the real batch against SQL Server,
/// because the clause is only valid if the server accepts it.
/// </summary>
[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Query_Keyset_That_Returns_Its_Selected_Ids
{
    private string _databaseName = null!;
    private string _connectionString = null!;

    private const string TestSchema = "hydselected";

    /// <summary>
    /// Sparse ids, so a maximum cannot be confused with a count or a row position.
    /// </summary>
    private const long FirstDocumentId = 501L;
    private const long SecondDocumentId = 509L;
    private const long ThirdDocumentId = 517L;

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
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'hydselected') EXEC('CREATE SCHEMA [hydselected]');

            CREATE TABLE dms.Document (
                DocumentId bigint PRIMARY KEY,
                DocumentUuid uniqueidentifier NOT NULL,
                ResourceKeyId smallint NOT NULL DEFAULT 0,
                ContentVersion bigint NOT NULL DEFAULT 1,
                IdentityVersion bigint NOT NULL DEFAULT 1,
                ContentLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                IdentityLastModifiedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                CreatedAt datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE hydselected.School (
                DocumentId bigint PRIMARY KEY,
                SchoolId int NOT NULL
            );

            CREATE TABLE hydselected.SchoolAddress (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL REFERENCES hydselected.School(DocumentId),
                Ordinal int NOT NULL,
                City varchar(100) NOT NULL
            );

            CREATE TABLE hydselected.SchoolAddressPeriod (
                CollectionItemId bigint PRIMARY KEY,
                School_DocumentId bigint NOT NULL,
                ParentCollectionItemId bigint NOT NULL REFERENCES hydselected.SchoolAddress(CollectionItemId),
                Ordinal int NOT NULL,
                BeginDate varchar(10) NOT NULL
            );
            """
        );
    }

    [SetUp]
    public async Task Setup()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await ExecuteSql(
            connection,
            """
            DELETE FROM hydselected.SchoolAddressPeriod;
            DELETE FROM hydselected.SchoolAddress;
            DELETE FROM hydselected.School;
            DELETE FROM dms.Document;

            INSERT INTO dms.Document (DocumentId, DocumentUuid)
            VALUES
                (501, '22222222-8888-8888-8888-222222222222'),
                (509, '33333333-9999-9999-9999-333333333333'),
                (517, '44444444-aaaa-aaaa-aaaa-444444444444');

            INSERT INTO hydselected.School (DocumentId, SchoolId)
            VALUES (501, 910001), (509, 910002), (517, 910003);
            """
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
    public async Task It_returns_the_maximum_selected_document_id_for_a_cursor_page()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql),
            CreateCursorKeyset(pageSize: 2L),
            SqlDialect.Mssql,
            CancellationToken.None
        );

        result.HighestSelectedDocumentId.Should().Be(SecondDocumentId);
        result
            .DocumentMetadata.Select(static documentMetadata => documentMetadata.DocumentId)
            .Should()
            .Equal(FirstDocumentId, SecondDocumentId);
    }

    [Test]
    public async Task It_returns_no_maximum_for_a_zero_size_cursor_page()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql),
            CreateCursorKeyset(pageSize: 0L),
            SqlDialect.Mssql,
            CancellationToken.None
        );

        result.HighestSelectedDocumentId.Should().BeNull();
        result.DocumentMetadata.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_no_maximum_when_the_range_selects_nothing()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql),
            CreateCursorKeyset(pageSize: 25L, inclusiveMinimum: 600L, inclusiveMaximum: 700L),
            SqlDialect.Mssql,
            CancellationToken.None
        );

        result.HighestSelectedDocumentId.Should().BeNull();
        result.DocumentMetadata.Should().BeEmpty();
    }

    /// <summary>
    /// Deletes every selected row inside the hydration batch, between the materialization that
    /// selected them and the hydration selects that follow it. This is a deterministic stand-in for a
    /// delete that commits in that same window — not a separate concurrent transaction — and it is the
    /// case a body-derived boundary would answer wrongly by stalling the walk.
    /// </summary>
    [Test]
    public async Task It_returns_the_maximum_when_every_selected_row_was_deleted_before_hydration()
    {
        const string SpliceAfter = "SELECT [DocumentId] FROM page_ids;";
        const string DeleteEverySelectedRow = """

            DELETE FROM hydselected.SchoolAddressPeriod;
            DELETE FROM hydselected.SchoolAddress;
            DELETE FROM hydselected.School;
            DELETE FROM dms.Document;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var splicedBatches = new List<string>();

        var result = await HydrationExecutor.ExecuteAsync(
            batchSql =>
            {
                CountOccurrences(batchSql, SpliceAfter)
                    .Should()
                    .Be(
                        1,
                        "the materialization statement is the splice point, so it must appear exactly once"
                    );

                var splicedBatch = batchSql.Replace(
                    SpliceAfter,
                    SpliceAfter + DeleteEverySelectedRow,
                    StringComparison.Ordinal
                );
                splicedBatches.Add(splicedBatch);

                var command = connection.CreateCommand();
                command.CommandText = splicedBatch;
                return command;
            },
            HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Mssql),
            CreateCursorKeyset(pageSize: 25L),
            SqlDialect.Mssql,
            new HydrationExecutionOptions(),
            CancellationToken.None
        );

        splicedBatches.Should().ContainSingle();
        result.HighestSelectedDocumentId.Should().Be(ThirdDocumentId);
        result.DocumentMetadata.Should().BeEmpty();
        result.TableRowsInDependencyOrder.Should().OnlyContain(tableRows => tableRows.Rows.Count == 0);
    }

    private static PageKeysetSpec.Query CreateCursorKeyset(
        object pageSize,
        long inclusiveMinimum = 1L,
        long inclusiveMaximum = long.MaxValue
    ) =>
        new(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT TOP (@pageSize) DocumentId FROM hydselected.School
                WHERE DocumentId >= @cursorMin
                  AND DocumentId <= @cursorMax
                ORDER BY DocumentId
                """,
                TotalCountSql: null,
                PageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.CursorInclusiveMinimum, "cursorMin"),
                    new QuerySqlParameter(QuerySqlParameterRole.CursorInclusiveMaximum, "cursorMax"),
                    new QuerySqlParameter(QuerySqlParameterRole.PageSize, "pageSize"),
                ],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?>
            {
                ["cursorMin"] = inclusiveMinimum,
                ["cursorMax"] = inclusiveMaximum,
                ["pageSize"] = pageSize,
            }
        );

    private static int CountOccurrences(string text, string value)
    {
        var occurrences = 0;
        var searchIndex = text.IndexOf(value, StringComparison.Ordinal);

        while (searchIndex >= 0)
        {
            occurrences++;
            searchIndex = text.IndexOf(value, searchIndex + value.Length, StringComparison.Ordinal);
        }

        return occurrences;
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
