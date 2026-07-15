// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Builds OAuth 2.0 protocol error responses (RFC 6749 section 5.2) as application/json
/// { error, error_description } bodies for the token endpoint, so that standards-compliant OAuth clients
/// can interpret them. These are intentionally NOT the Ed-Fi Problem Details contract. Descriptions are
/// fixed and generic: provider, database, and exception detail is logged server-side only and never
/// surfaced here.
/// </summary>
internal static class OAuthErrorResults
{
    // Advertised in the WWW-Authenticate challenge when client authentication fails.
    private const string BasicChallenge = "Basic realm=\"Ed-Fi DMS Configuration Service\"";

    /// <summary>A required parameter is missing or the request is otherwise malformed (HTTP 400).</summary>
    public static IResult InvalidRequest(string description) =>
        new OAuthErrorResult("invalid_request", description, StatusCodes.Status400BadRequest);

    /// <summary>The requested grant type is not supported (HTTP 400).</summary>
    public static IResult UnsupportedGrantType(string description) =>
        new OAuthErrorResult("unsupported_grant_type", description, StatusCodes.Status400BadRequest);

    /// <summary>
    /// Client authentication failed (HTTP 401). RFC 6749 section 5.2 requires a 401 with a
    /// WWW-Authenticate challenge advertising the supported client-authentication scheme.
    /// </summary>
    public static IResult InvalidClient(string description) =>
        new OAuthErrorResult(
            "invalid_client",
            description,
            StatusCodes.Status401Unauthorized,
            BasicChallenge
        );

    /// <summary>
    /// The authorization server cannot currently handle the request, e.g. the identity provider is
    /// unreachable or returned an unexpected failure (HTTP 503).
    /// </summary>
    public static IResult TemporarilyUnavailable(string description) =>
        new OAuthErrorResult("temporarily_unavailable", description, StatusCodes.Status503ServiceUnavailable);

    private sealed class OAuthErrorResult(
        string error,
        string errorDescription,
        int statusCode,
        string? wwwAuthenticate = null
    ) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (wwwAuthenticate is not null)
            {
                httpContext.Response.Headers.WWWAuthenticate = wwwAuthenticate;
            }

            return Results
                .Json(new { error, error_description = errorDescription }, statusCode: statusCode)
                .ExecuteAsync(httpContext);
        }
    }
}
