// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IEqualityConstraintValidator
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="documentBody"></param>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    IEnumerable<string>? Validate(JsonNode? documentBody, ValidatorContext validatorContext);
}

public class EqualityConstraintValidator : IEqualityConstraintValidator
{
    public IEnumerable<string>? Validate(JsonNode? documentBody, ValidatorContext validatorContext)
    {
        var formatValidationResult = documentBody.ValidateJsonFormat();

        if (formatValidationResult != null && formatValidationResult.Any())
        {
            return formatValidationResult;
        }

        var errors = new List<string>();
        foreach (EqualityConstraint equalityConstraint in validatorContext.ResourceJsonSchema.EqualityConstraints)
        {
            var sourcePathString = equalityConstraint.SourceJsonPath.Value;
            var targetPathString = equalityConstraint.TargetJsonPath.Value;

            var sourcePath = Json.Path.JsonPath.Parse(sourcePathString);
            var targetPath = Json.Path.JsonPath.Parse(targetPathString);

            var sourcePathResult = sourcePath.Evaluate(documentBody);
            var targetPathResult = targetPath.Evaluate(documentBody);

            var sourceMatches = sourcePathResult.Matches?.ToList();
            var targetMatches = targetPathResult.Matches?.ToList();

            if (sourcePathResult.Error != null)
            {
                errors.Add(sourcePathResult.Error);
            }

            if (targetPathResult.Error != null)
            {
                errors.Add(targetPathResult.Error);
            }

            if (sourceMatches != null && targetMatches != null)
            {
                errors.Add("BLA");
            }
        }

        return errors;
    }
}
