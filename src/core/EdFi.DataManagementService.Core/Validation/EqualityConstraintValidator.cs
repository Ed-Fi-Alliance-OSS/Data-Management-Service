// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using Json.More;

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
        List<string> errors = [];
        var validationErrors = new Dictionary<string, string[]>();
        foreach (var equalityConstraint in equalityConstraints)
        {
            var sourcePath = Json.Path.JsonPath.Parse(equalityConstraint.SourceJsonPath.Value);
            var targetPath = Json.Path.JsonPath.Parse(equalityConstraint.TargetJsonPath.Value);

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

            var sourceValues = sourcePathResult.Matches.Select(s => s.Value);
            var targetValues = targetPathResult.Matches.Select(t => t.Value);

            if (!AllEqual(sourceValues.Concat(targetValues).ToList()))
            {
                string conflictValues = string.Join(
                    ", ",
                    sourceValues
                        .Concat(targetValues)
                        .Distinct(new JsonNodeEqualityComparer())
                        .Select(x => $"'{x}'")
                        .ToArray()
                );
                string targetSegment = targetPath.Segments.Last().ToString()[1..];
                errors.Add(
                    $"All values supplied for '{targetSegment}' must match."
                        + " Review all references (including those higher up in the resource's data)"
                        + " and align the following conflicting values: "
                        + conflictValues
                );
                validationErrors.Add(sourcePath.ToString(), errors.ToArray());
            }

            bool AllEqual(IList<JsonNode?> nodes)
            {
                return nodes.All(n => JsonNode.DeepEquals(nodes[0], n));
            }
        }

        return validationErrors;
    }
}
