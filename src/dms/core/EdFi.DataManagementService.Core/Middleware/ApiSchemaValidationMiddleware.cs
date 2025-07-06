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
internal class ApiSchemaValidationMiddleware(IApiSchemaProvider apiSchemaProvider, ILogger logger)
    : IPipelineStep
{
    /// <summary>
    /// Prevents any operations from proceeding when ApiSchema is invalid.
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ApiSchemaValidationMiddleware- {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        if (!apiSchemaProvider.IsSchemaValid)
        {
            logger.LogError("API schema is invalid. Request cannot be processed.");
            requestInfo.FrontendResponse = new FrontendResponse(
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
