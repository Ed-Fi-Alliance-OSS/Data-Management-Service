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
public class ValidateDocumentMiddleware(ILogger _logger, IDocumentValidator _documentValidator) : IPipelineStep
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
            var exception = new BadRequestDataException("Data validation failed. See errors for details.", errors: errors).AsSerializableModel();

            _logger.LogDebug("'{Status}'.'{EndpointName}' - {TraceId}",
                 exception.Status.ToString(),
                 context.PathComponents.EndpointName,
                 context.FrontendRequest.TraceId);

            context.FrontendResponse = new(
               StatusCode: exception.Status,
               Body: JsonSerializer.Serialize(exception)
           );
            return;
        }
    }
}
