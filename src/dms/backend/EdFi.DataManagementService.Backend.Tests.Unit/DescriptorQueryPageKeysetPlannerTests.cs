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
public class Given_DescriptorQueryPageKeysetPlanner
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    [TestCase(
        SqlDialect.Pgsql,
        "\"dms\".\"Descriptor\" r",
        "INNER JOIN \"dms\".\"Document\" doc ON doc.\"DocumentId\" = r.\"DocumentId\"",
        "\"dms\".\"Descriptor\" d",
        "LIMIT @limit OFFSET @offset"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "[dms].[Descriptor] r",
        "INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = r.[DocumentId]",
        "[dms].[Descriptor] d",
        "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY"
    )]
    public void It_should_plan_typed_descriptor_page_and_total_count_sql_and_parameter_values(
        SqlDialect dialect,
        string expectedDescriptorFromFragment,
        string expectedDocumentJoinFragment,
        string unexpectedDescriptorJoinFragment,
        string expectedPagingFragment
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(dialect);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "id",
                        "$.id",
                        "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
                        "string",
                        new DescriptorQueryFieldTarget.DocumentUuid(),
                        new PreprocessedDescriptorQueryValue.DocumentUuid(
                            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
                        )
                    ),
                    CreateElement(
                        "namespace",
                        "$.namespace",
                        "uri://ed-fi.org/descriptor#Alternative",
                        "string",
                        new DescriptorQueryFieldTarget.Namespace(new DbColumnName("Namespace")),
                        new PreprocessedDescriptorQueryValue.Raw("uri://ed-fi.org/descriptor#Alternative")
                    ),
                    CreateElement(
                        "effectiveBeginDate",
                        "$.effectiveBeginDate",
                        "2026-01-15",
                        "date",
                        new DescriptorQueryFieldTarget.EffectiveBeginDate(
                            new DbColumnName("EffectiveBeginDate")
                        ),
                        new PreprocessedDescriptorQueryValue.DateOnlyValue(new DateOnly(2026, 1, 15))
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: null, Offset: null, TotalCount: true, MaximumPageSize: 500)
            )
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedDescriptorFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedDocumentJoinFragment);
        keyset.Plan.PageDocumentIdSql.Should().NotContain(unexpectedDescriptorJoinFragment);
        keyset.Plan.PageDocumentIdSql.Should().Contain("ResourceKeyId");
        keyset.Plan.PageDocumentIdSql.Should().Contain("DocumentUuid");
        keyset.Plan.PageDocumentIdSql.Should().Contain("Namespace");
        keyset.Plan.PageDocumentIdSql.Should().Contain("EffectiveBeginDate");
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedPagingFragment);

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedDescriptorFromFragment}");
        keyset.Plan.TotalCountSql.Should().Contain(expectedDocumentJoinFragment);
        keyset.Plan.TotalCountSql.Should().NotContain(unexpectedDescriptorJoinFragment);
        keyset.Plan.TotalCountSql.Should().Contain("ResourceKeyId");
        keyset.Plan.TotalCountSql.Should().Contain("DocumentUuid");
        keyset.Plan.TotalCountSql.Should().Contain("Namespace");
        keyset.Plan.TotalCountSql.Should().Contain("EffectiveBeginDate");
        keyset.Plan.TotalCountSql.Should().Contain("SELECT COUNT(1)");
        keyset.Plan.TotalCountSql.Should().NotContain("@offset");
        keyset.Plan.TotalCountSql.Should().NotContain("@limit");

        keyset.ParameterValues["resourceKeyId"].Should().Be((short)13);
        keyset.ParameterValues["id"].Should().Be(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        keyset.ParameterValues["namespace"].Should().Be("uri://ed-fi.org/descriptor#Alternative");
        keyset.ParameterValues["effectiveBeginDate"].Should().Be(new DateOnly(2026, 1, 15));
        keyset.ParameterValues["offset"].Should().Be(0L);
        keyset.ParameterValues["limit"].Should().Be(500L);
        keyset
            .Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("id", "effectiveBeginDate", "namespace", "resourceKeyId", "offset", "limit");
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();
        keyset
            .Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("id", "effectiveBeginDate", "namespace", "resourceKeyId");
    }

    [Test]
    public void It_should_emit_identical_sql_and_parameter_names_across_query_element_order_permutations()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var first = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "codeValue",
                        "$.codeValue",
                        "Alternative",
                        "string",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue")),
                        new PreprocessedDescriptorQueryValue.Raw("Alternative")
                    ),
                    CreateElement(
                        "description",
                        "$.description",
                        "Alternative description",
                        "string",
                        new DescriptorQueryFieldTarget.Description(new DbColumnName("Description")),
                        new PreprocessedDescriptorQueryValue.Raw("Alternative description")
                    ),
                    CreateElement(
                        "id",
                        "$.id",
                        "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
                        "string",
                        new DescriptorQueryFieldTarget.DocumentUuid(),
                        new PreprocessedDescriptorQueryValue.DocumentUuid(
                            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
                        )
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
            )
        );
        var second = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "id",
                        "$.id",
                        "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
                        "string",
                        new DescriptorQueryFieldTarget.DocumentUuid(),
                        new PreprocessedDescriptorQueryValue.DocumentUuid(
                            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
                        )
                    ),
                    CreateElement(
                        "description",
                        "$.description",
                        "Alternative description",
                        "string",
                        new DescriptorQueryFieldTarget.Description(new DbColumnName("Description")),
                        new PreprocessedDescriptorQueryValue.Raw("Alternative description")
                    ),
                    CreateElement(
                        "codeValue",
                        "$.codeValue",
                        "Alternative",
                        "string",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue")),
                        new PreprocessedDescriptorQueryValue.Raw("Alternative")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
            )
        );

        first.Plan.PageDocumentIdSql.Should().Be(second.Plan.PageDocumentIdSql);
        first.Plan.TotalCountSql.Should().Be(second.Plan.TotalCountSql);
        first
            .Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal(second.Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName));
        first.Plan.TotalCountParametersInOrder.Should().NotBeNull();
        second.Plan.TotalCountParametersInOrder.Should().NotBeNull();
        first
            .Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal(
                second.Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            );
        first
            .ParameterValues.Keys.OrderBy(static key => key, StringComparer.Ordinal)
            .Should()
            .Equal(second.ParameterValues.Keys.OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Test]
    public void It_should_assign_collision_free_parameter_names_for_reserved_and_sanitized_query_field_collisions()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "resourceKeyId",
                        "$.resourceKeyId",
                        "namespace value",
                        "string",
                        new DescriptorQueryFieldTarget.Namespace(new DbColumnName("Namespace")),
                        new PreprocessedDescriptorQueryValue.Raw("namespace value")
                    ),
                    CreateElement(
                        "offset",
                        "$.offsetQueryField",
                        "code value",
                        "string",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue")),
                        new PreprocessedDescriptorQueryValue.Raw("code value")
                    ),
                    CreateElement(
                        "limit",
                        "$.limitQueryField",
                        "short description value",
                        "string",
                        new DescriptorQueryFieldTarget.ShortDescription(new DbColumnName("ShortDescription")),
                        new PreprocessedDescriptorQueryValue.Raw("short description value")
                    ),
                    CreateElement(
                        "school-id",
                        "$.schoolIdDash",
                        "2026-01-15",
                        "date",
                        new DescriptorQueryFieldTarget.EffectiveBeginDate(
                            new DbColumnName("EffectiveBeginDate")
                        ),
                        new PreprocessedDescriptorQueryValue.DateOnlyValue(new DateOnly(2026, 1, 15))
                    ),
                    CreateElement(
                        "school_id",
                        "$.schoolIdUnderscore",
                        "2026-06-30",
                        "date",
                        new DescriptorQueryFieldTarget.EffectiveEndDate(new DbColumnName("EffectiveEndDate")),
                        new PreprocessedDescriptorQueryValue.DateOnlyValue(new DateOnly(2026, 6, 30))
                    ),
                    CreateElement(
                        "minChangeVersion",
                        "$.minChangeVersionQueryField",
                        "min collision",
                        "string",
                        new DescriptorQueryFieldTarget.Description(new DbColumnName("Description")),
                        new PreprocessedDescriptorQueryValue.Raw("min collision")
                    ),
                    CreateElement(
                        "maxChangeVersion",
                        "$.maxChangeVersionQueryField",
                        "cccccccc-1111-2222-3333-dddddddddddd",
                        "string",
                        new DescriptorQueryFieldTarget.DocumentUuid(),
                        new PreprocessedDescriptorQueryValue.DocumentUuid(
                            Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
                        )
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(100L, 200L)
        );

        keyset.ParameterValues.Keys.Should().Contain("resourceKeyId");
        keyset.ParameterValues.Keys.Should().Contain("offset");
        keyset.ParameterValues.Keys.Should().Contain("limit");
        keyset.ParameterValues.Keys.Should().Contain("resourceKeyId_2");
        keyset.ParameterValues.Keys.Should().Contain("offset_2");
        keyset.ParameterValues.Keys.Should().Contain("limit_2");
        keyset.ParameterValues.Keys.Should().Contain("school_id");
        keyset.ParameterValues.Keys.Should().Contain("school_id_2");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@resourceKeyId_2");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@offset_2");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@limit_2");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@school_id");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@school_id_2");
        // The change-version window keeps the bare reserved names; the colliding query fields are
        // suffixed so the window predicates and the query predicates never share a parameter.
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
        keyset.ParameterValues["minChangeVersion_2"].Should().Be("min collision");
        keyset
            .ParameterValues["maxChangeVersion_2"]
            .Should()
            .Be(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" > @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"Description\" = @minChangeVersion_2");
        keyset.Plan.PageDocumentIdSql.Should().Contain("doc.\"DocumentUuid\" = @maxChangeVersion_2");
    }

    [Test]
    public void It_should_assign_collision_free_parameter_names_when_a_query_field_collides_with_a_namespace_authorization_parameter()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "namespacePrefixes",
                        "$.namespacePrefixes",
                        "collides with auth parameter",
                        "string",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue")),
                        new PreprocessedDescriptorQueryValue.Raw("collides with auth parameter")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            CreateNamespaceAuthorization(SqlDialect.Pgsql, ["uri://ed-fi.org/"])
        );

        // The authorization parameter keeps the bare name; the colliding query field is suffixed so the
        // single-binding namespace LIKE and the query predicate never share a parameter.
        keyset.ParameterValues.Keys.Should().Contain("namespacePrefixes");
        keyset.ParameterValues.Keys.Should().Contain("namespacePrefixes_2");
        keyset.Plan.PageDocumentIdSql.Should().Contain("LIKE ANY(@namespacePrefixes)");
        keyset.Plan.PageDocumentIdSql.Should().Contain("@namespacePrefixes_2");
    }

    [Test]
    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Descriptor\" r")]
    [TestCase(SqlDialect.Mssql, "[dms].[Descriptor] r")]
    public void It_should_plan_descriptor_total_count_sql_without_optional_joins_when_only_resource_type_discrimination_is_required(
        SqlDialect dialect,
        string expectedDescriptorFromFragment
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(dialect);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
            )
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedDescriptorFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("doc.");

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedDescriptorFromFragment}");
        keyset.Plan.TotalCountSql.Should().Contain("ResourceKeyId");
        keyset.Plan.TotalCountSql.Should().NotContain("INNER JOIN");
        keyset.Plan.TotalCountSql.Should().NotContain("doc.");
        keyset.Plan.TotalCountSql.Should().NotContain("@offset");
        keyset.Plan.TotalCountSql.Should().NotContain("@limit");

        keyset.ParameterValues.Keys.Should().BeEquivalentTo("resourceKeyId", "offset", "limit");
        keyset
            .Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("resourceKeyId");
    }

    [Test]
    [TestCase(
        SqlDialect.Pgsql,
        "\"dms\".\"Descriptor\" r",
        "INNER JOIN \"dms\".\"Document\" doc ON doc.\"DocumentId\" = r.\"DocumentId\"",
        "\"dms\".\"Descriptor\" d",
        "doc.\"DocumentUuid\" = @id"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "[dms].[Descriptor] r",
        "INNER JOIN [dms].[Document] doc ON doc.[DocumentId] = r.[DocumentId]",
        "[dms].[Descriptor] d",
        "doc.[DocumentUuid] = @id"
    )]
    public void It_should_plan_descriptor_id_filters_through_the_shared_document_join(
        SqlDialect dialect,
        string expectedDescriptorFromFragment,
        string expectedDocumentJoinFragment,
        string unexpectedDescriptorJoinFragment,
        string expectedIdPredicateFragment
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(dialect);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "id",
                        "$.id",
                        "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
                        "string",
                        new DescriptorQueryFieldTarget.DocumentUuid(),
                        new PreprocessedDescriptorQueryValue.DocumentUuid(
                            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
                        )
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
            )
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedDescriptorFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedDocumentJoinFragment);
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedIdPredicateFragment);
        keyset.Plan.PageDocumentIdSql.Should().NotContain(unexpectedDescriptorJoinFragment);

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedDescriptorFromFragment}");
        keyset.Plan.TotalCountSql.Should().Contain(expectedDocumentJoinFragment);
        keyset.Plan.TotalCountSql.Should().Contain(expectedIdPredicateFragment);
        keyset.Plan.TotalCountSql.Should().NotContain(unexpectedDescriptorJoinFragment);
        keyset.Plan.TotalCountSql.Should().NotContain("@offset");
        keyset.Plan.TotalCountSql.Should().NotContain("@limit");

        keyset
            .Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("id", "resourceKeyId", "offset", "limit");
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();
        keyset
            .Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("id", "resourceKeyId");
    }

    [Test]
    [TestCase(SqlDialect.Pgsql, "r.\"CodeValue\" = @codeValue")]
    [TestCase(SqlDialect.Mssql, "r.[CodeValue] COLLATE Latin1_General_100_BIN2 = @codeValue")]
    public void It_should_preserve_mixed_case_string_filter_values_and_reuse_them_in_total_count_sql(
        SqlDialect dialect,
        string expectedPredicateFragment
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(dialect);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "codeValue",
                        "$.codeValue",
                        "MiXeDCaSeValue",
                        "string",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue")),
                        new PreprocessedDescriptorQueryValue.Raw("MiXeDCaSeValue")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            )
        );

        keyset.ParameterValues["codeValue"].Should().Be("MiXeDCaSeValue");
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedPredicateFragment);
        keyset.Plan.TotalCountSql.Should().Contain(expectedPredicateFragment);
    }

    [Test]
    public void It_should_reject_non_continue_preprocessing_outcomes()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.Plan(
                RelationalAccessTestData.CreateMappingSet(_requestResource),
                _descriptorResource,
                new DescriptorQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.EmptyPage("no matches"),
                    []
                ),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("preprocessingResult")
            .WithMessage(
                "Descriptor query page planning requires preprocessing results in the continue state.*"
            );
    }

    [Test]
    public void It_should_reject_non_continue_preprocessing_outcomes_for_the_candidate_entry_point()
    {
        // The candidate entry point carries the same caller contract as the paged one: a consumer that
        // reaches an empty preprocessing outcome must short-circuit the page itself rather than ask for a
        // candidate relation. There is no Try overload to fall back on, so the contract is asserted here.
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var act = () =>
            planner.PlanCandidates(
                RelationalAccessTestData.CreateMappingSet(_requestResource),
                _descriptorResource,
                new DescriptorQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.EmptyPage("no matches"),
                    []
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("preprocessingResult")
            .WithMessage(
                "Descriptor query page planning requires preprocessing results in the continue state.*"
            );
    }

    [Test]
    public void It_should_emit_pgsql_descriptor_namespace_filter_in_page_and_total_count_sql_when_authorization_is_supplied()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var authorization = CreateNamespaceAuthorization(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/"]
        );

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            authorization
        );

        // The namespace check binds to the dms.Descriptor root itself: no self-join.
        keyset.Plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql!.Should().NotContain("INNER JOIN");
        keyset
            .Plan.TotalCountSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_should_emit_mssql_descriptor_namespace_filter_in_page_and_total_count_sql_when_authorization_is_supplied()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Mssql);
        var authorization = CreateNamespaceAuthorization(
            SqlDialect.Mssql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/"]
        );

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            authorization
        );

        // The namespace check binds to the dms.Descriptor root itself: no self-join.
        keyset.Plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain(
                "(r.[Namespace] IS NOT NULL AND ("
                    + "r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' "
                    + "OR r.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\'"
                    + "))"
            );
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset
            .Plan.TotalCountSql!.Should()
            .Contain("(r.[Namespace] IS NOT NULL AND (r.[Namespace] LIKE @namespacePrefixes_0");
    }

    [Test]
    public void It_should_bind_pgsql_namespace_prefix_array_parameter_value_to_the_escaped_like_patterns()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var authorization = CreateNamespaceAuthorization(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/"]
        );

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            authorization
        );

        keyset.ParameterValues.Should().ContainKey("namespacePrefixes");
        keyset
            .ParameterValues["namespacePrefixes"]
            .Should()
            .BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should()
            .Equal("uri://ed-fi.org/%", "uri://gbisd.edu/%");
    }

    [Test]
    public void It_should_bind_mssql_scalar_namespace_prefix_parameter_values_in_order()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Mssql);
        var authorization = CreateNamespaceAuthorization(
            SqlDialect.Mssql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/"]
        );

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            authorization
        );

        keyset.ParameterValues["namespacePrefixes_0"].Should().Be("uri://ed-fi.org/%");
        keyset.ParameterValues["namespacePrefixes_1"].Should().Be("uri://gbisd.edu/%");
    }

    [Test]
    public void It_should_compose_descriptor_namespace_filter_with_a_descriptor_column_predicate_in_pgsql()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var authorization = CreateNamespaceAuthorization(SqlDialect.Pgsql, ["uri://ed-fi.org/"]);

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    CreateElement(
                        "codeValue",
                        "$.codeValue",
                        "Alternative",
                        "string",
                        new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue")),
                        new PreprocessedDescriptorQueryValue.Raw("Alternative")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            authorization
        );

        keyset.Plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"CodeValue\" = @codeValue");
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_should_leave_descriptor_page_sql_unchanged_when_no_authorization_is_supplied()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            )
        );

        keyset.Plan.PageDocumentIdSql.Should().NotContain("Namespace");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("namespacePrefixes");
        keyset.ParameterValues.Keys.Should().NotContain("namespacePrefixes");
    }

    [Test]
    [TestCase(
        SqlDialect.Pgsql,
        "r.\"ContentVersion\" > @minChangeVersion",
        "r.\"ContentVersion\" <= @maxChangeVersion"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "r.[ContentVersion] > @minChangeVersion",
        "r.[ContentVersion] <= @maxChangeVersion"
    )]
    public void It_should_filter_the_content_version_mirror_alongside_the_resource_key_predicate(
        SqlDialect dialect,
        string expectedMinPredicateFragment,
        string expectedMaxPredicateFragment
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(dialect);

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(100L, 200L)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedMinPredicateFragment);
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedMaxPredicateFragment);
        keyset.Plan.PageDocumentIdSql.Should().Contain("ResourceKeyId");
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain(expectedMinPredicateFragment);
        keyset.Plan.TotalCountSql.Should().Contain(expectedMaxPredicateFragment);
        keyset.Plan.TotalCountSql.Should().Contain("ResourceKeyId");
        keyset.ParameterValues["resourceKeyId"].Should().Be((short)13);
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
    }

    [Test]
    public void It_should_emit_only_the_min_bound_predicate_when_max_change_version_is_absent()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(100L, null)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" > @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("@maxChangeVersion");
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues.Keys.Should().NotContain("maxChangeVersion");
    }

    [Test]
    public void It_should_emit_only_the_max_bound_predicate_when_min_change_version_is_absent()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            changeVersionRange: new ChangeVersionRange(null, 200L)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("@minChangeVersion");
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
        keyset.ParameterValues.Keys.Should().NotContain("minChangeVersion");
    }

    [Test]
    public void It_should_leave_descriptor_page_sql_unchanged_when_no_change_version_bounds_are_supplied()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var paginationParameters = new CollectionPaging.Traditional(
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
        );
        var withoutRange = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            paginationParameters
        );

        var withNoneRange = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            paginationParameters,
            changeVersionRange: ChangeVersionRange.None
        );

        withNoneRange.Plan.PageDocumentIdSql.Should().Be(withoutRange.Plan.PageDocumentIdSql);
        withNoneRange.Plan.TotalCountSql.Should().Be(withoutRange.Plan.TotalCountSql);
        withNoneRange.Plan.PageDocumentIdSql.Should().NotContain("ContentVersion");
        withNoneRange.Plan.TotalCountSql.Should().NotContain("ContentVersion");
        withNoneRange.ParameterValues.Keys.Should().NotContain("minChangeVersion");
        withNoneRange.ParameterValues.Keys.Should().NotContain("maxChangeVersion");
    }

    [Test]
    public void It_should_compose_the_change_version_window_with_namespace_authorization_for_descriptor_queries()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var authorization = CreateNamespaceAuthorization(SqlDialect.Pgsql, ["uri://ed-fi.org/"]);

        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            ),
            authorization,
            new ChangeVersionRange(100L, 200L)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" > @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
        keyset.ParameterValues.Should().ContainKey("namespacePrefixes");
    }

    [Test]
    public void It_should_reject_a_descriptor_resource_whose_project_is_not_in_the_mapping_set()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        // The type predicate is the project-qualified ResourceKeyId: a same-named descriptor under a
        // different project must not resolve to this mapping set's SchoolTypeDescriptor key.
        var act = () =>
            planner.Plan(
                RelationalAccessTestData.CreateMappingSet(_requestResource),
                new QualifiedResourceName("Other-Project", "SchoolTypeDescriptor"),
                new DescriptorQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.Continue(),
                    []
                ),
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
                )
            );

        act.Should().Throw<KeyNotFoundException>().WithMessage("*Other-Project.SchoolTypeDescriptor*");
    }

    [Test]
    public void It_should_give_every_candidate_mode_the_same_descriptor_root_filters_and_filter_values()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var authorization = CreateNamespaceAuthorization(SqlDialect.Pgsql, ["uri://ed-fi.org/"]);
        var changeVersionRange = new ChangeVersionRange(100L, 200L);

        var traditional = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateParityPreprocessingResult(),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
            ),
            authorization,
            changeVersionRange
        );
        var cursor = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateParityPreprocessingResult(),
            new CollectionPaging.Cursor(new CursorRange(10L, 90L), new PageSize(25)),
            authorization,
            changeVersionRange
        );
        var unpaged = planner.PlanCandidates(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateParityPreprocessingResult(),
            authorization,
            changeVersionRange
        );

        string[] filterParameterNames =
        [
            "resourceKeyId",
            "namespace",
            "minChangeVersion",
            "maxChangeVersion",
        ];

        foreach (var filterParameterName in filterParameterNames)
        {
            cursor
                .ParameterValues[filterParameterName]
                .Should()
                .Be(traditional.ParameterValues[filterParameterName]);
            unpaged
                .ParameterValues[filterParameterName]
                .Should()
                .Be(traditional.ParameterValues[filterParameterName]);
        }

        // The descriptor root and its mandatory ResourceKeyId discriminator survive in every mode.
        foreach (var plan in new[] { traditional.Plan, cursor.Plan, unpaged.Plan })
        {
            plan.PageDocumentIdSql.Should().Contain("FROM \"dms\".\"Descriptor\" r");
            plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
            plan.PageDocumentIdSql.Should().Contain("r.\"Namespace\"");
            plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" > @minChangeVersion");
            plan.PageDocumentIdSql.Should().NotContain("DISTINCT");
        }
    }

    [Test]
    public void It_should_bind_the_descriptor_cursor_range_and_page_size_as_int64_values()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Mssql);

        var cursor = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateParityPreprocessingResult(),
            new CollectionPaging.Cursor(new CursorRange(10L, long.MaxValue), new PageSize(1))
        );

        cursor.ParameterValues["cursorMin"].Should().Be(10L);
        cursor.ParameterValues["cursorMax"].Should().Be(long.MaxValue);
        cursor.ParameterValues["pageSize"].Should().Be(1L);
        cursor.ParameterValues.Keys.Should().NotContain("offset").And.NotContain("limit");
        cursor.Plan.PageDocumentIdSql.Should().StartWith("SELECT TOP (@pageSize) r.[DocumentId]");
    }

    [Test]
    public void It_should_emit_no_ordering_for_the_unpaged_descriptor_candidate_relation()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Mssql);

        var unpaged = planner.PlanCandidates(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateParityPreprocessingResult()
        );

        unpaged.Plan.PageDocumentIdSql.Should().NotContain("ORDER BY");
        unpaged.Plan.PageDocumentIdSql.Should().NotContain("OFFSET");
        unpaged.Plan.PageDocumentIdSql.Should().NotContain("TOP (");
    }

    [TestCase("pageSize")]
    [TestCase("cursorMin")]
    [TestCase("cursorMax")]
    [TestCase("number")]
    [TestCase("minimumPartitionSize")]
    public void It_should_allocate_traditional_descriptor_filter_parameter_names_unsuffixed_for_names_no_traditional_page_emits(
        string queryFieldName
    )
    {
        // Traditional descriptor page selection emits only offset and limit, so a descriptor field whose
        // sanitized name matches a cursor or partition parameter has nothing to collide with and must
        // keep its plain name.
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var traditional = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateNamedFieldPreprocessingResult(queryFieldName),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            )
        );

        traditional.ParameterValues.Should().ContainKey(queryFieldName);
        traditional.Plan.PageDocumentIdSql.Should().Contain($"r.\"Namespace\" = @{queryFieldName}");
    }

    [TestCase("cursorMin")]
    [TestCase("pageSize")]
    public void It_should_disambiguate_descriptor_cursor_filter_parameter_names_that_actually_collide(
        string queryFieldName
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var cursor = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            CreateNamedFieldPreprocessingResult(queryFieldName),
            new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25))
        );

        // The descriptor filter is renamed out of the way; the plain name stays with the cursor
        // parameter that actually owns it in this mode.
        var namespaceFilterName = NamespaceFilterParameterName(cursor.Plan);

        namespaceFilterName.Should().NotBe(queryFieldName);
        cursor.Plan.PageDocumentIdSql.Should().Contain($"r.\"Namespace\" = @{namespaceFilterName}");
        cursor.ParameterValues.Should().ContainKeys("cursorMin", "cursorMax", "pageSize");
    }

    [Test]
    public void It_should_keep_cross_mode_descriptor_filter_parity_semantic_when_a_filter_name_collides()
    {
        // Cross-mode parity is the same predicate over the same column bound to the same value, not the
        // same parameter token. A descriptor filter sanitized to a name only cursor selection emits keeps
        // its plain name in every mode that does not emit it.
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var preprocessingResult = CreateNamedFieldPreprocessingResult("pageSize");

        var traditional = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            preprocessingResult,
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
            )
        );
        var cursor = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            preprocessingResult,
            new CollectionPaging.Cursor(new CursorRange(10L, 90L), new PageSize(25))
        );
        var unpaged = planner.PlanCandidates(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            preprocessingResult
        );

        var traditionalFilterName = NamespaceFilterParameterName(traditional.Plan);
        var cursorFilterName = NamespaceFilterParameterName(cursor.Plan);
        var unpagedFilterName = NamespaceFilterParameterName(unpaged.Plan);

        // Only cursor selection emits pageSize, so only cursor selection moves the filter off that name.
        traditionalFilterName.Should().Be("pageSize");
        unpagedFilterName.Should().Be("pageSize");
        cursorFilterName.Should().NotBe("pageSize");
        cursor.ParameterValues["pageSize"].Should().Be(25L);

        // Same column, same operator, same bound value under whichever name each mode allocated.
        var expectedFilterValue = traditional.ParameterValues[traditionalFilterName];

        expectedFilterValue.Should().Be("uri://ed-fi.org/descriptor#Alternative");
        cursor.ParameterValues[cursorFilterName].Should().Be(expectedFilterValue);
        unpaged.ParameterValues[unpagedFilterName].Should().Be(expectedFilterValue);

        traditional.Plan.PageDocumentIdSql.Should().Contain($"r.\"Namespace\" = @{traditionalFilterName}");
        cursor.Plan.PageDocumentIdSql.Should().Contain($"r.\"Namespace\" = @{cursorFilterName}");
        unpaged.Plan.PageDocumentIdSql.Should().Contain($"r.\"Namespace\" = @{unpagedFilterName}");
    }

    /// <summary>
    /// Returns the name allocated to the descriptor field filter, which is every filter parameter other
    /// than the mandatory <c>ResourceKeyId</c> discriminator the descriptor planner always emits.
    /// </summary>
    private static string NamespaceFilterParameterName(PageDocumentIdSqlPlan plan)
    {
        return plan
            .PageParametersInOrder.Single(parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
                && !string.Equals(parameter.ParameterName, "resourceKeyId", StringComparison.Ordinal)
            )
            .ParameterName;
    }

    private static DescriptorQueryPreprocessingResult CreateNamedFieldPreprocessingResult(
        string queryFieldName
    )
    {
        return new DescriptorQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            [
                CreateElement(
                    queryFieldName,
                    $"$.{queryFieldName}",
                    "uri://ed-fi.org/descriptor#Alternative",
                    "string",
                    new DescriptorQueryFieldTarget.Namespace(new DbColumnName("Namespace")),
                    new PreprocessedDescriptorQueryValue.Raw("uri://ed-fi.org/descriptor#Alternative")
                ),
            ]
        );
    }

    private static DescriptorQueryPreprocessingResult CreateParityPreprocessingResult()
    {
        return new DescriptorQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            [
                CreateElement(
                    "namespace",
                    "$.namespace",
                    "uri://ed-fi.org/descriptor#Alternative",
                    "string",
                    new DescriptorQueryFieldTarget.Namespace(new DbColumnName("Namespace")),
                    new PreprocessedDescriptorQueryValue.Raw("uri://ed-fi.org/descriptor#Alternative")
                ),
            ]
        );
    }

    private static PageDocumentIdAuthorizationSpec CreateNamespaceAuthorization(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes
    ) =>
        new(
            Strategies: [],
            NamespaceChecks:
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                    new DbColumnName("Namespace")
                ),
            ],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                dialect,
                namespacePrefixes,
                "namespacePrefixes"
            )
        );

    private static PreprocessedDescriptorQueryElement CreateElement(
        string queryFieldName,
        string documentPath,
        string rawValue,
        string type,
        DescriptorQueryFieldTarget target,
        PreprocessedDescriptorQueryValue value
    )
    {
        return new PreprocessedDescriptorQueryElement(
            new QueryElement(queryFieldName, [new JsonPath(documentPath)], rawValue, type),
            new SupportedDescriptorQueryField(queryFieldName, target),
            value
        );
    }
}
