// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Core.ApiSchema.Extensions;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IDocumentValidator
{
    /// <summary>
    /// Validates a document body against a JSON Schema
    /// </summary>
    /// <param name="documentBody"></param>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    (string[]?, Dictionary<string, string[]>?) Validate(JsonNode? documentBody, ValidatorContext validatorContext);
}

public class DocumentValidator(ISchemaValidator schemaValidator) : IDocumentValidator
{
    public (string[]?, Dictionary<string, string[]>?) Validate(JsonNode? documentBody, ValidatorContext validatorContext)
    {
        var formatValidationResult = documentBody.ValidateJsonFormat();

        if (formatValidationResult != null && formatValidationResult.Any())
        {
            return (formatValidationResult.ToArray(), null); //errors
        }

        EvaluationOptions? validatorEvaluationOptions =
            new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

        var resourceSchemaValidator = schemaValidator.GetSchema(validatorContext);
        var results = resourceSchemaValidator.Evaluate(documentBody, validatorEvaluationOptions);

        return (null, PruneValidationErrors(results)); //validationErrors

        Dictionary<string, string[]> PruneValidationErrors(EvaluationResults results)
        {
            Regex regex = new Regex("\"([^\"]*)\"");
            var validationErrors = new Dictionary<string, string[]>();
            var detailValidationError = new List<string>();
            foreach (var detail in results.Details)
            {
                var propertyName = string.Empty;

                if (detail.InstanceLocation != null && detail.InstanceLocation.Segments.Length != 0)
                {
                    propertyName = $"{detail.InstanceLocation.Segments[^1].Value} : ";
                }
                if (detail.HasErrors)
                {
                    if (detail.Errors != null && detail.Errors.Any())
                    {
                        foreach (var error in detail.Errors)
                        {
                            MatchCollection matches = regex.Matches(error.Value);
                            foreach (Match match in matches)
                            {
                                propertyName = match.Groups[1].Value;
                            }
                            detailValidationError.Add($"{propertyName}{error.Value}");
                            validationErrors.Add(propertyName, detailValidationError.ToArray());
                        }
                    }
                    if (
                        detail.EvaluationPath.Segments.Any()
                        && detail.EvaluationPath.Segments[^1] == "additionalProperties"
                    )
                    {
                        detailValidationError.Add($"{propertyName}Overpost");
                    }
                }
            }
            return validationErrors;
        }
    }

    public Dictionary<string, string[]>? ValidateFormat(JsonNode? documentBody, ValidatorContext validatorContext)
    {
        var result = new Dictionary<string, string[]>();

        return result;
    }
}
