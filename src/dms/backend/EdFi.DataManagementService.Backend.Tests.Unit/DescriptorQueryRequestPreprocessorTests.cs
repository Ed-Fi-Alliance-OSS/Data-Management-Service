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
public class Given_DescriptorQueryRequestPreprocessor
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    [Test]
    public void It_binds_all_supported_descriptor_fields_with_case_insensitive_query_parameter_lookup()
    {
        var result = DescriptorQueryRequestPreprocessor.Preprocess(
            CreateMappingSet(CreateSupportedQueryCapability()),
            _descriptorResource,
            [
                CreateQueryElement("ID", "$.id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb", "string"),
                CreateQueryElement(
                    "NameSpace",
                    "$.namespace",
                    "uri://ed-fi.org/descriptor#Alternative",
                    "string"
                ),
                CreateQueryElement("CODEVALUE", "$.codeValue", "Alternative", "string"),
                CreateQueryElement("shortdescription", "$.shortDescription", "Alternative short", "string"),
                CreateQueryElement("Description", "$.description", "Alternative description", "string"),
                CreateQueryElement("effectivebegindate", "$.effectiveBeginDate", "2026-01-15", "date"),
                CreateQueryElement("EFFECTIVEENDDATE", "$.effectiveEndDate", "2026-06-30", "date"),
            ]
        );

        result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        result.QueryElementsInOrder.Should().HaveCount(7);
        result.QueryElementsInOrder[0].SupportedField.QueryFieldName.Should().Be("id");
        result
            .QueryElementsInOrder[0]
            .Value.Should()
            .Be(
                new PreprocessedDescriptorQueryValue.DocumentUuid(
                    Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
                )
            );
        result.QueryElementsInOrder[1].SupportedField.QueryFieldName.Should().Be("namespace");
        result
            .QueryElementsInOrder[1]
            .Value.Should()
            .Be(new PreprocessedDescriptorQueryValue.Raw("uri://ed-fi.org/descriptor#Alternative"));
        result.QueryElementsInOrder[2].SupportedField.QueryFieldName.Should().Be("codeValue");
        result
            .QueryElementsInOrder[2]
            .Value.Should()
            .Be(new PreprocessedDescriptorQueryValue.Raw("Alternative"));
        result.QueryElementsInOrder[3].SupportedField.QueryFieldName.Should().Be("shortDescription");
        result
            .QueryElementsInOrder[3]
            .Value.Should()
            .Be(new PreprocessedDescriptorQueryValue.Raw("Alternative short"));
        result.QueryElementsInOrder[4].SupportedField.QueryFieldName.Should().Be("description");
        result
            .QueryElementsInOrder[4]
            .Value.Should()
            .Be(new PreprocessedDescriptorQueryValue.Raw("Alternative description"));
        result.QueryElementsInOrder[5].SupportedField.QueryFieldName.Should().Be("effectiveBeginDate");
        result
            .QueryElementsInOrder[5]
            .Value.Should()
            .Be(new PreprocessedDescriptorQueryValue.DateOnlyValue(new DateOnly(2026, 1, 15)));
        result.QueryElementsInOrder[6].SupportedField.QueryFieldName.Should().Be("effectiveEndDate");
        result
            .QueryElementsInOrder[6]
            .Value.Should()
            .Be(new PreprocessedDescriptorQueryValue.DateOnlyValue(new DateOnly(2026, 6, 30)));
    }

    [Test]
    public void It_short_circuits_invalid_id_values_to_an_empty_page()
    {
        var result = DescriptorQueryRequestPreprocessor.Preprocess(
            CreateMappingSet(CreateSupportedQueryCapability()),
            _descriptorResource,
            [CreateQueryElement("ID", "$.id", "not-a-guid", "string")]
        );

        result
            .Outcome.Should()
            .BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>()
            .Which.Reason.Should()
            .Contain("not-a-guid");
        result.QueryElementsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_short_circuits_unrepresentable_date_values_to_an_empty_page()
    {
        var result = DescriptorQueryRequestPreprocessor.Preprocess(
            CreateMappingSet(CreateSupportedQueryCapability()),
            _descriptorResource,
            [CreateQueryElement("effectiveBeginDate", "$.effectiveBeginDate", "2026-02-30", "date")]
        );

        result
            .Outcome.Should()
            .BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>()
            .Which.Reason.Should()
            .Contain("2026-02-30");
        result.QueryElementsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_surfaces_omitted_descriptor_query_capability_diagnostics_without_rewriting_them()
    {
        var omissionReason =
            "ApiSchema queryFieldMapping disagrees with the shared descriptor query contract: field 'namespace' must map to exactly one path '$.namespace' with type 'string'.";

        var act = () =>
            DescriptorQueryRequestPreprocessor.Preprocess(
                CreateMappingSet(
                    new DescriptorQueryCapability(
                        new DescriptorQuerySupport.Omitted(
                            new DescriptorQueryCapabilityOmission(
                                DescriptorQueryCapabilityOmissionKind.ApiSchemaMismatch,
                                omissionReason
                            )
                        ),
                        new Dictionary<string, SupportedDescriptorQueryField>(
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                ),
                _descriptorResource,
                []
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Descriptor query capability for resource 'Ed-Fi.SchoolTypeDescriptor' was intentionally omitted: "
                    + omissionReason
            );
    }

    private static MappingSet CreateMappingSet(DescriptorQueryCapability descriptorQueryCapability)
    {
        return RelationalAccessTestData.CreateMappingSet(_requestResource) with
        {
            DescriptorQueryCapabilitiesByResource = new Dictionary<
                QualifiedResourceName,
                DescriptorQueryCapability
            >
            {
                [_descriptorResource] = descriptorQueryCapability,
            },
        };
    }

    private static DescriptorQueryCapability CreateSupportedQueryCapability()
    {
        return new DescriptorQueryCapability(
            new DescriptorQuerySupport.Supported(),
            new Dictionary<string, SupportedDescriptorQueryField>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = CreateSupportedField("id", new DescriptorQueryFieldTarget.DocumentUuid()),
                ["namespace"] = CreateSupportedField(
                    "namespace",
                    new DescriptorQueryFieldTarget.Namespace(new DbColumnName("Namespace"))
                ),
                ["codeValue"] = CreateSupportedField(
                    "codeValue",
                    new DescriptorQueryFieldTarget.CodeValue(new DbColumnName("CodeValue"))
                ),
                ["shortDescription"] = CreateSupportedField(
                    "shortDescription",
                    new DescriptorQueryFieldTarget.ShortDescription(new DbColumnName("ShortDescription"))
                ),
                ["description"] = CreateSupportedField(
                    "description",
                    new DescriptorQueryFieldTarget.Description(new DbColumnName("Description"))
                ),
                ["effectiveBeginDate"] = CreateSupportedField(
                    "effectiveBeginDate",
                    new DescriptorQueryFieldTarget.EffectiveBeginDate(new DbColumnName("EffectiveBeginDate"))
                ),
                ["effectiveEndDate"] = CreateSupportedField(
                    "effectiveEndDate",
                    new DescriptorQueryFieldTarget.EffectiveEndDate(new DbColumnName("EffectiveEndDate"))
                ),
            }
        );
    }

    private static QueryElement CreateQueryElement(
        string queryFieldName,
        string documentPath,
        string value,
        string type
    )
    {
        return new QueryElement(queryFieldName, [new JsonPath(documentPath)], value, type);
    }

    private static SupportedDescriptorQueryField CreateSupportedField(
        string queryFieldName,
        DescriptorQueryFieldTarget target
    )
    {
        return new SupportedDescriptorQueryField(queryFieldName, target);
    }
}
