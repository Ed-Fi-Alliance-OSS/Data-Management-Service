// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Response;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

/// <summary>
/// Validates that the resource document is properly shaped.
/// </summary>
public class ValidateDocumentMiddleware(ILogger _logger, IDocumentValidator _documentValidator)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateDocumentMiddleware- {TraceId}", context.FrontendRequest.TraceId);

        var validatorContext = new ValidatorContext(context.ResourceSchema, context.FrontendRequest.Method);

        // First check for basic JSON errors
        var jsonErrors = _documentValidator.ValidateJsonFormat(context.FrontendRequest.Body);
        if (jsonErrors != null && jsonErrors.Any())
        {
            SendFailureResponse(context, jsonErrors.ToArray());
        }

        // Evaluate schema
        var evaluationResult = _documentValidator.Evaluate(context.FrontendRequest.Body, validatorContext);

        if (
            _documentValidator.PruneOverPostedData(
                context.FrontendRequest.Body,
                evaluationResult,
                out JsonNode? prunedBody
            )
        )
        {
            // Used pruned body for the remainder of pipeline
            context.FrontendRequest = context.FrontendRequest with
            {
                Body = prunedBody
            };
        }

        var errors = _documentValidator.Validate(context.FrontendRequest.Body, validatorContext)?.ToArray();

        if (errors == null || errors.Length == 0)
        {
            await next();
        }
        else
        {
            SendFailureResponse(context, errors);
        }
    }

    private void SendFailureResponse(PipelineContext context, string[] errors)
    {
        var failureResponse = FailureResponse.ForDataValidation(
            "Data validation failed. See errors for details.",
            null,
            errors
        );

        _logger.LogDebug(
            "'{Status}'.'{EndpointName}' - {TraceId}",
            failureResponse.status.ToString(),
            context.PathComponents.EndpointName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = new(
            StatusCode: failureResponse.status,
            Body: JsonSerializer.Serialize(failureResponse)
        );
        return;
    }
}
