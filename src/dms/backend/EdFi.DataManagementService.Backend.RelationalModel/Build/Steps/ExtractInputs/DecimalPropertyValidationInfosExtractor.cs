// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs.ApiSchemaNodeRequirements;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

internal static class DecimalPropertyValidationInfosExtractor
{
    /// <summary>
    /// Extracts decimal validation metadata from <c>decimalPropertyValidationInfos</c>, keyed by the
    /// canonical JSON path.
    /// </summary>
    /// <param name="resourceSchema">The resource schema containing decimal validation metadata.</param>
    /// <returns>A mapping of canonical JSON path to decimal validation information.</returns>
    internal static Dictionary<string, DecimalPropertyValidationInfo> ExtractDecimalPropertyValidationInfos(
        JsonObject resourceSchema
    )
    {
        Dictionary<string, DecimalPropertyValidationInfo> decimalInfosByPath = new(StringComparer.Ordinal);

        if (resourceSchema["decimalPropertyValidationInfos"] is JsonArray decimalInfos)
        {
            foreach (var decimalInfo in decimalInfos)
            {
                if (decimalInfo is null)
                {
                    throw new InvalidOperationException(
                        "Expected decimalPropertyValidationInfos to not contain null entries, invalid ApiSchema."
                    );
                }

                if (decimalInfo is not JsonObject decimalInfoObject)
                {
                    throw new InvalidOperationException(
                        "Expected decimalPropertyValidationInfos entries to be objects, invalid ApiSchema."
                    );
                }

                var decimalPath = RequireString(decimalInfoObject, "path");
                var totalDigits = decimalInfoObject["totalDigits"]?.GetValue<short?>();
                var decimalPlaces = decimalInfoObject["decimalPlaces"]?.GetValue<short?>();
                var decimalJsonPath = JsonPathExpressionCompiler.Compile(decimalPath);

                if (
                    !decimalInfosByPath.TryAdd(
                        decimalJsonPath.Canonical,
                        new DecimalPropertyValidationInfo(decimalJsonPath, totalDigits, decimalPlaces)
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Decimal validation info for '{decimalJsonPath.Canonical}' is already defined."
                    );
                }
            }
        }

        return decimalInfosByPath;
    }
}
