// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server evidence that the ownership page predicate against <c>CreatedByOwnershipTokenId</c> seeks
/// the filtered index rather than scanning <c>dms.Document</c>.
/// </summary>
/// <remarks>
/// <para>
/// The SQL under measurement is compiled by the production page-keyset planner, not written here, so
/// what the plan describes is what an ownership-filtered first page really executes. No DDL is added:
/// the filtered index is already emitted for every in-scope database, and this fixture is what shows the
/// runtime predicate and the emitted index actually meet. Index choice is an optimizer decision made per
/// engine, so evidence from PostgreSQL says nothing about SQL Server.
/// </para>
/// <para>
/// A plan assertion is only meaningful at a volume where the optimizer has a choice, so the fixture seeds
/// enough rows for a table scan to be the expensive option and asserts the queried token's share really
/// is a small fraction of them before reading any plan.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("OwnershipTokenQueryPlan")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Ownership_Token_Page_Query_Plan
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    /// <summary>
    /// Enough rows that a full table scan is plainly the expensive option against the queried token's
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

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private static readonly CollectionPaging _paging = new CollectionPaging.Traditional(
        new PaginationParameters(Limit: PageLimit, Offset: 0, TotalCount: false, MaximumPageSize: 500)
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_Ownership_Token_Page_Query_Plan)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );

        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;

        await SeedSchoolsAsync();

        await _database.ExecuteNonQueryAsync("UPDATE STATISTICS [dms].[Document];");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_lease is not null)
        {
            await _lease.DisposeAsync();
        }

        if (_baseline is not null)
        {
            await _baseline.DisposeAsync();
        }
    }

    /// <summary>
    /// The compiled ownership page predicate - <c>IS NOT NULL AND IN (...)</c> against
    /// <c>CreatedByOwnershipTokenId</c> - seeks the filtered index rather than scanning the table, at a
    /// volume where the optimizer has an actual choice to make.
    /// </summary>
    [Test]
    public async Task It_seeks_the_ownership_token_index_for_the_page_predicate()
    {
        await AssertSeededVolumeAsync();

        var readPlan = _fixture.MappingSet.GetReadPlanOrThrow(SchoolResource);
        var keyset = new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql).Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paging,
            authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                OwnershipTokenParameterization: OwnershipTokenParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    [QueriedOwnershipTokenId],
                    "ownershipTokenIds"
                )
            )
        );

        string plan = await CapturePlanAsync(keyset);

        DocumentAccess documentAccess = SoleDocumentAccess(plan);

        documentAccess
            .PhysicalOp.Should()
            .Be(
                "Index Seek",
                "the ownership predicate is an equality membership test against the filtered index's key "
                    + "column, so the matching rows are sought rather than found by scanning the table"
            );
        documentAccess
            .Index.Should()
            .Be(
                "[IX_Document_CreatedByOwnershipTokenId]",
                "the seek has to land on the index this change filters rather than on any other index "
                    + "that could serve the predicate"
            );
        documentAccess
            .IsFiltered.Should()
            .BeTrue(
                "an index of the same name carrying every row would seek just as well and would say "
                    + "nothing about the filter this change adds"
            );
    }

    /// <summary>
    /// The one showplan operator that reaches <c>dms.Document</c> through an index, described by how it
    /// got there.
    /// </summary>
    /// <remarks>
    /// Read structurally rather than by substring because the plan also reaches the root table through
    /// its primary key for the join, so the index name, the access method and the filtered flag all have
    /// to come off the same operator. Separate substring assertions over the whole plan are satisfied by
    /// a plan that scanned this index and sought a different one, which is the regression worth catching.
    /// A key lookup back into the clustered index is left out of the count: it is a routine companion of
    /// a nonclustered seek, it still means the filtered index was sought, and it is the seek that carries
    /// the answer.
    /// </remarks>
    private static DocumentAccess SoleDocumentAccess(string plan)
    {
        XNamespace showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

        var accesses = XDocument
            .Parse(plan)
            .Descendants(showplan + "Object")
            .Where(o =>
                (string?)o.Attribute("Schema") == "[dms]" && (string?)o.Attribute("Table") == "[Document]"
            )
            .Where(o => !IsKeyLookup(o))
            .Select(o => new { Object = o, Operator = o.Ancestors(showplan + "RelOp").FirstOrDefault() })
            .Where(access => access.Operator is not null)
            .Select(access => new DocumentAccess(
                (string?)access.Operator!.Attribute("PhysicalOp"),
                (string?)access.Object.Attribute("Index"),
                IsFilteredIndex(access.Object)
            ))
            .ToList();

        accesses
            .Should()
            .ContainSingle(
                "the compiled page selection reaches dms.Document through one index, so one operator "
                    + "carries the whole answer about how the ownership predicate was served"
            );

        return accesses[0];
    }

    /// <summary>
    /// Whether the showplan object names a filtered index. The attribute is written only for one, and the
    /// boolean renders in either form depending on the engine version.
    /// </summary>
    private static bool IsFilteredIndex(XElement showplanObject) =>
        (string?)showplanObject.Attribute("Filtered") is "1" or "true";

    /// <summary>
    /// Whether the showplan object is the target of a key lookup: a seek back into the clustered index
    /// for columns the nonclustered index did not carry. Showplan marks it on the enclosing scan element,
    /// and the boolean renders in either form depending on the engine version.
    /// </summary>
    private static bool IsKeyLookup(XElement showplanObject) =>
        (string?)showplanObject.Parent?.Attribute("Lookup") is "1" or "true";

    /// <summary>How one showplan operator reached <c>dms.Document</c>.</summary>
    private sealed record DocumentAccess(string? PhysicalOp, string? Index, bool IsFiltered);

    /// <summary>
    /// The seeded collection's shape, asserted here rather than left implicit. A queried-token share that
    /// had come to hold most of the rows would make an index seek the wrong plan for the optimizer to
    /// choose, and the plan assertion above would then be failing about the seed instead of about the
    /// predicate.
    /// </summary>
    private async Task AssertSeededVolumeAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[SchoolResource];

        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                COUNT_BIG(*) AS [TotalRows],
                COUNT_BIG(CASE WHEN [CreatedByOwnershipTokenId] = @queriedOwnershipTokenId THEN 1 END)
                    AS [MatchingRows]
            FROM [dms].[Document]
            WHERE [ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("resourceKeyId", resourceKeyId),
            new SqlParameter("queriedOwnershipTokenId", QueriedOwnershipTokenId)
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
                    + "collection, which is what makes an index seek the plan the optimizer would choose"
            );
    }

    /// <summary>
    /// Runs the compiled page-selection SQL with the parameter values the planner produced for it and
    /// collects the showplan SQL Server emits alongside it, so the plan describes the statement
    /// production would have executed rather than a rewrite of it.
    /// </summary>
    private async Task<string> CapturePlanAsync(PageKeysetSpec.Query keyset)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SetStatisticsXmlAsync(connection, enabled: true);

        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = keyset.Plan.PageDocumentIdSql;
            command.CommandTimeout = 300;

            foreach (var parameter in keyset.ParameterValues)
            {
                command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
            }

            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            StringBuilder plan = new();
            do
            {
                while (await reader.ReadAsync())
                {
                    if (
                        reader.FieldCount == 1
                        && reader.GetFieldType(0) == typeof(string)
                        && !await reader.IsDBNullAsync(0)
                    )
                    {
                        plan.AppendLine(reader.GetString(0));
                    }
                }
            } while (await reader.NextResultAsync());

            // The evidence itself, captured so a plan change can be read rather than only reported as a
            // failed substring assertion.
            await TestContext.Out.WriteLineAsync(keyset.Plan.PageDocumentIdSql);
            await TestContext.Out.WriteLineAsync(plan.ToString());

            return plan.ToString();
        }
        finally
        {
            await SetStatisticsXmlAsync(connection, enabled: false);
        }
    }

    private static async Task SetStatisticsXmlAsync(SqlConnection connection, bool enabled)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = enabled ? "SET STATISTICS XML ON;" : "SET STATISTICS XML OFF;";
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
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
            WITH "numbers" AS (
                SELECT TOP (@rowCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS "Ordinal"
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId], [CreatedByOwnershipTokenId])
            SELECT
                NEWID(),
                @resourceKeyId,
                CASE
                    WHEN "Ordinal" <= @matchingTokenRowCount THEN @queriedOwnershipTokenId
                    WHEN "Ordinal" % 4 = 0 THEN NULL
                    ELSE 2 + ("Ordinal" % 3)
                END
            FROM "numbers";

            INSERT INTO [edfi].[School] ([DocumentId], [ContentVersion], [NameOfInstitution], [SchoolId])
            SELECT
                source.[DocumentId],
                source.[ContentVersion],
                CONCAT('Ownership Plan School ', source.[DocumentId]),
                source.[DocumentId]
            FROM [dms].[Document] AS source
            WHERE source.[ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("resourceKeyId", resourceKeyId),
            new SqlParameter("rowCount", SeededRowCount),
            new SqlParameter("matchingTokenRowCount", MatchingTokenRowCount),
            new SqlParameter("queriedOwnershipTokenId", QueriedOwnershipTokenId)
        );
    }
}
