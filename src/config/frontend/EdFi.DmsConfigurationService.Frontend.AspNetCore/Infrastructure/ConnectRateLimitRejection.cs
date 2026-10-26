// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The 429 answer for a <c>/connect/*</c> request refused by <see cref="ConnectRateLimiter"/>, shared by
/// the source gate (middleware) and the client gate (handlers) through <see cref="Create"/>
/// so both answer identically. The format follows the route's error contract, the same signal
/// <see cref="GlobalExceptionHandler"/> uses: the OAuth body on routes carrying
/// <see cref="OAuthErrorContractMetadata"/> (only <c>/connect/revoke</c>), Ed-Fi problem details otherwise.
/// </summary>
internal static class ConnectRateLimitRejection
{
    public const string SourceGate = "source";
    public const string ClientGate = "client";

    /// <summary>RFC 6749 and RFC 7009 define no throttling code; DMS-1491 may revisit this value.</summary>
    public const string OAuthError = "temporarily_unavailable";

    public static IResult Create(HttpContext context, RateLimitLease lease, string gate, ILogger logger)
    {
        Prepare(context, lease, gate, logger);
        return UsesOAuthContract(context)
            ? OAuthErrorResponseWriter.Create(
                StatusCodes.Status429TooManyRequests,
                OAuthError,
                FailureResponse.TooManyRequestsDetail
            )
            : FailureResults.TooManyRequests(context.TraceIdentifier);
    }

    private static void Prepare(HttpContext context, RateLimitLease lease, string gate, ILogger logger)
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
    }

    private static bool UsesOAuthContract(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<OAuthErrorContractMetadata>() is not null;
}
