// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="HydrationExecutor"/> against a real PostgreSQL database.
/// </summary>
/// <remarks>
/// These tests provision a temporary schema with <c>dms.Document</c> and test resource tables,
/// insert known data, execute hydration, and assert the returned row structure.
/// </remarks>
[TestFixture]
public class Given_A_Page_With_Multiple_Documents
{
    private NpgsqlDataSource _dataSource = null!;
    private HydratedPage _result = null!;

    private const string TestSchema = "hydtest";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        // Provision schemas
        await ExecuteSql(
            connection,
            """
            DROP SCHEMA IF EXISTS hydtest CASCADE;
            CREATE SCHEMA hydtest;
            CREATE SCHEMA IF NOT EXISTS dms;

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

            CREATE TABLE hydtest."School" (
                "DocumentId" bigint PRIMARY KEY,
                "SchoolId" integer NOT NULL
            );

            CREATE TABLE hydtest."SchoolAddress" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL REFERENCES hydtest."School"("DocumentId"),
                "Ordinal" integer NOT NULL,
                "City" varchar(100) NOT NULL
            );

            CREATE TABLE hydtest."SchoolAddressPeriod" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL,
                "ParentCollectionItemId" bigint NOT NULL REFERENCES hydtest."SchoolAddress"("CollectionItemId"),
                "Ordinal" integer NOT NULL,
                "BeginDate" varchar(10) NOT NULL
            );
            """
        );

        // Insert test data
        await ExecuteSql(
            connection,
            """
            DELETE FROM dms."Document" WHERE "DocumentId" IN (101, 102);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ContentVersion", "IdentityVersion")
            VALUES
                (101, 'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa', 10, 10),
                (102, 'bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb', 20, 20);

            INSERT INTO hydtest."School" ("DocumentId", "SchoolId")
            VALUES
                (101, 255901),
                (102, 255902);

            INSERT INTO hydtest."SchoolAddress" ("CollectionItemId", "School_DocumentId", "Ordinal", "City")
            VALUES
                (1001, 101, 0, 'Springfield'),
                (1002, 101, 1, 'Shelbyville'),
                (1003, 102, 0, 'Centerville');

            INSERT INTO hydtest."SchoolAddressPeriod" ("CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "BeginDate")
            VALUES
                (5001, 101, 1001, 0, '2020-01-01'),
                (5002, 101, 1001, 1, '2021-06-15'),
                (5003, 101, 1002, 0, '2022-09-01'),
                (5004, 102, 1003, 0, '2023-03-01');
            """
        );

        // Build read plan
        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Pgsql);

        // Execute hydration
        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT "DocumentId" FROM hydtest."School"
                ORDER BY "DocumentId"
                LIMIT @limit OFFSET @offset
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

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await ExecuteSql(
                connection,
                """
                DROP SCHEMA IF EXISTS hydtest CASCADE;
                DELETE FROM dms."Document" WHERE "DocumentId" IN (101, 102);
                """
            );
            await _dataSource.DisposeAsync();
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
public class Given_A_Single_DocumentId_Keyset
{
    private NpgsqlDataSource _dataSource = null!;
    private HydratedPage _result = null!;

    private const string TestSchema = "hydsingle";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await ExecuteSql(
            connection,
            """
            DROP SCHEMA IF EXISTS hydsingle CASCADE;
            CREATE SCHEMA hydsingle;
            CREATE SCHEMA IF NOT EXISTS dms;

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

            CREATE TABLE hydsingle."School" (
                "DocumentId" bigint PRIMARY KEY,
                "SchoolId" integer NOT NULL
            );

            CREATE TABLE hydsingle."SchoolAddress" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL REFERENCES hydsingle."School"("DocumentId"),
                "Ordinal" integer NOT NULL,
                "City" varchar(100) NOT NULL
            );

            CREATE TABLE hydsingle."SchoolAddressPeriod" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL,
                "ParentCollectionItemId" bigint NOT NULL REFERENCES hydsingle."SchoolAddress"("CollectionItemId"),
                "Ordinal" integer NOT NULL,
                "BeginDate" varchar(10) NOT NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            DELETE FROM dms."Document" WHERE "DocumentId" IN (201, 202);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid")
            VALUES
                (201, 'cccccccc-3333-3333-3333-cccccccccccc'),
                (202, 'dddddddd-4444-4444-4444-dddddddddddd');

            INSERT INTO hydsingle."School" ("DocumentId", "SchoolId")
            VALUES (201, 100001), (202, 100002);

            INSERT INTO hydsingle."SchoolAddress" ("CollectionItemId", "School_DocumentId", "Ordinal", "City")
            VALUES (2001, 201, 0, 'Alpha'), (2002, 202, 0, 'Beta');

            INSERT INTO hydsingle."SchoolAddressPeriod" ("CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "BeginDate")
            VALUES (6001, 201, 2001, 0, '2020-01-01'), (6002, 202, 2002, 0, '2023-03-01');
            """
        );

        // Build plan using the hydsingle schema
        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Pgsql);
        var keyset = new PageKeysetSpec.Single(201L);

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await ExecuteSql(
                connection,
                """
                DROP SCHEMA IF EXISTS hydsingle CASCADE;
                DELETE FROM dms."Document" WHERE "DocumentId" IN (201, 202);
                """
            );
            await _dataSource.DisposeAsync();
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
public class Given_A_Query_With_TotalCount_Requested
{
    private NpgsqlDataSource _dataSource = null!;
    private HydratedPage _result = null!;

    private const string TestSchema = "hydcount";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await ExecuteSql(
            connection,
            """
            DROP SCHEMA IF EXISTS hydcount CASCADE;
            CREATE SCHEMA hydcount;
            CREATE SCHEMA IF NOT EXISTS dms;

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

            CREATE TABLE hydcount."School" (
                "DocumentId" bigint PRIMARY KEY,
                "SchoolId" integer NOT NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            DELETE FROM dms."Document" WHERE "DocumentId" IN (301, 302, 303);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid")
            VALUES
                (301, 'eeeeeeee-5555-5555-5555-eeeeeeeeeeee'),
                (302, 'ffffffff-6666-6666-6666-ffffffffffff'),
                (303, '11111111-7777-7777-7777-111111111111');

            INSERT INTO hydcount."School" ("DocumentId", "SchoolId")
            VALUES (301, 900001), (302, 900002), (303, 900003);
            """
        );

        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Pgsql);

        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT "DocumentId" FROM hydcount."School"
                ORDER BY "DocumentId"
                LIMIT @limit OFFSET @offset
                """,
                TotalCountSql: "SELECT COUNT(1) FROM hydcount.\"School\"",
                PageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                TotalCountParametersInOrder: []
            ),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 2L }
        );

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await ExecuteSql(
                connection,
                """
                DROP SCHEMA IF EXISTS hydcount CASCADE;
                DELETE FROM dms."Document" WHERE "DocumentId" IN (301, 302, 303);
                """
            );
            await _dataSource.DisposeAsync();
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
