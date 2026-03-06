// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ReadPlanCompiler : WritePlanCompilerTestBase
{
    private const string RuntimePlanCompilationFixturePath =
        "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private static readonly QualifiedResourceName _rootOnlyFixtureResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _multiTableFixtureResource = new(
        "Ed-Fi",
        "StudentAddressCollection"
    );
    private RelationalResourceModel _resourceModel = null!;
    private ResourceReadPlan _pgsqlReadPlan = null!;
    private ResourceReadPlan _mssqlReadPlan = null!;

    [SetUp]
    public void SetUpReadPlanCompiler()
    {
        _resourceModel = CreateMultiTableResourceModel();
        _pgsqlReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(_resourceModel);
        _mssqlReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(_resourceModel);
    }

    [Test]
    public void It_should_compile_a_table_plan_for_every_table_in_dependency_order()
    {
        _pgsqlReadPlan
            .TablePlansInDependencyOrder.Select(static tablePlan => tablePlan.TableModel)
            .Should()
            .Equal(_resourceModel.TablesInDependencyOrder);
    }

    [Test]
    public void It_should_preserve_the_keyset_contract_and_empty_projection_arrays_for_story_05()
    {
        _pgsqlReadPlan.Model.Should().Be(_resourceModel);
        _pgsqlReadPlan
            .KeysetTable.Should()
            .Be(KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql));
        _pgsqlReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        _pgsqlReadPlan.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_compile_identical_root_only_read_plans_across_repeated_compilation_and_fixture_resource_order_permutations()
    {
        var compiler = new ReadPlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(
            BuildFixtureResourceModel(_rootOnlyFixtureResource, SqlDialect.Pgsql, false)
        );
        var second = compiler.Compile(
            BuildFixtureResourceModel(_rootOnlyFixtureResource, SqlDialect.Pgsql, false)
        );
        var permuted = compiler.Compile(
            BuildFixtureResourceModel(_rootOnlyFixtureResource, SqlDialect.Pgsql, true)
        );

        var firstFingerprint = CreateReadPlanFingerprint(first);
        var secondFingerprint = CreateReadPlanFingerprint(second);
        var permutedFingerprint = CreateReadPlanFingerprint(permuted);

        secondFingerprint.Should().BeEquivalentTo(firstFingerprint);
        permutedFingerprint.Should().BeEquivalentTo(firstFingerprint);
    }

    [Test]
    public void It_should_compile_identical_multi_table_read_plans_across_repeated_compilation_and_fixture_resource_order_permutations()
    {
        var compiler = new ReadPlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(
            BuildFixtureResourceModel(_multiTableFixtureResource, SqlDialect.Pgsql, false)
        );
        var second = compiler.Compile(
            BuildFixtureResourceModel(_multiTableFixtureResource, SqlDialect.Pgsql, false)
        );
        var permuted = compiler.Compile(
            BuildFixtureResourceModel(_multiTableFixtureResource, SqlDialect.Pgsql, true)
        );

        var firstFingerprint = CreateReadPlanFingerprint(first);
        var secondFingerprint = CreateReadPlanFingerprint(second);
        var permutedFingerprint = CreateReadPlanFingerprint(permuted);

        secondFingerprint.Should().BeEquivalentTo(firstFingerprint);
        permutedFingerprint.Should().BeEquivalentTo(firstFingerprint);
    }

    [Test]
    public void It_should_emit_select_list_and_order_by_columns_in_model_order_for_every_table_plan()
    {
        AssertSqlProjectionAndOrderingMatchesModel(_pgsqlReadPlan);
        AssertSqlProjectionAndOrderingMatchesModel(_mssqlReadPlan);
    }

    [Test]
    public void It_should_use_stable_root_table_non_root_table_and_keyset_aliases_across_dialects()
    {
        AssertStableAliases(_pgsqlReadPlan);
        AssertStableAliases(_mssqlReadPlan);
    }

    [Test]
    public void It_should_emit_exact_pgsql_SelectByKeysetSql_for_root_child_nested_and_extension_tables()
    {
        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "Student",
            """
            SELECT
                r."DocumentId",
                r."StudentUniqueId"
            FROM "edfi"."Student" r
            INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
            ORDER BY
                r."DocumentId" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentAddress",
            """
            SELECT
                t."Student_DocumentId",
                t."Ordinal",
                t."City"
            FROM "edfi"."StudentAddress" t
            INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
            ORDER BY
                t."Student_DocumentId" ASC,
                t."Ordinal" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentAddressPeriod",
            """
            SELECT
                t."Student_DocumentId",
                t."AddressOrdinal",
                t."Ordinal",
                t."BeginDate"
            FROM "edfi"."StudentAddressPeriod" t
            INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
            ORDER BY
                t."Student_DocumentId" ASC,
                t."AddressOrdinal" ASC,
                t."Ordinal" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentExtension",
            """
            SELECT
                t."DocumentId",
                t."FavoriteColor"
            FROM "sample"."StudentExtension" t
            INNER JOIN "page" k ON t."DocumentId" = k."DocumentId"
            ORDER BY
                t."DocumentId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_emit_exact_mssql_SelectByKeysetSql_for_root_child_nested_and_extension_tables()
    {
        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "Student",
            """
            SELECT
                r.[DocumentId],
                r.[StudentUniqueId]
            FROM [edfi].[Student] r
            INNER JOIN [#page] k ON r.[DocumentId] = k.[DocumentId]
            ORDER BY
                r.[DocumentId] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentAddress",
            """
            SELECT
                t.[Student_DocumentId],
                t.[Ordinal],
                t.[City]
            FROM [edfi].[StudentAddress] t
            INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
            ORDER BY
                t.[Student_DocumentId] ASC,
                t.[Ordinal] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentAddressPeriod",
            """
            SELECT
                t.[Student_DocumentId],
                t.[AddressOrdinal],
                t.[Ordinal],
                t.[BeginDate]
            FROM [edfi].[StudentAddressPeriod] t
            INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
            ORDER BY
                t.[Student_DocumentId] ASC,
                t.[AddressOrdinal] ASC,
                t.[Ordinal] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentExtension",
            """
            SELECT
                t.[DocumentId],
                t.[FavoriteColor]
            FROM [sample].[StudentExtension] t
            INNER JOIN [#page] k ON t.[DocumentId] = k.[DocumentId]
            ORDER BY
                t.[DocumentId] ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_fail_fast_when_document_id_parent_key_part_is_not_first_in_key_order()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithDocumentIdNotFirstInKeyOrder());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': expected document-id ParentKeyPart key column ('DocumentId' or '*_DocumentId') to be first in key order, but found 'ParentAddressOrdinal:ParentKeyPart'. Key columns: [ParentAddressOrdinal:ParentKeyPart, DocumentId:ParentKeyPart, Ordinal:Ordinal]."
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_does_not_include_exactly_one_document_id_parent_key_part()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMissingDocumentIdParentKeyPart());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.Student': expected exactly one ParentKeyPart document-id key column ('DocumentId' or '*_DocumentId'), but found 0. Key columns: [SchoolYear:ParentKeyPart]."
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_column_kind_is_not_parent_key_part_or_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithUnsupportedKeyColumnKind());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.Student': key column 'SchoolYear' has unsupported kind 'Scalar'. Supported key kinds are ParentKeyPart and Ordinal."
            );
    }

    [Test]
    public void It_should_fail_fast_when_ordinal_key_column_is_not_last_in_key_order()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithOrdinalNotLastInKeyOrder());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': expected Ordinal key column to be last in key order. Key columns: [DocumentId:ParentKeyPart, Ordinal:Ordinal, ParentAddressOrdinal:ParentKeyPart]."
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_contains_multiple_ordinal_columns()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMultipleOrdinalKeyColumns());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': expected at most one Ordinal key column, but found 2. Key columns: [DocumentId:ParentKeyPart, ParentAddressOrdinal:ParentKeyPart, Ordinal:Ordinal, Ordinal:Ordinal]."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_fk_column_does_not_resolve_to_a_hydration_select_list_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMissingDocumentReferenceFkColumn());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': document-reference binding '$.addresses[*].schoolReference' FK column 'MissingSchool_DocumentId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_fail_fast_when_reference_identity_binding_column_does_not_resolve_to_a_hydration_select_list_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateModelWithMissingReferenceIdentityBindingColumn()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': reference-identity binding '$.addresses[*].schoolReference.schoolId' for reference '$.addresses[*].schoolReference' column 'MissingSchoolId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_fk_column_does_not_resolve_to_a_hydration_select_list_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMissingDescriptorEdgeFkColumn());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': descriptor edge source '$.addresses[*].programTypeDescriptor' FK column 'MissingProgramTypeDescriptorId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_mark_shared_descriptor_storage_as_unsupported()
    {
        var unsupportedModel = _resourceModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        ReadPlanCompiler.IsSupported(unsupportedModel).Should().BeFalse();

        var wasCompiled = new ReadPlanCompiler(SqlDialect.Pgsql).TryCompile(
            unsupportedModel,
            out var readPlan
        );

        wasCompiled.Should().BeFalse();
        readPlan.Should().BeNull();
    }

    private static void AssertSelectByKeysetSql(
        ResourceReadPlan readPlan,
        string tableName,
        string expectedSql
    )
    {
        readPlan
            .TablePlansInDependencyOrder.Single(tablePlan => tablePlan.TableModel.Table.Name == tableName)
            .SelectByKeysetSql.Should()
            .Be(expectedSql);
    }

    private static void AssertSqlProjectionAndOrderingMatchesModel(ResourceReadPlan readPlan)
    {
        foreach (var tablePlan in readPlan.TablePlansInDependencyOrder)
        {
            var expectedSelectList = tablePlan
                .TableModel.Columns.Select(static column => column.ColumnName.Value)
                .ToArray();
            var expectedOrderBy = tablePlan
                .TableModel.Key.Columns.Select(static column => column.ColumnName.Value)
                .ToArray();

            ExtractSelectedColumnNames(tablePlan.SelectByKeysetSql).Should().Equal(expectedSelectList);
            ExtractOrderByColumnNames(tablePlan.SelectByKeysetSql).Should().Equal(expectedOrderBy);
        }
    }

    private static void AssertStableAliases(ResourceReadPlan readPlan)
    {
        for (var index = 0; index < readPlan.TablePlansInDependencyOrder.Length; index++)
        {
            var tablePlan = readPlan.TablePlansInDependencyOrder[index];
            var expectedTableAlias =
                index == 0
                    ? PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root)
                    : PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Table);

            ExtractFromAlias(tablePlan.SelectByKeysetSql).Should().Be(expectedTableAlias);
            ExtractJoinAlias(tablePlan.SelectByKeysetSql)
                .Should()
                .Be(PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset));
        }
    }

    private static RelationalResourceModel BuildFixtureResourceModel(
        QualifiedResourceName resourceName,
        SqlDialect dialect,
        bool reverseResourceSchemaOrder
    )
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(
            RuntimePlanCompilationFixturePath,
            dialect,
            reverseResourceSchemaOrder
        );

        return modelSet
            .ConcreteResourcesInNameOrder.Single(resource => resource.ResourceKey.Resource == resourceName)
            .RelationalModel;
    }

    private static ResourceReadPlanFingerprint CreateReadPlanFingerprint(ResourceReadPlan readPlan)
    {
        return new ResourceReadPlanFingerprint(
            KeysetTempTableName: readPlan.KeysetTable.Table.Name,
            KeysetDocumentIdColumnName: readPlan.KeysetTable.DocumentIdColumnName.Value,
            TablePlansInDependencyOrder:
            [
                .. readPlan.TablePlansInDependencyOrder.Select(tablePlan => new TableReadPlanFingerprint(
                    Table: tablePlan.TableModel.Table.ToString(),
                    SelectByKeysetSql: tablePlan.SelectByKeysetSql,
                    SelectListColumnsInOrder:
                    [
                        .. tablePlan.TableModel.Columns.Select(static column => column.ColumnName.Value),
                    ],
                    OrderByKeyColumnsInOrder:
                    [
                        .. tablePlan.TableModel.Key.Columns.Select(static column => column.ColumnName.Value),
                    ]
                )),
            ]
        );
    }

    private static string[] ExtractSelectedColumnNames(string sql)
    {
        return ExtractSqlSectionLines(sql, "SELECT", "FROM").Select(ExtractQualifiedColumnName).ToArray();
    }

    private static string[] ExtractOrderByColumnNames(string sql)
    {
        return ExtractSqlSectionLines(sql, "ORDER BY", ";").Select(ExtractQualifiedColumnName).ToArray();
    }

    private static IReadOnlyList<string> ExtractSqlSectionLines(
        string sql,
        string sectionStart,
        string sectionEnd
    )
    {
        List<string> lines = [];
        var inSection = false;

        foreach (var rawLine in sql.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (line == sectionStart)
            {
                inSection = true;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            if (line.StartsWith(sectionEnd, StringComparison.Ordinal))
            {
                break;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static string ExtractQualifiedColumnName(string line)
    {
        var lineWithoutSuffix = line.Replace(" ASC", string.Empty, StringComparison.Ordinal).TrimEnd(',');
        var qualifierSeparatorIndex = lineWithoutSuffix.IndexOf('.', StringComparison.Ordinal);

        qualifierSeparatorIndex.Should().BeGreaterThanOrEqualTo(0);

        return lineWithoutSuffix[(qualifierSeparatorIndex + 1)..].Trim('"', '[', ']');
    }

    private static string ExtractFromAlias(string sql)
    {
        return ExtractTrailingAlias(sql, "FROM ");
    }

    private static string ExtractJoinAlias(string sql)
    {
        var joinLine = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Trim())
            .Single(segment => segment.StartsWith("INNER JOIN ", StringComparison.Ordinal));
        var relationWithAlias = joinLine["INNER JOIN ".Length..];
        var onClauseIndex = relationWithAlias.IndexOf(" ON ", StringComparison.Ordinal);

        onClauseIndex.Should().BeGreaterThan(0);

        var relationTokens = relationWithAlias[..onClauseIndex]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return relationTokens[^1];
    }

    private static string ExtractTrailingAlias(string sql, string linePrefix)
    {
        var line = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Trim())
            .Single(segment => segment.StartsWith(linePrefix, StringComparison.Ordinal));
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return tokens[^1];
    }

    private sealed record ResourceReadPlanFingerprint(
        string KeysetTempTableName,
        string KeysetDocumentIdColumnName,
        IReadOnlyList<TableReadPlanFingerprint> TablePlansInDependencyOrder
    );

    private sealed record TableReadPlanFingerprint(
        string Table,
        string SelectByKeysetSql,
        IReadOnlyList<string> SelectListColumnsInOrder,
        IReadOnlyList<string> OrderByKeyColumnsInOrder
    );

    private static RelationalResourceModel CreateMultiTableResourceModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
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
                    ColumnName: new DbColumnName("StudentUniqueId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.studentUniqueId",
                        [new JsonPathSegment.Property("studentUniqueId")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("Student_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
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
                    ScalarType: new RelationalScalarType(ScalarKind.String),
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

        var nestedTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressPeriod"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*].periods[*]",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement(),
                ]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressPeriod",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("Student_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("AddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AddressOrdinal"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
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
                    ColumnName: new DbColumnName("BeginDate"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].periods[*].beginDate",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("periods"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("beginDate"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var extensionTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
            JsonScope: new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtension",
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
                    ColumnName: new DbColumnName("FavoriteColor"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$._ext.sample.favoriteColor",
                        [
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable, nestedTable, extensionTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateModelWithMissingDocumentIdParentKeyPart()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: model.Root.Key.ConstraintName,
                Columns: [new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.ParentKeyPart)]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithUnsupportedKeyColumnKind()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: model.Root.Key.ConstraintName,
                Columns: [new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar)]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithDocumentIdNotFirstInKeyOrder()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTable = GetStudentAddressTable(model);

        var updatedChildTable = childTable with
        {
            Key = new TableKey(
                ConstraintName: childTable.Key.ConstraintName,
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
        };

        return ReplaceStudentAddressTable(model, updatedChildTable);
    }

    private static RelationalResourceModel CreateModelWithOrdinalNotLastInKeyOrder()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTable = GetStudentAddressTable(model);

        var updatedChildTable = childTable with
        {
            Key = new TableKey(
                ConstraintName: childTable.Key.ConstraintName,
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                ]
            ),
        };

        return ReplaceStudentAddressTable(model, updatedChildTable);
    }

    private static RelationalResourceModel CreateModelWithMultipleOrdinalKeyColumns()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var childTable = GetStudentAddressTable(model);

        var updatedChildTable = childTable with
        {
            Key = new TableKey(
                ConstraintName: childTable.Key.ConstraintName,
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
        };

        return ReplaceStudentAddressTable(model, updatedChildTable);
    }

    private static RelationalResourceModel CreateModelWithMissingDocumentReferenceFkColumn()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var binding = model.DocumentReferenceBindings.Single() with
        {
            FkColumn = new DbColumnName("MissingSchool_DocumentId"),
        };

        return model with
        {
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingReferenceIdentityBindingColumn()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var binding = model.DocumentReferenceBindings.Single() with
        {
            IdentityBindings =
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: CreatePath(
                        "$.addresses[*].schoolReference.schoolId",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolId")
                    ),
                    Column: new DbColumnName("MissingSchoolId")
                ),
            ],
        };

        return model with
        {
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingDescriptorEdgeFkColumn()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var edgeSource = model.DescriptorEdgeSources.Single() with
        {
            FkColumn = new DbColumnName("MissingProgramTypeDescriptorId"),
        };

        return model with
        {
            DescriptorEdgeSources = [edgeSource],
        };
    }

    private static DbTableModel GetStudentAddressTable(RelationalResourceModel model)
    {
        return model.TablesInDependencyOrder.Single(table =>
            table.Table.Equals(new DbTableName(new DbSchemaName("edfi"), "StudentAddress"))
        );
    }

    private static RelationalResourceModel ReplaceStudentAddressTable(
        RelationalResourceModel model,
        DbTableModel updatedChildTable
    )
    {
        return model with { TablesInDependencyOrder = [model.Root, updatedChildTable] };
    }
}
