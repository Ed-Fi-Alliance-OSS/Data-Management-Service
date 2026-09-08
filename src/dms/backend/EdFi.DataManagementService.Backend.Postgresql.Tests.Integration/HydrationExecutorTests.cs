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
[NonParallelizable]
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
[NonParallelizable]
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
[NonParallelizable]
public class Given_HydrationExecutor_Single_Document_Fast_Path_With_DescriptorProjection_And_DocumentReferenceLookup
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

    private NpgsqlDataSource _dataSource = null!;
    private ResourceReadPlan _plan = null!;
    private HydratedPage _keysetResult = null!;
    private HydratedPage _fastPathResult = null!;
    private string _fastPathBatchSql = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await ExecuteSql(
            connection,
            """
            DROP SCHEMA IF EXISTS hydfastpath CASCADE;
            CREATE SCHEMA hydfastpath;
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

            CREATE TABLE hydfastpath."StudentSchoolAssociation" (
                "DocumentId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NULL,
                "School_SchoolId" bigint NULL,
                "EntryGradeLevelDescriptor_DescriptorId" bigint NULL
            );

            CREATE TABLE hydfastpath."StudentSchoolAssociationProgram" (
                "CollectionItemId" bigint PRIMARY KEY,
                "StudentSchoolAssociation_DocumentId" bigint NOT NULL REFERENCES hydfastpath."StudentSchoolAssociation"("DocumentId"),
                "Ordinal" integer NOT NULL,
                "Program_DocumentId" bigint NULL,
                "Program_ProgramName" varchar(100) NULL,
                "ProgramTypeDescriptor_DescriptorId" bigint NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            DELETE FROM dms."Descriptor" WHERE "DocumentId" IN (12001, 12002, 12003);
            DELETE FROM dms."Document" WHERE "DocumentId" IN (10001, 10002, 11001, 11002, 11003, 12001, 12002, 12003);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "IdentityVersion")
            VALUES
                (10001, '00000000-0000-0000-0000-000000010001', 1, 11, 11),
                (10002, '00000000-0000-0000-0000-000000010002', 1, 12, 12),
                (11001, '00000000-0000-0000-0000-000000011001', 2, 1, 1),
                (11002, '00000000-0000-0000-0000-000000011002', 3, 1, 1),
                (11003, '00000000-0000-0000-0000-000000011003', 4, 1, 1),
                (12001, '00000000-0000-0000-0000-000000012001', 5, 1, 1),
                (12002, '00000000-0000-0000-0000-000000012002', 6, 1, 1),
                (12003, '00000000-0000-0000-0000-000000012003', 7, 1, 1);

            INSERT INTO dms."Descriptor" ("DocumentId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri")
            VALUES
                (12001, 'uri://ed-fi.org/GradeLevelDescriptor', 'Ninth grade', 'Ninth grade', 'edfi.GradeLevelDescriptor', 'uri://ed-fi.org/GradeLevelDescriptor#Ninth grade'),
                (12002, 'uri://ed-fi.org/ProgramTypeDescriptor', 'Gifted', 'Gifted', 'edfi.ProgramTypeDescriptor', 'uri://ed-fi.org/ProgramTypeDescriptor#Gifted'),
                (12003, 'uri://ed-fi.org/GradeLevelDescriptor', 'Tenth grade', 'Tenth grade', 'edfi.GradeLevelDescriptor', 'uri://ed-fi.org/GradeLevelDescriptor#Tenth grade');

            INSERT INTO hydfastpath."StudentSchoolAssociation"
                ("DocumentId", "School_DocumentId", "School_SchoolId", "EntryGradeLevelDescriptor_DescriptorId")
            VALUES
                (10001, 11001, 255901, 12001),
                (10002, 11003, 255902, 12003);

            INSERT INTO hydfastpath."StudentSchoolAssociationProgram"
                ("CollectionItemId", "StudentSchoolAssociation_DocumentId", "Ordinal", "Program_DocumentId", "Program_ProgramName", "ProgramTypeDescriptor_DescriptorId")
            VALUES
                (20001, 10001, 0, 11002, 'Gifted', 12002),
                (20002, 10001, 1, NULL, NULL, NULL),
                (20003, 10002, 0, 11003, 'Other', 12003);
            """
        );

        _plan = BuildReadPlan();
        _fastPathBatchSql = HydrationBatchBuilder.Build(_plan, _keyset, SqlDialect.Pgsql, _fastPathOptions);

        await using var keysetConnection = await _dataSource.OpenConnectionAsync();
        _keysetResult = await HydrationExecutor.ExecuteAsync(
            keysetConnection,
            _plan,
            _keyset,
            SqlDialect.Pgsql,
            _keysetOptions,
            CancellationToken.None
        );

        await using var fastPathConnection = await _dataSource.OpenConnectionAsync();
        _fastPathResult = await HydrationExecutor.ExecuteAsync(
            fastPathConnection,
            _plan,
            _keyset,
            SqlDialect.Pgsql,
            _fastPathOptions,
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
                DROP SCHEMA IF EXISTS hydfastpath CASCADE;
                DELETE FROM dms."Descriptor" WHERE "DocumentId" IN (12001, 12002, 12003);
                DELETE FROM dms."Document" WHERE "DocumentId" IN (10001, 10002, 11001, 11002, 11003, 12001, 12002, 12003);
                """
            );
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_generates_a_batch_without_the_page_temp_table()
    {
        _fastPathBatchSql.Should().NotContain("\"page\"");
        _fastPathBatchSql.Should().NotContain("CREATE TEMP TABLE");
        _fastPathBatchSql.Should().NotContain("DROP TABLE");
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

        return new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
[NonParallelizable]
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

            CREATE TABLE hydcount."SchoolAddress" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL REFERENCES hydcount."School"("DocumentId"),
                "Ordinal" integer NOT NULL,
                "City" varchar(100) NOT NULL
            );

            CREATE TABLE hydcount."SchoolAddressPeriod" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL,
                "ParentCollectionItemId" bigint NOT NULL REFERENCES hydcount."SchoolAddress"("CollectionItemId"),
                "Ordinal" integer NOT NULL,
                "BeginDate" varchar(10) NOT NULL
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

[TestFixture]
[NonParallelizable]
public class Given_A_Reference_Bearing_Resource
{
    private NpgsqlDataSource _dataSource = null!;
    private HydratedPage _result = null!;
    private ResourceReadPlan _plan = null!;

    private const string TestSchema = "hydref";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await ExecuteSql(
            connection,
            """
            DROP SCHEMA IF EXISTS hydref CASCADE;
            CREATE SCHEMA hydref;
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

            CREATE TABLE hydref."StudentSchoolAssociation" (
                "DocumentId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NULL,
                "School_SchoolId" bigint NULL,
                "Calendar_DocumentId" bigint NULL,
                "Calendar_CalendarCode" varchar(60) NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            DELETE FROM dms."Document" WHERE "DocumentId" IN (401, 402, 403);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid")
            VALUES
                (401, 'aaaa0001-0001-0001-0001-aaaa00000001'),
                (402, 'aaaa0002-0002-0002-0002-aaaa00000002'),
                (403, 'aaaa0003-0003-0003-0003-aaaa00000003');

            INSERT INTO hydref."StudentSchoolAssociation" ("DocumentId", "School_DocumentId", "School_SchoolId", "Calendar_DocumentId", "Calendar_CalendarCode")
            VALUES
                (401, 10, 255901, 50, 'CAL-101'),
                (402, NULL, NULL, NULL, NULL),
                (403, 20, 255902, 60, 'CAL-202');
            """
        );

        _plan = HydrationTestHelper.BuildStudentSchoolAssociationReadPlan(TestSchema, SqlDialect.Pgsql);

        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: """
                SELECT "DocumentId" FROM hydref."StudentSchoolAssociation"
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
            _plan,
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
                DROP SCHEMA IF EXISTS hydref CASCADE;
                DELETE FROM dms."Document" WHERE "DocumentId" IN (401, 402, 403);
                """
            );
            await _dataSource.DisposeAsync();
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

        rootRows.Rows[0][1].Should().NotBeNull();
        ((long)rootRows.Rows[0][1]!).Should().Be(10);
        ((long)rootRows.Rows[0][2]!).Should().Be(255901);
        ((long)rootRows.Rows[0][3]!).Should().Be(50);
        ((string)rootRows.Rows[0][4]!).Should().Be("CAL-101");

        rootRows.Rows[1][1].Should().BeNull();
        rootRows.Rows[1][2].Should().BeNull();
        rootRows.Rows[1][3].Should().BeNull();
        rootRows.Rows[1][4].Should().BeNull();

        ((long)rootRows.Rows[2][1]!).Should().Be(20);
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_Two_Postgresql_Hydration_Batches_On_The_Same_Transaction
{
    private NpgsqlDataSource _dataSource = null!;
    private HydratedPage _firstResult = null!;
    private HydratedPage _secondResult = null!;
    private bool _tempTableExistsBeforeSecondHydration;

    private const string TestSchema = "hydreuse";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await ExecuteSql(
            connection,
            """
            DROP SCHEMA IF EXISTS hydreuse CASCADE;
            CREATE SCHEMA hydreuse;
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

            CREATE TABLE hydreuse."School" (
                "DocumentId" bigint PRIMARY KEY,
                "SchoolId" integer NOT NULL
            );

            CREATE TABLE hydreuse."SchoolAddress" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL REFERENCES hydreuse."School"("DocumentId"),
                "Ordinal" integer NOT NULL,
                "City" varchar(100) NOT NULL
            );

            CREATE TABLE hydreuse."SchoolAddressPeriod" (
                "CollectionItemId" bigint PRIMARY KEY,
                "School_DocumentId" bigint NOT NULL,
                "ParentCollectionItemId" bigint NOT NULL REFERENCES hydreuse."SchoolAddress"("CollectionItemId"),
                "Ordinal" integer NOT NULL,
                "BeginDate" varchar(10) NOT NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            """
            DELETE FROM dms."Document" WHERE "DocumentId" IN (401, 402);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid")
            VALUES
                (401, '12121212-1111-1111-1111-121212121212'),
                (402, '34343434-2222-2222-2222-343434343434');

            INSERT INTO hydreuse."School" ("DocumentId", "SchoolId")
            VALUES (401, 700001), (402, 700002);

            INSERT INTO hydreuse."SchoolAddress" ("CollectionItemId", "School_DocumentId", "Ordinal", "City")
            VALUES (4001, 401, 0, 'Gamma'), (4002, 402, 0, 'Delta');

            INSERT INTO hydreuse."SchoolAddressPeriod" ("CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "BeginDate")
            VALUES (8001, 401, 4001, 0, '2024-01-01'), (8002, 402, 4002, 0, '2024-02-01');
            """
        );

        var plan = HydrationTestHelper.BuildSchoolReadPlan(TestSchema, SqlDialect.Pgsql);

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await hydrationConnection.BeginTransactionAsync();

        _firstResult = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            new PageKeysetSpec.Single(401L),
            SqlDialect.Pgsql,
            transaction,
            CancellationToken.None
        );

        _tempTableExistsBeforeSecondHydration = await TempTableExistsAsync(hydrationConnection, transaction);

        _secondResult = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            new PageKeysetSpec.Single(402L),
            SqlDialect.Pgsql,
            transaction,
            CancellationToken.None
        );

        await transaction.RollbackAsync();
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
                DROP SCHEMA IF EXISTS hydreuse CASCADE;
                DELETE FROM dms."Document" WHERE "DocumentId" IN (401, 402);
                """
            );
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_keeps_the_pgsql_temp_table_alive_until_commit_between_hydration_calls()
    {
        _tempTableExistsBeforeSecondHydration.Should().BeTrue();
    }

    [Test]
    public void It_allows_the_second_hydration_batch_to_reuse_the_same_transaction()
    {
        _firstResult.DocumentMetadata.Should().ContainSingle();
        _firstResult.DocumentMetadata[0].DocumentId.Should().Be(401L);
        _secondResult.DocumentMetadata.Should().ContainSingle();
        _secondResult.DocumentMetadata[0].DocumentId.Should().Be(402L);
        _secondResult.TableRowsInDependencyOrder.Should().HaveCount(3);
        ((long)_secondResult.TableRowsInDependencyOrder[0].Rows[0][0]!).Should().Be(402L);
        ((int)_secondResult.TableRowsInDependencyOrder[0].Rows[0][1]!).Should().Be(700002);
    }

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TempTableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.oid = pg_my_temp_schema()
                  AND c.relname = 'page'
            );
            """,
            connection,
            transaction
        );

        var result = await command.ExecuteScalarAsync();
        return result is true;
    }
}
