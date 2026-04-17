// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalQueryRequestPreprocessor
{
    public static RelationalQueryPreprocessingResult Preprocess(
        IReadOnlyList<QueryElement> queryElements,
        RelationalQueryCapability queryCapability
    )
    {
        ArgumentNullException.ThrowIfNull(queryElements);
        ArgumentNullException.ThrowIfNull(queryCapability);

        if (queryCapability.Support is not RelationalQuerySupport.Supported)
        {
            throw new ArgumentException(
                "Relational query preprocessing requires resource query capability metadata in the supported state.",
                nameof(queryCapability)
            );
        }

        var preprocessedElements = new PreprocessedRelationalQueryElement[queryElements.Count];

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
                    $"Relational query preprocessing could not find supported query metadata for field "
                        + $"'{queryElement.QueryFieldName}'."
                );
            }

            if (supportedField.Target is not RelationalQueryFieldTarget.DocumentUuid)
            {
                preprocessedElements[index] = new PreprocessedRelationalQueryElement(
                    queryElement,
                    supportedField,
                    new PreprocessedRelationalQueryValue.Raw(queryElement.Value)
                );
                continue;
            }

            if (!Guid.TryParse(queryElement.Value, out var documentUuid))
            {
                return new RelationalQueryPreprocessingResult(
                    new RelationalQueryPreprocessingOutcome.EmptyPage(
                        $"Relational query preprocessing determined query field '{queryElement.QueryFieldName}' value "
                            + $"'{queryElement.Value}' is not a valid UUID, so the query has no matches."
                    ),
                    []
                );
            }

            preprocessedElements[index] = new PreprocessedRelationalQueryElement(
                queryElement,
                supportedField,
                new PreprocessedRelationalQueryValue.DocumentUuid(documentUuid)
            );
        }

        return new RelationalQueryPreprocessingResult(
            new RelationalQueryPreprocessingOutcome.Continue(),
            preprocessedElements
        );
    }
}
