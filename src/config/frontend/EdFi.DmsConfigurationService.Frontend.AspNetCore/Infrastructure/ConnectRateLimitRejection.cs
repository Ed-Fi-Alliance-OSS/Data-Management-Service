// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Threading.RateLimiting;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The 429 answer for a <c>/connect/*</c> request refused by <see cref="ConnectRateLimiter"/>, shared by
/// the source gate (middleware) and the client gate (handlers) through <see cref="Create"/>
/// so both answer with Ed-Fi problem details, including on routes whose other errors use the OAuth
/// contract. DMS-1482 requires this format specifically for rate-limit rejections.
/// </summary>
internal static class ConnectRateLimitRejection
{
    public const string SourceGate = "source";
    public const string ClientGate = "client";

    public static IResult Create(HttpContext context, RateLimitLease lease, string gate, ILogger logger)
    {
        // The gate and the route template are fixed, service-owned text. The client id, the address
        // and every credential stay out of the log.
        logger.LogDebug(
            "Connect rate limit rejected a request at the {Gate} gate for route {Route}",
            gate,
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown"
        );

        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            // Rounded up so a client never retries sooner than the limiter recommends (DMS precedent).
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                CultureInfo.InvariantCulture
            );
        }

        return FailureResults.TooManyRequests(context.TraceIdentifier);
    }
}
