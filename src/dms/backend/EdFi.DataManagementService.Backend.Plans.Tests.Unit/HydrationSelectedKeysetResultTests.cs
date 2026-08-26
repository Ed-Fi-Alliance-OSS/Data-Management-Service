// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.HydrationBatchBuilderTestHelper;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// A query keyset materialization returns the ids it inserted, which is how a page's selected-keyset
/// boundary leaves hydration without a second candidate query. GET-by-id keysets select nothing and
/// keep their existing batch shape.
/// </summary>
[TestFixture]
public class Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization
{
    private string _pgsqlBatch = null!;
    private string _mssqlBatch = null!;

    [SetUp]
    public void Setup()
    {
        _pgsqlBatch = BuildCursorQueryBatch(SqlDialect.Pgsql, pageSize: 25L);
        _mssqlBatch = BuildCursorQueryBatch(SqlDialect.Mssql, pageSize: 25L);
    }

    [Test]
    public void It_returns_the_inserted_ids_from_a_pgsql_returning_clause()
    {
        _pgsqlBatch
            .Should()
            .Contain(
                """
                INSERT INTO "page" ("DocumentId")
                SELECT "DocumentId" FROM page_ids RETURNING "DocumentId";
                """
            );
    }

    [Test]
    public void It_returns_the_inserted_ids_from_an_mssql_output_clause()
    {
        _mssqlBatch
            .Should()
            .Contain(
                """
                INSERT INTO [#page] ([DocumentId])
                OUTPUT INSERTED.[DocumentId]
                SELECT [DocumentId] FROM page_ids;
                """
            );
    }

    [Test]
    public void It_uses_only_the_dialect_specific_form_on_each_provider()
    {
        _pgsqlBatch.Should().NotContain("OUTPUT INSERTED");
        _mssqlBatch.Should().NotContain("RETURNING");
    }

    [Test]
    public void It_returns_the_inserted_ids_before_document_metadata()
    {
        AssertSelectedIdsPrecedeDocumentMetadata(
            _pgsqlBatch,
            "RETURNING \"DocumentId\";",
            "\"dms\".\"Document\""
        );
        AssertSelectedIdsPrecedeDocumentMetadata(
            _mssqlBatch,
            "OUTPUT INSERTED.[DocumentId]",
            "[dms].[Document]"
        );
    }

    [Test]
    public void It_returns_the_inserted_ids_before_an_optional_total_count()
    {
        var pgsqlBatchWithTotalCount = BuildTraditionalQueryBatch(
            SqlDialect.Pgsql,
            limit: 25L,
            includeTotalCountSql: true
        );
        var mssqlBatchWithTotalCount = BuildTraditionalQueryBatch(
            SqlDialect.Mssql,
            limit: 25L,
            includeTotalCountSql: true
        );

        AssertSelectedIdsPrecedeDocumentMetadata(
            pgsqlBatchWithTotalCount,
            "RETURNING \"DocumentId\";",
            "SELECT COUNT(1)"
        );
        AssertSelectedIdsPrecedeDocumentMetadata(
            mssqlBatchWithTotalCount,
            "OUTPUT INSERTED.[DocumentId]",
            "SELECT COUNT(1)"
        );
    }

    private static void AssertSelectedIdsPrecedeDocumentMetadata(
        string batch,
        string selectedIdsClause,
        string followingStatement
    )
    {
        var selectedIdsIndex = batch.IndexOf(selectedIdsClause, StringComparison.Ordinal);
        var followingIndex = batch.IndexOf(followingStatement, StringComparison.Ordinal);

        selectedIdsIndex.Should().BePositive();
        followingIndex.Should().BeGreaterThan(selectedIdsIndex);
    }

    internal static string BuildCursorQueryBatch(SqlDialect dialect, object pageSize) =>
        HydrationBatchBuilder.Build(
            BuildTestReadPlan(dialect),
            CreateCursorKeyset(dialect, pageSize),
            dialect
        );

