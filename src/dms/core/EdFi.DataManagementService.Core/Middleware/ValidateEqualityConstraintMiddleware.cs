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
/// Validates equality constraints for a JSON document. These constraints come from implicit and
/// explicit merges in the MetaEd model.
/// </summary>
/// <param name="_logger"></param>
/// <param name="_equalityConstraintValidator"></param>
internal class ValidateEqualityConstraintMiddleware(
    ILogger _logger,
    IEqualityConstraintValidator _equalityConstraintValidator
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateEqualityConstraintMiddleware- {TraceId}",
            context.FrontendRequest.TraceId
        );

        Dictionary<string, string[]> validationErrors = _equalityConstraintValidator.Validate(
            context.ParsedBody,
            context.ResourceSchema.EqualityConstraints
        );

        if (validationErrors.Count == 0)
        {
            await next();
        }
        else
        {
            var failureResponse = FailureResponse.ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                context.FrontendRequest.TraceId,
                validationErrors,
                []
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                "400",
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            context.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: failureResponse,
                Headers: []
            );
            return;
        }
    }
}
