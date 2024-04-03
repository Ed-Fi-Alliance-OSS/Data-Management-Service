// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IEqualityConstraintValidator
{
    /// <summary>
    /// Validates the equality constraints defined in MetaEd model are correct for the given API body.
    /// </summary>
    /// <param name="documentBody"></param>
    /// <param name="equalityConstraints"></param>
    /// <returns>Returns a list of validation failure messages.</returns>
    IEnumerable<string>? Validate(JsonNode? documentBody, IEnumerable<EqualityConstraint> equalityConstraints);
}

public class EqualityConstraintValidator : IEqualityConstraintValidator
{
    public IEnumerable<string> Validate(JsonNode? documentBody, IEnumerable<EqualityConstraint> equalityConstraints)
    {
        var errors = new List<string>();
        foreach (EqualityConstraint equalityConstraint in equalityConstraints)
        {

            var sourcePath = Json.Path.JsonPath.Parse(equalityConstraint.SourceJsonPath.Value);
            var targetPath = Json.Path.JsonPath.Parse(equalityConstraint.TargetJsonPath.Value);

            var sourcePathResult = sourcePath.Evaluate(documentBody);
            var targetPathResult = targetPath.Evaluate(documentBody);

            if (sourcePathResult.Error != null)
            {
                errors.Add($"Constraint failure: {sourcePathResult.Error}");
            }

            if (targetPathResult.Error != null)
            {
                errors.Add($"Constraint failure: {targetPathResult.Error}");
            }

            var sourceValues = sourcePathResult.Matches!.Select(s => s.Value);
            var targetValues = targetPathResult.Matches!.Select(t => t.Value);

            if (!AllEqual(sourceValues.Concat(targetValues).ToList()))
            {
                errors.Add($"Constraint failure: document paths {equalityConstraint.SourceJsonPath.Value} and {equalityConstraint.TargetJsonPath.Value} must have the same values");
            }

            bool AllEqual(IList<JsonNode?> nodes)
            {
                return !nodes.Any() || nodes.All(n => n!.ToString().Equals(nodes[0]!.ToString()));
            }

        }

        return errors;
    }
}
