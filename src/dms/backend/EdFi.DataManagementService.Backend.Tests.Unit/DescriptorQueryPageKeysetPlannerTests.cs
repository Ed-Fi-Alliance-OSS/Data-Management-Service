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
    public void It_should_plan_typed_descriptor_page_sql_and_parameter_values()
    {
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
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

        keyset.Plan.PageDocumentIdSql.Should().Contain("FROM \"dms\".\"Document\" r");
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"DocumentUuid\" = @id");
        keyset.Plan.PageDocumentIdSql.Should().Contain("d.\"Namespace\" = @namespace");
        keyset.Plan.PageDocumentIdSql.Should().Contain("d.\"EffectiveBeginDate\" = @effectiveBeginDate");
        keyset.Plan.TotalCountSql.Should().BeNull();

        keyset.ParameterValues["resourceKeyId"].Should().Be((short)13);
        keyset.ParameterValues["id"].Should().Be(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        keyset.ParameterValues["namespace"].Should().Be("uri://ed-fi.org/descriptor#Alternative");
        keyset.ParameterValues["effectiveBeginDate"].Should().Be(new DateOnly(2026, 1, 15));
        keyset.ParameterValues["offset"].Should().Be(0L);
        keyset.ParameterValues["limit"].Should().Be(500L);
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
            new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
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
            new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
        );

        first.Plan.PageDocumentIdSql.Should().Be(second.Plan.PageDocumentIdSql);
        first
            .Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal(second.Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName));
        first
            .ParameterValues.Keys.OrderBy(static key => key, StringComparer.Ordinal)
            .Should()
            .Equal(second.ParameterValues.Keys.OrderBy(static key => key, StringComparer.Ordinal));
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
