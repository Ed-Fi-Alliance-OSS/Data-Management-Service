// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Identification-code collection filters plan into one correlated <c>EXISTS</c> per collection table,
/// typed and bounded by the collection column's metadata, beside the root predicates and the
/// change-version window in every candidate mode.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_RelationalQueryPageKeysetPlanner_with_identification_code_filters(SqlDialect dialect)
{
    private static readonly DbSchemaName _edfi = new("edfi");
    private static readonly DbTableName _collectionTableName = new(_edfi, "AcademicWeekIdentificationCode");
    private static readonly DbColumnName _rootLocatorColumn = new("AcademicWeek_DocumentId");
    private static readonly QualifiedResourceName _systemDescriptor = new(
        "Ed-Fi",
        "IdentificationSystemDescriptor"
    );

    private static readonly CollectionPaging _offsetPageWithCount = new CollectionPaging.Traditional(
        new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
    );

    private readonly RelationalQueryPageKeysetPlanner _planner = new(dialect);

    private string ExistsSql =>
        dialect is SqlDialect.Pgsql
            ? "EXISTS (SELECT 1 FROM \"edfi\".\"AcademicWeekIdentificationCode\" ci WHERE ci.\"AcademicWeek_DocumentId\" = r.\"DocumentId\" AND ci.\"IdentificationCode\" = @identificationCode AND ci.\"IdentificationSystemDescriptor_DescriptorId\" = @identificationSystemDescriptor AND ci.\"Sequence\" = @sequence)"
            : "EXISTS (SELECT 1 FROM [edfi].[AcademicWeekIdentificationCode] ci WHERE ci.[AcademicWeek_DocumentId] = r.[DocumentId] AND ci.[IdentificationCode] = @identificationCode AND ci.[IdentificationSystemDescriptor_DescriptorId] = @identificationSystemDescriptor AND ci.[Sequence] = @sequence)";

    [Test]
    public void It_groups_every_collection_predicate_into_one_exists_after_the_root_predicates()
    {
        var planned = _planner.Plan(
            CreateRootTable(),
            Preprocessed(RootElement(), CodeElement("ABC-1"), SystemElement(41), SequenceElement("7")),
            _offsetPageWithCount,
            resourceTables: ResourceTables()
        );

        WhereTerms(planned.Plan.PageDocumentIdSql)
            .Should()
            .Equal(
                dialect is SqlDialect.Pgsql ? "(r.\"SchoolId\" = @schoolId)" : "(r.[SchoolId] = @schoolId)",
                $"AND ({ExistsSql})"
            );
        WhereTerms(planned.Plan.TotalCountSql!).Should().Equal(WhereTerms(planned.Plan.PageDocumentIdSql));
        planned
            .Plan.PageParametersInOrder.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            )
            .Should()
            .Equal(
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolId"),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "identificationCode",
                    QuerySqlParameterBinding.CreateString(60)
                ),
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "identificationSystemDescriptor"),
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "sequence")
            );
        planned.ParameterValues["identificationCode"].Should().Be("ABC-1");
        planned.ParameterValues["identificationSystemDescriptor"].Should().Be(41);
        planned.ParameterValues["sequence"].Should().Be(7);
    }

    [Test]
    public void It_returns_no_plan_when_a_collection_value_cannot_be_represented_in_its_column()
    {
        _planner
            .TryPlan(
                CreateRootTable(),
                Preprocessed(CodeElement("ABC-1"), SequenceElement("not-a-number")),
                _offsetPageWithCount,
                out var plannedQuery,
                out var emptyPageReason,
                resourceTables: ResourceTables()
            )
            .Should()
            .BeFalse();

        plannedQuery.Should().BeNull();
        emptyPageReason.Should().Contain("'sequence'").And.Contain("Int32");
    }

    [Test]
    public void It_keeps_the_change_version_window_as_root_predicates_before_the_exists_on_a_document_id_page()
    {
        var planned = _planner.Plan(
            CreateRootTable(),
            Preprocessed(CodeElement("ABC-1"), SystemElement(41), SequenceElement("7")),
            _offsetPageWithCount,
            changeVersionRange: new ChangeVersionRange(100L, 200L),
            resourceTables: ResourceTables()
        );

        var terms = WhereTerms(planned.Plan.PageDocumentIdSql);

        terms.Should().HaveCount(3);
        terms[0].Should().Contain("ContentVersion").And.Contain("@minChangeVersion");
        terms[1].Should().Contain("ContentVersion").And.Contain("@maxChangeVersion");
        terms[2].Should().Be($"AND ({ExistsSql})");
        WhereTerms(planned.Plan.TotalCountSql!).Should().Equal(terms);
        planned.ParameterValues["minChangeVersion"].Should().Be(100L);
        planned.ParameterValues["maxChangeVersion"].Should().Be(200L);
    }

    [Test]
    public void It_folds_the_change_version_window_into_content_version_cursor_bounds_beside_the_exists()
    {
        var planned = _planner.Plan(
            CreateRootTable(),
            Preprocessed(CodeElement("ABC-1"), SystemElement(41), SequenceElement("7")),
            new CollectionPaging.Cursor(new CursorRange(150L, 250L), new PageSize(25)),
            changeVersionRange: new ChangeVersionRange(100L, 200L),
            orderingMode: PageOrderingMode.ContentVersion,
            resourceTables: ResourceTables()
        );

        var terms = WhereTerms(planned.Plan.PageDocumentIdSql);

        terms.Should().HaveCount(3);
        terms[0].Should().Be($"({ExistsSql})");
        terms[1].Should().Contain("ContentVersion").And.Contain("@cursorMin");
        terms[2].Should().Contain("ContentVersion").And.Contain("@cursorMax");
        planned.ParameterValues["cursorMin"].Should().Be(150L);
        planned.ParameterValues["cursorMax"].Should().Be(200L);
        planned
            .ParameterValues.Keys.Should()
            .NotContain("minChangeVersion")
            .And.NotContain("maxChangeVersion");
    }

    [Test]
    public void It_gives_every_candidate_mode_the_same_exists_filter_and_values()
    {
        var preprocessed = Preprocessed(
            RootElement(),
            CodeElement("ABC-1"),
            SystemElement(41),
            SequenceElement("7")
        );
        var changeVersionRange = new ChangeVersionRange(100L, 200L);

        var offsetPage = _planner.Plan(
            CreateRootTable(),
            preprocessed,
            _offsetPageWithCount,
            changeVersionRange: changeVersionRange,
            resourceTables: ResourceTables()
        );
        var cursorPage = _planner.Plan(
            CreateRootTable(),
            preprocessed,
            new CollectionPaging.Cursor(new CursorRange(10L, 90L), new PageSize(25)),
            changeVersionRange: changeVersionRange,
            resourceTables: ResourceTables()
        );
        _planner
            .TryPlanCandidates(
                CreateRootTable(),
                preprocessed,
                out var candidates,
                out _,
                changeVersionRange: changeVersionRange,
                resourceTables: ResourceTables()
            )
            .Should()
            .BeTrue();

        foreach (var sql in new[] { offsetPage.Plan, cursorPage.Plan, candidates!.Plan })
        {
            WhereTerms(sql.PageDocumentIdSql)
                .Count(term => term.Contains(ExistsSql, StringComparison.Ordinal))
                .Should()
                .Be(1);
        }

        foreach (
            var parameterName in new[] { "identificationCode", "identificationSystemDescriptor", "sequence" }
        )
        {
            cursorPage.ParameterValues[parameterName].Should().Be(offsetPage.ParameterValues[parameterName]);
            candidates.ParameterValues[parameterName].Should().Be(offsetPage.ParameterValues[parameterName]);
        }
    }

    [TestCase("minChangeVersion")]
    [TestCase("offset")]
    [TestCase("limit")]
    public void It_names_a_collection_parameter_apart_from_mode_and_change_version_parameters(
        string queryFieldName
    )
    {
        var planned = _planner.Plan(
            CreateRootTable(),
            Preprocessed(
                CreateElement(
                    queryFieldName,
                    $"$.identificationCodes[*].{queryFieldName}",
                    "string",
                    CollectionTarget("IdentificationCode"),
                    new PreprocessedRelationalQueryValue.Raw("ABC-1")
                )
            ),
            _offsetPageWithCount,
            changeVersionRange: new ChangeVersionRange(100L, 200L),
            resourceTables: ResourceTables()
        );

        var collectionParameterName = planned
            .Plan.PageParametersInOrder.Single(parameter => parameter.Binding?.StringContract is not null)
            .ParameterName;

        new[] { "minChangeVersion", "maxChangeVersion", "offset", "limit" }
            .Should()
            .NotContain(collectionParameterName);
        planned.ParameterValues[collectionParameterName].Should().Be("ABC-1");
        planned.Plan.PageDocumentIdSql.Should().Contain($"= @{collectionParameterName})");
    }

    [Test]
    public void It_requires_the_collection_table_model()
    {
        var plan = () =>
            _planner.Plan(CreateRootTable(), Preprocessed(CodeElement("ABC-1")), _offsetPageWithCount);

        plan.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*collection table 'edfi.AcademicWeekIdentificationCode'*was not supplied.");
    }

    [Test]
    public void It_rejects_a_non_equality_operator_on_a_collection_field()
    {
        var plan = () =>
            _planner.Plan(
                CreateRootTable(),
                Preprocessed(CodeElement("ABC-1")),
                _offsetPageWithCount,
                comparisonOperatorResolver: static _ => QueryComparisonOperator.Like,
                resourceTables: ResourceTables()
            );

        plan.Should()
            .Throw<NotSupportedException>()
            .WithMessage("*exact-match equality*'identificationCode'*");
    }

    [Test]
    public void It_rejects_a_descriptor_target_whose_column_is_not_a_descriptor_fk()
    {
        var plan = () =>
            _planner.Plan(
                CreateRootTable(),
                Preprocessed(
                    CreateElement(
                        "identificationSystemDescriptor",
                        "$.identificationCodes[*].identificationSystemDescriptor",
                        "string",
                        CollectionTarget("IdentificationCode", _systemDescriptor),
                        new PreprocessedRelationalQueryValue.DescriptorId(41)
                    )
                ),
                _offsetPageWithCount,
                resourceTables: ResourceTables()
            );

        plan.Should().Throw<InvalidOperationException>().WithMessage("*Int32 descriptor FK metadata*");
    }

    /// <summary>
    /// The WHERE terms of a statement, one per line, without indentation.
    /// </summary>
    private static string[] WhereTerms(string sql)
    {
        var lines = sql.ReplaceLineEndings("\n").Split('\n');
        var whereIndex = Array.IndexOf(lines, "WHERE");

        return
        [
            .. lines
                .Skip(whereIndex + 1)
                .TakeWhile(static line => line.StartsWith("    ", StringComparison.Ordinal))
                .Select(static line => line.Trim()),
        ];
    }

    private static RelationalQueryPreprocessingResult Preprocessed(
        params PreprocessedRelationalQueryElement[] elements
    ) => new(new RelationalQueryPreprocessingOutcome.Continue(), elements);

    private static PreprocessedRelationalQueryElement RootElement() =>
        CreateElement(
            "schoolId",
            "$.schoolId",
            "number",
            new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
            new PreprocessedRelationalQueryValue.Raw("456")
        );

    private static PreprocessedRelationalQueryElement CodeElement(string value) =>
        CreateElement(
            "identificationCode",
            "$.identificationCodes[*].identificationCode",
            "string",
            CollectionTarget("IdentificationCode"),
            new PreprocessedRelationalQueryValue.Raw(value)
        );

    private static PreprocessedRelationalQueryElement SequenceElement(string value) =>
        CreateElement(
            "sequence",
            "$.identificationCodes[*].sequence",
            "number",
            CollectionTarget("Sequence"),
            new PreprocessedRelationalQueryValue.Raw(value)
        );

    private static PreprocessedRelationalQueryElement SystemElement(int descriptorId) =>
        CreateElement(
            "identificationSystemDescriptor",
            "$.identificationCodes[*].identificationSystemDescriptor",
            "string",
            CollectionTarget("IdentificationSystemDescriptor_DescriptorId", _systemDescriptor),
            new PreprocessedRelationalQueryValue.DescriptorId(descriptorId)
        );

    private static RelationalQueryFieldTarget.CollectionColumn CollectionTarget(
        string column,
        QualifiedResourceName? descriptorResource = null
    ) => new(_collectionTableName, _rootLocatorColumn, new DbColumnName(column), descriptorResource);

    private static PreprocessedRelationalQueryElement CreateElement(
        string queryFieldName,
        string path,
        string type,
        RelationalQueryFieldTarget target,
        PreprocessedRelationalQueryValue value
    ) =>
        new(
            new QueryElement(queryFieldName, [new JsonPath(path)], "raw", type),
            new SupportedRelationalQueryField(
                queryFieldName,
                new RelationalQueryFieldPath(new JsonPathExpression(path, []), type),
                target
            ),
            value
        );

    private static IReadOnlyList<DbTableModel> ResourceTables() =>
        [CreateRootTable(), CreateCollectionTable()];

    private static DbTableModel CreateRootTable() =>
        new DbTableModel(
            new DbTableName(_edfi, "AcademicWeek"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_AcademicWeek",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn(
                    "ContentVersion",
                    ColumnKind.MirroredContentVersion,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn(
                    "DocumentId",
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn("SchoolId", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Int32)),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

    private static DbTableModel CreateCollectionTable() =>
        new DbTableModel(
            _collectionTableName,
            new JsonPathExpression("$.identificationCodes[*]", []),
            new TableKey(
                "PK_AcademicWeekIdentificationCode",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn(
                    "CollectionItemId",
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn(
                    _rootLocatorColumn.Value,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn(
                    "IdentificationCode",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                ),
                CreateColumn("Sequence", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Int32)),
                CreateColumn(
                    "IdentificationSystemDescriptor_DescriptorId",
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int32)
                ),
            ],
            []
        );

    private static DbColumnModel CreateColumn(
        string columnName,
        ColumnKind columnKind,
        RelationalScalarType scalarType
    ) =>
        new(
            new DbColumnName(columnName),
            columnKind,
            scalarType,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.Stored()
        );
}
