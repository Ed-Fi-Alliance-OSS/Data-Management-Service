// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.OAuth;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

internal static class TokenErrorResponseWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        HttpResponseMessage response,
        CancellationToken cancellationToken = default
    )
    {
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType =
            response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";

        if (
            !response.IsSuccessStatusCode
            && response.Headers.TryGetValues(OAuthErrorResponse.CorrelationHeaderName, out var correlationIds)
        )
        {
            context.Response.Headers[OAuthErrorResponse.CorrelationHeaderName] = correlationIds.ToArray();
        }

        if (!response.IsSuccessStatusCode && response.Headers.WwwAuthenticate.Count > 0)
        {
            context.Response.Headers.WWWAuthenticate = response.Headers.WwwAuthenticate.ToString();
        }

        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
