// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that the resource document is properly shaped.
/// </summary>
internal class ValidateDocumentMiddleware(ILogger _logger, IDocumentValidator _documentValidator)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateDocumentMiddleware- {TraceId}", context.FrontendRequest.TraceId);

        var (errors, validationErrors) = _documentValidator.Validate(context);

        if (errors.Length == 0 && validationErrors.Count == 0)
        {
            await next();
        }
        else
        {
            FailureResponse failureResponse;

            if (errors.Length > 0)
            {
                failureResponse = FailureResponse.ForBadRequest(
                    "The request could not be processed. See 'errors' for details.",
                    validationErrors,
                    errors
                );
            }
            else
            {
                failureResponse = FailureResponse.ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
                    validationErrors,
                    errors
                );
            }

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                failureResponse.status.ToString(),
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            context.FrontendResponse = new FrontendResponse(
                StatusCode: failureResponse.status,
                Body: JsonSerializer.Serialize(failureResponse, options),
                Headers: []
            );
        }
    }
}