    internal static string BuildTraditionalQueryBatch(
        SqlDialect dialect,
        object limit,
        bool includeTotalCountSql = false
    ) =>
        HydrationBatchBuilder.Build(
            BuildTestReadPlan(dialect),
            CreateTraditionalKeyset(dialect, limit, includeTotalCountSql),
            dialect
        );

    internal static PageKeysetSpec.Query CreateCursorKeyset(SqlDialect dialect, object pageSize)
    {
        var mode = new PageCandidateMode.Cursor();

        return new PageKeysetSpec.Query(
            Compile(dialect, mode),
            new Dictionary<string, object?>
            {
                [mode.InclusiveMinimumParameterName] = 1L,
                [mode.InclusiveMaximumParameterName] = long.MaxValue,
                [mode.PageSizeParameterName] = pageSize,
            }
        );
    }

    /// <summary>
    /// A cursor keyset anchored on <c>ContentVersion</c>. The candidate mode and the keyset carry the
    /// same anchor, exactly as the page keyset planner produces them: the mode decides what the embedded
    /// page-selection SQL projects, and the keyset decides what the materialization carries out of it.
    /// </summary>
    internal static PageKeysetSpec.Query CreateAnchoredCursorKeyset(SqlDialect dialect, object pageSize)
    {
        var mode = new PageCandidateMode.Cursor(OrderingMode: PageOrderingMode.ContentVersion);

        return new PageKeysetSpec.Query(
            Compile(dialect, mode),
            new Dictionary<string, object?>
            {
                [mode.InclusiveMinimumParameterName] = 1L,
                [mode.InclusiveMaximumParameterName] = long.MaxValue,
                [mode.PageSizeParameterName] = pageSize,
            },
            PageOrderingMode.ContentVersion
        );
    }

    internal static PageKeysetSpec.Query CreateTraditionalKeyset(
        SqlDialect dialect,
        object limit,
        bool includeTotalCountSql
    )
    {
        var mode = new PageCandidateMode.Traditional(IncludeTotalCountSql: includeTotalCountSql);

        return new PageKeysetSpec.Query(
            Compile(dialect, mode),
            new Dictionary<string, object?>
            {
                [mode.OffsetParameterName] = 0L,
                [mode.LimitParameterName] = limit,
            }
        );
    }

    private static PageDocumentIdSqlPlan Compile(SqlDialect dialect, PageCandidateMode mode) =>
        new PageDocumentIdSqlCompiler(dialect).Compile(
            new PageDocumentIdQuerySpec(
                RootTable: new DbTableName(new DbSchemaName("edfi"), "School"),
                Predicates: [],
                UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                Mode: mode
            )
        );
}

/// <summary>
/// A <c>ContentVersion</c>-anchored page carries its anchor out of selection alongside the ids. Both
/// columns are load-bearing and neither is optional: <c>DocumentId</c> feeds the keyset table and every
/// hydration join that follows, while <c>ContentVersion</c> is the value the page's continuation token
/// is expressed in, and hydration can read no column the embedded page-selection SQL did not project.
/// Recovering the anchor after selection instead would stall on a page whose rows were all deleted.
/// </summary>
[TestFixture]
public class Given_HydrationBatchBuilder_With_A_Content_Version_Anchored_Query_Keyset
{
    private static string BuildAnchoredBatch(SqlDialect dialect, object pageSize = null!) =>
        HydrationBatchBuilder.Build(
            BuildTestReadPlan(dialect),
            Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateAnchoredCursorKeyset(
                dialect,
                pageSize ?? 25L
            ),
            dialect
        );

    [Test]
    public void It_adds_a_nullable_anchor_column_to_the_pgsql_keyset_table()
    {
        BuildAnchoredBatch(SqlDialect.Pgsql)
            .Should()
            .Contain(
                "CREATE TEMP TABLE \"page\" (\"DocumentId\" bigint PRIMARY KEY, \"Ordinal\" int NULL, "
                    + "\"ContentVersion\" bigint NULL) ON COMMIT DROP;"
            );
    }

