// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
/// PostgreSQL evidence that the ownership page predicate against <c>CreatedByOwnershipTokenId</c> reaches
/// <c>dms.Document</c> through the partial index rather than by scanning the relation.
/// </summary>
/// <remarks>
/// <para>
/// The SQL under measurement is compiled by the production page-keyset planner, not written here, so
/// what the plan describes is what an ownership-filtered first page really executes. No DDL is added:
/// the partial index is already emitted for every in-scope database, and this fixture is what shows the
/// runtime predicate and the emitted index actually meet. Index choice is a planner decision made per
/// engine, so evidence from SQL Server says nothing about PostgreSQL.
/// </para>
/// <para>
/// A plan assertion is only meaningful at a volume where the planner has a choice, so the fixture seeds
/// enough rows for a sequential scan to be the expensive option and asserts the queried token's share
/// really is a small fraction of them before reading any plan.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("OwnershipTokenQueryPlan")]
public class Given_A_Postgresql_Ownership_Token_Page_Query_Plan
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    /// <summary>
    /// Enough rows that a sequential scan is plainly the expensive option against the queried token's
    /// share below, and few enough that seeding them through the emitted stamp triggers stays quick.
    /// </summary>
    private const int SeededRowCount = 2_000;

    /// <summary>
    /// The rows the seed gives the queried token. Small against the seeded volume, which is the whole
    /// point: the queried token names a fraction of the collection and the plan has to reach it without
    /// touching the rest.
    /// </summary>
    private const int MatchingTokenRowCount = 50;

    /// <summary>The ownership token the compiled page predicate filters on.</summary>
    private const short QueriedOwnershipTokenId = 1;

    private const int PageLimit = 25;

    private const string OwnershipTokenIndexName = "IX_Document_CreatedByOwnershipTokenId";

    /// <summary>
    /// The partial-index predicate as the catalog renders it, the same text the provisioned-schema
    /// manifest golden pins.
    /// </summary>
    private const string OwnershipTokenIndexPredicate = "(\"CreatedByOwnershipTokenId\" IS NOT NULL)";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

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

        await _database.ExecuteNonQueryAsync(
            """
            ANALYZE "dms"."Document";
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
    /// The compiled ownership page predicate - <c>IS NOT NULL AND = ANY(...)</c> against
    /// <c>CreatedByOwnershipTokenId</c> - reaches <c>dms.Document</c> through the partial index rather than
    /// by a sequential scan, at a volume where the planner has an actual choice to make.
    /// </summary>
    [Test]
    public async Task It_reads_the_ownership_token_index_for_the_page_predicate()
    {
        await AssertIndexIsPartialAsync();
        await AssertSeededVolumeAsync();

        var readPlan = _fixture.MappingSet.GetReadPlanOrThrow(SchoolResource);
        var keyset = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql).Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paging,
            authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                OwnershipTokenParameterization: OwnershipTokenParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    [QueriedOwnershipTokenId],
                    "ownershipTokenIds"
                )
            )
        );

        JsonElement plan = await ExplainAsync(keyset);

        DocumentAccess documentAccess = SoleDocumentAccess(plan);

        documentAccess
            .NodeType.Should()
            .NotBe(
                "Seq Scan",
                "the ownership predicate is an equality membership test against the partial index's key "
                    + "column, so the matching rows are reached through the index rather than by reading "
                    + "the whole relation"
            );
        documentAccess
            .IndexName.Should()
            .Be(
                OwnershipTokenIndexName,
                "the access has to land on the index this change filters rather than on any other index "
                    + "that could serve the predicate"
            );
    }

    /// <summary>
    /// The one plan node that scans <c>dms.Document</c>, described by how it reached the relation.
    /// </summary>
    /// <remarks>
    /// Read structurally rather than by substring because the plan also reaches the root table for the
    /// join, so the node type and the index name have to come off the same scan. A plain index scan names
    /// its index on the scan node itself; a bitmap heap scan names it on the bitmap index scan beneath it,
    /// which is the shape PostgreSQL picks for an array membership test over a small fraction of the rows.
    /// </remarks>
    private static DocumentAccess SoleDocumentAccess(JsonElement plan)
    {
        var scanPath = PostgresqlQueryPlanNavigator.FindRelationScanPath(plan, "Document");
        JsonElement scan = scanPath[^1];
        string nodeType = PostgresqlQueryPlanNavigator.GetNodeType(scan);

        if (scan.TryGetProperty("Index Name", out var indexName))
        {
            return new DocumentAccess(nodeType, indexName.GetString());
        }

        if (nodeType == "Bitmap Heap Scan" && scan.TryGetProperty("Plans", out var children))
        {
            var bitmapIndexScans = children
                .EnumerateArray()
                .Where(child => PostgresqlQueryPlanNavigator.GetNodeType(child) == "Bitmap Index Scan")
                .ToList();

            bitmapIndexScans
                .Should()
                .ContainSingle(
                    "a bitmap heap scan over one predicate reads one bitmap, so one bitmap index scan "
                        + "names the index that served it"
                );

            return new DocumentAccess(
                nodeType,
                bitmapIndexScans[0].TryGetProperty("Index Name", out var bitmapIndexName)
                    ? bitmapIndexName.GetString()
                    : null
            );
        }

        return new DocumentAccess(nodeType, null);
    }

    /// <summary>How the plan reached <c>dms.Document</c>.</summary>
    private sealed record DocumentAccess(string NodeType, string? IndexName);

    /// <summary>
    /// The index the plan is about to be read against really is the partial one. An index of the same
    /// name carrying every row would be read just as well and would say nothing about the filter this
    /// change adds, and PostgreSQL's plan output does not show the predicate, so it is read from the
    /// catalog instead, the way the provisioned-schema manifest does.
    /// </summary>
    private async Task AssertIndexIsPartialAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT pg_get_expr(ix.indpred, ix.indrelid) AS "Filter"
            FROM pg_catalog.pg_index ix
            JOIN pg_catalog.pg_class i ON i.oid = ix.indexrelid
            WHERE i.relname = @indexName;
            """,
            new NpgsqlParameter("indexName", NpgsqlDbType.Text) { Value = OwnershipTokenIndexName }
        );

        rows.Should().ContainSingle("the emitted DDL creates this index exactly once");
        rows[0]
            ["Filter"]
            .Should()
            .Be(
                OwnershipTokenIndexPredicate,
                "the plan assertion below is only evidence about the filtered index if the index it names "
                    + "actually carries the filter"
            );
    }

    /// <summary>
    /// The seeded collection's shape, asserted here rather than left implicit. A queried-token share that
    /// had come to hold most of the rows would make an index read the wrong plan for the planner to
    /// choose, and the plan assertion above would then be failing about the seed instead of about the
    /// predicate.
    /// </summary>
    private async Task AssertSeededVolumeAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[SchoolResource];

        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                COUNT(*) AS "TotalRows",
                COUNT(*) FILTER (WHERE "CreatedByOwnershipTokenId" = @queriedOwnershipTokenId)
                    AS "MatchingRows"
            FROM "dms"."Document"
            WHERE "ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("queriedOwnershipTokenId", NpgsqlDbType.Smallint)
            {
                Value = QueriedOwnershipTokenId,
            }
        );

        long totalRows = Convert.ToInt64(rows[0]["TotalRows"], CultureInfo.InvariantCulture);
        long matchingRows = Convert.ToInt64(rows[0]["MatchingRows"], CultureInfo.InvariantCulture);

        totalRows
            .Should()
            .Be(SeededRowCount, "the plan under measurement is only meaningful over the seeded volume");

        matchingRows
            .Should()
            .Be(
                MatchingTokenRowCount,
                "the seed gives the queried token exactly this many rows, a small fraction of the "
                    + "collection, which is what makes an index read the plan the planner would choose"
            );
    }

    /// <summary>
    /// Explains the compiled page-selection SQL with the parameter values the planner produced for it,
    /// so the plan describes the statement production would have executed rather than a rewrite of it.
    /// </summary>
    private async Task<JsonElement> ExplainAsync(PageKeysetSpec.Query keyset)
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
        // failed assertion.
        await TestContext.Out.WriteLineAsync(keyset.Plan.PageDocumentIdSql);
        await TestContext.Out.WriteLineAsync(explainJson);

        using var document = JsonDocument.Parse(explainJson);

        return document.RootElement[0].GetProperty("Plan").Clone();
    }

    /// <summary>
    /// Seeds <see cref="SeededRowCount" /> <c>dms.Document</c> rows carrying a School resource key, of
    /// which <see cref="MatchingTokenRowCount" /> carry <see cref="QueriedOwnershipTokenId" />; the rest
    /// are split deterministically by row ordinal between other tokens and <c>NULL</c>.
    /// </summary>
    private async Task SeedSchoolsAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[SchoolResource];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId", "CreatedByOwnershipTokenId")
            SELECT
                ('10000000-0000-0000-0000-' || lpad(series::text, 12, '0'))::uuid,
                @resourceKeyId,
                CASE
                    WHEN series <= @matchingTokenRowCount THEN @queriedOwnershipTokenId
                    WHEN series % 4 = 0 THEN NULL
                    ELSE (2 + (series % 3))::smallint
                END
            FROM generate_series(1, @rowCount) AS series;

            INSERT INTO "edfi"."School" ("DocumentId", "ContentVersion", "NameOfInstitution", "SchoolId")
            SELECT
                source."DocumentId",
                source."ContentVersion",
                'Ownership Plan School ' || source."DocumentId",
                source."DocumentId"
            FROM "dms"."Document" AS source
            WHERE source."ResourceKeyId" = @resourceKeyId;
            """,
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("rowCount", NpgsqlDbType.Integer) { Value = SeededRowCount },
            new NpgsqlParameter("matchingTokenRowCount", NpgsqlDbType.Integer)
            {
                Value = MatchingTokenRowCount,
            },
            new NpgsqlParameter("queriedOwnershipTokenId", NpgsqlDbType.Smallint)
            {
                Value = QueriedOwnershipTokenId,
            }
        );
    }
}
