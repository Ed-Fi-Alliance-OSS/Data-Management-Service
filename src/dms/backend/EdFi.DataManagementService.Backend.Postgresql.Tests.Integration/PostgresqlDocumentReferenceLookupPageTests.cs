// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Page-level document-reference lookup coverage against a real PostgreSQL database, using a
/// resource shaped like StudentSectionAssociation: a root table carrying three reference columns
/// plus a descriptor edge, and a child collection carrying a fourth reference. That shape exercises
/// both branch forms of the lookup at once — the root is scanned once with its columns expanded
/// inline, the child keeps the plain single-column projection.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Postgresql_Page_With_Multi_Column_And_Child_Document_References
{
    private const string TestSchema = "refpage";

    private NpgsqlDataSource _dataSource = null!;
    private ResourceReadPlan _plan = null!;
    private HydratedPage _result = null!;
    private IReadOnlyList<JsonNode> _reconstitutedPage = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.PostgresqlProvisionSql(TestSchema));
        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.PostgresqlSeedSql(TestSchema));

        _plan = HydrationTestHelper.BuildStudentSectionAssociationReadPlan(TestSchema, SqlDialect.Pgsql);

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            _plan,
            PostgresqlDocumentReferenceLookupPageKeyset.ExcludingOffPageDocument(TestSchema),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        _reconstitutedPage = DocumentReconstituter.ReconstitutePage(_plan, _result);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await ExecuteSql(connection, DocumentReferenceLookupPageFixture.PostgresqlCleanupSql(TestSchema));
            await _dataSource.DisposeAsync();
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Confirms an empty page produces no document-reference lookup rows rather than failing or
/// leaking references belonging to documents outside the page.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Postgresql_Empty_Page_With_Document_References
{
    private const string TestSchema = "refpageempty";

    private NpgsqlDataSource _dataSource = null!;
    private HydratedPage _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.PostgresqlProvisionSql(TestSchema));
        await ExecuteSql(connection, DocumentReferenceLookupPageFixture.PostgresqlSeedSql(TestSchema));

        var plan = HydrationTestHelper.BuildStudentSectionAssociationReadPlan(TestSchema, SqlDialect.Pgsql);

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            plan,
            PostgresqlDocumentReferenceLookupPageKeyset.Empty(TestSchema),
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
            await ExecuteSql(connection, DocumentReferenceLookupPageFixture.PostgresqlCleanupSql(TestSchema));
            await _dataSource.DisposeAsync();
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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Confirms a resource carrying descriptor edges but no document references compiles without a
/// document-reference lookup, so the hydration batch emits the descriptor projection result set and
/// no lookup result set at all.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Postgresql_Descriptor_Only_Resource_Page
{
    private const string TestSchema = "refpagedesc";
    private const long GradeLevelDescriptorId = 810;
    private const string GradeLevelUri = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";

    private NpgsqlDataSource _dataSource = null!;
    private ResourceReadPlan _plan = null!;
    private HydratedPage _result = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        await ExecuteSql(
            connection,
            $"""
            DROP SCHEMA IF EXISTS {TestSchema} CASCADE;
            CREATE SCHEMA {TestSchema};
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

            CREATE TABLE {TestSchema}."DescriptorOnly" (
                "DocumentId" bigint PRIMARY KEY,
                "GradeLevelDescriptor_DescriptorId" bigint NULL
            );
            """
        );

        await ExecuteSql(connection, CleanupRowsSql);
        await ExecuteSql(
            connection,
            $"""
            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId")
            VALUES
                (901, '00000000-0000-0000-0000-000000000901', 30),
                (902, '00000000-0000-0000-0000-000000000902', 30);

            INSERT INTO dms."Descriptor" ("DocumentId", "Uri")
            VALUES ({GradeLevelDescriptorId}, '{GradeLevelUri}');

            INSERT INTO {TestSchema}."DescriptorOnly" ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES (901, {GradeLevelDescriptorId}), (902, NULL);
            """
        );

        _plan = HydrationTestHelper.BuildDescriptorOnlyReadPlan(TestSchema, SqlDialect.Pgsql);

        await using var hydrationConnection = await _dataSource.OpenConnectionAsync();
        _result = await HydrationExecutor.ExecuteAsync(
            hydrationConnection,
            _plan,
            new PageKeysetSpec.Query(
                new PageDocumentIdSqlPlan(
                    PageDocumentIdSql: $"""
                    SELECT "DocumentId" FROM {TestSchema}."DescriptorOnly"
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
            ),
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
            await ExecuteSql(connection, $"DROP SCHEMA IF EXISTS {TestSchema} CASCADE;");
            await ExecuteSql(connection, CleanupRowsSql);
            await _dataSource.DisposeAsync();
        }
    }

    private static string CleanupRowsSql =>
        $"""
            DELETE FROM dms."Document" WHERE "DocumentId" IN (901, 902);
            DELETE FROM dms."Descriptor" WHERE "DocumentId" = {GradeLevelDescriptorId};
            """;

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

    private static async Task ExecuteSql(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

internal static class PostgresqlDocumentReferenceLookupPageKeyset
{
    public static PageKeysetSpec ExcludingOffPageDocument(string schema) =>
        Create(
            $"""
            SELECT "DocumentId" FROM {schema}."StudentSectionAssociation"
            WHERE "DocumentId" <> {DocumentReferenceLookupPageFixture.OffPageDocumentId}
            ORDER BY "DocumentId"
            LIMIT @limit OFFSET @offset
            """,
            limit: 25L
        );

    public static PageKeysetSpec Empty(string schema) =>
        Create(
            $"""
            SELECT "DocumentId" FROM {schema}."StudentSectionAssociation"
            ORDER BY "DocumentId"
            LIMIT @limit OFFSET @offset
            """,
            limit: 0L
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
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = limit }
        );
}
