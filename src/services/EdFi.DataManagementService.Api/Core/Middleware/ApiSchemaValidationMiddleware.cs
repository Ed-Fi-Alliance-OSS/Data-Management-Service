// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Response;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

public class ApiSchemaValidationMiddleware(
    IApiSchemaProvider _apiSchemaProvider,
    IApiSchemaValidator _apiSchemaValidator,
    ILogger _logger
) : IPipelineStep
{
    private readonly Lazy<List<SchemaValidationFailure>> _schemaValidationFailures =
        new(() =>
        {
            var validationErrors = _apiSchemaValidator.Validate(_apiSchemaProvider.ApiSchemaRootNode).Value;
            if (validationErrors.Any())
            {
                _logger.LogCritical("Api schema validation failed.");
                foreach (var error in validationErrors)
                {
                    _logger.LogCritical(error.FailurePath.Value, error.FailureMessages);
                }
            }
            return validationErrors;
        });

    public List<SchemaValidationFailure> SchemaValidationFailures => _schemaValidationFailures.Value;

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ApiSchemaValidationMiddleware- {TraceId}",
            context.FrontendRequest.TraceId
        );

        if (SchemaValidationFailures.Any())
        {
            context.FrontendResponse = new(StatusCode: 500, Body: string.Empty);
        }
        else
        {
            await next();
        }
    }
}
