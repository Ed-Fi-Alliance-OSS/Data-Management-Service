// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using static EdFi.DataManagementService.Api.Core.ResponseBody.DataValidationFailureElements;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Api.Core.Exceptions;

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
        var errors = _documentValidator.Validate(context.FrontendRequest.Body, validatorContext)?.ToArray();

        if (errors == null || errors.Length == 0)
        {
            await next();
        }
        else
        {
            FailureResponseBody body =
                new(
                    title: FailureTitle,
                    status: 400,
                    detail: FailureDetail,
                    type: FailureType,
                    errors: errors,
                    correlationId: context.FrontendRequest.TraceId.Value
                );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                body.status.ToString(),
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            context.FrontendResponse = new(StatusCode: body.status, Body: JsonSerializer.Serialize(body));
            return;
        }
    }
}