    [Test]
    public void It_adds_a_nullable_anchor_column_to_the_mssql_keyset_table()
    {
        BuildAnchoredBatch(SqlDialect.Mssql)
            .Should()
            .Contain(
                "CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL, "
                    + "[ContentVersion] bigint NULL);"
            );
    }

    [Test]
    public void It_inserts_and_returns_both_columns_on_pgsql()
    {
        BuildAnchoredBatch(SqlDialect.Pgsql)
            .Should()
            .Contain(
                """
                INSERT INTO "page" ("DocumentId", "ContentVersion")
                SELECT "DocumentId", "ContentVersion" FROM page_ids RETURNING "DocumentId", "ContentVersion";
                """
            );
    }

    [Test]
    public void It_inserts_and_returns_both_columns_on_mssql()
    {
        BuildAnchoredBatch(SqlDialect.Mssql)
            .Should()
            .Contain(
                """
                INSERT INTO [#page] ([DocumentId], [ContentVersion])
                OUTPUT INSERTED.[DocumentId], INSERTED.[ContentVersion]
                SELECT [DocumentId], [ContentVersion] FROM page_ids;
                """
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_selects_both_columns_from_the_embedded_page_selection(SqlDialect dialect)
    {
        // The two-column projection comes from the candidate compiler, not from this builder. Asserted
        // here because the insert and returning clauses above are only valid if page_ids really carries
        // both, and a one-column projection would fail at the provider rather than at compilation.
        // PostgreSQL limits a cursor page with a trailing LIMIT and SQL Server with a leading TOP, so
        // only the projection is shared between them.
        var quoted =
            dialect == SqlDialect.Pgsql
                ? "SELECT r.\"DocumentId\", r.\"ContentVersion\""
                : "SELECT TOP (@pageSize) r.[DocumentId], r.[ContentVersion]";

        BuildAnchoredBatch(dialect).Should().Contain(quoted);
    }

    [Test]
    public void It_keeps_the_two_column_shape_for_a_zero_size_anchored_page()
    {
        // A zero-size page returns an empty result set rather than none, and it has to have the same
        // column count as any other anchored page or the reader takes an ordinal that is not there.
        BuildAnchoredBatch(SqlDialect.Pgsql, pageSize: 0L)
            .Should()
            .Contain(
                """
                INSERT INTO "page" ("DocumentId", "ContentVersion")
                SELECT CAST(NULL AS bigint) AS "DocumentId", CAST(NULL AS bigint) AS "ContentVersion" WHERE 1 = 0 RETURNING "DocumentId", "ContentVersion";
                """
            );
        BuildAnchoredBatch(SqlDialect.Mssql, pageSize: 0L)
            .Should()
            .Contain(
                """
                INSERT INTO [#page] ([DocumentId], [ContentVersion])
                OUTPUT INSERTED.[DocumentId], INSERTED.[ContentVersion]
                SELECT CAST(NULL AS bigint) AS [DocumentId], CAST(NULL AS bigint) AS [ContentVersion] WHERE 1 = 0;
                """
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_emits_one_selected_keyset_result_set_under_either_anchor(SqlDialect dialect)
    {
        // The anchor widens the existing result set rather than adding one, so nothing downstream has to
        // move: a co-batched reader skips the same number of result sets either way.
        var plan = BuildTestReadPlan(dialect);
        var options = new HydrationExecutionOptions();

        HydrationExecutor
            .GetResultSetCount(
                plan,
                Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateAnchoredCursorKeyset(
                    dialect,
                    25L
                ),
                options
            )
            .Should()
            .Be(
                HydrationExecutor.GetResultSetCount(
                    plan,
                    Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateCursorKeyset(
                        dialect,
                        25L
                    ),
                    options
                )
            );
    }
}

/// <summary>
/// Every batch that is not a <c>ContentVersion</c>-anchored query keyset emits the text it always has.
/// This is the gate that keeps the shipped traditional and GET-by-id SQL untouched by this change.
/// </summary>
[TestFixture]
public class Given_A_Keyset_That_Carries_No_Anchor
{
    [Test]
    public void It_creates_the_pgsql_keyset_table_with_no_anchor_column()
    {
        var expected =
            "CREATE TEMP TABLE \"page\" (\"DocumentId\" bigint PRIMARY KEY, \"Ordinal\" int NULL) "
            + "ON COMMIT DROP;";

        Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization
            .BuildCursorQueryBatch(SqlDialect.Pgsql, 25L)
            .Should()
            .Contain(expected);
        Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization
            .BuildTraditionalQueryBatch(SqlDialect.Pgsql, 25L)
            .Should()
            .Contain(expected);
        HydrationBatchBuilder
            .Build(
                BuildTestReadPlan(SqlDialect.Pgsql),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(UseSingleDocumentFastPath: false)
            )
            .Should()
            .Contain(expected);
    }

    [Test]
    public void It_creates_the_mssql_keyset_table_with_no_anchor_column()
    {
        var expected = "CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);";

        Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization
            .BuildCursorQueryBatch(SqlDialect.Mssql, 25L)
            .Should()
            .Contain(expected);
        Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization
            .BuildTraditionalQueryBatch(SqlDialect.Mssql, 25L)
            .Should()
            .Contain(expected);
        HydrationBatchBuilder
            .Build(
                BuildTestReadPlan(SqlDialect.Mssql),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Mssql,
                new HydrationExecutionOptions(UseSingleDocumentFastPath: false)
            )
            .Should()
            .Contain(expected);
    }
}

/// <summary>
/// A zero-size page selects nothing, so it materializes the empty keyset instead of running the
/// candidate query for zero rows — the behavior traditional paging already had, now reached by a
/// cursor page size as well. The selected-id result set is still emitted, with no rows, so the
/// positions of every later result set are independent of selection size.
/// </summary>
[TestFixture]
public class Given_HydrationBatchBuilder_With_A_Zero_Size_Query_Keyset
{
    [Test]
    public void It_returns_an_empty_pgsql_selected_id_result_set_for_a_zero_cursor_page_size()
    {
        var batch = Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildCursorQueryBatch(
            SqlDialect.Pgsql,
            pageSize: 0L
        );

        batch
            .Should()
            .Contain(
                """
                INSERT INTO "page" ("DocumentId")
                SELECT CAST(NULL AS bigint) AS "DocumentId" WHERE 1 = 0 RETURNING "DocumentId";
                """
            );
    }

    [Test]
    public void It_returns_an_empty_mssql_selected_id_result_set_for_a_zero_cursor_page_size()
    {
        var batch = Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildCursorQueryBatch(
            SqlDialect.Mssql,
            pageSize: 0L
        );

        batch
            .Should()
            .Contain(
                """
                INSERT INTO [#page] ([DocumentId])
                OUTPUT INSERTED.[DocumentId]
                SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;
                """
            );
    }

    [Test]
    public void It_returns_an_empty_selected_id_result_set_for_a_zero_traditional_limit()
    {
        var pgsqlBatch =
            Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildTraditionalQueryBatch(
                SqlDialect.Pgsql,
                limit: 0L
            );
        var mssqlBatch =
            Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildTraditionalQueryBatch(
                SqlDialect.Mssql,
                limit: 0L
            );

        pgsqlBatch.Should().Contain("WHERE 1 = 0 RETURNING \"DocumentId\";");
        mssqlBatch.Should().Contain("OUTPUT INSERTED.[DocumentId]");
        mssqlBatch.Should().Contain("WHERE 1 = 0;");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_does_not_run_candidate_selection_for_a_zero_cursor_page_size(SqlDialect dialect)
    {
        var batch = Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildCursorQueryBatch(
            dialect,
            pageSize: 0L
        );

        batch.Should().NotContain("WITH page_ids AS (");
        batch.Should().NotContain("@cursorMin");
        batch.Should().NotContain("@pageSize");
    }

    [TestCaseSource(nameof(ZeroPageSizeValues))]
    public void It_materializes_an_empty_keyset_for_all_integral_zero_cursor_page_sizes(object zeroPageSize)
    {
        var batch = Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildCursorQueryBatch(
            SqlDialect.Mssql,
            zeroPageSize
        );

        batch.Should().Contain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
        batch.Should().NotContain("WITH page_ids AS (");
    }

    [TestCaseSource(nameof(NonZeroOrUnsupportedPageSizeValues))]
    public void It_runs_candidate_selection_for_non_zero_or_unsupported_cursor_page_sizes(object pageSize)
    {
        var batch = Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.BuildCursorQueryBatch(
            SqlDialect.Mssql,
            pageSize
        );

        batch.Should().Contain("WITH page_ids AS (");
        batch.Should().Contain("TOP (@pageSize)");
        batch.Should().NotContain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
    }

    private static IEnumerable<object> ZeroPageSizeValues() =>
        [(byte)0, (sbyte)0, (short)0, (ushort)0, 0, 0U, 0L, 0UL];

    private static IEnumerable<object> NonZeroOrUnsupportedPageSizeValues() =>
        [(byte)1, (sbyte)1, (short)1, (ushort)1, 1, 1U, 1L, 1UL, "0"];
}

/// <summary>
/// GET-by-id and guarded write-path hydration materialize a keyset they were handed rather than
/// selecting one, so neither returns selected ids and neither moves a result-set position.
/// </summary>
[TestFixture]
public class Given_HydrationBatchBuilder_With_A_Single_Document_Keyset
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_does_not_return_selected_ids_from_the_values_materialization(SqlDialect dialect)
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(dialect),
            new PageKeysetSpec.Single(42L),
            dialect,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: false)
        );

        batch.Should().NotContain("RETURNING");
        batch.Should().NotContain("OUTPUT INSERTED");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_does_not_return_selected_ids_from_the_guarded_materialization(SqlDialect dialect)
    {
        var batch = HydrationBatchBuilder.BuildGuardedSingleDocumentBatch(
            BuildTestReadPlan(dialect),
            dialect,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: false),
            "1 = 1"
        );

        batch.Should().NotContain("RETURNING");
        batch.Should().NotContain("OUTPUT INSERTED");
    }
}

/// <summary>
/// The result-set count is what a co-batched reader uses to skip past a hydration batch, so it must
/// grow with the selected-id result set exactly when the batch emits one.
/// </summary>
[TestFixture]
public class Given_HydrationExecutor_Result_Set_Count_With_Selected_Ids
{
    [Test]
    public void It_counts_the_selected_id_result_set_for_a_query_keyset()
    {
        var plan = BuildTestReadPlan(SqlDialect.Pgsql);

        HydrationExecutor
            .GetResultSetCount(
                plan,
                Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateCursorKeyset(
                    SqlDialect.Pgsql,
                    25L
                ),
                new HydrationExecutionOptions()
            )
            .Should()
            // Selected ids, document metadata, root table, child table.
            .Be(2 + plan.TablePlansInDependencyOrder.Length);
    }

