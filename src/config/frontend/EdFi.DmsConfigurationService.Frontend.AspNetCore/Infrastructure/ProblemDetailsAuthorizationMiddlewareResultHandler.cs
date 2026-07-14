// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Adds the Ed-Fi Problem Details contract to framework-generated authentication (401) and
/// authorization (403) failures on secured endpoints, so those responses carry the same structured
/// body and application/problem+json content type as the rest of the API instead of an empty
/// framework body.
/// </summary>
/// <remarks>
/// The default handler runs first so the configured authentication scheme performs its
/// challenge/forbid (status code, <c>WWW-Authenticate</c> header, and any other scheme-produced
/// behavior). This keeps the behavior provider-agnostic and preserves the challenge semantics; the
/// Problem Details body is then written into the otherwise-empty response.
/// </remarks>
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
        // Let the configured authentication scheme perform the challenge/forbid first (preserving the
        // status code and WWW-Authenticate header). The default challenge/forbid writes no body.
        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);

        if (!authorizeResult.Challenged && !authorizeResult.Forbidden)
        {
            // Authorization succeeded (the endpoint ran) - nothing to enrich.
            return;
        }

        if (context.Response.HasStarted)
        {
            // Something already produced a body; do not overwrite it.
            return;
        }

        IResult problemDetails = authorizeResult.Challenged
            ? FailureResults.Unauthorized(
                ["Authentication is required to access this resource."],
                context.TraceIdentifier
            )
            : FailureResults.Forbidden(["Access to this resource is forbidden."], context.TraceIdentifier);

        await problemDetails.ExecuteAsync(context);
    }
}
