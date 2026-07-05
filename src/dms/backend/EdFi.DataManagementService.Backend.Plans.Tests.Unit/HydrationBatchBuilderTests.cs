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

[TestFixture]
public class Given_HydrationBatchBuilder_With_Single_Keyset
{
    private string _pgsqlBatch = null!;
    private string _mssqlBatch = null!;

    [SetUp]
    public void Setup()
    {
        var keyset = new PageKeysetSpec.Single(42L);

        _pgsqlBatch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql),
            keyset,
            SqlDialect.Pgsql
        );
        _mssqlBatch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Mssql),
            keyset,
            SqlDialect.Mssql
        );
    }

    [Test]
    public void It_should_emit_pgsql_temp_table_creation()
    {
        _pgsqlBatch.Should().Contain("DROP TABLE IF EXISTS \"page\";");
        _pgsqlBatch.Should().Contain("CREATE TEMP TABLE");
        _pgsqlBatch.Should().Contain("ON COMMIT DROP");
    }

    [Test]
    public void It_should_drop_the_existing_pgsql_temp_table_before_recreating_it()
    {
        var dropIndex = _pgsqlBatch.IndexOf("DROP TABLE IF EXISTS \"page\";", StringComparison.Ordinal);
        var createIndex = _pgsqlBatch.IndexOf("CREATE TEMP TABLE \"page\"", StringComparison.Ordinal);

        dropIndex.Should().BeGreaterThanOrEqualTo(0);
        createIndex.Should().BeGreaterThan(dropIndex);
    }

    [Test]
    public void It_should_emit_mssql_temp_table_creation()
    {
        _mssqlBatch.Should().Contain("CREATE TABLE");
        _mssqlBatch.Should().Contain("[#page]");
        _mssqlBatch.Should().Contain("IF OBJECT_ID('tempdb..");
    }

    [Test]
    public void It_should_emit_single_document_keyset_materialization()
    {
        _pgsqlBatch.Should().Contain("INSERT INTO");
        _pgsqlBatch.Should().Contain("VALUES (@DocumentId)");

        _mssqlBatch.Should().Contain("INSERT INTO");
        _mssqlBatch.Should().Contain("VALUES (@DocumentId)");
    }

    [Test]
    public void It_should_emit_single_document_keyset_batch_with_statement_boundaries()
    {
        _pgsqlBatch
            .Should()
            .StartWith(
                """
                DROP TABLE IF EXISTS "page";
                CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY) ON COMMIT DROP;

                INSERT INTO "page" ("DocumentId") VALUES (@DocumentId);

                SELECT
                    d."DocumentId",
                """
            );
        _pgsqlBatch.Should().Contain("ORDER BY d.\"DocumentId\";\n\nSELECT root columns FROM root;\n\n");
        _pgsqlBatch.Should().Contain("SELECT root columns FROM root;\n\nSELECT child columns FROM child;\n");

        _mssqlBatch
            .Should()
            .StartWith(
                """
                IF OBJECT_ID('tempdb..[#page]') IS NOT NULL
                    DROP TABLE [#page];
                CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY);

                INSERT INTO [#page] ([DocumentId]) VALUES (@DocumentId);

                SELECT
                    d.[DocumentId],
                """
            );
        _mssqlBatch.Should().Contain("ORDER BY d.[DocumentId];\n\nSELECT root columns FROM root;\n\n");
        _mssqlBatch.Should().Contain("SELECT root columns FROM root;\n\nSELECT child columns FROM child;\n");
    }

    [Test]
    public void It_should_emit_document_metadata_select()
    {
        _pgsqlBatch.Should().Contain("\"DocumentUuid\"");
        _pgsqlBatch.Should().Contain("\"ContentVersion\"");
        _pgsqlBatch.Should().Contain("\"IdentityVersion\"");
        _pgsqlBatch.Should().Contain("\"ContentLastModifiedAt\"");
        _pgsqlBatch.Should().Contain("\"IdentityLastModifiedAt\"");
        _pgsqlBatch.Should().Contain("\"dms\".\"Document\"");
    }

    [Test]
    public void It_should_emit_document_metadata_columns_in_fixed_reader_ordinal_order()
    {
        AssertAppearsInOrder(
            _pgsqlBatch,
            "d.\"DocumentId\"",
            "d.\"DocumentUuid\"",
            "d.\"ContentVersion\"",
            "d.\"IdentityVersion\"",
            "d.\"ContentLastModifiedAt\"",
            "d.\"IdentityLastModifiedAt\""
        );
        AssertAppearsInOrder(
            _mssqlBatch,
            "d.[DocumentId]",
            "d.[DocumentUuid]",
            "d.[ContentVersion]",
            "d.[IdentityVersion]",
            "d.[ContentLastModifiedAt]",
            "d.[IdentityLastModifiedAt]"
        );
    }

    [Test]
    public void It_should_emit_deterministic_order_by_on_document_metadata()
    {
        _pgsqlBatch.Should().Contain("ORDER BY d.\"DocumentId\"");
        _mssqlBatch.Should().Contain("ORDER BY d.[DocumentId]");
    }

    [Test]
    public void It_should_emit_table_hydration_selects()
    {
        _pgsqlBatch.Should().Contain("SELECT root columns FROM root");
        _pgsqlBatch.Should().Contain("SELECT child columns FROM child");
    }

    [Test]
    public void It_should_not_emit_total_count()
    {
        _pgsqlBatch.Should().NotContain("TotalCount");
    }

    [Test]
    public void It_should_emit_result_sets_in_correct_order()
    {
        // The document metadata select must come before the table hydration selects
        var docMetadataIndex = _pgsqlBatch.IndexOf("\"dms\".\"Document\"", StringComparison.Ordinal);
        var rootSelectIndex = _pgsqlBatch.IndexOf("SELECT root columns FROM root", StringComparison.Ordinal);
        var childSelectIndex = _pgsqlBatch.IndexOf(
            "SELECT child columns FROM child",
            StringComparison.Ordinal
        );

        docMetadataIndex.Should().BePositive();
        rootSelectIndex.Should().BeGreaterThan(docMetadataIndex);
        childSelectIndex.Should().BeGreaterThan(rootSelectIndex);
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Pgsql_Single_Document_Fast_Path
{
    private const string RootDescriptorKeysetSql = "SELECT descriptor keyset rows FROM root_descriptor;";
    private const string RootDescriptorSingleDocumentSql =
        "SELECT descriptor single-document rows FROM root_descriptor;";
    private const string ChildDescriptorKeysetSql = "SELECT descriptor keyset rows FROM child_descriptor;";
    private const string ChildDescriptorSingleDocumentSql =
        "SELECT descriptor single-document rows FROM child_descriptor;";
    private const string LookupKeysetSql = "SELECT lookup keyset rows FROM document_reference_lookup;";
    private const string LookupSingleDocumentSql =
        "SELECT lookup single-document rows FROM document_reference_lookup;";

    private string _batch = null!;

    [SetUp]
    public void Setup()
    {
        _batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(
                SqlDialect.Pgsql,
                BuildDescriptorPlans(),
                BuildLookupPlan(),
                includeSingleDocumentSql: true
            ),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: true,
                UseSingleDocumentFastPath: true
            )
        );
    }

    [Test]
    public void It_should_start_with_direct_document_metadata_select()
    {
        _batch
            .Should()
            .StartWith(
                """
                SELECT
                    d."DocumentId",
                    d."DocumentUuid",
                    d."ContentVersion",
                    d."IdentityVersion",
                    d."ContentLastModifiedAt",
                    d."IdentityLastModifiedAt"
                FROM "dms"."Document" d
                WHERE d."DocumentId" = @DocumentId
                ORDER BY d."DocumentId";
                """
            );
    }

    [Test]
    public void It_should_not_emit_pgsql_page_temp_table_work()
    {
        _batch.Should().NotContain("DROP TABLE IF EXISTS \"page\"");
        _batch.Should().NotContain("CREATE TEMP TABLE \"page\"");
        _batch.Should().NotContain("INSERT INTO \"page\"");
        _batch.Should().NotContain("INNER JOIN \"page\"");
        _batch.Should().NotContain("\"page\"");
    }

    [Test]
    public void It_should_emit_single_document_result_sets_in_reader_order()
    {
        AssertAppearsInOrder(
            _batch,
            "FROM \"dms\".\"Document\" d",
            HydrationBatchBuilderTestHelper.RootSingleDocumentSql,
            HydrationBatchBuilderTestHelper.ChildSingleDocumentSql,
            RootDescriptorSingleDocumentSql,
            ChildDescriptorSingleDocumentSql,
            LookupSingleDocumentSql
        );

        _batch.Should().NotContain("SELECT root columns FROM root;");
        _batch.Should().NotContain("SELECT child columns FROM child;");
        _batch.Should().NotContain(RootDescriptorKeysetSql);
        _batch.Should().NotContain(ChildDescriptorKeysetSql);
        _batch.Should().NotContain(LookupKeysetSql);
    }

    [Test]
    public void It_should_honor_descriptor_and_lookup_execution_options()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(
                SqlDialect.Pgsql,
                BuildDescriptorPlans(),
                BuildLookupPlan(),
                includeSingleDocumentSql: true
            ),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: false,
                IncludeDocumentReferenceLookup: false,
                UseSingleDocumentFastPath: true
            )
        );

        batch.Should().Contain(HydrationBatchBuilderTestHelper.RootSingleDocumentSql);
        batch.Should().Contain(HydrationBatchBuilderTestHelper.ChildSingleDocumentSql);
        batch.Should().NotContain(RootDescriptorSingleDocumentSql);
        batch.Should().NotContain(ChildDescriptorSingleDocumentSql);
        batch.Should().NotContain(LookupSingleDocumentSql);
    }

    [Test]
    public void It_should_reuse_cached_single_document_batch_for_the_same_plan_and_options()
    {
        var readPlan = BuildTestReadPlan(
            SqlDialect.Pgsql,
            BuildDescriptorPlans(),
            BuildLookupPlan(),
            includeSingleDocumentSql: true
        );
        var keyset = new PageKeysetSpec.Single(42L);
        var options = new HydrationExecutionOptions(
            IncludeDescriptorProjection: true,
            IncludeDocumentReferenceLookup: true,
            UseSingleDocumentFastPath: true
        );

        var firstBatch = HydrationBatchBuilder.Build(readPlan, keyset, SqlDialect.Pgsql, options);
        var secondBatch = HydrationBatchBuilder.Build(readPlan, keyset, SqlDialect.Pgsql, options);

        ReferenceEquals(firstBatch, secondBatch).Should().BeTrue();
    }

    [Test]
    public void It_should_cache_distinct_single_document_batches_for_distinct_execution_options()
    {
        var readPlan = BuildTestReadPlan(
            SqlDialect.Pgsql,
            BuildDescriptorPlans(),
            BuildLookupPlan(),
            includeSingleDocumentSql: true
        );
        var keyset = new PageKeysetSpec.Single(42L);

        var fullBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: true,
                UseSingleDocumentFastPath: true
            )
        );
        var storageOnlyBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: false,
                IncludeDocumentReferenceLookup: false,
                UseSingleDocumentFastPath: true
            )
        );
        var repeatedStorageOnlyBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: false,
                IncludeDocumentReferenceLookup: false,
                UseSingleDocumentFastPath: true
            )
        );

        fullBatch.Should().Contain(RootDescriptorSingleDocumentSql);
        fullBatch.Should().Contain(LookupSingleDocumentSql);
        storageOnlyBatch.Should().NotContain(RootDescriptorSingleDocumentSql);
        storageOnlyBatch.Should().NotContain(LookupSingleDocumentSql);
        ReferenceEquals(fullBatch, storageOnlyBatch).Should().BeFalse();
        ReferenceEquals(storageOnlyBatch, repeatedStorageOnlyBatch).Should().BeTrue();
    }

    [Test]
    public void It_should_keep_the_keyset_path_when_the_feature_flag_is_disabled()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(
                SqlDialect.Pgsql,
                BuildDescriptorPlans(),
                BuildLookupPlan(),
                includeSingleDocumentSql: true
            ),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: true,
                UseSingleDocumentFastPath: false
            )
        );

        batch.Should().Contain("DROP TABLE IF EXISTS \"page\";");
        batch.Should().Contain("INSERT INTO \"page\" (\"DocumentId\") VALUES (@DocumentId);");
        batch.Should().Contain("SELECT root columns FROM root;");
        batch.Should().Contain("SELECT child columns FROM child;");
        batch.Should().Contain(RootDescriptorKeysetSql);
        batch.Should().Contain(LookupKeysetSql);
        batch.Should().NotContain(HydrationBatchBuilderTestHelper.RootSingleDocumentSql);
        batch.Should().NotContain(RootDescriptorSingleDocumentSql);
        batch.Should().NotContain(LookupSingleDocumentSql);
    }

    [Test]
    public void It_should_match_the_existing_single_document_keyset_batch_when_the_feature_flag_is_disabled()
    {
        var keyset = new PageKeysetSpec.Single(42L);
        var expectedKeysetBatch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, BuildDescriptorPlans(), BuildLookupPlan()),
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: true
            )
        );

        var disabledFastPathBatch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(
                SqlDialect.Pgsql,
                BuildDescriptorPlans(),
                BuildLookupPlan(),
                includeSingleDocumentSql: true
            ),
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: true,
                UseSingleDocumentFastPath: false
            )
        );

        disabledFastPathBatch.Should().Be(expectedKeysetBatch);
    }

    [Test]
    public void It_should_keep_the_keyset_path_for_query_keysets_even_when_the_feature_flag_is_enabled()
    {
        var queryPlan = new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r ORDER BY r.\"DocumentId\" LIMIT @limit OFFSET @offset",
            TotalCountSql: null,
            PageParametersInOrder:
            [
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            TotalCountParametersInOrder: null
        );
        var keyset = new PageKeysetSpec.Query(
            queryPlan,
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );

        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, includeSingleDocumentSql: true),
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: true)
        );

        batch.Should().Contain("DROP TABLE IF EXISTS \"page\";");
        batch.Should().Contain("WITH page_ids AS (");
        batch.Should().Contain("INSERT INTO \"page\" (\"DocumentId\")");
        batch.Should().Contain("SELECT root columns FROM root;");
        batch.Should().NotContain(HydrationBatchBuilderTestHelper.RootSingleDocumentSql);
    }

    [Test]
    public void It_should_match_the_existing_query_keyset_batch_when_the_feature_flag_is_enabled()
    {
        var queryPlan = new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r ORDER BY r.\"DocumentId\" LIMIT @limit OFFSET @offset",
            TotalCountSql: null,
            PageParametersInOrder:
            [
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            TotalCountParametersInOrder: null
        );
        var keyset = new PageKeysetSpec.Query(
            queryPlan,
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );
        var readPlan = BuildTestReadPlan(SqlDialect.Pgsql, includeSingleDocumentSql: true);

        var expectedKeysetBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: false)
        );
        var fastPathFlaggedBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: true)
        );

        fastPathFlaggedBatch.Should().Be(expectedKeysetBatch);
    }

    [Test]
    public void It_should_keep_the_keyset_path_for_sql_server_even_when_the_feature_flag_is_enabled()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Mssql),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Mssql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: true)
        );

        batch.Should().Contain("CREATE TABLE [#page]");
        batch.Should().Contain("INSERT INTO [#page] ([DocumentId]) VALUES (@DocumentId);");
        batch.Should().Contain("SELECT root columns FROM root;");
        batch.Should().NotContain(HydrationBatchBuilderTestHelper.RootSingleDocumentSql);
    }

    [Test]
    public void It_should_match_the_existing_sql_server_keyset_batch_when_the_feature_flag_is_enabled()
    {
        var readPlan = BuildTestReadPlan(SqlDialect.Mssql);
        var keyset = new PageKeysetSpec.Single(42L);

        var expectedKeysetBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Mssql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: false)
        );
        var fastPathFlaggedBatch = HydrationBatchBuilder.Build(
            readPlan,
            keyset,
            SqlDialect.Mssql,
            new HydrationExecutionOptions(UseSingleDocumentFastPath: true)
        );

        fastPathFlaggedBatch.Should().Be(expectedKeysetBatch);
    }

    private static DescriptorProjectionPlan[] BuildDescriptorPlans() =>
        [
            new DescriptorProjectionPlan(
                SelectByKeysetSql: RootDescriptorKeysetSql,
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: [],
                SelectBySingleDocumentSql: RootDescriptorSingleDocumentSql
            ),
            new DescriptorProjectionPlan(
                SelectByKeysetSql: ChildDescriptorKeysetSql,
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: [],
                SelectBySingleDocumentSql: ChildDescriptorSingleDocumentSql
            ),
        ];

    private static DocumentReferenceLookupPlan BuildLookupPlan() =>
        new(
            SelectByKeysetSql: LookupKeysetSql,
            ResultShape: new DocumentReferenceLookupResultShape(
                DocumentIdOrdinal: 0,
                DocumentUuidOrdinal: 1,
                ResourceKeyIdOrdinal: 2
            ),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ],
            SelectBySingleDocumentSql: LookupSingleDocumentSql
        );
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Pgsql_Single_Document_Fast_Path_Missing_Sql
{
    [Test]
    public void It_should_throw_when_a_table_plan_is_missing_single_document_sql()
    {
        var act = () =>
            HydrationBatchBuilder.Build(
                BuildTestReadPlan(SqlDialect.Pgsql),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(UseSingleDocumentFastPath: true)
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .Be(
                "PostgreSQL single-document hydration requires table read plan at index '0' for table 'edfi.School' to provide SelectBySingleDocumentSql."
            );
    }

    [Test]
    public void It_should_throw_when_a_table_plan_has_empty_single_document_sql()
    {
        var act = () =>
            HydrationBatchBuilder.Build(
                BuildTestReadPlan(
                    SqlDialect.Pgsql,
                    rootSelectBySingleDocumentSql: "   ",
                    childSelectBySingleDocumentSql: HydrationBatchBuilderTestHelper.ChildSingleDocumentSql
                ),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(UseSingleDocumentFastPath: true)
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("table read plan at index '0'");
        exception.Message.Should().Contain("SelectBySingleDocumentSql");
    }

    [Test]
    public void It_should_throw_when_an_enabled_descriptor_plan_is_missing_single_document_sql()
    {
        var descriptorPlan = new DescriptorProjectionPlan(
            SelectByKeysetSql: "SELECT descriptor keyset rows FROM root_descriptor;",
            ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
            SourcesInOrder: []
        );

        var act = () =>
            HydrationBatchBuilder.Build(
                BuildTestReadPlan(
                    SqlDialect.Pgsql,
                    descriptorProjectionPlans: [descriptorPlan],
                    includeSingleDocumentSql: true
                ),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: true,
                    UseSingleDocumentFastPath: true
                )
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .Be(
                "PostgreSQL single-document hydration requires descriptor projection plan at index '0' to provide SelectBySingleDocumentSql."
            );
    }

    [Test]
    public void It_should_not_require_descriptor_single_document_sql_when_descriptor_projection_is_disabled()
    {
        var descriptorPlan = new DescriptorProjectionPlan(
            SelectByKeysetSql: "SELECT descriptor keyset rows FROM root_descriptor;",
            ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
            SourcesInOrder: []
        );

        var act = () =>
            HydrationBatchBuilder.Build(
                BuildTestReadPlan(
                    SqlDialect.Pgsql,
                    descriptorProjectionPlans: [descriptorPlan],
                    includeSingleDocumentSql: true
                ),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: false,
                    UseSingleDocumentFastPath: true
                )
            );

        act.Should().NotThrow();
    }

    [Test]
    public void It_should_throw_when_an_enabled_lookup_plan_is_missing_single_document_sql()
    {
        var lookupPlan = new DocumentReferenceLookupPlan(
            SelectByKeysetSql: "SELECT lookup keyset rows FROM lookup;",
            ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

        var act = () =>
            HydrationBatchBuilder.Build(
                BuildTestReadPlan(
                    SqlDialect.Pgsql,
                    documentReferenceLookup: lookupPlan,
                    includeSingleDocumentSql: true
                ),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: false,
                    IncludeDocumentReferenceLookup: true,
                    UseSingleDocumentFastPath: true
                )
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .Be(
                "PostgreSQL single-document hydration requires document-reference lookup plan to provide SelectBySingleDocumentSql."
            );
    }

    [Test]
    public void It_should_not_require_lookup_single_document_sql_when_lookup_is_disabled()
    {
        var lookupPlan = new DocumentReferenceLookupPlan(
            SelectByKeysetSql: "SELECT lookup keyset rows FROM lookup;",
            ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

        var act = () =>
            HydrationBatchBuilder.Build(
                BuildTestReadPlan(
                    SqlDialect.Pgsql,
                    documentReferenceLookup: lookupPlan,
                    includeSingleDocumentSql: true
                ),
                new PageKeysetSpec.Single(42L),
                SqlDialect.Pgsql,
                new HydrationExecutionOptions(
                    IncludeDescriptorProjection: false,
                    IncludeDocumentReferenceLookup: false,
                    UseSingleDocumentFastPath: true
                )
            );

        act.Should().NotThrow();
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Query_Keyset
{
    private string _pgsqlBatch = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildTestReadPlan(SqlDialect.Pgsql);
        var queryPlan = new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r ORDER BY r.\"DocumentId\" LIMIT @limit OFFSET @offset",
            TotalCountSql: "SELECT COUNT(*) AS TotalCount FROM \"edfi\".\"School\" r",
            PageParametersInOrder:
            [
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            TotalCountParametersInOrder: []
        );
        var keyset = new PageKeysetSpec.Query(
            queryPlan,
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );

        _pgsqlBatch = HydrationBatchBuilder.Build(plan, keyset, SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_emit_cte_based_keyset_materialization()
    {
        _pgsqlBatch.Should().Contain("WITH page_ids AS (");
        _pgsqlBatch.Should().Contain("INSERT INTO");
        _pgsqlBatch.Should().Contain("FROM page_ids");
    }

    [Test]
    public void It_should_emit_total_count_sql()
    {
        _pgsqlBatch.Should().Contain("SELECT COUNT(*) AS TotalCount");
    }

    [Test]
    public void It_should_emit_total_count_before_document_metadata()
    {
        var totalCountIndex = _pgsqlBatch.IndexOf("SELECT COUNT(*)", StringComparison.Ordinal);
        var docMetadataIndex = _pgsqlBatch.IndexOf("\"dms\".\"Document\"", StringComparison.Ordinal);

        totalCountIndex.Should().BePositive();
        docMetadataIndex.Should().BeGreaterThan(totalCountIndex);
    }

    [Test]
    public void It_should_emit_query_keyset_batch_with_terminated_statement_boundaries()
    {
        _pgsqlBatch
            .Should()
            .StartWith(
                """
                DROP TABLE IF EXISTS "page";
                CREATE TEMP TABLE "page" ("DocumentId" bigint PRIMARY KEY) ON COMMIT DROP;

                WITH page_ids AS (
                SELECT r."DocumentId" FROM "edfi"."School" r ORDER BY r."DocumentId" LIMIT @limit OFFSET @offset
                )
                INSERT INTO "page" ("DocumentId")
                SELECT "DocumentId" FROM page_ids;

                SELECT COUNT(*) AS TotalCount FROM "edfi"."School" r;

                SELECT
                """
            );
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Compiled_Query_Keyset
{
    private string _pgsqlBatch = null!;
    private string _mssqlBatch = null!;

    [SetUp]
    public void Setup()
    {
        foreach (var dialect in new[] { SqlDialect.Pgsql, SqlDialect.Mssql })
        {
            var plan = BuildTestReadPlan(dialect);
            var compiler = new PageDocumentIdSqlCompiler(dialect);
            var compiledQueryPlan = compiler.Compile(
                new PageDocumentIdQuerySpec(
                    RootTable: new DbTableName(new DbSchemaName("edfi"), "School"),
                    Predicates: [],
                    UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                    IncludeTotalCountSql: true
                )
            );
            var keyset = new PageKeysetSpec.Query(
                compiledQueryPlan,
                new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
            );

            var batch = HydrationBatchBuilder.Build(plan, keyset, dialect);

            if (dialect == SqlDialect.Pgsql)
            {
                _pgsqlBatch = batch;
            }
            else
            {
                _mssqlBatch = batch;
            }
        }
    }

    [Test]
    public void It_should_not_embed_semicolon_inside_cte_body()
    {
        // PageDocumentIdSqlCompiler emits a trailing semicolon on its SQL.
        // HydrationBatchBuilder must strip it before embedding in the CTE.
        var pgsqlCteStart = _pgsqlBatch.IndexOf("WITH page_ids AS (", StringComparison.Ordinal);
        var pgsqlCteEnd = _pgsqlBatch.IndexOf(")\nINSERT INTO", StringComparison.Ordinal);
        pgsqlCteStart.Should().BePositive();
        pgsqlCteEnd.Should().BeGreaterThan(pgsqlCteStart);
        var pgsqlCteBody = _pgsqlBatch[(pgsqlCteStart + "WITH page_ids AS (".Length)..pgsqlCteEnd];
        pgsqlCteBody.Should().NotContain(";", "semicolons inside a CTE body produce invalid SQL");

        var mssqlCteStart = _mssqlBatch.IndexOf("WITH page_ids AS (", StringComparison.Ordinal);
        var mssqlCteEnd = _mssqlBatch.IndexOf(")\nINSERT INTO", StringComparison.Ordinal);
        mssqlCteStart.Should().BePositive();
        mssqlCteEnd.Should().BeGreaterThan(mssqlCteStart);
        var mssqlCteBody = _mssqlBatch[(mssqlCteStart + "WITH page_ids AS (".Length)..mssqlCteEnd];
        mssqlCteBody.Should().NotContain(";", "semicolons inside a CTE body produce invalid SQL");
    }

    [Test]
    public void It_should_emit_valid_cte_structure()
    {
        _pgsqlBatch.Should().Contain("WITH page_ids AS (");
        _pgsqlBatch.Should().Contain("FROM page_ids;");
        _mssqlBatch.Should().Contain("WITH page_ids AS (");
        _mssqlBatch.Should().Contain("FROM page_ids;");
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Zero_Limit_Query_Keyset
{
    private string _mssqlBatchWithTotalCount = null!;
    private string _mssqlBatchWithoutTotalCount = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildTestReadPlan(SqlDialect.Mssql);
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        _mssqlBatchWithTotalCount = HydrationBatchBuilder.Build(
            plan,
            CreateZeroLimitKeyset(compiler, includeTotalCountSql: true),
            SqlDialect.Mssql
        );
        _mssqlBatchWithoutTotalCount = HydrationBatchBuilder.Build(
            plan,
            CreateZeroLimitKeyset(compiler, includeTotalCountSql: false),
            SqlDialect.Mssql
        );
    }

    [Test]
    public void It_should_materialize_an_empty_keyset_without_embedding_page_sql()
    {
        _mssqlBatchWithTotalCount.Should().Contain("INSERT INTO [#page] ([DocumentId])");
        _mssqlBatchWithTotalCount
            .Should()
            .Contain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
        _mssqlBatchWithTotalCount.Should().NotContain("WITH page_ids AS (");
        _mssqlBatchWithTotalCount.Should().NotContain("FETCH NEXT @limit ROWS ONLY");
    }

    [Test]
    public void It_should_still_emit_total_count_sql_before_document_metadata()
    {
        var totalCountIndex = _mssqlBatchWithTotalCount.IndexOf("SELECT COUNT(1)", StringComparison.Ordinal);
        var docMetadataIndex = _mssqlBatchWithTotalCount.IndexOf(
            "[dms].[Document]",
            StringComparison.Ordinal
        );

        totalCountIndex.Should().BePositive();
        docMetadataIndex.Should().BeGreaterThan(totalCountIndex);
    }

    [Test]
    public void It_should_not_emit_total_count_sql_when_not_requested()
    {
        _mssqlBatchWithoutTotalCount.Should().NotContain("SELECT COUNT(1)");
        _mssqlBatchWithoutTotalCount
            .Should()
            .Contain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
    }

    [TestCaseSource(nameof(ZeroLimitValues))]
    public void It_should_materialize_an_empty_keyset_for_all_integral_zero_limit_values(
        object zeroLimitValue
    )
    {
        var plan = BuildTestReadPlan(SqlDialect.Mssql);
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var batch = HydrationBatchBuilder.Build(
            plan,
            CreateLimitKeyset(compiler, includeTotalCountSql: true, zeroLimitValue),
            SqlDialect.Mssql
        );

        batch.Should().Contain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
        batch.Should().NotContain("WITH page_ids AS (");
        batch.Should().NotContain("FETCH NEXT @limit ROWS ONLY");
    }

    [TestCaseSource(nameof(NonZeroOrUnsupportedLimitValues))]
    public void It_should_materialize_the_query_keyset_for_non_zero_or_unsupported_limit_values(
        object limitValue
    )
    {
        var plan = BuildTestReadPlan(SqlDialect.Mssql);
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var batch = HydrationBatchBuilder.Build(
            plan,
            CreateLimitKeyset(compiler, includeTotalCountSql: true, limitValue),
            SqlDialect.Mssql
        );

        batch.Should().Contain("WITH page_ids AS (");
        batch.Should().Contain("FETCH NEXT @limit ROWS ONLY");
        batch.Should().NotContain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
    }

    [Test]
    public void It_should_materialize_the_query_keyset_when_the_query_plan_has_no_limit_parameter()
    {
        var plan = BuildTestReadPlan(SqlDialect.Mssql);
        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: "SELECT r.[DocumentId] FROM [edfi].[School] r ORDER BY r.[DocumentId]",
                TotalCountSql: null,
                PageParametersInOrder: [new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset")],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?> { ["offset"] = 0L }
        );

        var batch = HydrationBatchBuilder.Build(plan, keyset, SqlDialect.Mssql);

        batch.Should().Contain("WITH page_ids AS (");
        batch.Should().Contain("SELECT r.[DocumentId] FROM [edfi].[School] r ORDER BY r.[DocumentId]");
        batch.Should().NotContain("SELECT CAST(NULL AS bigint) AS [DocumentId] WHERE 1 = 0;");
    }

    private static PageKeysetSpec.Query CreateZeroLimitKeyset(
        PageDocumentIdSqlCompiler compiler,
        bool includeTotalCountSql
    ) => CreateLimitKeyset(compiler, includeTotalCountSql, 0L);

    private static PageKeysetSpec.Query CreateLimitKeyset(
        PageDocumentIdSqlCompiler compiler,
        bool includeTotalCountSql,
        object limitValue
    )
    {
        var compiledQueryPlan = compiler.Compile(
            new PageDocumentIdQuerySpec(
                RootTable: new DbTableName(new DbSchemaName("edfi"), "School"),
                Predicates: [],
                UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                IncludeTotalCountSql: includeTotalCountSql
            )
        );

        return new PageKeysetSpec.Query(
            compiledQueryPlan,
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = limitValue }
        );
    }

    private static IEnumerable<object> ZeroLimitValues() =>
        [(byte)0, (sbyte)0, (short)0, (ushort)0, 0, 0U, 0L, 0UL];

    private static IEnumerable<object> NonZeroOrUnsupportedLimitValues() =>
        [(byte)1, (sbyte)1, (short)1, (ushort)1, 1, 1U, 1L, 1UL, "0"];
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Query_Keyset_Without_TotalCount
{
    private string _pgsqlBatch = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildTestReadPlan(SqlDialect.Pgsql);
        var queryPlan = new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r ORDER BY r.\"DocumentId\" LIMIT @limit OFFSET @offset",
            TotalCountSql: null,
            PageParametersInOrder:
            [
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            TotalCountParametersInOrder: null
        );
        var keyset = new PageKeysetSpec.Query(
            queryPlan,
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );

        _pgsqlBatch = HydrationBatchBuilder.Build(plan, keyset, SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_not_emit_total_count_sql()
    {
        _pgsqlBatch.Should().NotContain("TotalCount");
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_AddParameters_With_Query_Keyset
{
    [Test]
    public void It_should_throw_when_a_required_page_parameter_value_is_missing()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(totalCountParameterNames: null),
            new Dictionary<string, object?> { ["offset"] = 0L }
        );

        var act = () => HydrationBatchBuilder.AddParameters(command, keyset);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("'limit'");
        command.Parameters.Count.Should().Be(0);
    }

    [Test]
    public void It_should_throw_when_a_required_total_count_parameter_value_is_missing()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(totalCountParameterNames: ["schoolYear"]),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );

        var act = () => HydrationBatchBuilder.AddParameters(command, keyset);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("'schoolYear'");
        command.Parameters.Count.Should().Be(0);
    }

    [Test]
    public void It_should_treat_present_null_values_as_bound_parameters()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParameterNames: ["schoolYear", "offset", "limit"],
                totalCountParameterNames: null
            ),
            new Dictionary<string, object?>
            {
                ["schoolYear"] = null,
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        HydrationBatchBuilder.AddParameters(command, keyset);

        command.Parameters.Count.Should().Be(3);
        command.Parameters.Contains("@schoolYear").Should().BeTrue();
    }

    private static PageDocumentIdSqlPlan CreateQueryPlan(
        string[]? pageParameterNames = null,
        string[]? totalCountParameterNames = null
    )
    {
        pageParameterNames ??= ["offset", "limit"];

        return new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT r.\"DocumentId\" FROM \"edfi\".\"School\" r ORDER BY r.\"DocumentId\" LIMIT @limit OFFSET @offset",
            TotalCountSql: totalCountParameterNames is null
                ? null
                : "SELECT COUNT(*) AS TotalCount FROM \"edfi\".\"School\" r WHERE r.\"SchoolYear\" = @schoolYear",
            PageParametersInOrder: [.. pageParameterNames.Select(CreatePageParameter)],
            TotalCountParametersInOrder: totalCountParameterNames is null
                ? null
                : [.. totalCountParameterNames.Select(CreateTotalCountParameter)]
        );
    }

    private static QuerySqlParameter CreatePageParameter(string parameterName) =>
        parameterName switch
        {
            "offset" => new QuerySqlParameter(QuerySqlParameterRole.Offset, parameterName),
            "limit" => new QuerySqlParameter(QuerySqlParameterRole.Limit, parameterName),
            _ => new QuerySqlParameter(QuerySqlParameterRole.Filter, parameterName),
        };

    private static QuerySqlParameter CreateTotalCountParameter(string parameterName) =>
        new(QuerySqlParameterRole.Filter, parameterName);
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Descriptor_Projection_Plans
{
    private string _pgsqlBatch = null!;
    private string _pgsqlBatchWithoutDescriptorProjection = null!;

    [SetUp]
    public void Setup()
    {
        var descriptorPlans = new[]
        {
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM root_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM child_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        };

        _pgsqlBatch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql
        );
        _pgsqlBatchWithoutDescriptorProjection = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(IncludeDescriptorProjection: false)
        );
    }

    [Test]
    public void It_should_emit_descriptor_projection_result_sets_after_table_hydration()
    {
        var childSelectIndex = _pgsqlBatch.IndexOf(
            "SELECT child columns FROM child;",
            StringComparison.Ordinal
        );
        var firstDescriptorIndex = _pgsqlBatch.IndexOf(
            "SELECT descriptor rows FROM root_descriptor;",
            StringComparison.Ordinal
        );
        var secondDescriptorIndex = _pgsqlBatch.IndexOf(
            "SELECT descriptor rows FROM child_descriptor;",
            StringComparison.Ordinal
        );

        childSelectIndex.Should().BePositive();
        firstDescriptorIndex.Should().BeGreaterThan(childSelectIndex);
        secondDescriptorIndex.Should().BeGreaterThan(firstDescriptorIndex);
        _pgsqlBatch
            .Should()
            .Contain(
                "SELECT child columns FROM child;\n\nSELECT descriptor rows FROM root_descriptor;\n\nSELECT descriptor rows FROM child_descriptor;\n\n"
            );
        _pgsqlBatch.Should().NotContain(";;");
    }

    [Test]
    public void It_should_allow_descriptor_projection_to_be_omitted()
    {
        _pgsqlBatchWithoutDescriptorProjection
            .Should()
            .NotContain("SELECT descriptor rows FROM root_descriptor;");
        _pgsqlBatchWithoutDescriptorProjection
            .Should()
            .NotContain("SELECT descriptor rows FROM child_descriptor;");
        _pgsqlBatchWithoutDescriptorProjection.Should().Contain("SELECT child columns FROM child;");
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Document_Reference_Lookup_Plan
{
    private const string LookupSqlMarker = "SELECT lookup rows FROM document_reference_lookup;";
    private const string FirstDescriptorSqlMarker = "SELECT descriptor rows FROM root_descriptor;";
    private const string SecondDescriptorSqlMarker = "SELECT descriptor rows FROM child_descriptor;";

    private static DocumentReferenceLookupPlan BuildLookupPlan() =>
        new(
            SelectByKeysetSql: LookupSqlMarker,
            ResultShape: new DocumentReferenceLookupResultShape(
                DocumentIdOrdinal: 0,
                DocumentUuidOrdinal: 1,
                ResourceKeyIdOrdinal: 2
            ),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

    private static DescriptorProjectionPlan[] BuildDescriptorPlans() =>
        [
            new DescriptorProjectionPlan(
                SelectByKeysetSql: FirstDescriptorSqlMarker,
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
            new DescriptorProjectionPlan(
                SelectByKeysetSql: SecondDescriptorSqlMarker,
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        ];

    [Test]
    public void It_should_emit_lookup_sql_after_descriptor_projections_when_plan_carries_lookup()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, BuildDescriptorPlans(), BuildLookupPlan()),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql
        );

        AssertAppearsInOrder(
            batch,
            "SELECT child columns FROM child;",
            FirstDescriptorSqlMarker,
            SecondDescriptorSqlMarker,
            LookupSqlMarker
        );
        batch
            .Should()
            .Contain(
                $"SELECT child columns FROM child;\n\n{FirstDescriptorSqlMarker}\n\n{SecondDescriptorSqlMarker}\n\n{LookupSqlMarker}\n\n"
            );
        batch.Should().NotContain(";;");
    }

    [Test]
    public void It_should_emit_lookup_sql_after_table_hydration_when_descriptor_projection_is_omitted()
    {
        // The plan has no descriptor projections (empty list), so the lookup must immediately
        // follow the last table-hydration statement.
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorProjectionPlans: [], BuildLookupPlan()),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql
        );

        AssertAppearsInOrder(batch, "SELECT child columns FROM child;", LookupSqlMarker);
        batch.Should().NotContain("SELECT descriptor rows FROM");
    }

    [Test]
    public void It_should_omit_lookup_sql_when_plan_has_null_DocumentReferenceLookup()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, BuildDescriptorPlans(), documentReferenceLookup: null),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql
        );

        batch.Should().NotContain(LookupSqlMarker);
        batch.Should().Contain(FirstDescriptorSqlMarker);
        batch.Should().Contain(SecondDescriptorSqlMarker);
    }

    [Test]
    public void It_should_emit_lookup_sql_for_mssql_after_descriptor_projections()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Mssql, BuildDescriptorPlans(), BuildLookupPlan()),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Mssql
        );

        AssertAppearsInOrder(batch, FirstDescriptorSqlMarker, SecondDescriptorSqlMarker, LookupSqlMarker);
    }

    [Test]
    public void It_should_omit_lookup_sql_when_execution_option_opts_out_even_if_plan_has_lookup()
    {
        // Write-path callers (current-state load, committed readback) pass
        // IncludeDocumentReferenceLookup: false because they never consume the lookup result.
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, BuildDescriptorPlans(), BuildLookupPlan()),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: false
            )
        );

        batch.Should().NotContain(LookupSqlMarker);
        batch.Should().Contain(FirstDescriptorSqlMarker);
        batch.Should().Contain(SecondDescriptorSqlMarker);
    }

    [Test]
    public void It_should_omit_both_descriptor_and_lookup_when_execution_options_opt_out_of_both()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, BuildDescriptorPlans(), BuildLookupPlan()),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: false,
                IncludeDocumentReferenceLookup: false
            )
        );

        batch.Should().NotContain(LookupSqlMarker);
        batch.Should().NotContain(FirstDescriptorSqlMarker);
        batch.Should().NotContain(SecondDescriptorSqlMarker);
        batch.Should().Contain("SELECT child columns FROM child;");
    }
}

