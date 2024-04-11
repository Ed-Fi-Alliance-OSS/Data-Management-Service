// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.Pipeline;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IDocumentValidator
{
    /// <summary>
    /// Validates a document body against a JSON Schema
    /// </summary>
    /// <param name="context"></param>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    IEnumerable<string> Validate(PipelineContext context, ValidatorContext validatorContext);
}

public class DocumentValidator(ISchemaValidator schemaValidator) : IDocumentValidator
{
    public IEnumerable<string> Validate(PipelineContext context, ValidatorContext validatorContext)
    {
        var formatValidationResult = context.FrontendRequest.Body.ValidateJsonFormat();

        if (formatValidationResult != null && formatValidationResult.Any())
        {
            return formatValidationResult;
        }

        EvaluationOptions validatorEvaluationOptions =
            new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

        var resourceSchemaValidator = schemaValidator.GetSchema(validatorContext);
        var results = resourceSchemaValidator.Evaluate(
            context.FrontendRequest.Body,
            validatorEvaluationOptions
        );

        if (PruneOverpostedData(context.FrontendRequest.Body, results, out JsonNode? prunedBody))
        {
            // Used pruned body for the remainder of pipeline
            context.FrontendRequest = context.FrontendRequest with
            {
                Body = prunedBody
            };

            results = resourceSchemaValidator.Evaluate(
                context.FrontendRequest.Body,
                validatorEvaluationOptions
            );
        }

        return PruneValidationErrors(results);

        bool PruneOverpostedData(
            JsonNode? documentBody,
            EvaluationResults evaluationResults,
            out JsonNode? prunedDocumentBody
        )
        {
            Trace.Assert(documentBody != null, "Null document body failure");
            prunedDocumentBody = documentBody;

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
                var prunedJsonObject = jsonObject.RemoveProperty(
                    additionalProperty.InstanceLocation.Segments
                );
                documentBody = JsonNode.Parse(prunedJsonObject.ToJsonString());
                Trace.Assert(documentBody != null);
            }

            prunedDocumentBody = documentBody;
            return true;
        }

        List<string> PruneValidationErrors(EvaluationResults results)
        {
            var validationErrors = new List<string>();
            foreach (var detail in results.Details)
            {
                var propertyName = string.Empty;

                if (detail.InstanceLocation != null && detail.InstanceLocation.Segments.Length != 0)
                {
                    propertyName = $"{detail.InstanceLocation.Segments[^1].Value} : ";
                }
                if (detail.Errors != null && detail.Errors.Any())
                {
                    foreach (var error in detail.Errors)
                    {
                        validationErrors.Add($"{propertyName}{error.Value}");
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

        var node = jsonObject[segments[0].Value];
        Trace.Assert(node != null, "PointerSegment not found on JsonObject");
        var nodeObj = node.AsObject();
        nodeObj.RemoveProperty(segments.Skip(1).ToArray());
        return jsonObject;
    }
}
