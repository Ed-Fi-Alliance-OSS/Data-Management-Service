// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class DescriptorQueryRequestPreprocessor
{
    private static readonly RelationalScalarType DateScalarType = new(ScalarKind.Date);

    public static DescriptorQueryPreprocessingResult Preprocess(
        MappingSet mappingSet,
        QualifiedResourceName requestResource,
        IReadOnlyList<QueryElement> queryElements
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(queryElements);

        var queryCapability = mappingSet.GetDescriptorQueryCapabilityOrThrow(requestResource);
        var preprocessedElements = new PreprocessedDescriptorQueryElement[queryElements.Count];

        for (var index = 0; index < queryElements.Count; index++)
        {
            var queryElement =
                queryElements[index]
                ?? throw new ArgumentException(
                    "Query elements must not contain null entries.",
                    nameof(queryElements)
                );

            if (
                !queryCapability.SupportedFieldsByQueryField.TryGetValue(
                    queryElement.QueryFieldName,
                    out var supportedField
                )
            )
            {
                throw new InvalidOperationException(
                    $"Descriptor query preprocessing could not find supported query metadata for field "
                        + $"'{queryElement.QueryFieldName}'."
                );
            }

            if (
                !TryPreprocessValue(
                    queryElement,
                    supportedField,
                    out var preprocessedValue,
                    out var emptyPageReason
                )
            )
            {
                return new DescriptorQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.EmptyPage(emptyPageReason!),
                    []
                );
            }

            preprocessedElements[index] = new PreprocessedDescriptorQueryElement(
                queryElement,
                supportedField,
                preprocessedValue!
            );
        }

        return new DescriptorQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            preprocessedElements
        );
    }

    private static bool TryPreprocessValue(
        QueryElement queryElement,
        SupportedDescriptorQueryField supportedField,
        out PreprocessedDescriptorQueryValue? preprocessedValue,
        out string? emptyPageReason
    )
    {
        switch (supportedField.Target)
        {
            case DescriptorQueryFieldTarget.DocumentUuid:
                ValidateCompatibleQueryTypeOrThrow(
                    supportedField.QueryFieldName,
                    queryElement.Type,
                    expectedType: "string"
                );

                if (!Guid.TryParse(queryElement.Value, out var documentUuid))
                {
                    preprocessedValue = null;
                    emptyPageReason =
                        $"Descriptor query preprocessing determined query field '{queryElement.QueryFieldName}' value "
                        + $"'{queryElement.Value}' is not a valid UUID, so the query has no matches.";
                    return false;
                }

                preprocessedValue = new PreprocessedDescriptorQueryValue.DocumentUuid(documentUuid);
                emptyPageReason = null;
                return true;

            case DescriptorQueryFieldTarget.Namespace:
            case DescriptorQueryFieldTarget.CodeValue:
            case DescriptorQueryFieldTarget.ShortDescription:
            case DescriptorQueryFieldTarget.Description:
                ValidateCompatibleQueryTypeOrThrow(
                    supportedField.QueryFieldName,
                    queryElement.Type,
                    expectedType: "string"
                );

                preprocessedValue = new PreprocessedDescriptorQueryValue.Raw(queryElement.Value);
                emptyPageReason = null;
                return true;

            case DescriptorQueryFieldTarget.EffectiveBeginDate:
            case DescriptorQueryFieldTarget.EffectiveEndDate:
                ValidateCompatibleQueryTypeOrThrow(
                    supportedField.QueryFieldName,
                    queryElement.Type,
                    expectedType: "date"
                );

                if (
                    !RelationalScalarLiteralParser.TryParse(
                        queryElement.Value,
                        DateScalarType,
                        out var parsedValue
                    ) || parsedValue is not DateOnly dateOnlyValue
                )
                {
                    preprocessedValue = null;
                    emptyPageReason =
                        $"Descriptor query preprocessing determined query field '{queryElement.QueryFieldName}' value "
                        + $"'{queryElement.Value}' cannot be represented as relational scalar kind '{DateScalarType.Kind}', "
                        + "so the query has no matches.";
                    return false;
                }

                preprocessedValue = new PreprocessedDescriptorQueryValue.DateOnlyValue(dateOnlyValue);
                emptyPageReason = null;
                return true;

            default:
                throw new InvalidOperationException(
                    $"Descriptor query preprocessing does not recognize supported target type "
                        + $"'{supportedField.Target.GetType().Name}' for query field '{supportedField.QueryFieldName}'."
                );
        }
    }

    private static void ValidateCompatibleQueryTypeOrThrow(
        string queryFieldName,
        string queryType,
        string expectedType
    )
    {
        if (string.Equals(queryType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Descriptor query preprocessing found incompatible query metadata for field '{queryFieldName}'. "
                + $"ApiSchema type '{queryType}' cannot bind to descriptor query type '{expectedType}'."
        );
    }
}
