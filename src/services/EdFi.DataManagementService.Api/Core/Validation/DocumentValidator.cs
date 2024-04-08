// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
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
    IEnumerable<string>? Validate(JsonNode? documentBody, ValidatorContext validatorContext);
}

public class DocumentValidator(ISchemaValidator schemaValidator) : IDocumentValidator
{
    public IEnumerable<string>? Validate(JsonNode? documentBody, ValidatorContext validatorContext)
    {
        var formatValidationResult = documentBody.ValidateJsonFormat();

        if (formatValidationResult != null && formatValidationResult.Any())
        {
            return formatValidationResult;
        }

        EvaluationOptions? validatorEvaluationOptions =
            new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

        var resourceSchemaValidator = schemaValidator.GetSchema(validatorContext);
        var results = resourceSchemaValidator.Evaluate(documentBody, validatorEvaluationOptions);

        var additionalProperties = results.Details.Where(r =>
            r.EvaluationPath.Segments.Any() && r.EvaluationPath.Segments[^1] == "additionalProperties"
        );

        var instancePointers = additionalProperties.Select(a => a.EvaluationPath);
        Trace.Assert(documentBody != null);
        Trace.Assert(instancePointers.Any());

        JsonObject jsonObject = documentBody.AsObject();

        foreach (var additionalProperty in additionalProperties)
        {
            var prunedJsonObject = jsonObject.RemoveProperty(additionalProperty.InstanceLocation.Segments);
            documentBody = JsonNode.Parse(prunedJsonObject.ToJsonString());
        }

        results = resourceSchemaValidator.Evaluate(documentBody, validatorEvaluationOptions);
        return PruneValidationErrors(results);

        List<string>? PruneValidationErrors(EvaluationResults results)
        {
            var validationErrors = new List<string>();
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
                            validationErrors.Add($"{propertyName}{error.Value}");
                        }
                    }
                    if (
                        detail.EvaluationPath.Segments.Any()
                        && detail.EvaluationPath.Segments[^1] == "additionalProperties"
                    )
                    {
                        validationErrors.Add($"{propertyName}Overpost");
                    }
                }
            }
            return validationErrors
                .Where(x => !x.Contains("All values fail against the false schema"))
                .ToList();
        }
    }
}

public static class JsonObjectExtensions
{
    public static JsonObject RemoveProperty(
        this JsonObject jsonObject,
        Json.Pointer.PointerSegment[] segments
    )
    {
        if (segments.Length == 0)
            return jsonObject;
        if (segments.Length == 1)
        {
            jsonObject.Remove(segments[0].Value);
            return jsonObject;
        }

        var node = jsonObject[segments[0].Value]!;
        var nodeObj = node.AsObject();
        nodeObj.RemoveProperty(segments.Skip(1).ToArray());
        return jsonObject;
    }
}
