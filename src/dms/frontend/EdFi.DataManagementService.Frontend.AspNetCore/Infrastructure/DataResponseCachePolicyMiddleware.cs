// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Marks every response from a <see cref="DataEndpointMetadata"/> endpoint <c>Cache-Control:
/// no-store</c>, including the 2xx and 304 shapes <see cref="SecurityHeadersMiddleware"/> leaves
/// alone, so neither a shared cache nor the client's own cache keeps a copy of education records.
/// The resource endpoints still send their ETag, so a client that keeps it can revalidate with
/// If-None-Match. Registered after <see cref="TokenResponseCachePolicyMiddleware"/> and before the
/// rate-limiter block, so it still recognizes the endpoint on a request the rate limiter later rejects.
/// </summary>
/// <remarks>
/// The header is set with an assignment rather than <c>TryAdd</c>. Response.OnStarting callbacks
/// run last-registered-first: this middleware runs later in the pipeline than
/// <see cref="SecurityHeadersMiddleware"/>, so its callback is registered second and therefore
/// fires first, writing the value before <see cref="SecurityHeadersMiddleware"/>'s later
/// <c>TryAdd</c> runs and finds the header already present. The result is exactly one
/// <c>Cache-Control</c> value on every data response, success or failure.
/// </remarks>
public class DataResponseCachePolicyMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DataEndpointMetadata>() is not null)
        {
            context.Response.OnStarting(ApplyNoStore, context);
        }

        return next(context);
    }

    private static Task ApplyNoStore(object state)
    {
        ((HttpContext)state).Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}
