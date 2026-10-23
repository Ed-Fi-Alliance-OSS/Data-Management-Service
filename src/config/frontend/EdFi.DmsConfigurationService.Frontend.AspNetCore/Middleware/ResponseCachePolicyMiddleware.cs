// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Marks responses that carry tokens, credentials or other sensitive information as not cacheable,
/// including the 2xx shapes <see cref="SecurityHeadersMiddleware"/> deliberately leaves alone. An OAuth
/// endpoint (<see cref="OAuthEndpointMetadata"/>) gets <c>Cache-Control: no-store</c> and
/// <c>Pragma: no-cache</c> (RFC 6749 section 5.1). Any other endpoint that requires authorization gets
/// <c>Cache-Control: no-store</c> unless its handler already set a value. Other endpoints are left alone.
/// </summary>
/// <remarks>
/// The decision is made at request time, because the endpoint is only known after routing, and the
/// headers are written in a Response.OnStarting callback, which survives the response clear that the
/// framework exception handler middleware performs before <c>GlobalExceptionHandler</c> runs.
/// OnStarting callbacks run last-registered-first: this middleware runs later in the pipeline than
/// <see cref="SecurityHeadersMiddleware"/>, so its callback is registered second and therefore fires
/// first, writing the value before <see cref="SecurityHeadersMiddleware"/>'s later <c>TryAdd</c> runs
/// and finds the header already present. On an unhandled exception the framework middleware registers
/// its own callback last, so that one fires first and writes <c>Cache-Control: no-cache,no-store</c>,
/// <c>Pragma: no-cache</c> and <c>Expires: -1</c>; this callback then replaces the Cache-Control value
/// with <c>no-store</c> on an OAuth endpoint and keeps the framework's value on any other endpoint.
/// Either way every marked response, success or failure, carries exactly one <c>Cache-Control</c>
/// value.
/// </remarks>
public class ResponseCachePolicyMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata is not null)
        {
            if (metadata.GetMetadata<OAuthEndpointMetadata>() is not null)
            {
                context.Response.OnStarting(ApplyOAuthNoStore, context);
            }
            else if (
                metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
                && metadata.GetMetadata<IAllowAnonymous>() is null
            )
            {
                context.Response.OnStarting(ApplyAuthorizedNoStore, context);
            }
        }

        return next(context);
    }

    private static Task ApplyOAuthNoStore(object state)
    {
        var headers = ((HttpContext)state).Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        return Task.CompletedTask;
    }

    private static Task ApplyAuthorizedNoStore(object state)
    {
        ((HttpContext)state).Response.Headers.TryAdd("Cache-Control", "no-store");
        return Task.CompletedTask;
    }
}
