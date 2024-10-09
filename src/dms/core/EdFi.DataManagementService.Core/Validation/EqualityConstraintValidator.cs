// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using Json.More;
using Json.Path;

namespace EdFi.DataManagementService.Core.Validation;

internal interface IEqualityConstraintValidator
{
    /// <summary>
    /// Validates the equality constraints defined in MetaEd model are correct for the given API body.
    /// </summary>
    /// <param name="documentBody"></param>
    /// <param name="equalityConstraints"></param>
    /// <returns>Returns a list of validation failure messages.</returns>
    Dictionary<string, string[]> Validate(
        JsonNode? documentBody,
        IEnumerable<EqualityConstraint> equalityConstraints
    );
}

internal class EqualityConstraintValidator : IEqualityConstraintValidator
{
    public Dictionary<string, string[]> Validate(
        JsonNode? documentBody,
        IEnumerable<EqualityConstraint> equalityConstraints
    )
    {
        var validationErrors = new Dictionary<string, string[]>();
        foreach (var equalityConstraint in equalityConstraints)
        {
            var sourcePath = JsonPath.Parse(equalityConstraint.SourceJsonPath.Value);
            var targetPath = JsonPath.Parse(equalityConstraint.TargetJsonPath.Value);

            var sourcePathResult = sourcePath.Evaluate(documentBody);
            var targetPathResult = targetPath.Evaluate(documentBody);

            Trace.Assert(
                sourcePathResult.Matches != null,
                "Evaluation of sourcePathResult.Matches resulted in unexpected null"
            );
            Trace.Assert(
                targetPathResult.Matches != null,
                "Evaluation of targetPathResult.Matches resulted in unexpected null"
            );

            var combinedValues = new HashSet<JsonNode?>(sourcePathResult.Matches.Select(s => s.Value), new JsonNodeEqualityComparer());
            combinedValues.UnionWith(targetPathResult.Matches.Select(t => t.Value));

            if (combinedValues.Count > 1)
            {
                string conflictValues = string.Join(", ", combinedValues.Select(x => $"'{x}'"));
                AddValidationError(validationErrors, sourcePath, conflictValues);
                AddValidationError(validationErrors, targetPath, conflictValues);
            }
        }
        return validationErrors;
    }

    private static void AddValidationError(Dictionary<string, string[]> validationErrors, JsonPath path, string conflictValues)
    {
        string segment = path.Segments[^1].ToString().TrimStart('.');
        string errorMessage = $"All values supplied for '{segment}' must match."
                + " Review all references (including those higher up in the resource's data)"
                + $" and align the following conflicting values: {conflictValues}";

        if (validationErrors.TryGetValue(path.ToString(), out string[]? existingErrors))
        {
            validationErrors[path.ToString()] = existingErrors.Append(errorMessage).ToArray();
        }
        else
        {
            validationErrors[path.ToString()] = [errorMessage];
        }
    }
}
