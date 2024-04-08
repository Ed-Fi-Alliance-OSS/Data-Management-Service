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
    /// Validates basic Json Format
    /// </summary>
    /// <param name="documentBody"></param>
    /// <returns>Error messages</returns>
    IEnumerable<string>? ValidateJsonFormat(JsonNode? documentBody);

    /// <summary>
    /// Evaluates a document body against a JSON schema
    /// </summary>
    /// <param name="documentBody"></param>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    EvaluationResults Evaluate(JsonNode? documentBody, ValidatorContext validatorContext);

    /// <summary>
    /// Prunes over posted data from the document body in constructs where "additionalProperties" = false.
    /// </summary>
    /// <param name="documentBody">The posted document body with potentially over posted properties</param>
    /// <param name="evaluationResults">The results from evaluating the body against a JSON schema</param>
    /// <param name="prunedDocumentBody">Out parameter after being pruned of additional properties</param>
    /// <returns>Value indicating whether additional properties were found</returns>
    bool PruneOverPostedData(
        JsonNode? documentBody,
        EvaluationResults evaluationResults,
        out JsonNode? prunedDocumentBody
    );

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
    public IEnumerable<string>? ValidateJsonFormat(JsonNode? documentBody)
    {
        var formatValidationResult = documentBody.ValidateJsonFormat();

        if (formatValidationResult != null && formatValidationResult.Any())
        {
            return formatValidationResult;
        }
        return null;
    }

    public EvaluationResults Evaluate(JsonNode? documentBody, ValidatorContext validatorContext)
    {
        EvaluationOptions? validatorEvaluationOptions =
            new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

        var resourceSchemaValidator = schemaValidator.GetSchema(validatorContext);
        return resourceSchemaValidator.Evaluate(documentBody, validatorEvaluationOptions);
    }

    public bool PruneOverPostedData(
        JsonNode? documentBody,
        EvaluationResults evaluationResults,
        out JsonNode? prunedDocumentBody
    )
    {
        prunedDocumentBody = documentBody;

        if (documentBody == null)
            return false;

        var additionalProperties = evaluationResults
            .Details.Where(r =>
                r.EvaluationPath.Segments.Any() && r.EvaluationPath.Segments[^1] == "additionalProperties"
            )
            .ToList();

        if (additionalProperties.Count == 0)
            return false;

        foreach (var additionalProperty in additionalProperties)
        {
            JsonObject jsonObject = documentBody.AsObject();
            var prunedJsonObject = jsonObject.RemoveProperty(additionalProperty.InstanceLocation.Segments);
            documentBody = JsonNode.Parse(prunedJsonObject.ToJsonString())!;
        }

        prunedDocumentBody = documentBody;
        return true;
    }

    public IEnumerable<string>? Validate(JsonNode? documentBody, ValidatorContext validatorContext)
    {
        EvaluationOptions? validatorEvaluationOptions =
            new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

        var resourceSchemaValidator = schemaValidator.GetSchema(validatorContext);
        var results = resourceSchemaValidator.Evaluate(documentBody, validatorEvaluationOptions);

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

internal static class JsonObjectExtensions
{
    internal static JsonObject RemoveProperty(
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
