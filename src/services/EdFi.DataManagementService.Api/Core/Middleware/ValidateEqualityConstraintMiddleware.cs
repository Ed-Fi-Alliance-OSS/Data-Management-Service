// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Api.Core.Response;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

/// <summary>
/// Validates equality constraints for a JSON document. These constraints come from implicit and explicit merges in the MetaEd model.
/// </summary>
/// <param name="_logger"></param>
/// <param name="_equalityConstraintValidator"></param>
public class ValidateEqualityConstraintMiddleware(ILogger _logger, IEqualityConstraintValidator _equalityConstraintValidator) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateEqualityConstraintMiddleware- {TraceId}", context.FrontendRequest.TraceId);

        var validatorContext = new ValidatorContext(context.ResourceSchema, context.FrontendRequest.Method);
        var errors = _equalityConstraintValidator.Validate(context.FrontendRequest.Body, validatorContext.ResourceJsonSchema.EqualityConstraints)?.ToArray();

        if (errors == null || errors.Length == 0)
        {
            await next();
        }
        else
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
}