internal static class HydrationBatchBuilderTestHelper
{
    internal const string RootSingleDocumentSql = "SELECT root single-document columns FROM root;";
    internal const string ChildSingleDocumentSql = "SELECT child single-document columns FROM child;";

    public static ResourceReadPlan BuildTestReadPlan(
        SqlDialect dialect = SqlDialect.Pgsql,
        IReadOnlyList<DescriptorProjectionPlan>? descriptorProjectionPlans = null,
        DocumentReferenceLookupPlan? documentReferenceLookup = null,
        bool includeSingleDocumentSql = false,
        string? rootSelectBySingleDocumentSql = null,
        string? childSelectBySingleDocumentSql = null
    )
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "School"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_School",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolId",
                        [new JsonPathSegment.Property("schoolId")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("City"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 100),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].city",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("city"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        // Use stub SelectByKeysetSql values for batch builder testing (SQL shape validation)
        var rootTablePlan = new TableReadPlan(
            rootTable,
            "SELECT root columns FROM root;",
            rootSelectBySingleDocumentSql ?? (includeSingleDocumentSql ? RootSingleDocumentSql : null)
        );
        var childTablePlan = new TableReadPlan(
            childTable,
            "SELECT child columns FROM child;",
            childSelectBySingleDocumentSql ?? (includeSingleDocumentSql ? ChildSingleDocumentSql : null)
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(dialect),
            TablePlansInDependencyOrder: [rootTablePlan, childTablePlan],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: descriptorProjectionPlans ?? [],
            DocumentReferenceLookup: documentReferenceLookup
        );
    }

    internal static void AssertAppearsInOrder(string text, params string[] values)
    {
        var previousIndex = -1;

        foreach (var value in values)
        {
            var currentIndex = text.IndexOf(value, StringComparison.Ordinal);
            currentIndex.Should().BeGreaterThan(previousIndex, $"expected '{value}' in ordinal order");
            previousIndex = currentIndex;
        }
    }
}
