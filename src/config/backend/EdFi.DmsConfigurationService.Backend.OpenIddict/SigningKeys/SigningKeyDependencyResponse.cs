// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The answer when a dependency of authentication cannot be read (spec D-9): 503, a <c>Retry-After</c> of the refresh
/// interval capped at 30 seconds, and a generic problem body that carries only the correlation id (I-7). The request
/// boundary of both bearer schemes and the JWKS endpoint answer with it.
/// </summary>
public static class SigningKeyDependencyResponse
{
    private const string ProblemJsonContentType = "application/problem+json";
    private const int MaxRetryAfterSeconds = 30;

    /// <summary>The <c>Retry-After</c> value, in whole seconds: the refresh interval, capped at 30 seconds.</summary>
    public static string RetryAfterSeconds(SigningKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return ((int)Math.Min(settings.RefreshInterval.TotalSeconds, MaxRetryAfterSeconds)).ToString(
            CultureInfo.InvariantCulture
        );
    }

    /// <summary>Writes the 503 to <paramref name="context"/>'s response; does nothing once the response has started.</summary>
    public static async Task WriteAsync(HttpContext context, string retryAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpResponse response = context.Response;
        if (response.HasStarted)
        {
            return;
        }

        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.Headers.RetryAfter = retryAfterSeconds;
        response.ContentType = ProblemJsonContentType;
        await response.WriteAsync(
            FailureResponse
                .ForUnclassifiedStatus(
                    StatusCodes.Status503ServiceUnavailable,
                    "Service Unavailable",
                    context.TraceIdentifier
                )
                .ToJsonString(),
            context.RequestAborted
        );
    }
}
