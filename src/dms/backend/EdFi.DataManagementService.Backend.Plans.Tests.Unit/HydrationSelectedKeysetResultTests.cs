// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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
/// returned row.
/// </summary>
[TestFixture]
public class Given_HydrationReader_With_A_Selected_Keyset_Result_Set
{
    [Test]
    public async Task It_reads_the_maximum_from_descending_ids()
    {
        using var reader = HydrationDescriptorResultTestHelper.CreateReader(
            CreateSelectedIdsTable(2509L, 2508L, 7L)
        );

        var maximum = await HydrationReader.ReadSelectedDocumentIdMaximumAsync(
            reader,
            CancellationToken.None
        );

        maximum.Should().Be(2509L);
    }

    [Test]
    public async Task It_reads_the_maximum_from_shuffled_ids()
    {
        using var reader = HydrationDescriptorResultTestHelper.CreateReader(
            CreateSelectedIdsTable(11L, 2509L, 5L, 400L)
        );

        var maximum = await HydrationReader.ReadSelectedDocumentIdMaximumAsync(
            reader,
            CancellationToken.None
        );

        maximum.Should().Be(2509L);
    }

    [Test]
    public async Task It_reads_a_single_id_as_the_maximum()
    {
        using var reader = HydrationDescriptorResultTestHelper.CreateReader(CreateSelectedIdsTable(42L));

        var maximum = await HydrationReader.ReadSelectedDocumentIdMaximumAsync(
            reader,
            CancellationToken.None
        );

        maximum.Should().Be(42L);
    }

    [Test]
    public async Task It_reads_no_maximum_from_an_empty_selection()
    {
        using var reader = HydrationDescriptorResultTestHelper.CreateReader(CreateSelectedIdsTable());

        var maximum = await HydrationReader.ReadSelectedDocumentIdMaximumAsync(
            reader,
            CancellationToken.None
        );

        maximum.Should().BeNull();
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
            CreateDocumentMetadataTable((42L, DocumentUuid, 44L, 45L), (84L, OtherDocumentUuid, 46L, 47L)),
            CreateRootTableRows((42L, 255901), (84L, 255902)),
            CreateChildTableRows((100L, 42L, 0, "Springfield"))
        );

        result.HighestSelectedDocumentId.Should().Be(84L);
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

        result.HighestSelectedDocumentId.Should().Be(84L);
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

        result.HighestSelectedDocumentId.Should().BeNull();
        result.DocumentMetadata.Should().BeEmpty();
    }

    [Test]
    public async Task It_carries_no_selected_maximum_for_a_single_document_keyset()
    {
        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable((42L, DocumentUuid, 44L, 45L)),
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

        result.HighestSelectedDocumentId.Should().BeNull();
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

    private static Task<HydratedPage> ExecuteQueryHydrationAsync(params DataTable[] resultSets)
    {
        var command = new RecordingDbCommand(HydrationDescriptorResultTestHelper.CreateReader(resultSets));

        return HydrationExecutor.ExecuteAsync(
            new RecordingDbConnection(command),
            BuildTestReadPlan(SqlDialect.Pgsql),
            Given_HydrationBatchBuilder_With_A_Query_Keyset_Materialization.CreateCursorKeyset(
                SqlDialect.Pgsql,
                25L
            ),
            SqlDialect.Pgsql,
            CancellationToken.None
        );
    }

    private static DataTable CreateDocumentMetadataTable(
        params (long DocumentId, Guid DocumentUuid, long ContentVersion, long IdentityVersion)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("IdentityLastModifiedAt", typeof(DateTimeOffset));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.IdentityVersion,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
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
