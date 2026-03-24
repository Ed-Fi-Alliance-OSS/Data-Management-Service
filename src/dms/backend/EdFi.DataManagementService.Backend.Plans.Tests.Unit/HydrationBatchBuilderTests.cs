// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        _pgsqlBatch.Should().Contain("CREATE TEMP TABLE");
        _pgsqlBatch.Should().Contain("ON COMMIT DROP");
    }

    [Test]
    public void It_should_emit_mssql_temp_table_creation()
    {
        _mssqlBatch.Should().Contain("CREATE TABLE");
        _mssqlBatch.Should().Contain("[#page]");
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

internal static class HydrationBatchBuilderTestHelper
{
    public static ResourceReadPlan BuildTestReadPlan(SqlDialect dialect = SqlDialect.Pgsql)
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
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    Kind: ColumnKind.CollectionKey,
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
        var rootTablePlan = new TableReadPlan(rootTable, "SELECT root columns FROM root;");
        var childTablePlan = new TableReadPlan(childTable, "SELECT child columns FROM child;");

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(dialect),
            TablePlansInDependencyOrder: [rootTablePlan, childTablePlan],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }
}