    [Test]
    public void It_counts_the_selected_id_result_set_alongside_an_optional_total_count()
    {
        var plan = BuildTestReadPlan(SqlDialect.Pgsql);

        HydrationExecutor
            .GetResultSetCount(
                plan,
                Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateTraditionalKeyset(
                    SqlDialect.Pgsql,
                    25L,
                    includeTotalCountSql: true
                ),
                new HydrationExecutionOptions()
            )
            .Should()
            .Be(3 + plan.TablePlansInDependencyOrder.Length);
    }

    [Test]
    public void It_does_not_count_a_selected_id_result_set_for_a_single_document_keyset()
    {
        var plan = BuildTestReadPlan(SqlDialect.Pgsql);

        HydrationExecutor
            .GetResultSetCount(plan, new PageKeysetSpec.Single(42L), new HydrationExecutionOptions())
            .Should()
            .Be(1 + plan.TablePlansInDependencyOrder.Length);
    }
}

/// <summary>
/// Neither <c>RETURNING</c> nor <c>OUTPUT</c> promises an order, so the maximum is taken across every
/// returned row. That is also what makes the widening to two columns safe: the anchor's maximum is
/// found the same way whichever row happens to carry it.
/// </summary>
[TestFixture]
public class Given_HydrationReader_With_A_Selected_Keyset_Result_Set
{
    private static async Task<long?> ReadMaximum(DataTable selectedKeys, bool carriesAnchorColumn = false)
    {
        using var reader = HydrationDescriptorResultTestHelper.CreateReader(selectedKeys);

        return await HydrationReader.ReadSelectedAnchorMaximumAsync(
            reader,
            carriesAnchorColumn,
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_reads_the_maximum_from_descending_ids()
    {
        (await ReadMaximum(CreateSelectedIdsTable(2509L, 2508L, 7L))).Should().Be(2509L);
    }

    [Test]
    public async Task It_reads_the_maximum_from_shuffled_ids()
    {
        (await ReadMaximum(CreateSelectedIdsTable(11L, 2509L, 5L, 400L))).Should().Be(2509L);
    }

    [Test]
    public async Task It_reads_a_single_id_as_the_maximum()
    {
        (await ReadMaximum(CreateSelectedIdsTable(42L))).Should().Be(42L);
    }

    [Test]
    public async Task It_reads_no_maximum_from_an_empty_selection()
    {
        (await ReadMaximum(CreateSelectedIdsTable())).Should().BeNull();
    }

    [Test]
    public async Task It_reads_the_anchor_maximum_rather_than_the_document_id_maximum()
    {
        // The two orders disagree on purpose: the highest DocumentId carries the lowest ContentVersion.
        // Reading the wrong ordinal would still return a plausible number, and the walk would continue
        // from a point in the wrong sequence entirely.
        var selectedKeys = CreateAnchoredSelectedKeysTable((7L, 900L), (2509L, 100L), (400L, 550L));

        (await ReadMaximum(selectedKeys, carriesAnchorColumn: true)).Should().Be(900L);
    }

    [Test]
    public async Task It_reads_the_anchor_maximum_without_depending_on_returned_row_order()
    {
        var ascending = CreateAnchoredSelectedKeysTable((1L, 100L), (2L, 550L), (3L, 900L));
        var descending = CreateAnchoredSelectedKeysTable((3L, 900L), (2L, 550L), (1L, 100L));

        (await ReadMaximum(ascending, carriesAnchorColumn: true)).Should().Be(900L);
        (await ReadMaximum(descending, carriesAnchorColumn: true)).Should().Be(900L);
    }

    [Test]
    public async Task It_reads_no_anchor_maximum_from_a_zero_size_anchored_page()
    {
        // A zero-size page returns an empty result set rather than none, so the read succeeds and
        // reports no boundary — which is what ends the walk instead of failing it.
        (await ReadMaximum(CreateAnchoredSelectedKeysTable(), carriesAnchorColumn: true))
            .Should()
            .BeNull();
    }

    [Test]
    public async Task It_rejects_an_anchored_read_of_a_single_column_result_set()
    {
        // The materialization SQL and this reader both derive the shape from the keyset's anchor, so
        // disagreement means one of them regressed. Failing loudly beats reading DocumentId as the
        // anchor and handing the client a token in the wrong sequence.
        var act = async () => await ReadMaximum(CreateSelectedIdsTable(42L), carriesAnchorColumn: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'ContentVersion' column*");
    }

    [Test]
    public async Task It_reads_the_anchor_by_name_rather_than_from_the_column_beside_the_document_id()
    {
        // A column between the ids and the anchor is exactly what a fixed second-column ordinal cannot
        // survive: it would read a plausible long that is not the anchor and continue the walk from a
        // position in the wrong sequence, with nothing failing. The interposed values are higher than
        // every anchor so reading the wrong column would change the answer.
        var selectedKeys = new DataTable();
        selectedKeys.Columns.Add("DocumentId", typeof(long));
        selectedKeys.Columns.Add("Ordinal", typeof(long));
        selectedKeys.Columns.Add("ContentVersion", typeof(long));
        selectedKeys.Rows.Add(7L, 5000L, 900L);
        selectedKeys.Rows.Add(2509L, 6000L, 100L);

        (await ReadMaximum(selectedKeys, carriesAnchorColumn: true)).Should().Be(900L);
    }

    internal static DataTable CreateSelectedIdsTable(params long[] selectedDocumentIds)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));

        foreach (var selectedDocumentId in selectedDocumentIds)
        {
            table.Rows.Add(selectedDocumentId);
        }

        return table;
    }

