// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.RateLimiting;
using EdFi.DataManagementService.Core.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Applies the host's global rate limiter to every request. DMS configures only a global limiter,
/// so this replaces ASP.NET Core's rate-limiting middleware, which also builds an endpoint-policy
/// limiter that it never disposes; that limiter's replenishment timer keeps the whole request
/// pipeline, and so the host, reachable after the host is disposed. The global limiter is a
/// container-owned singleton, disposed with the host.
///
/// A request whose bearer token Core's JWT validation accepts, and which carries a non-empty
/// <c>client_id</c>, spends that client's budget; every other request spends its connection
/// address's budget. Resolving the client only chooses a partition: it neither authenticates nor
/// authorizes, and Core still authenticates every admitted request on its own.
/// </summary>
internal sealed class GlobalRateLimitingMiddleware(
    RequestDelegate next,
    PartitionedRateLimiter<HttpContext> globalLimiter,
    IValidatedClientIdResolver clientIdResolver
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        RateLimitLease lease;
        try
        {
            context.Features.Set(new RateLimitPartitionFeature(await ResolvePartitionKeyAsync(context)));
            lease = await AcquireAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away while its identity was resolved or while it waited for a permit,
            // so there is no one to answer.
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

    /// <summary>
    /// The Authorization header is passed as the data pipeline hands it to Core: every value,
    /// comma-joined. A blank or repeated header is then the malformed header Core rejects, and
    /// selects no client. StringValues.ToString() is not used because it drops empty values, which
    /// would turn a valid token sent with a blank duplicate into a header that parses. Without the
    /// header the resolver is not called at all.
    /// </summary>
    private async Task<RateLimitPartitionKey> ResolvePartitionKeyAsync(HttpContext context)
    {
        if (
            context.Request.Headers.TryGetValue(HeaderNames.Authorization, out StringValues authorization)
            && await clientIdResolver.ResolveAsync(
                string.Join(",", authorization.ToArray()),
                context.RequestAborted
            )
                is { } clientId
        )
        {
            return RateLimitPartitionKey.ForClient(clientId);
        }

        return RateLimitPartitionKey.ForAddress(context.Connection.RemoteIpAddress);
    }

    /// <summary>
    /// The partitioned limiter hands back a partition's limiter after releasing its lock, and its idle
    /// cleanup can dispose that partition in between, so acquiring from it throws
    /// ObjectDisposedException. Acquiring again resolves the key's current partition. Only acquisition
    /// is retried, and only once: a second failure, including the limiter itself having been disposed
    /// with the host, propagates.
    /// </summary>
    private async ValueTask<RateLimitLease> AcquireAsync(HttpContext context)
    {
        try
        {
            return await globalLimiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
        }
        catch (ObjectDisposedException)
        {
            return await globalLimiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
        }
    }
}
