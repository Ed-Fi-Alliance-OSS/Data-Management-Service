// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Api.Core.Exceptions;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

/// <summary>
/// Validates that the resource document is properly shaped.
/// </summary>
public class ValidateDocumentMiddleware(ILogger _logger, IDocumentValidatorResolver _documentValidatorResolver) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateDocumentMiddleware- {TraceId}", context.FrontendRequest.TraceId);

        var validator = _documentValidatorResolver.Resolve(new ValidatorContext
        {
            RequestActionMethod = context.FrontendRequest.Method,
            ResourceJsonSchema = context.ResourceSchema,
            IsDescriptor = context.ResourceInfo.IsDescriptor
        });

        var validationErrors = validator.Validate(context.FrontendRequest.Body)?.ToArray();

        if (validationErrors == null)
        {
            await next();
        }
        else
        {
            var exception = new BadRequestDataException("Data validation failed. See Errors for details.", errors: validationErrors);

            _logger.LogDebug("'{Status}'.'{EndpointName}' - {TraceId}",
                 exception.Status.ToString(),
                 context.PathComponents.EndpointName,
                 context.FrontendRequest.TraceId);

            context.FrontendResponse = new(
               StatusCode: exception.Status,
               Body: JsonSerializer.Serialize<BaseDetailedException>(exception)
           );
            return;
        }
    }
}