    internal static DataTable CreateAnchoredSelectedKeysTable(
        params (long DocumentId, long ContentVersion)[] selectedKeys
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("ContentVersion", typeof(long));

        foreach (var selectedKey in selectedKeys)
        {
            table.Rows.Add(selectedKey.DocumentId, selectedKey.ContentVersion);
        }

        return table;
    }
}

/// <summary>
/// The boundary describes the keys page selection chose, not the rows hydration found. Every selected
/// row can be deleted between materialization and the hydration selects that follow it in the same
/// batch, which is exactly the case a body-derived boundary would get wrong.
/// </summary>
[TestFixture]
public class Given_HydrationExecutor_With_A_Selected_Keyset_Result_Set
{
    [Test]
    public async Task It_carries_the_selected_maximum_alongside_hydrated_documents()
    {
        var result = await ExecuteQueryHydrationAsync(
            Given_HydrationReader_With_A_Selected_Keyset_Result_Set.CreateSelectedIdsTable(84L, 42L),
            CreateDocumentMetadataTable(
                (42L, DocumentUuid, 44L, new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero), (short)1),
                (
                    84L,
                    OtherDocumentUuid,
                    46L,
                    new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero),
                    (short)1
                )
            ),
            CreateRootTableRows((42L, 255901), (84L, 255902)),
            CreateChildTableRows((100L, 42L, 0, "Springfield"))
        );

