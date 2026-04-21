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

[TestFixture]
[Parallelizable]
public class Given_RelationalQueryPageKeysetPlanner
{
    [Test]
    public void It_should_convert_supported_query_value_types_into_typed_sql_parameters_and_compile_document_uuid_join_sql()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var queryResult = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "schoolId",
                        "$.schoolId",
                        "number",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                        "456",
                        new PreprocessedRelationalQueryValue.Raw("456")
                    ),
                    CreateElement(
                        "totalInstructionalDays",
                        "$.totalInstructionalDays",
                        "number",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("TotalInstructionalDays")),
                        "123.45",
                        new PreprocessedRelationalQueryValue.Raw("123.45")
                    ),
                    CreateElement(
                        "isRequired",
                        "$.isRequired",
                        "boolean",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("IsRequired")),
                        "true",
                        new PreprocessedRelationalQueryValue.Raw("true")
                    ),
                    CreateElement(
                        "beginDate",
                        "$.beginDate",
                        "date",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("BeginDate")),
                        "2025-01-01",
                        new PreprocessedRelationalQueryValue.Raw("2025-01-01")
                    ),
                    CreateElement(
                        "endDate",
                        "$.endDate",
                        "date-time",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("EndDate")),
                        "2025-12-31T00:00:00Z",
                        new PreprocessedRelationalQueryValue.Raw("2025-12-31T00:00:00Z")
                    ),
                    CreateElement(
                        "classStartTime",
                        "$.classStartTime",
                        "time",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("ClassStartTime")),
                        "10:30:00",
                        new PreprocessedRelationalQueryValue.Raw("10:30:00")
                    ),
                    CreateElement(
                        "nameOfInstitution",
                        "$.nameOfInstitution",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("NameOfInstitution")),
                        "Lincoln High",
                        new PreprocessedRelationalQueryValue.Raw("Lincoln High")
                    ),
                    CreateElement(
                        "id",
                        "$.id",
                        "string",
                        new RelationalQueryFieldTarget.DocumentUuid(),
                        "11111111-1111-1111-1111-111111111111",
                        new PreprocessedRelationalQueryValue.DocumentUuid(
                            Guid.Parse("11111111-1111-1111-1111-111111111111")
                        )
                    ),
                    CreateElement(
                        "schoolCategoryDescriptor",
                        "$.schoolCategoryDescriptor",
                        "string",
                        new RelationalQueryFieldTarget.DescriptorIdColumn(
                            new DbColumnName("SchoolCategoryDescriptorId"),
                            new QualifiedResourceName("Ed-Fi", "SchoolCategoryDescriptor")
                        ),
                        "uri://schoolCategoryDescriptor",
                        new PreprocessedRelationalQueryValue.DescriptorDocumentId(800L)
                    ),
                ]
            ),
            new PaginationParameters(Limit: null, Offset: null, TotalCount: true, MaximumPageSize: 500)
        );

        var keyset = queryResult;

        keyset.Plan.PageDocumentIdSql.Should().Contain("INNER JOIN \"dms\".\"Document\" doc");
        keyset.Plan.PageDocumentIdSql.Should().Contain("doc.\"DocumentUuid\" = @id");
        keyset.Plan.TotalCountSql.Should().Contain("doc.\"DocumentUuid\" = @id");
        keyset.Plan.PageParametersInOrder.Should().Contain(parameter => parameter.ParameterName == "id");

        keyset.ParameterValues["offset"].Should().Be(0L);
        keyset.ParameterValues["limit"].Should().Be(500L);
        keyset.ParameterValues["schoolId"].Should().Be(456);
        keyset.ParameterValues["totalInstructionalDays"].Should().Be(123.45m);
        keyset.ParameterValues["isRequired"].Should().Be(true);
        keyset.ParameterValues["beginDate"].Should().Be(new DateOnly(2025, 1, 1));
        keyset.ParameterValues["endDate"].Should().Be(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        keyset.ParameterValues["classStartTime"].Should().Be(new TimeOnly(10, 30, 0));
        keyset.ParameterValues["nameOfInstitution"].Should().Be("Lincoln High");
        keyset.ParameterValues["id"].Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        keyset.ParameterValues["schoolCategoryDescriptor"].Should().Be(800L);
    }

    [TestCase("1.5")]
    [TestCase("2147483648")]
    public void It_should_signal_empty_page_when_integer_number_query_values_cannot_be_represented(
        string rawValue
    )
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var result = planner.TryPlan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "schoolId",
                        "$.schoolId",
                        "number",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                        rawValue,
                        new PreprocessedRelationalQueryValue.Raw(rawValue)
                    ),
                ]
            ),
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500),
            out var plannedQuery,
            out var emptyPageReason
        );

        result.Should().BeFalse();
        plannedQuery.Should().BeNull();
        emptyPageReason
            .Should()
            .Be(
                $"Relational query planning determined query field 'schoolId' value '{rawValue}' cannot be represented as relational scalar kind 'Int32', so the query has no matches."
            );
    }

    [Test]
    public void It_should_emit_identical_query_plans_and_parameter_bindings_across_query_element_order_permutations()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var first = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "nameOfInstitution",
                        "$.nameOfInstitution",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("NameOfInstitution")),
                        "Lincoln High",
                        new PreprocessedRelationalQueryValue.Raw("Lincoln High")
                    ),
                    CreateElement(
                        "schoolId",
                        "$.schoolId",
                        "number",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                        "255901",
                        new PreprocessedRelationalQueryValue.Raw("255901")
                    ),
                    CreateElement(
                        "id",
                        "$.id",
                        "string",
                        new RelationalQueryFieldTarget.DocumentUuid(),
                        "11111111-1111-1111-1111-111111111111",
                        new PreprocessedRelationalQueryValue.DocumentUuid(
                            Guid.Parse("11111111-1111-1111-1111-111111111111")
                        )
                    ),
                ]
            ),
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
        );
        var second = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "id",
                        "$.id",
                        "string",
                        new RelationalQueryFieldTarget.DocumentUuid(),
                        "11111111-1111-1111-1111-111111111111",
                        new PreprocessedRelationalQueryValue.DocumentUuid(
                            Guid.Parse("11111111-1111-1111-1111-111111111111")
                        )
                    ),
                    CreateElement(
                        "nameOfInstitution",
                        "$.nameOfInstitution",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("NameOfInstitution")),
                        "Lincoln High",
                        new PreprocessedRelationalQueryValue.Raw("Lincoln High")
                    ),
                    CreateElement(
                        "schoolId",
                        "$.schoolId",
                        "number",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                        "255901",
                        new PreprocessedRelationalQueryValue.Raw("255901")
                    ),
                ]
            ),
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
        );

        second.Plan.PageDocumentIdSql.Should().Be(first.Plan.PageDocumentIdSql);
        second.Plan.TotalCountSql.Should().Be(first.Plan.TotalCountSql);
        second.Plan.PageParametersInOrder.Should().Equal(first.Plan.PageParametersInOrder);
        second
            .Plan.TotalCountParametersInOrder.Should()
            .BeEquivalentTo(first.Plan.TotalCountParametersInOrder);
        second.ParameterValues.Should().BeEquivalentTo(first.ParameterValues);
    }

    [Test]
    public void It_should_assign_collision_free_parameter_names_for_reserved_and_sanitized_query_field_collisions()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var result = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "offset",
                        "$.offsetQueryField",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("OffsetQueryField")),
                        "offset value",
                        new PreprocessedRelationalQueryValue.Raw("offset value")
                    ),
                    CreateElement(
                        "limit",
                        "$.limitQueryField",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("LimitQueryField")),
                        "limit value",
                        new PreprocessedRelationalQueryValue.Raw("limit value")
                    ),
                    CreateElement(
                        "school-id",
                        "$.schoolIdDash",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolIdDash")),
                        "dash",
                        new PreprocessedRelationalQueryValue.Raw("dash")
                    ),
                    CreateElement(
                        "school_id",
                        "$.schoolIdUnderscore",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolIdUnderscore")),
                        "underscore",
                        new PreprocessedRelationalQueryValue.Raw("underscore")
                    ),
                ]
            ),
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
        );

        result.ParameterValues.Keys.Should().Contain("offset");
        result.ParameterValues.Keys.Should().Contain("limit");
        result.ParameterValues.Keys.Should().Contain("offset_2");
        result.ParameterValues.Keys.Should().Contain("limit_2");
        result.ParameterValues.Keys.Should().Contain("school_id");
        result.ParameterValues.Keys.Should().Contain("school_id_2");
        result.Plan.PageDocumentIdSql.Should().Contain("@offset_2");
        result.Plan.PageDocumentIdSql.Should().Contain("@limit_2");
        result.Plan.PageDocumentIdSql.Should().Contain("@school_id");
        result.Plan.PageDocumentIdSql.Should().Contain("@school_id_2");
    }

    [Test]
    public void It_should_reject_empty_page_inputs_already_short_circuited_by_the_repository()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.EmptyPage("no matches"),
                    []
                ),
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Relational query planning requires preprocessing results in the continue state. (Parameter 'preprocessingResult')"
            );
    }

    [Test]
    public void It_should_reject_non_equality_operators_for_DMS_993_runtime_query_execution()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    [
                        CreateElement(
                            "nameOfInstitution",
                            "$.nameOfInstitution",
                            "string",
                            new RelationalQueryFieldTarget.RootColumn(new DbColumnName("NameOfInstitution")),
                            "Lincoln High",
                            new PreprocessedRelationalQueryValue.Raw("Lincoln High")
                        ),
                    ]
                ),
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500),
                static _ => QueryComparisonOperator.Like
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Relational query planning only supports exact-match equality predicates. Query field 'nameOfInstitution' was routed with operator 'Like'."
            );
    }

    [Test]
    public void It_should_reject_multi_path_query_elements_routed_to_the_planner()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    [
                        CreateElement(
                            "schoolId",
                            "$.schoolId",
                            "number",
                            new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                            "255901",
                            new PreprocessedRelationalQueryValue.Raw("255901"),
                            ["$.schoolId", "$.localEducationAgencyReference.educationOrganizationId"]
                        ),
                    ]
                ),
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Relational query planning only supports one compiled document path per query field. Query field 'schoolId' was routed with 2 paths."
            );
    }

    private static PreprocessedRelationalQueryElement CreateElement(
        string queryFieldName,
        string path,
        string type,
        RelationalQueryFieldTarget target,
        string rawValue,
        PreprocessedRelationalQueryValue value,
        IReadOnlyList<string>? documentPaths = null
    )
    {
        return new PreprocessedRelationalQueryElement(
            new QueryElement(
                queryFieldName,
                (documentPaths ?? [path]).Select(static documentPath => new JsonPath(documentPath)).ToArray(),
                rawValue,
                type
            ),
            new SupportedRelationalQueryField(
                queryFieldName,
                new RelationalQueryFieldPath(new JsonPathExpression(path, []), type),
                target
            ),
            value
        );
    }

    private static DbTableModel CreateRootTable()
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "AcademicWeek"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_AcademicWeek",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn(
                    "DocumentId",
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn("SchoolId", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Int32)),
                CreateColumn(
                    "TotalInstructionalDays",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Decimal, Decimal: (10, 2))
                ),
                CreateColumn("IsRequired", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Boolean)),
                CreateColumn("BeginDate", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Date)),
                CreateColumn("EndDate", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.DateTime)),
                CreateColumn("ClassStartTime", ColumnKind.Scalar, new RelationalScalarType(ScalarKind.Time)),
                CreateColumn(
                    "NameOfInstitution",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "SchoolCategoryDescriptorId",
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                CreateColumn(
                    "OffsetQueryField",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "LimitQueryField",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "SchoolIdDash",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "SchoolIdUnderscore",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
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
    }

    private static DbColumnModel CreateColumn(
        string columnName,
        ColumnKind columnKind,
        RelationalScalarType scalarType
    )
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            columnKind,
            scalarType,
            IsNullable: columnName != "DocumentId",
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.Stored()
        );
    }
}
