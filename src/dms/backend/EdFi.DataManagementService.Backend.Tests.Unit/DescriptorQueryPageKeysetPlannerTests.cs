// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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
        "\"dms\".\"Document\" r",
        "\"dms\".\"Document\" doc",
        "\"dms\".\"Descriptor\" d",
        "LIMIT @limit OFFSET @offset"
    )]
    [TestCase(
        SqlDialect.Mssql,
        "[dms].[Document] r",
        "[dms].[Document] doc",
        "[dms].[Descriptor] d",
        "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY"
    )]
    public void It_should_plan_typed_descriptor_page_and_total_count_sql_and_parameter_values(
        SqlDialect dialect,
        string expectedDocumentFromFragment,
        string unexpectedDocumentJoinFragment,
        string expectedDescriptorJoinFragment,
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
            new PaginationParameters(Limit: null, Offset: null, TotalCount: true, MaximumPageSize: 500)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().NotContain($"INNER JOIN {unexpectedDocumentJoinFragment}");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("doc.");
        keyset.Plan.PageDocumentIdSql.Should().Contain($"INNER JOIN {expectedDescriptorJoinFragment} ON d.");
        keyset.Plan.PageDocumentIdSql.Should().Contain("ResourceKeyId");
        keyset.Plan.PageDocumentIdSql.Should().Contain("DocumentUuid");
        keyset.Plan.PageDocumentIdSql.Should().Contain("Namespace");
        keyset.Plan.PageDocumentIdSql.Should().Contain("EffectiveBeginDate");
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedPagingFragment);

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        keyset.Plan.TotalCountSql.Should().NotContain($"INNER JOIN {unexpectedDocumentJoinFragment}");
        keyset.Plan.TotalCountSql.Should().NotContain("doc.");
        keyset.Plan.TotalCountSql.Should().Contain($"INNER JOIN {expectedDescriptorJoinFragment} ON d.");
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
            .Equal("effectiveBeginDate", "namespace", "id", "resourceKeyId", "offset", "limit");
        keyset.Plan.TotalCountParametersInOrder.Should().NotBeNull();
        keyset
            .Plan.TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("effectiveBeginDate", "namespace", "id", "resourceKeyId");
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
            new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
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
            new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
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
    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Document\" r")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document] r")]
    public void It_should_plan_descriptor_total_count_sql_without_optional_joins_when_only_resource_type_discrimination_is_required(
        SqlDialect dialect,
        string expectedDocumentFromFragment
    )
    {
        var planner = new DescriptorQueryPageKeysetPlanner(dialect);
        var keyset = planner.Plan(
            RelationalAccessTestData.CreateMappingSet(_requestResource),
            _descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("Descriptor");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("doc.");

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        keyset.Plan.TotalCountSql.Should().Contain("ResourceKeyId");
        keyset.Plan.TotalCountSql.Should().NotContain("Descriptor");
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
        "\"dms\".\"Document\" r",
        "\"dms\".\"Document\" doc",
        "r.\"DocumentUuid\" = @id"
    )]
    [TestCase(SqlDialect.Mssql, "[dms].[Document] r", "[dms].[Document] doc", "r.[DocumentUuid] = @id")]
    public void It_should_plan_descriptor_id_filters_without_redundant_self_join_or_descriptor_join(
        SqlDialect dialect,
        string expectedDocumentFromFragment,
        string unexpectedDocumentJoinFragment,
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
            new PaginationParameters(Limit: 25, Offset: 75, TotalCount: true, MaximumPageSize: 500)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedIdPredicateFragment);
        keyset.Plan.PageDocumentIdSql.Should().NotContain($"INNER JOIN {unexpectedDocumentJoinFragment}");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("doc.");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("Descriptor");

        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedDocumentFromFragment}");
        keyset.Plan.TotalCountSql.Should().Contain(expectedIdPredicateFragment);
        keyset.Plan.TotalCountSql.Should().NotContain($"INNER JOIN {unexpectedDocumentJoinFragment}");
        keyset.Plan.TotalCountSql.Should().NotContain("doc.");
        keyset.Plan.TotalCountSql.Should().NotContain("Descriptor");
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
    [TestCase(SqlDialect.Pgsql, "d.\"CodeValue\" = @codeValue")]
    [TestCase(SqlDialect.Mssql, "d.[CodeValue] COLLATE Latin1_General_100_BIN2 = @codeValue")]
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
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
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
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("preprocessingResult")
            .WithMessage(
                "Descriptor query page planning requires preprocessing results in the continue state.*"
            );
    }

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
