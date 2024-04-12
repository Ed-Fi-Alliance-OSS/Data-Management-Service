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

        var pruneResult = PruneOverpostedData(context.FrontendRequest.Body, results);

        if (pruneResult is PruneResult.Pruned pruned)
        {
            // Used pruned body for the remainder of pipeline
            context.FrontendRequest = context.FrontendRequest with
            {
                Body = pruned.prunedDocumentBody
            };

            // Now re-evaluate the pruned body
            results = resourceSchemaValidator.Evaluate(
                context.FrontendRequest.Body,
                validatorEvaluationOptions
            );
        }

        return ValidationErrorsFrom(results);

        PruneResult PruneOverpostedData(JsonNode? documentBody, EvaluationResults evaluationResults)
        {
            if (documentBody == null)
                return new PruneResult.NotPruned();

            var additionalProperties = evaluationResults
                .Details.Where(r =>
                    r.EvaluationPath.Segments.Any() && r.EvaluationPath.Segments[^1] == "additionalProperties"
                )
                .ToList();

            if (additionalProperties.Count == 0)
                return new PruneResult.NotPruned();

            foreach (var additionalProperty in additionalProperties)
            {
                JsonObject jsonObject = documentBody.AsObject();
                var prunedJsonObject = jsonObject.RemoveProperty(
                    additionalProperty.InstanceLocation.Segments
                );
                var prunedDocumentBody = JsonNode.Parse(prunedJsonObject.ToJsonString());
                Trace.Assert(prunedDocumentBody != null, "Unexpected null after parsing pruned object");
                documentBody = prunedDocumentBody;
            }

            return new PruneResult.Pruned(documentBody);
        }

        List<string> ValidationErrorsFrom(EvaluationResults results)
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

/// <summary>
/// The result of pruning "additionalProperties" aka overposted data from a JsonNode.
/// If none were found, result should be NotPruned. Otherwise, Pruned with the pruneDocumentBody
/// </summary>
internal abstract record PruneResult
{
    public record NotPruned() : PruneResult;

    public record Pruned(JsonNode prunedDocumentBody) : PruneResult;
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
