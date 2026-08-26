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
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: null, Offset: null, TotalCount: true, MaximumPageSize: 500)
            )
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

    [Test]
    public void It_should_plan_reference_alias_predicates_against_local_root_binding_columns()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        const string queryPath = "$.studentReference.studentAcademicRecordUniqueId";
        var queryElement = CreateElement(
            "studentUniqueId",
            queryPath,
            "string",
            new RelationalQueryFieldTarget.RootColumn(
                new DbColumnName("StudentAcademicRecord_StudentUniqueId")
            ),
            "800000001",
            new PreprocessedRelationalQueryValue.Raw("800000001")
        );

        var keyset = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [queryElement]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            )
        );

        queryElement.QueryElement.DocumentPaths.Should().ContainSingle();
        queryElement.QueryElement.DocumentPaths[0].Value.Should().Be(queryPath);
        queryElement.SupportedField.Path.Path.Canonical.Should().Be(queryPath);
        queryElement
            .SupportedField.Target.Should()
            .Be(
                new RelationalQueryFieldTarget.RootColumn(
                    new DbColumnName("StudentAcademicRecord_StudentUniqueId")
                )
            );
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"StudentAcademicRecord_StudentUniqueId\" = @studentUniqueId");
        keyset
            .Plan.TotalCountSql.Should()
            .Contain("r.\"StudentAcademicRecord_StudentUniqueId\" = @studentUniqueId");
        keyset.ParameterValues["studentUniqueId"].Should().Be("800000001");
    }

    [Test]
    public void It_should_add_authorization_claim_EdOrg_scalar_parameter_values_to_planned_query()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql);
        var authorizationParameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
            "ClaimEducationOrganizationIds",
            [111L, 222L],
            ["ClaimEducationOrganizationIds_0", "ClaimEducationOrganizationIds_1"]
        );

        var keyset = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            authorization: new PageDocumentIdAuthorizationSpec(
                [
                    new PageDocumentIdAuthorizationStrategy(
                        "RelationshipsWithEdOrgsOnly",
                        [
                            new PageDocumentIdAuthorizationEdOrgSubject(
                                new DbTableName(new DbSchemaName("edfi"), "AcademicWeek"),
                                new DbColumnName("SchoolId"),
                                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                                    RelationshipAuthorizationHierarchyDirection.Normal
                                ),
                                []
                            ),
                        ]
                    ),
                ],
                authorizationParameterization
            )
        );

        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(111L);
        keyset.ParameterValues["ClaimEducationOrganizationIds_1"].Should().Be(222L);
        keyset
            .Plan.PageParametersInOrder.Should()
            .Contain(parameter => parameter.ParameterName == "ClaimEducationOrganizationIds_0");
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();
        keyset
            .Plan.TotalCountParametersInOrder!.Value.Should()
            .Contain(parameter => parameter.ParameterName == "ClaimEducationOrganizationIds_1");
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
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
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
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            )
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
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            )
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
                    CreateElement(
                        "minChangeVersion",
                        "$.minChangeVersionQueryField",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(
                            new DbColumnName("MinChangeVersionQueryField")
                        ),
                        "min collision",
                        new PreprocessedRelationalQueryValue.Raw("min collision")
                    ),
                    CreateElement(
                        "maxChangeVersion",
                        "$.maxChangeVersionQueryField",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(
                            new DbColumnName("MaxChangeVersionQueryField")
                        ),
                        "max collision",
                        new PreprocessedRelationalQueryValue.Raw("max collision")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(100L, 200L)
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
        // The change-version window keeps the bare reserved names; the colliding query fields are
        // suffixed so the window predicates and the query predicates never share a parameter.
        result.ParameterValues["minChangeVersion"].Should().Be(100L);
        result.ParameterValues["maxChangeVersion"].Should().Be(200L);
        result.ParameterValues["minChangeVersion_2"].Should().Be("min collision");
        result.ParameterValues["maxChangeVersion_2"].Should().Be("max collision");
        result.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
        result.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        result
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"MinChangeVersionQueryField\" = @minChangeVersion_2");
        result
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"MaxChangeVersionQueryField\" = @maxChangeVersion_2");
    }

    [Test]
    public void It_should_assign_collision_free_parameter_names_when_a_query_field_collides_with_a_namespace_authorization_parameter()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var result = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "namespacePrefixes",
                        "$.namespacePrefixes",
                        "string",
                        new RelationalQueryFieldTarget.RootColumn(
                            new DbColumnName("NamespacePrefixesQueryField")
                        ),
                        "collides with auth parameter",
                        new PreprocessedRelationalQueryValue.Raw("collides with auth parameter")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        new DbTableName(new DbSchemaName("edfi"), "AcademicWeek"),
                        new DbColumnName("Namespace")
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        // The authorization parameter keeps the bare name; the colliding query field is suffixed so the
        // single-binding namespace LIKE and the query predicate never share a parameter.
        result.ParameterValues.Keys.Should().Contain("namespacePrefixes");
        result.ParameterValues.Keys.Should().Contain("namespacePrefixes_2");
        result.Plan.PageDocumentIdSql.Should().Contain("LIKE ANY(@namespacePrefixes)");
        result.Plan.PageDocumentIdSql.Should().Contain("@namespacePrefixes_2");
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
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
                )
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
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                ),
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
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                )
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Relational query planning only supports one compiled document path per query field. Query field 'schoolId' was routed with 2 paths."
            );
    }

    [Test]
    public void It_should_give_every_candidate_mode_the_same_filters_authorization_and_filter_values()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var changeVersionRange = new ChangeVersionRange(100L, 200L);

        var traditional = planner.Plan(
            CreateRootTable(),
            CreateParityPreprocessingResult(),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
            ),
            changeVersionRange: changeVersionRange
        );
        var cursor = planner.Plan(
            CreateRootTable(),
            CreateParityPreprocessingResult(),
            new CollectionPaging.Cursor(new CursorRange(10L, 90L), new PageSize(25)),
            changeVersionRange: changeVersionRange
        );

        planner
            .TryPlanCandidates(
                CreateRootTable(),
                CreateParityPreprocessingResult(),
                out var unpaged,
                out _,
                changeVersionRange: changeVersionRange
            )
            .Should()
            .BeTrue();

        string[] filterParameterNames = ["schoolId", "minChangeVersion", "maxChangeVersion"];

        foreach (var filterParameterName in filterParameterNames)
        {
            cursor
                .ParameterValues[filterParameterName]
                .Should()
                .Be(traditional.ParameterValues[filterParameterName]);
            unpaged!
                .ParameterValues[filterParameterName]
                .Should()
                .Be(traditional.ParameterValues[filterParameterName]);
        }

        FilterParameterNames(cursor.Plan).Should().Equal(FilterParameterNames(traditional.Plan));
        FilterParameterNames(unpaged!.Plan).Should().Equal(FilterParameterNames(traditional.Plan));

        // Every mode compiles the same change-version window against the same mirrored root column.
        foreach (var sql in new[] { traditional.Plan, cursor.Plan, unpaged.Plan })
        {
            sql.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
            sql.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
            sql.PageDocumentIdSql.Should().Contain("r.\"SchoolId\" = @schoolId");
            sql.PageDocumentIdSql.Should().NotContain("DISTINCT");
        }
    }

    [TestCase(PageOrderingMode.DocumentId, "r.\"DocumentId\"")]
    [TestCase(PageOrderingMode.ContentVersion, "r.\"ContentVersion\"")]
    public void It_should_compile_the_unpaged_candidate_relation_against_the_requested_anchor(
        PageOrderingMode orderingMode,
        string expectedProjection
    )
    {
        // Partition boundaries are cut on whatever this relation projects, so the anchor the request
        // resolved has to survive the trip through the planner. Discarding it here still compiles and
        // still selects the right rows; it just cuts boundaries on a key no page of the same request
        // seeks on, which a client cannot replay.
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        planner
            .TryPlanCandidates(
                CreateRootTable(),
                CreateParityPreprocessingResult(),
                out var unpaged,
                out _,
                changeVersionRange: new ChangeVersionRange(100L, 200L),
                orderingMode: orderingMode
            )
            .Should()
            .BeTrue();

        unpaged!.Plan.PageDocumentIdSql.Should().StartWith($"SELECT {expectedProjection}");
    }

    [Test]
    public void It_should_bind_the_cursor_range_and_page_size_as_int64_values()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var cursor = planner.Plan(
            CreateRootTable(),
            CreateParityPreprocessingResult(),
            new CollectionPaging.Cursor(new CursorRange(10L, long.MaxValue), new PageSize(25))
        );

        cursor.ParameterValues["cursorMin"].Should().Be(10L);
        cursor.ParameterValues["cursorMax"].Should().Be(long.MaxValue);
        cursor.ParameterValues["pageSize"].Should().Be(25L);
        cursor.ParameterValues.Keys.Should().NotContain("offset").And.NotContain("limit");
    }

    [Test]
    public void It_should_bind_an_inverted_cursor_range_without_rejecting_it()
    {
        // An inverted range is a match-nothing window, and it is how a bounded partition reaches its
        // terminal empty page. It is not an error.
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var cursor = planner.Plan(
            CreateRootTable(),
            CreateParityPreprocessingResult(),
            new CollectionPaging.Cursor(new CursorRange(90L, 10L), new PageSize(0))
        );

        cursor.ParameterValues["cursorMin"].Should().Be(90L);
        cursor.ParameterValues["cursorMax"].Should().Be(10L);
        cursor.ParameterValues["pageSize"].Should().Be(0L);
    }

    [Test]
    public void It_should_bind_extreme_int64_cursor_bounds()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var cursor = planner.Plan(
            CreateRootTable(),
            CreateParityPreprocessingResult(),
            new CollectionPaging.Cursor(new CursorRange(long.MinValue, long.MaxValue), new PageSize(500))
        );

        cursor.ParameterValues["cursorMin"].Should().Be(long.MinValue);
        cursor.ParameterValues["cursorMax"].Should().Be(long.MaxValue);
        cursor.ParameterValues["pageSize"].Should().Be(500L);
    }

    [Test]
    public void It_should_reach_the_same_empty_candidate_short_circuit_in_every_candidate_mode()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        planner
            .TryPlan(
                CreateRootTable(),
                CreateUnrepresentableValuePreprocessingResult(),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                ),
                out var traditional,
                out var traditionalReason
            )
            .Should()
            .BeFalse();
        planner
            .TryPlan(
                CreateRootTable(),
                CreateUnrepresentableValuePreprocessingResult(),
                new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25)),
                out var cursor,
                out var cursorReason
            )
            .Should()
            .BeFalse();
        planner
            .TryPlanCandidates(
                CreateRootTable(),
                CreateUnrepresentableValuePreprocessingResult(),
                out var unpaged,
                out var unpagedReason
            )
            .Should()
            .BeFalse();

        traditional.Should().BeNull();
        cursor.Should().BeNull();
        unpaged.Should().BeNull();
        cursorReason.Should().Be(traditionalReason);
        unpagedReason.Should().Be(traditionalReason);
    }

    [TestCase("pageSize")]
    [TestCase("cursorMin")]
    [TestCase("cursorMax")]
    [TestCase("number")]
    [TestCase("minimumPartitionSize")]
    public void It_should_allocate_traditional_filter_parameter_names_unsuffixed_for_names_no_traditional_page_emits(
        string queryFieldName
    )
    {
        // Traditional page selection emits only offset and limit, so a resource field whose sanitized
        // name matches a cursor or partition parameter has nothing to collide with and must keep its
        // plain name. Suffixing it would move traditional SQL and its bindings.
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var traditional = planner.Plan(
            CreateRootTable(),
            CreateNamedFieldPreprocessingResult(queryFieldName),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            )
        );

        FilterParameterNames(traditional.Plan).Should().Equal(queryFieldName);
        traditional.ParameterValues.Should().ContainKey(queryFieldName);
        traditional.Plan.PageDocumentIdSql.Should().Contain($"r.\"SchoolId\" = @{queryFieldName}");
    }

    [TestCase("cursorMin")]
    [TestCase("cursorMax")]
    [TestCase("pageSize")]
    public void It_should_disambiguate_cursor_filter_parameter_names_that_actually_collide(
        string queryFieldName
    )
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var cursor = planner.Plan(
            CreateRootTable(),
            CreateNamedFieldPreprocessingResult(queryFieldName),
            new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25))
        );

        // The filter is renamed out of the way and keeps its own value; the plain name stays with the
        // cursor parameter that actually owns it in this mode.
        var filterParameterNames = FilterParameterNames(cursor.Plan);

        filterParameterNames.Should().ContainSingle().Which.Should().NotBe(queryFieldName);
        cursor.ParameterValues[filterParameterNames[0]].Should().Be(456);
        cursor.ParameterValues.Should().ContainKeys("cursorMin", "cursorMax", "pageSize");
    }

    [TestCase("number")]
    [TestCase("minimumPartitionSize")]
    public void It_should_disambiguate_unpaged_candidate_filter_parameter_names_that_collide_with_reserved_partition_names(
        string queryFieldName
    )
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        planner
            .TryPlanCandidates(
                CreateRootTable(),
                CreateNamedFieldPreprocessingResult(queryFieldName),
                out var unpaged,
                out _
            )
            .Should()
            .BeTrue();

        FilterParameterNames(unpaged!.Plan).Should().NotContain(queryFieldName);
    }

    [Test]
    public void It_should_keep_cross_mode_filter_parity_semantic_when_a_filter_name_collides()
    {
        // Cross-mode parity is the same predicate over the same column bound to the same value, not the
        // same parameter token. A filter sanitized to a name only cursor selection emits keeps its plain
        // name in every mode that does not emit it, so the token differs across modes while the
        // predicate and the value do not.
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var preprocessingResult = CreateNamedFieldPreprocessingResult("pageSize");

        var traditional = planner.Plan(
            CreateRootTable(),
            preprocessingResult,
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
            )
        );
        var cursor = planner.Plan(
            CreateRootTable(),
            preprocessingResult,
            new CollectionPaging.Cursor(new CursorRange(10L, 90L), new PageSize(25))
        );

        planner
            .TryPlanCandidates(CreateRootTable(), preprocessingResult, out var unpaged, out _)
            .Should()
            .BeTrue();

        var traditionalFilterName = FilterParameterNames(traditional.Plan).Single();
        var cursorFilterName = FilterParameterNames(cursor.Plan).Single();
        var unpagedFilterName = FilterParameterNames(unpaged!.Plan).Single();

        // Only cursor selection emits pageSize, so only cursor selection moves the filter off that name.
        traditionalFilterName.Should().Be("pageSize");
        unpagedFilterName.Should().Be("pageSize");
        cursorFilterName.Should().NotBe("pageSize");
        cursor.ParameterValues["pageSize"].Should().Be(25L);

        // Same column, same operator, same bound value under whichever name each mode allocated.
        traditional.ParameterValues[traditionalFilterName].Should().Be(456);
        cursor.ParameterValues[cursorFilterName].Should().Be(456);
        unpaged.ParameterValues[unpagedFilterName].Should().Be(456);

        traditional.Plan.PageDocumentIdSql.Should().Contain($"r.\"SchoolId\" = @{traditionalFilterName}");
        cursor.Plan.PageDocumentIdSql.Should().Contain($"r.\"SchoolId\" = @{cursorFilterName}");
        unpaged.Plan.PageDocumentIdSql.Should().Contain($"r.\"SchoolId\" = @{unpagedFilterName}");
    }

    private static RelationalQueryPreprocessingResult CreateNamedFieldPreprocessingResult(
        string queryFieldName
    )
    {
        return new RelationalQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            [
                CreateElement(
                    queryFieldName,
                    $"$.{queryFieldName}",
                    "number",
                    new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                    "456",
                    new PreprocessedRelationalQueryValue.Raw("456")
                ),
            ]
        );
    }

    private static IReadOnlyList<string> FilterParameterNames(PageDocumentIdSqlPlan plan)
    {
        return
        [
            .. plan
                .PageParametersInOrder.Where(static parameter =>
                    parameter.Role is QuerySqlParameterRole.Filter
                )
                .Select(static parameter => parameter.ParameterName),
        ];
    }

    private static RelationalQueryPreprocessingResult CreateParityPreprocessingResult()
    {
        return new RelationalQueryPreprocessingResult(
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
            ]
        );
    }

    private static RelationalQueryPreprocessingResult CreateUnrepresentableValuePreprocessingResult()
    {
        return new RelationalQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            [
                CreateElement(
                    "schoolId",
                    "$.schoolId",
                    "number",
                    new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId")),
                    "not-a-number",
                    new PreprocessedRelationalQueryValue.Raw("not-a-number")
                ),
            ]
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

    [Test]
    [TestCase(
        SqlDialect.Pgsql,
        "r.\"ContentVersion\" >= @minChangeVersion",
        "r.\"ContentVersion\" <= @maxChangeVersion"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[ContentVersion] >= @minChangeVersion",
        "r.[ContentVersion] <= @maxChangeVersion"
    )]
    public void It_should_filter_the_root_mirrored_content_version_when_both_change_version_bounds_are_supplied(
        SqlDialect dialect,
        string expectedMinPredicateFragment,
        string expectedMaxPredicateFragment
    )
    {
        var planner = new RelationalQueryPageKeysetPlanner(dialect);

        var keyset = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(100L, 200L)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedMinPredicateFragment);
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedMaxPredicateFragment);
        // The change-version predicate filters the concrete root alias only; no dms.Document join.
        keyset.Plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain(expectedMinPredicateFragment);
        keyset.Plan.TotalCountSql.Should().Contain(expectedMaxPredicateFragment);
        keyset.Plan.TotalCountSql.Should().NotContain("INNER JOIN");
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
        keyset
            .Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("minChangeVersion", "maxChangeVersion", "offset", "limit");
        keyset
            .Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("minChangeVersion", "maxChangeVersion");
    }

    [Test]
    public void It_should_emit_only_the_min_bound_predicate_when_max_change_version_is_absent()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(100L, null)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("@maxChangeVersion");
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues.Keys.Should().NotContain("maxChangeVersion");
    }

    [Test]
    public void It_should_emit_only_the_max_bound_predicate_when_min_change_version_is_absent()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(null, 200L)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("@minChangeVersion");
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
        keyset.ParameterValues.Keys.Should().NotContain("minChangeVersion");
    }

    [Test]
    public void It_should_leave_the_plan_unchanged_when_no_change_version_bounds_are_supplied()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var paginationParameters = new CollectionPaging.Traditional(
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
        );
        var withoutRange = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            paginationParameters
        );

        var withNoneRange = planner.Plan(
            CreateRootTable(),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            paginationParameters,
            changeVersionRange: ChangeVersionRange.None
        );

        withNoneRange.Plan.PageDocumentIdSql.Should().Be(withoutRange.Plan.PageDocumentIdSql);
        withNoneRange.Plan.TotalCountSql.Should().Be(withoutRange.Plan.TotalCountSql);
        withNoneRange.Plan.PageDocumentIdSql.Should().NotContain("ContentVersion");
        withNoneRange.ParameterValues.Keys.Should().NotContain("minChangeVersion");
        withNoneRange.ParameterValues.Keys.Should().NotContain("maxChangeVersion");
    }

    [Test]
    public void It_should_compose_the_change_version_window_with_query_filters_and_authorization()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql);
        var authorizationParameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
            "ClaimEducationOrganizationIds",
            [111L],
            ["ClaimEducationOrganizationIds_0"]
        );

        var keyset = planner.Plan(
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
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            authorization: new PageDocumentIdAuthorizationSpec(
                [
                    new PageDocumentIdAuthorizationStrategy(
                        "RelationshipsWithEdOrgsOnly",
                        [
                            new PageDocumentIdAuthorizationEdOrgSubject(
                                new DbTableName(new DbSchemaName("edfi"), "AcademicWeek"),
                                new DbColumnName("SchoolId"),
                                RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                                    RelationshipAuthorizationHierarchyDirection.Normal
                                ),
                                []
                            ),
                        ]
                    ),
                ],
                authorizationParameterization
            ),
            changeVersionRange: new ChangeVersionRange(100L, 200L)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("r.[SchoolId] = @schoolId");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.[ContentVersion] >= @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.[ContentVersion] <= @maxChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@ClaimEducationOrganizationIds_0");
        keyset.ParameterValues["schoolId"].Should().Be(456);
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
        keyset.ParameterValues["ClaimEducationOrganizationIds_0"].Should().Be(111L);
    }

    [Test]
    public void It_should_throw_when_a_change_version_bound_is_supplied_and_the_root_table_has_no_content_version_column()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(contentVersionColumn: null),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    []
                ),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                ),
                changeVersionRange: new ChangeVersionRange(100L, null)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*mirrored Int64 'ContentVersion'*Neither has a non-mirror fallback*");
    }

    /// <summary>
    /// The anchor requires the mirror column on its own, with no change-version bound to require it
    /// first. The anchor reaches the planner as its own request value rather than being derived from the
    /// window here, so a plan can name ContentVersion while the window carries no bound — and without
    /// this check that plan would compile SQL against a column nothing had verified and fail at the
    /// provider instead.
    /// </summary>
    [Test]
    public void It_should_throw_when_the_content_version_anchor_is_requested_and_the_root_table_has_no_content_version_column()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(contentVersionColumn: null),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    []
                ),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                ),
                changeVersionRange: null,
                orderingMode: PageOrderingMode.ContentVersion
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*mirrored Int64 'ContentVersion'*Neither has a non-mirror fallback*");
    }

    /// <summary>
    /// The complement: a DocumentId-anchored plan with no change-version bound needs no mirror column,
    /// so widening the check above must not start rejecting the most ordinary request there is.
    /// </summary>
    [Test]
    public void It_should_plan_a_document_id_anchored_page_when_the_root_table_has_no_content_version_column()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            CreateRootTable(contentVersionColumn: null),
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            )
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("ORDER BY r.\"DocumentId\" ASC");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("ContentVersion");
    }

    [Test]
    public void It_should_throw_when_the_content_version_column_is_not_a_mirror_column()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(
                    CreateColumn(
                        "ContentVersion",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Int64)
                    )
                ),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    []
                ),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                ),
                changeVersionRange: new ChangeVersionRange(null, 200L)
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*ColumnKind.MirroredContentVersion*");
    }

    [Test]
    public void It_should_throw_when_the_mirrored_content_version_column_is_not_int64()
    {
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                CreateRootTable(
                    CreateColumn(
                        "ContentVersion",
                        ColumnKind.MirroredContentVersion,
                        new RelationalScalarType(ScalarKind.Int32)
                    )
                ),
                new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    []
                ),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                ),
                changeVersionRange: new ChangeVersionRange(100L, 200L)
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*mirrored Int64 'ContentVersion'*");
    }

    private static DbTableModel CreateRootTable()
    {
        return CreateRootTable(
            CreateColumn(
                "ContentVersion",
                ColumnKind.MirroredContentVersion,
                new RelationalScalarType(ScalarKind.Int64)
            )
        );
    }

    private static DbTableModel CreateRootTable(DbColumnModel? contentVersionColumn)
    {
        DbColumnModel[] contentVersionColumns = contentVersionColumn is null ? [] : [contentVersionColumn];

        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "AcademicWeek"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_AcademicWeek",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                .. contentVersionColumns,
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
                    "StudentAcademicRecord_StudentUniqueId",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32)
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
                CreateColumn(
                    "MinChangeVersionQueryField",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "MaxChangeVersionQueryField",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "NamespacePrefixesQueryField",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                CreateColumn(
                    "Namespace",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 255)
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
