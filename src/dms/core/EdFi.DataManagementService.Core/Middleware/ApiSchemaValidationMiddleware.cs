// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that validates the ApiSchema. Acts as a fail-fast mechanism to ensure
/// the system doesn't operate with invalid or corrupted schema definitions.
/// </summary>
internal class ApiSchemaValidationMiddleware : IPipelineStep
{
    private readonly IApiSchemaProvider _apiSchemaProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes the middleware with schema provider. The middleware now only checks
    /// the validation state from the provider instead of performing validation itself.
    /// </summary>
    public ApiSchemaValidationMiddleware(IApiSchemaProvider apiSchemaProvider, ILogger logger)
    {
        _apiSchemaProvider = apiSchemaProvider;
        _logger = logger;
    }

    /// <summary>
    /// Prevents any operations from proceeding when ApiSchema is invalid.
    /// </summary>
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ApiSchemaValidationMiddleware- {TraceId}",
            requestData.FrontendRequest.TraceId.Value
        );

        if (!_apiSchemaProvider.IsSchemaValid)
        {
            _logger.LogError("API schema is invalid. Request cannot be processed.");
            requestData.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: string.Empty,
                Headers: []
            );
        }
        else
        {
            await next();
        }
    }
}
