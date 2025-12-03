// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that tenant information is present when multitenancy is enabled.
/// Returns 400 Bad Request if multitenancy is enabled but tenant is missing from the request.
/// </summary>
internal class TenantValidationMiddleware(bool multiTenancyEnabled, ILogger logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering TenantValidationMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        if (multiTenancyEnabled && string.IsNullOrEmpty(requestInfo.FrontendRequest.Tenant))
        {
            logger.LogWarning(
                "TenantValidationMiddleware: Multitenancy is enabled but tenant is missing from request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.BadRequest,
                Body: FailureResponse.ForBadRequest(
                    "The tenant identifier is required when multi-tenancy is enabled. "
                        + "Include the tenant as the first segment of the URL path.",
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors: [],
                    errors: ["Missing tenant identifier in URL path."]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        await next();
    }
}
