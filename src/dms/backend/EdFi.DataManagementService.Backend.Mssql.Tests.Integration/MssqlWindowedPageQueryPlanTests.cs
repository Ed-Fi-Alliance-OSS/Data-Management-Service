// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
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
/// SQL Server twin of the windowed page-plan evidence: a max-bearing change-version window is ordered
/// by <c>ContentVersion</c> so the engine can seek the change-version index for the upper tail instead
/// of scanning the primary key and discarding almost everything it reads.
/// </summary>
/// <remarks>
/// <para>
/// The SQL under measurement is compiled by the production page-keyset planners, not written here, so
/// what the plan describes is what a windowed first page really executes. No DDL is added: both indexes
/// are already emitted for every in-scope root, and this fixture is what shows the runtime predicate and
/// the emitted index actually meet. Index choice is an optimizer decision made per engine, so evidence
/// from PostgreSQL says nothing about SQL Server.
/// </para>
/// <para>
/// A plan assertion is only meaningful at a volume where the optimizer has a choice, so the fixture
/// seeds enough rows for a dead-run scan to be the expensive option and asserts the tail really is a
/// small fraction of them before reading any plan.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("WindowedPageQueryPlan")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Windowed_Page_Query_Plan
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
            $"{nameof(Given_A_Mssql_Windowed_Page_Query_Plan)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );

        // One lease for the fixture rather than one per test: both cases read plans over the same seeded
        // volume, and seeding it twice would double the fixture's cost for no additional evidence.
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;

        await SeedSchoolsAsync();
        await SeedDescriptorsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE STATISTICS [dms].[Document];
            UPDATE STATISTICS [dms].[Descriptor];
            UPDATE STATISTICS [edfi].[School];
            """
        );
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
    /// A regular resource's windowed first page seeks <c>IX_&lt;Table&gt;_ContentVersion</c> and never
    /// reads the root through its primary key, which is the dead-run scan the ordering exists to avoid.
    /// </summary>
    [Test]
    public async Task It_seeks_the_content_version_index_for_a_regular_resource_upper_tail()
    {
        var window = await UpperTailWindowAsync("[edfi].[School]");
        var keyset = new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql).Plan(
            _fixture.MappingSet.GetReadPlanOrThrow(SchoolResource).Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paging,
            changeVersionRange: window,
            orderingMode: PageOrderingMode.ContentVersion
        );

        string plan = await CapturePlanAsync(keyset);

        plan.Should().Contain("[IX_School_ContentVersion]");
        plan.Should()
            .Contain(
                "PhysicalOp=\"Index Seek\"",
                "the window's bounds are a range over the index's leading column, so the tail is sought "
                    + "rather than arrived at by reading forward"
            );
        plan.Should()
            .NotContain(
                "[PK_School]",
                "reading the root through its primary key is the dead run the ContentVersion ordering "
                    + "exists to avoid"
            );
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
            "[dms].[Descriptor]",
            $"WHERE [ResourceKeyId] = {resourceKeyId.ToString(CultureInfo.InvariantCulture)}"
        );
        var keyset = new DescriptorQueryPageKeysetPlanner(SqlDialect.Mssql).Plan(
            _fixture.MappingSet,
            DescriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paging,
            changeVersionRange: window,
            orderingMode: PageOrderingMode.ContentVersion
        );

        string plan = await CapturePlanAsync(keyset);

        plan.Should().Contain("[IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId]");
        plan.Should()
            .Contain(
                "PhysicalOp=\"Index Seek\"",
                "the resource key is an equality prefix and the window is a range over the next column, "
                    + "so the tail is sought rather than arrived at by reading forward"
            );
        plan.Should()
            .NotContain(
                "[PK_Descriptor]",
                "reading the descriptor root through its primary key is the dead run the ContentVersion "
                    + "ordering exists to avoid"
            );
    }

    /// <summary>
    /// The window a first page of a freshly opened change-version window reads: the top
    /// <see cref="TailRowCount" /> anchors of the seeded collection. Derived from the rows themselves
    /// rather than from the values the seed supplied, because the emitted stamp triggers are what decide
    /// a row's final change version.
    /// </summary>
    /// <remarks>
    /// The tail's share of the collection is asserted here rather than left implicit. A window that had
    /// come to hold most of the rows would make an index seek the wrong plan for the optimizer to
    /// choose, and the plan assertions would then be failing about the seed instead of about the
    /// predicate.
    /// </remarks>
    private async Task<ChangeVersionRange> UpperTailWindowAsync(
        string qualifiedTable,
        string whereClause = ""
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                MAX([ContentVersion]) AS [Ceiling],
                COUNT_BIG(*) AS [TotalRows]
            FROM {qualifiedTable}
            {whereClause};
            """
        );

        long ceiling = Convert.ToInt64(rows[0]["Ceiling"], CultureInfo.InvariantCulture);
        long totalRows = Convert.ToInt64(rows[0]["TotalRows"], CultureInfo.InvariantCulture);

        totalRows
            .Should()
            .Be(SeededRowCount, "the plan under measurement is only meaningful over the seeded volume");

        ChangeVersionRange window = new(ceiling - TailRowCount + 1, ceiling);

        var tailRows = await _database.QueryRowsAsync(
            $"""
            SELECT COUNT_BIG(*) AS [TailRows]
            FROM {qualifiedTable}
            {(whereClause.Length == 0 ? "WHERE" : $"{whereClause} AND")}
                [ContentVersion] >= @minChangeVersion AND [ContentVersion] <= @maxChangeVersion;
            """,
            new SqlParameter("minChangeVersion", window.MinChangeVersion!),
            new SqlParameter("maxChangeVersion", window.MaxChangeVersion!)
        );

        Convert
            .ToInt64(tailRows[0]["TailRows"], CultureInfo.InvariantCulture)
            .Should()
            .BeLessThanOrEqualTo(
                TailRowCount,
                "the window has to name a small fraction of the collection for an index seek to be the "
                    + "plan the optimizer would choose"
            );

        return window;
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

    private async Task SeedSchoolsAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[SchoolResource];

        await _database.ExecuteNonQueryAsync(
            """
            WITH "numbers" AS (
                SELECT TOP (@rowCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS "Ordinal"
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            SELECT NEWID(), @resourceKeyId FROM "numbers";

            INSERT INTO [edfi].[School] ([DocumentId], [ContentVersion], [NameOfInstitution], [SchoolId])
            SELECT
                source.[DocumentId],
                source.[ContentVersion],
                CONCAT('Windowed Plan School ', source.[DocumentId]),
                source.[DocumentId]
            FROM [dms].[Document] AS source
            WHERE source.[ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("resourceKeyId", resourceKeyId),
            new SqlParameter("rowCount", SeededRowCount)
        );
    }

    private async Task SeedDescriptorsAsync()
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[DescriptorResource];

        await _database.ExecuteNonQueryAsync(
            """
            WITH "numbers" AS (
                SELECT TOP (@rowCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS "Ordinal"
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            SELECT NEWID(), @resourceKeyId FROM "numbers";

            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [ResourceKeyId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Discriminator],
                [Uri],
                [ContentVersion]
            )
            SELECT
                source.[DocumentId],
                @resourceKeyId,
                'uri://ed-fi.org/AcademicSubjectDescriptor',
                CONCAT('plan-', source.[DocumentId]),
                CONCAT('Windowed Plan Descriptor ', source.[DocumentId]),
                'edfi.AcademicSubjectDescriptor',
                CONCAT('uri://ed-fi.org/AcademicSubjectDescriptor#plan-', source.[DocumentId]),
                source.[ContentVersion]
            FROM [dms].[Document] AS source
            WHERE source.[ResourceKeyId] = @resourceKeyId;
            """,
            new SqlParameter("resourceKeyId", resourceKeyId),
            new SqlParameter("rowCount", SeededRowCount)
        );
    }
}
