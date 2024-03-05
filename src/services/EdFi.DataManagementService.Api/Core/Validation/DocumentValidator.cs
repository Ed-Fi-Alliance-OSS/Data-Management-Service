// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema.Extensions;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IDocumentValidator
{
    /// <summary>
    /// Validates input json
    /// </summary>
    /// <param name="input"></param>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    IEnumerable<string>? Validate(JsonNode? input, ValidatorContext validatorContext);
}

public class DocumentValidator(ISchemaValidator schemaValidator) : IDocumentValidator
{
    public IEnumerable<string>? Validate(JsonNode? input, ValidatorContext validatorContext)
    {
        var formatValidationResult = input.ValidateJsonFormat();

        if (formatValidationResult != null
            && formatValidationResult.Any())
        {
            return formatValidationResult;
        }

        EvaluationOptions? validatorEvaluationOptions = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        };

        var resourceSchemaValidator = schemaValidator.GetSchema(validatorContext);
        var results = resourceSchemaValidator.Evaluate(input, validatorEvaluationOptions);

        return PruneValidationErrors(results);

        List<string>? PruneValidationErrors(EvaluationResults results)
        {
            var validationErrors = new List<string>();
            foreach (var detail in results.Details)
            {
                var propertyName = string.Empty;

                if (detail.InstanceLocation != null &&
                    detail.InstanceLocation.Segments.Length != 0)
                {
                    propertyName = $"{detail.InstanceLocation.Segments[^1].Value} : ";
                }
                if (detail.HasErrors)
                {
                    if (detail.Errors != null && detail.Errors.Any())
                    {
                        foreach (var error in detail.Errors)
                        {
                            validationErrors.Add($"{propertyName}{error.Value}");
                        }
                    }
                    if (detail.EvaluationPath.Segments.Any() && detail.EvaluationPath.Segments[^1] == "additionalProperties")
                    {
                        validationErrors.Add($"{propertyName}Overpost");
                    }
                }
            }
            return validationErrors.Where(x => !x.Contains("All values fail against the false schema")).ToList();
        }
    }
}
