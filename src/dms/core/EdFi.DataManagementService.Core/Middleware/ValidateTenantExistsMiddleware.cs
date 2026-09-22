// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Identity-pipeline-only step that confirms the request's tenant is known before any client-to-tenant
/// binding or claim-set lookup runs (design.md D4). Pass-through when <paramref name="multiTenancyEnabled" />
/// is false, matching <see cref="TenantValidationMiddleware" />'s constructor shape. Otherwise asks the
/// shared <see cref="IdentityTenantSnapshot" /> whether the request's tenant exists and maps
/// <see cref="TenantExistenceOutcome.Absent" /> to a tenant <c>404</c> and
/// <see cref="TenantExistenceOutcome.Unavailable" /> to a <c>503</c>; an
/// <see cref="OperationCanceledException" /> for the request's own <see cref="RequestInfo.RequestCancellationToken" />
/// propagates rather than producing either response.
/// </summary>
internal sealed class ValidateTenantExistsMiddleware(
    bool multiTenancyEnabled,
    IdentityTenantSnapshot identityTenantSnapshot,
    ILogger<ValidateTenantExistsMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (!multiTenancyEnabled)
        {
            await next();
            return;
        }

        string? tenant = requestInfo.FrontendRequest.Tenant;
        TenantExistenceOutcome outcome = await identityTenantSnapshot.CheckAsync(
            tenant ?? string.Empty,
            requestInfo.RequestCancellationToken
        );

        switch (outcome)
        {
            case TenantExistenceOutcome.Exists:
                await next();
                return;
            case TenantExistenceOutcome.Absent:
                logger.LogInformation(
                    "Identity request tenant was not found - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 404,
                    Body: FailureResponse.ForNotFound(
                        "The specified tenant could not be found.",
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
            case TenantExistenceOutcome.Unavailable:
                logger.LogWarning(
                    "Identity request tenant existence could not be determined - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 503,
                    Body: FailureResponse.ForServiceUnavailable(requestInfo.FrontendRequest.TraceId),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
        }
    }
}
