// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The reason a max-bearing change-version window is ordered by <c>ContentVersion</c> at all: the
/// database can seek the change-version index for the upper tail instead of walking the primary key and
/// discarding almost everything it reads.
/// </summary>
/// <remarks>
/// <para>
/// The SQL under measurement is compiled by the production page-keyset planners, not written here, so
/// what the plan describes is what a windowed first page really executes. No DDL is added: both indexes
/// are already emitted for every in-scope root, and this fixture is what shows the runtime predicate and
/// the emitted index actually meet.
/// </para>
/// <para>
/// A plan assertion is only meaningful at a volume where the planner has a choice, so the fixture seeds
/// enough rows for a dead-run scan to be the expensive option and asserts the tail really is a small
/// fraction of them before reading any plan. Below that volume PostgreSQL would sequentially scan a
/// small table whatever the SQL said, and the test would pass or fail for reasons that have nothing to
/// do with the predicate.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("WindowedPageQueryPlan")]
public class Given_A_Postgresql_Windowed_Page_Query_Plan
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    /// <summary>
    /// Enough rows that a full scan is plainly the expensive option at the tail size below, and few
    /// enough that seeding them through the emitted stamp triggers stays quick.
    /// </summary>
    private const int SeededRowCount = 2_000;

    /// <summary>
    /// The upper tail a first page of a freshly opened window reads. Small against the seeded volume,
    /// which is the whole point: the window names a fraction of the collection and the plan has to
    /// reach it without touching the rest.
    /// </summary>
    private const int TailRowCount = 50;

    private const int PageLimit = 25;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName DescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

    private static readonly CollectionPaging _paging = new CollectionPaging.Traditional(
        new PaginationParameters(Limit: PageLimit, Offset: 0, TotalCount: false, MaximumPageSize: 500)
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        await SeedSchoolsAsync();
        await SeedDescriptorsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            ANALYZE "dms"."Document";
            ANALYZE "dms"."Descriptor";
            ANALYZE "edfi"."School";
            """
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    /// <summary>
    /// A regular resource's windowed first page seeks <c>IX_&lt;Table&gt;_ContentVersion</c> and never
    /// reads the root through its primary key, which is the dead-run scan the ordering exists to avoid.
    /// </summary>
    [Test]
    public async Task It_seeks_the_content_version_index_for_a_regular_resource_upper_tail()
    {
        var window = await UpperTailWindowAsync("\"edfi\".\"School\"");
        var keyset = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql).Plan(
            _fixture.MappingSet.GetReadPlanOrThrow(SchoolResource).Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paging,
            changeVersionRange: window,
            orderingMode: PageOrderingMode.ContentVersion
        );

        var explained = await ExplainAsync(keyset);

        explained.Json.Should().Contain("IX_School_ContentVersion");
        explained
            .Json.Should()
            .NotContain(
                "PK_School",
                "reading the root through its primary key is the dead run the ContentVersion ordering "
                    + "exists to avoid"
            );
        AssertNoSequentialScan(explained.Plan, "School");
    }

    /// <summary>
    /// A descriptor's windowed first page seeks the composite descriptor index, whose leading column is
    /// the authoritative <c>ResourceKeyId</c> the descriptor page predicate filters on and whose second
    /// is the anchor it orders by.
    /// </summary>
    [Test]
    public async Task It_seeks_the_descriptor_content_version_index_for_an_upper_tail()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[DescriptorResource];
        var window = await UpperTailWindowAsync(
            "\"dms\".\"Descriptor\"",
            $"WHERE \"ResourceKeyId\" = {resourceKeyId}"
        );
        var keyset = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql).Plan(
            _fixture.MappingSet,
            DescriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paging,
            changeVersionRange: window,
            orderingMode: PageOrderingMode.ContentVersion
        );

        var explained = await ExplainAsync(keyset);

        explained.Json.Should().Contain("IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId");
        explained
            .Json.Should()
            .NotContain(
                "PK_Descriptor",
                "reading the descriptor root through its primary key is the dead run the ContentVersion "
                    + "ordering exists to avoid"
            );
        AssertNoSequentialScan(explained.Plan, "Descriptor");
    }

    /// <summary>
    /// The window a first page of a freshly opened change-version window reads: the top
    /// <see cref="TailRowCount" /> anchors of the seeded collection. Derived from the rows themselves
    /// rather than from the values the seed supplied, because the emitted stamp triggers are what decide
    /// a row's final change version.
    /// </summary>
    /// <remarks>
    /// The tail's share of the collection is asserted here rather than left implicit. A window that had
    /// come to hold most of the rows would make an index seek the wrong plan for the planner to choose,
    /// and the plan assertions would then be failing about the seed instead of about the predicate.
    /// </remarks>
    private async Task<ChangeVersionRange> UpperTailWindowAsync(
        string qualifiedTable,
        string whereClause = ""
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                MAX("ContentVersion") AS "Ceiling",
                COUNT(*) AS "TotalRows"
            FROM {qualifiedTable}
            {whereClause};
            """
        );

        long ceiling = Convert.ToInt64(rows[0]["Ceiling"], System.Globalization.CultureInfo.InvariantCulture);
        long totalRows = Convert.ToInt64(
            rows[0]["TotalRows"],
            System.Globalization.CultureInfo.InvariantCulture
        );

        totalRows
            .Should()
            .Be(SeededRowCount, $"the plan under measurement is only meaningful over the seeded volume");

        ChangeVersionRange window = new(ceiling - TailRowCount + 1, ceiling);

        var tailRows = await _database.QueryRowsAsync(
            $"""
            SELECT COUNT(*) AS "TailRows"
            FROM {qualifiedTable}
            {(whereClause.Length == 0 ? "WHERE" : $"{whereClause} AND")}
                "ContentVersion" >= @minChangeVersion AND "ContentVersion" <= @maxChangeVersion;
            """,
            new NpgsqlParameter("minChangeVersion", NpgsqlDbType.Bigint) { Value = window.MinChangeVersion! },
            new NpgsqlParameter("maxChangeVersion", NpgsqlDbType.Bigint) { Value = window.MaxChangeVersion! }
        );

        Convert
            .ToInt64(tailRows[0]["TailRows"], System.Globalization.CultureInfo.InvariantCulture)
            .Should()
            .BeLessThanOrEqualTo(
                TailRowCount,
                "the window has to name a small fraction of the collection for an index seek to be the "
                    + "plan the planner would choose"
            );

        return window;
    }

    /// <summary>
    /// Explains the compiled page-selection SQL with the parameter values the planner produced for it,
    /// so the plan describes the statement production would have executed rather than a rewrite of it.
    /// </summary>
    private async Task<ExplainedPagePlan> ExplainAsync(PageKeysetSpec.Query keyset)
    {
        NpgsqlParameter[] parameters =
        [
            .. keyset.ParameterValues.Select(static parameter => new NpgsqlParameter(
                parameter.Key,
                parameter.Value ?? DBNull.Value
            )),
        ];

        var rows = await _database.QueryRowsAsync(
            $"EXPLAIN (FORMAT JSON) {keyset.Plan.PageDocumentIdSql}",
            parameters
        );
        rows.Should().ContainSingle();

        string explainJson = rows[0]["QUERY PLAN"]?.ToString() ?? string.Empty;
        explainJson.Should().NotBeNullOrEmpty();

        // The evidence itself, captured so a plan change can be read rather than only reported as a
        // failed substring assertion.
        await TestContext.Out.WriteLineAsync(keyset.Plan.PageDocumentIdSql);
        await TestContext.Out.WriteLineAsync(explainJson);

        using var document = JsonDocument.Parse(explainJson);

        return new ExplainedPagePlan(document.RootElement[0].GetProperty("Plan").Clone(), explainJson);
    }

    /// <summary>
    /// No node reads the root relation sequentially. Asserted on the node types rather than on the plan
    /// text, so a relation named inside an unrelated property cannot satisfy or break it.
    /// </summary>
    private static void AssertNoSequentialScan(JsonElement plan, string relationName)
    {
        foreach (var path in PostgresqlQueryPlanNavigator.FindAllRelationScanPaths(plan, relationName))
        {
            PostgresqlQueryPlanNavigator
                .GetNodeType(path[^1])
                .Should()
                .NotBe(
                    "Seq Scan",
                    $"a windowed page must reach '{relationName}' through an index rather than reading "
                        + "the whole relation"
                );
        }
    }

    private async Task SeedSchoolsAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[SchoolResource];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            SELECT
                ('10000000-0000-0000-0000-' || lpad(series::text, 12, '0'))::uuid,
                @resourceKeyId
            FROM generate_series(1, @rowCount) AS series;

            INSERT INTO "edfi"."School" ("DocumentId", "ContentVersion", "NameOfInstitution", "SchoolId")
            SELECT
                source."DocumentId",
                source."ContentVersion",
                'Windowed Plan School ' || source."DocumentId",
                source."DocumentId"
            FROM "dms"."Document" AS source
            WHERE source."ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("rowCount", NpgsqlDbType.Integer) { Value = SeededRowCount }
        );
    }

    private async Task SeedDescriptorsAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[DescriptorResource];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            SELECT
                ('20000000-0000-0000-0000-' || lpad(series::text, 12, '0'))::uuid,
                @resourceKeyId
            FROM generate_series(1, @rowCount) AS series;

            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Discriminator",
                "Uri",
                "ContentVersion"
            )
            SELECT
                source."DocumentId",
                @resourceKeyId,
                'uri://ed-fi.org/AcademicSubjectDescriptor',
                'plan-' || source."DocumentId",
                'Windowed Plan Descriptor ' || source."DocumentId",
                'edfi.AcademicSubjectDescriptor',
                'uri://ed-fi.org/AcademicSubjectDescriptor#plan-' || source."DocumentId",
                source."ContentVersion"
            FROM "dms"."Document" AS source
            WHERE source."ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("rowCount", NpgsqlDbType.Integer) { Value = SeededRowCount }
        );
    }

    private sealed record ExplainedPagePlan(JsonElement Plan, string Json);
}
