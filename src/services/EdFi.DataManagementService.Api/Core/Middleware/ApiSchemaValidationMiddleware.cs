// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

public class ApiSchemaValidationMiddleware(
    IApiSchemaProvider _apiSchemaProvider,
    IApiSchemaValidator _apiSchemaValidator,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ApiSchemaValidationMiddleware- {TraceId}",
            context.FrontendRequest.TraceId
        );

        var document = _apiSchemaProvider.ApiSchemaRootNode;
        var validationErrors = _apiSchemaValidator.Validate(document).Value;

        if (validationErrors != null && validationErrors.Any())
        {
            context.FrontendResponse = new(StatusCode: 500, Body: string.Empty);
        }
        else
        {
            await next();
        }
    }
}
