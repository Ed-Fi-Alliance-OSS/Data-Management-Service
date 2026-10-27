// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// The source gate for the <c>/connect/*</c> endpoints (DMS-1482). Applies the per-source budget to every
/// endpoint carrying <see cref="ConnectRateLimitMetadata"/> and answers a refusal with 429 without reading
/// the request body, so the handlers' own parsing and validation, revoke's encoding check included, stay
/// exactly as they are. The per-client budget is applied later, inside token, revoke and authenticated
/// introspect handlers.
/// </summary>
public class ConnectSourceRateLimitMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <remarks>
    /// The limiter is resolved per request, not in the constructor: the pipeline is built even when
    /// ReportInvalidConfiguration has found invalid ConnectRateLimit settings, and constructing the
    /// limiter then would fail startup instead of being reported.
    /// </remarks>
    public async Task InvokeAsync(
        HttpContext context,
        ConnectRateLimiter rateLimiter,
        ILogger<ConnectSourceRateLimitMiddleware> logger
    )
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ConnectRateLimitMetadata>() is null)
        {
            await _next(context);
            return;
        }

        using RateLimitLease lease = rateLimiter.AttemptSource(context);
        if (!lease.IsAcquired)
        {
            await ConnectRateLimitRejection
                .Create(context, lease, ConnectRateLimitRejection.SourceGate, logger)
                .ExecuteAsync(context);
            return;
        }

        await _next(context);
    }
}
