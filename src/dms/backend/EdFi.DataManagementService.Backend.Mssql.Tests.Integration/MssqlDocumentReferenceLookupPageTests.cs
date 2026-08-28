// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Page-level document-reference lookup coverage against a real SQL Server database, mirroring
/// <c>Given_A_Postgresql_Page_With_Multi_Column_And_Child_Document_References</c> document for
/// document so both providers are held to the same expectations.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Page_With_Multi_Column_And_Child_Document_References
{
    private const string TestSchema = "refpage";

    private string _databaseName = null!;
    private string _connectionString = null!;
    private ResourceReadPlan _plan = null!;
    private HydratedPage _result = null!;
    private IReadOnlyList<JsonNode> _reconstitutedPage = null!;

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

        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.MssqlProvisionSql(TestSchema));
        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.MssqlSeedSql(TestSchema));

        _plan = HydrationTestHelper.BuildStudentSectionAssociationReadPlan(TestSchema, SqlDialect.Mssql);

        await using var hydrationConnection = new SqlConnection(_connectionString);
        await hydrationConnection.OpenAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            _plan,
            MssqlDocumentReferenceLookupPageKeyset.ExcludingOffPageDocument(TestSchema),
            SqlDialect.Mssql,
            CancellationToken.None
        );

        _reconstitutedPage = DocumentReconstituter.ReconstitutePage(_plan, _result);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_hydrates_only_the_page_documents()
    {
        _result
            .DocumentMetadata.Select(static row => row.DocumentId)
            .Should()
            .Equal(DocumentReferenceLookupPageFixture.PageDocumentIdsInOrder);
    }

    [Test]
    public void It_returns_each_distinct_referenced_document_exactly_once_in_document_id_order()
    {
        _result.DocumentReferenceLookup.Should().NotBeNull();
        _result
            .DocumentReferenceLookup!.Rows.Select(static row => row.DocumentId)
            .Should()
            .Equal(
                DocumentReferenceLookupPageFixture.ExpectedReferencedDocumentIdsInOrder,
                "ids repeated across columns of one row, across rows, and across the root and child tables collapse to one row each"
            );
    }

    [Test]
    public void It_excludes_references_reachable_only_from_off_page_child_rows()
    {
        _result
            .DocumentReferenceLookup!.Rows.Select(static row => row.DocumentId)
            .Should()
            .NotContain(
                DocumentReferenceLookupPageFixture.OffPageProgram,
                "the child branch scopes through the child's root-scope locator, not its own key"
            );
    }

    [Test]
    public void It_returns_the_document_uuid_and_resource_key_for_each_referenced_document()
    {
        _result
            .DocumentReferenceLookup!.Rows.Select(static row =>
                (row.DocumentId, row.DocumentUuid, row.ResourceKeyId)
            )
            .Should()
            .Equal(
                (
                    DocumentReferenceLookupPageFixture.StudentA,
                    DocumentReferenceLookupPageFixture.UuidFor(DocumentReferenceLookupPageFixture.StudentA),
                    DocumentReferenceLookupPageFixture.StudentResourceKeyId
                ),
                (
                    DocumentReferenceLookupPageFixture.StudentB,
                    DocumentReferenceLookupPageFixture.UuidFor(DocumentReferenceLookupPageFixture.StudentB),
                    DocumentReferenceLookupPageFixture.StudentResourceKeyId
                ),
                (
                    DocumentReferenceLookupPageFixture.Section,
                    DocumentReferenceLookupPageFixture.UuidFor(DocumentReferenceLookupPageFixture.Section),
                    DocumentReferenceLookupPageFixture.SectionResourceKeyId
                ),
                (
                    DocumentReferenceLookupPageFixture.DualCreditEdOrg,
                    DocumentReferenceLookupPageFixture.UuidFor(
                        DocumentReferenceLookupPageFixture.DualCreditEdOrg
                    ),
                    DocumentReferenceLookupPageFixture.EducationOrganizationResourceKeyId
                ),
                (
                    DocumentReferenceLookupPageFixture.Program,
                    DocumentReferenceLookupPageFixture.UuidFor(DocumentReferenceLookupPageFixture.Program),
                    DocumentReferenceLookupPageFixture.ProgramResourceKeyId
                )
            );
    }

    [Test]
    public void It_resolves_the_descriptor_uri_for_the_page()
    {
        _result
            .DescriptorRowsInPlanOrder.Single()
            .Rows.Select(static row => (row.DescriptorId, row.Uri))
            .Should()
            .Equal(
                (
                    DocumentReferenceLookupPageFixture.AttemptStatusDescriptorId,
                    DocumentReferenceLookupPageFixture.AttemptStatusUri
                )
            );
    }

    [Test]
    public void It_reconstitutes_one_document_per_page_row()
    {
        _reconstitutedPage
            .Should()
            .HaveCount(DocumentReferenceLookupPageFixture.PageDocumentIdsInOrder.Length);
    }

    [Test]
    public void It_reconstitutes_the_whole_page_exactly_with_no_link_objects()
    {
        DocumentReferenceLookupPageFixture.AssertNonLinkPageMatchesExactly(_reconstitutedPage);
    }

    [Test]
    public void It_reconstitutes_a_fully_populated_document_with_every_reference_and_descriptor()
    {
        var document = _reconstitutedPage[0];

        document["attemptStatusDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(DocumentReferenceLookupPageFixture.AttemptStatusUri);
        document["studentReference"]!["studentUniqueId"]!.GetValue<string>().Should().Be("S-701");
        document["sectionReference"]!["sectionIdentifier"]!.GetValue<string>().Should().Be("SEC-X");
        document["dualCreditEducationOrganizationReference"]!["educationOrganizationId"]!
            .GetValue<long>()
            .Should()
            .Be(255901);

        var programs = document["programs"]!.AsArray();
        programs.Should().HaveCount(2);
        programs[0]!["programReference"]!["programName"]!.GetValue<string>().Should().Be("Program P");
        programs[1]!["programReference"]!["programName"]!
            .GetValue<string>()
            .Should()
            .Be("Program Named Like Student");
    }

    [Test]
    public void It_reconstitutes_a_document_whose_optional_reference_is_null_but_others_are_populated()
    {
        var document = _reconstitutedPage[1];

        document["studentReference"]!["studentUniqueId"]!.GetValue<string>().Should().Be("S-702");
        document["sectionReference"]!["sectionIdentifier"]!.GetValue<string>().Should().Be("SEC-X");
        document["dualCreditEducationOrganizationReference"]
            .Should()
            .BeNull("a null reference column must not produce a partial reference object");
        document["attemptStatusDescriptor"].Should().BeNull();
    }

    [Test]
    public void It_omits_every_reference_object_for_a_document_whose_references_are_all_null()
    {
        var document = _reconstitutedPage[2];

        document["studentReference"].Should().BeNull();
        document["sectionReference"].Should().BeNull();
        document["dualCreditEducationOrganizationReference"].Should().BeNull();
        document["attemptStatusDescriptor"].Should().BeNull();
        document["programs"].Should().BeNull();
    }

    [Test]
    public void It_reconstitutes_a_document_that_repeats_one_referenced_document_across_columns()
    {
        var document = _reconstitutedPage[3];

        document["studentReference"]!["studentUniqueId"]!.GetValue<string>().Should().Be("S-730");
        document["sectionReference"]!["sectionIdentifier"]!.GetValue<string>().Should().Be("SEC-DUP");
        document["dualCreditEducationOrganizationReference"]!["educationOrganizationId"]!
            .GetValue<long>()
            .Should()
            .Be(255902);
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Confirms an empty page produces no document-reference lookup rows on SQL Server rather than
/// failing or leaking references belonging to documents outside the page.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Empty_Page_With_Document_References
{
    private const string TestSchema = "refpageempty";

    private string _databaseName = null!;
    private HydratedPage _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("MSSQL connection string not configured.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.MssqlProvisionSql(TestSchema));
        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.MssqlSeedSql(TestSchema));

        var plan = HydrationTestHelper.BuildStudentSectionAssociationReadPlan(TestSchema, SqlDialect.Mssql);

        await using var hydrationConnection = new SqlConnection(connectionString);
        await hydrationConnection.OpenAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            MssqlDocumentReferenceLookupPageKeyset.Empty(TestSchema),
            SqlDialect.Mssql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_no_documents()
    {
        _result.DocumentMetadata.Should().BeEmpty();
    }

    [Test]
    public void It_returns_an_empty_document_reference_lookup_result_set()
    {
        _result.DocumentReferenceLookup.Should().NotBeNull();
        _result.DocumentReferenceLookup!.Rows.Should().BeEmpty();
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Confirms a resource carrying descriptor edges but no document references compiles without a
/// document-reference lookup on SQL Server, so the hydration batch emits the descriptor projection
/// result set and no lookup result set at all.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Descriptor_Only_Resource_Page
{
    private const string TestSchema = "refpagedesc";
    private const long GradeLevelDescriptorId = 810;
    private const string GradeLevelUri = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";

    private string _databaseName = null!;
    private ResourceReadPlan _plan = null!;
    private HydratedPage _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("MSSQL connection string not configured.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await ExecuteSql(
            connection,
            $"""
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{TestSchema}') EXEC('CREATE SCHEMA [{TestSchema}]');

            CREATE TABLE dms.[Document] (
                [DocumentId] bigint PRIMARY KEY,
                [DocumentUuid] uniqueidentifier NOT NULL,
                [ResourceKeyId] smallint NOT NULL DEFAULT 0,
                [ContentVersion] bigint NOT NULL DEFAULT 1,
                [ContentLastModifiedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                [CreatedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE dms.[Descriptor] (
                [DocumentId] bigint PRIMARY KEY,
                [Namespace] varchar(255) NOT NULL DEFAULT '',
                [CodeValue] varchar(50) NOT NULL DEFAULT '',
                [ShortDescription] varchar(75) NOT NULL DEFAULT '',
                [Description] varchar(1024) NULL,
                [EffectiveBeginDate] date NULL,
                [EffectiveEndDate] date NULL,
                [Discriminator] varchar(128) NOT NULL DEFAULT '',
                [Uri] varchar(306) NOT NULL
            );

            CREATE TABLE {TestSchema}.[DescriptorOnly] (
                [DocumentId] bigint PRIMARY KEY,
                [GradeLevelDescriptor_DescriptorId] bigint NULL
            );
            """
        );

        await ExecuteSql(
            connection,
            $"""
            INSERT INTO dms.[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId])
            VALUES
                (901, '00000000-0000-0000-0000-000000000901', 30),
                (902, '00000000-0000-0000-0000-000000000902', 30);

            INSERT INTO dms.[Descriptor] ([DocumentId], [Uri])
            VALUES ({GradeLevelDescriptorId}, '{GradeLevelUri}');

            INSERT INTO {TestSchema}.[DescriptorOnly] ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES (901, {GradeLevelDescriptorId}), (902, NULL);
            """
        );

        _plan = HydrationTestHelper.BuildDescriptorOnlyReadPlan(TestSchema, SqlDialect.Mssql);

        await using var hydrationConnection = new SqlConnection(connectionString);
        await hydrationConnection.OpenAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            _plan,
            MssqlDocumentReferenceLookupPageKeyset.AllDescriptorOnlyRows(TestSchema),
            SqlDialect.Mssql,
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_compiles_no_document_reference_lookup_plan()
    {
        _plan.DocumentReferenceLookup.Should().BeNull();
    }

    [Test]
    public void It_returns_no_document_reference_lookup_result_set()
    {
        _result.DocumentReferenceLookup.Should().BeNull();
    }

    [Test]
    public void It_still_resolves_descriptor_uris_for_the_page()
    {
        _result.DocumentMetadata.Should().HaveCount(2);
        _result
            .DescriptorRowsInPlanOrder.Single()
            .Rows.Select(static row => (row.DescriptorId, row.Uri))
            .Should()
            .Equal((GradeLevelDescriptorId, GradeLevelUri));
    }

    private static async Task ExecuteSql(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

internal static class MssqlDocumentReferenceLookupPageKeyset
{
    public static PageKeysetSpec ExcludingOffPageDocument(string schema) =>
        Create(
            $"""
            SELECT [DocumentId] FROM {schema}.[StudentSectionAssociation]
            WHERE [DocumentId] <> {DocumentReferenceLookupPageFixture.OffPageDocumentId}
            ORDER BY [DocumentId]
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
            """,
            limit: 25L
        );

    public static PageKeysetSpec Empty(string schema) =>
        Create(
            $"""
            SELECT [DocumentId] FROM {schema}.[StudentSectionAssociation]
            ORDER BY [DocumentId]
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
            """,
            limit: 0L
        );

    public static PageKeysetSpec AllDescriptorOnlyRows(string schema) =>
        Create(
            $"""
            SELECT [DocumentId] FROM {schema}.[DescriptorOnly]
            ORDER BY [DocumentId]
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
            """,
            limit: 25L
        );

    private static PageKeysetSpec Create(string pageDocumentIdSql, long limit) =>
        new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: pageDocumentIdSql,
                TotalCountSql: null,
                PageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = limit },
            PageOrderingMode.DocumentId
        );
}
