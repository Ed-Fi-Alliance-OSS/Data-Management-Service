// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that the resource document is properly shaped.
/// </summary>
internal class ValidateDocumentMiddleware(ILogger _logger, IDocumentValidator _documentValidator)
    : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateDocumentMiddleware- {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        var (errors, validationErrors) = _documentValidator.Validate(requestInfo);

        if (errors.Length == 0 && validationErrors.Count == 0)
        {
            await next();
        }
        else
        {
            JsonNode failureResponse;

            if (errors.Length > 0)
            {
                failureResponse = FailureResponse.ForBadRequest(
                    "The request could not be processed. See 'errors' for details.",
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    errors
                );
            }
            else
            {
                failureResponse = FailureResponse.ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    errors
                );
            }

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                "400",
                requestInfo.PathComponents.EndpointName,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: failureResponse,
                Headers: []
            );
        }
    }
}
