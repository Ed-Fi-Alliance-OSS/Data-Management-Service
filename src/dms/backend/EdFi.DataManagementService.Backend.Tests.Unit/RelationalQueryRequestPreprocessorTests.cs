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
public class Given_RelationalQueryRequestPreprocessor
{
    [Test]
    public void It_short_circuits_invalid_id_values_to_an_empty_page()
    {
        var result = RelationalQueryRequestPreprocessor.Preprocess(
            [CreateQueryElement("id", "$.id", "not-a-guid", "string")],
            CreateSupportedQueryCapability(
                CreateSupportedField("id", "$.id", "string", new RelationalQueryFieldTarget.DocumentUuid())
            )
        );

        result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.EmptyPage>();
        result.QueryElementsInOrder.Should().BeEmpty();
        result.RequiresDocumentUuidJoin.Should().BeFalse();
    }

    [Test]
    public void It_parses_valid_id_values_and_marks_document_uuid_join_requirement()
    {
        var documentUuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var result = RelationalQueryRequestPreprocessor.Preprocess(
            [
                CreateQueryElement("id", "$.id", documentUuid.ToString(), "string"),
                CreateQueryElement("schoolId", "$.schoolId", "255901", "integer"),
            ],
            CreateSupportedQueryCapability(
                CreateSupportedField("id", "$.id", "string", new RelationalQueryFieldTarget.DocumentUuid()),
                CreateSupportedField(
                    "schoolId",
                    "$.schoolId",
                    "integer",
                    new RelationalQueryFieldTarget.RootColumn(new DbColumnName("SchoolId"))
                )
            )
        );

        result.Outcome.Should().BeOfType<RelationalQueryPreprocessingOutcome.Continue>();
        result.RequiresDocumentUuidJoin.Should().BeTrue();
        result.QueryElementsInOrder.Should().HaveCount(2);
        result
            .QueryElementsInOrder[0]
            .Value.Should()
            .Be(new PreprocessedRelationalQueryValue.DocumentUuid(documentUuid));
        result.QueryElementsInOrder[1].Value.Should().Be(new PreprocessedRelationalQueryValue.Raw("255901"));
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

    private static RelationalQueryCapability CreateSupportedQueryCapability(
        params SupportedRelationalQueryField[] supportedFields
    )
    {
        return new RelationalQueryCapability(
            new RelationalQuerySupport.Supported(),
            supportedFields.ToDictionary(
                static supportedField => supportedField.QueryFieldName,
                static supportedField => supportedField,
                StringComparer.Ordinal
            ),
            new Dictionary<string, UnsupportedRelationalQueryField>(StringComparer.Ordinal)
        );
    }

    private static SupportedRelationalQueryField CreateSupportedField(
        string queryFieldName,
        string path,
        string type,
        RelationalQueryFieldTarget target
    )
    {
        return new SupportedRelationalQueryField(
            queryFieldName,
            new RelationalQueryFieldPath(new JsonPathExpression(path, []), type),
            target
        );
    }
}