        result.HighestSelectedAnchor.Should().Be(84L);
        result.DocumentMetadata.Should().HaveCount(2);
    }

    [Test]
    public async Task It_carries_the_selected_maximum_when_every_selected_row_was_deleted()
    {
        var result = await ExecuteQueryHydrationAsync(
            Given_HydrationReader_With_A_Selected_Keyset_Result_Set.CreateSelectedIdsTable(84L, 42L),
            CreateDocumentMetadataTable(),
            CreateRootTableRows(),
            CreateChildTableRows()
        );

        result.HighestSelectedAnchor.Should().Be(84L);
        result.DocumentMetadata.Should().BeEmpty();
        result.TableRowsInDependencyOrder.Should().OnlyContain(tableRows => tableRows.Rows.Count == 0);
    }

    [Test]
    public async Task It_carries_no_selected_maximum_when_selection_was_empty()
    {
        var result = await ExecuteQueryHydrationAsync(
            Given_HydrationReader_With_A_Selected_Keyset_Result_Set.CreateSelectedIdsTable(),
            CreateDocumentMetadataTable(),
            CreateRootTableRows(),
            CreateChildTableRows()
        );

        result.HighestSelectedAnchor.Should().BeNull();
        result.DocumentMetadata.Should().BeEmpty();
    }

    [Test]
    public async Task It_carries_the_content_version_maximum_for_an_anchored_page()
    {
        // The document ids and the content versions run in opposite directions, so a boundary of 46
        // could only have come from the anchor column.
        var result = await ExecuteAnchoredQueryHydrationAsync(
            Given_HydrationReader_With_A_Selected_Keyset_Result_Set.CreateAnchoredSelectedKeysTable(
                (84L, 44L),
                (42L, 46L)
            ),
            CreateDocumentMetadataTable(
                (42L, DocumentUuid, 46L, new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero), (short)1),
                (
                    84L,
                    OtherDocumentUuid,
                    44L,
                    new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero),
                    (short)1
                )
            ),
            CreateRootTableRows((42L, 255901), (84L, 255902)),
            CreateChildTableRows((100L, 42L, 0, "Springfield"))
        );

        result.HighestSelectedAnchor.Should().Be(46L);
        result.DocumentMetadata.Should().HaveCount(2);
    }

    [Test]
    public async Task It_carries_the_content_version_maximum_when_every_selected_row_was_deleted()
    {
        // The concurrency case this whole indirection exists for. Nothing survives to hydrate, and the
        // anchor still has to come back or the walk ends early and silently — an empty body with no
        // continuation is indistinguishable from a finished walk.
        var result = await ExecuteAnchoredQueryHydrationAsync(
            Given_HydrationReader_With_A_Selected_Keyset_Result_Set.CreateAnchoredSelectedKeysTable(
                (84L, 44L),
                (42L, 46L)
            ),
            CreateDocumentMetadataTable(),
            CreateRootTableRows(),
            CreateChildTableRows()
        );

        result.HighestSelectedAnchor.Should().Be(46L);
        result.DocumentMetadata.Should().BeEmpty();
    }

    [Test]
    public async Task It_carries_no_selected_maximum_for_a_single_document_keyset()
    {
        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        DocumentUuid,
                        44L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        (short)1
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield"))
            )
        );

        var result = await HydrationExecutor.ExecuteAsync(
            new RecordingDbConnection(command),
            BuildTestReadPlan(SqlDialect.Pgsql),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: false),
            CancellationToken.None
        );

        result.HighestSelectedAnchor.Should().BeNull();
    }

    [Test]
    public async Task It_reports_a_batch_that_stops_after_the_selected_ids()
    {
        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                Given_HydrationReader_With_A_Selected_Keyset_Result_Set.CreateSelectedIdsTable(42L)
            )
        );

        var act = () =>
            HydrationExecutor.ExecuteAsync(
                new RecordingDbConnection(command),
                BuildTestReadPlan(SqlDialect.Pgsql),
                Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateCursorKeyset(
                    SqlDialect.Pgsql,
                    25L
                ),
                SqlDialect.Pgsql,
                CancellationToken.None
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception
            .Which.Message.Should()
            .Be(
                "Expected a result set after the selected page keyset ids but no more result sets available."
            );
    }

    private static readonly Guid DocumentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly Guid OtherDocumentUuid = Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd");

    private static Task<HydratedPage> ExecuteQueryHydrationAsync(params DataTable[] resultSets) =>
        ExecuteAsync(
            Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateCursorKeyset(
                SqlDialect.Pgsql,
                25L
            ),
            resultSets
        );

    private static Task<HydratedPage> ExecuteAnchoredQueryHydrationAsync(params DataTable[] resultSets) =>
        ExecuteAsync(
            Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateAnchoredCursorKeyset(
                SqlDialect.Pgsql,
                25L
            ),
            resultSets
        );

    private static Task<HydratedPage> ExecuteAsync(PageKeysetSpec.Query keyset, params DataTable[] resultSets)
    {
        var command = new RecordingDbCommand(HydrationDescriptorResultTestHelper.CreateReader(resultSets));

        return HydrationExecutor.ExecuteAsync(
            new RecordingDbConnection(command),
            BuildTestReadPlan(SqlDialect.Pgsql),
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
    }

    private static DataTable CreateDocumentMetadataTable(
        params (
            long DocumentId,
            Guid DocumentUuid,
            long ContentVersion,
            DateTimeOffset ContentLastModifiedAt,
            short ResourceKeyId
        )[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("ResourceKeyId", typeof(short));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.ContentLastModifiedAt,
                row.ResourceKeyId
            );
        }

        return table;
    }

    private static DataTable CreateRootTableRows(params (long DocumentId, int SchoolId)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("SchoolId", typeof(int));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.SchoolId);
        }

        return table;
    }

    private static DataTable CreateChildTableRows(
        params (long CollectionItemId, long SchoolDocumentId, int Ordinal, string City)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("CollectionItemId", typeof(long));
        table.Columns.Add("School_DocumentId", typeof(long));
        table.Columns.Add("Ordinal", typeof(int));
        table.Columns.Add("City", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(row.CollectionItemId, row.SchoolDocumentId, row.Ordinal, row.City);
        }

        return table;
    }
}
