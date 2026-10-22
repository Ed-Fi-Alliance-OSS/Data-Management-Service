// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Marks every DMS <c>/oauth/token</c> response <c>Cache-Control: no-store</c> and
/// <c>Pragma: no-cache</c> (RFC 6749 section 5.1), including the 2xx shapes
/// <see cref="SecurityHeadersMiddleware"/> deliberately leaves alone. Registered immediately after
/// <see cref="IdentityResponseCachePolicyMiddleware"/> and before the rate-limiter block, so it
/// still recognizes the endpoint on a request the rate limiter later rejects.
/// </summary>
/// <remarks>
/// The headers are set with an assignment rather than <c>TryAdd</c>. Response.OnStarting callbacks
/// run last-registered-first: this middleware runs later in the pipeline than
/// <see cref="SecurityHeadersMiddleware"/>, so its callback is registered second and therefore
/// fires first, writing the value before <see cref="SecurityHeadersMiddleware"/>'s later
/// <c>TryAdd</c> runs and finds the header already present. The result is exactly one
/// <c>Cache-Control</c> value on every token response, success or failure.
/// </remarks>
public class TokenResponseCachePolicyMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TokenEndpointMetadata>() is not null)
        {
            context.Response.OnStarting(ApplyNoStore, context);
        }

        return next(context);
    }

    private static Task ApplyNoStore(object state)
    {
        var headers = ((HttpContext)state).Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        return Task.CompletedTask;
    }
}
