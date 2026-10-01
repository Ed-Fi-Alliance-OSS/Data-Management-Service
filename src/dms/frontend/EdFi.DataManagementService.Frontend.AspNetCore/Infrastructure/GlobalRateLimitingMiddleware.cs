// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Applies the host's global rate limiter to every request. DMS configures only a global limiter,
/// so this replaces ASP.NET Core's rate-limiting middleware, which also builds an endpoint-policy
/// limiter that it never disposes; that limiter's replenishment timer keeps the whole request
/// pipeline, and so the host, reachable after the host is disposed. The global limiter is a
/// container-owned singleton, disposed with the host.
/// </summary>
internal sealed class GlobalRateLimitingMiddleware(
    RequestDelegate next,
    PartitionedRateLimiter<HttpContext> globalLimiter
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        RateLimitLease lease;
        try
        {
            lease = await globalLimiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away while waiting for a permit; there is no one to answer.
            return;
        }

        using (lease)
        {
            if (lease.IsAcquired)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await WebApplicationBuilderExtensions.WriteRateLimitRejectionAsync(
                new OnRejectedContext { HttpContext = context, Lease = lease },
                context.RequestAborted
            );
        }
    }
}
