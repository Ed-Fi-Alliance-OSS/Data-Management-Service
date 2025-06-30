// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates decimal scale and precision
/// </summary>
internal class ValidateDecimalMiddleware(ILogger _logger, IDecimalValidator _decimalValidator) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateDecimalMiddleware - {TraceId}",
            requestData.FrontendRequest.TraceId.Value
        );

        Dictionary<string, string[]> validationErrors = _decimalValidator.Validate(
            requestData.ParsedBody,
            requestData.ResourceSchema.DecimalPropertyValidationInfos
        );

        if (validationErrors.Count == 0)
        {
            await next();
        }
        else
        {
            var failureResponse = FailureResponse.ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                requestData.FrontendRequest.TraceId,
                validationErrors,
                []
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                "400",
                requestData.PathComponents.EndpointName,
                requestData.FrontendRequest.TraceId.Value
            );

            requestData.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: failureResponse,
                Headers: []
            );
            return;
        }
    }
}
