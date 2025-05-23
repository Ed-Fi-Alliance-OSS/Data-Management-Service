// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using Json.Path;

namespace EdFi.DataManagementService.Core.Validation;

internal interface IDecimalValidator
{
    Dictionary<string, string[]> Validate(
        JsonNode documentBody,
        IEnumerable<DecimalValidationInfo> decimalValidationInfos
    );
}

internal class DecimalValidator : IDecimalValidator
{
    public Dictionary<string, string[]> Validate(
        JsonNode documentBody,
        IEnumerable<DecimalValidationInfo> decimalValidationInfos
    )
    {
        var errors = new Dictionary<string, List<string>>();

        foreach (var info in decimalValidationInfos)
        {
            PathResult? result = JsonPath.Parse(info.Path.Value).Evaluate(documentBody);

            Trace.Assert(
                result.Matches != null,
                "Evaluation of decimalValidationInfos.Path.Matches resulted in unexpected null"
            );

            foreach (var match in result.Matches)
            {
                decimal? value = match.Value!.GetValue<decimal?>();
                if (value == null)
                {
                    AddError(errors, info.Path.Value, "Value is not a valid decimal.");
                    continue;
                }

                if (info is { TotalDigits: short totalDigits, DecimalPlaces: short decimalPlaces })
                {
                    // Calculate the maximum and minimum allowed values
                    int integerDigits = totalDigits - decimalPlaces;

                    decimal maxValue =
                        (decimal)Math.Pow(10, integerDigits) - (decimal)Math.Pow(10, -decimalPlaces);
                    decimal minValue = -maxValue;

                    if (value < minValue || value > maxValue)
                    {
                        AddError(
                            errors,
                            info.Path.Value,
                            $"{info.Path.Value[2..]} must be between {minValue} and {maxValue}."
                        );
                        continue;
                    }
                }
            }
        }

        return errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    private static void AddError(Dictionary<string, List<string>> errors, string path, string message)
    {
        if (!errors.TryGetValue(path, out var list))
        {
            list = new List<string>();
            errors[path] = list;
        }
        list.Add(message);
    }
}
