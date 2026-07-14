// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Writes the Ed-Fi Problem Details contract for framework-generated authentication (401) and
/// authorization (403) failures on secured endpoints, so those responses carry the same structured
/// body and application/problem+json content type as the rest of the API instead of an empty
/// framework body. Successful authorization is delegated to the default handler.
/// </summary>
internal sealed class ProblemDetailsAuthorizationMiddlewareResultHandler
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    )
    {
        if (authorizeResult.Challenged)
        {
            await FailureResults
                .Unauthorized(
                    ["Authentication is required to access this resource."],
                    context.TraceIdentifier
                )
                .ExecuteAsync(context);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await FailureResults
                .Forbidden(["Access to this resource is forbidden."], context.TraceIdentifier)
                .ExecuteAsync(context);
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
