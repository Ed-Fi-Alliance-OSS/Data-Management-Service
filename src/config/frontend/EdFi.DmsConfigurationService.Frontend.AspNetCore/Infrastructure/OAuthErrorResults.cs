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

    /// <summary>
    /// A required parameter is missing or the request is otherwise malformed (HTTP 400 by default). A
    /// framework form-read rejection carries its own request status — for example 413 Payload Too Large —
    /// which is passed through <paramref name="statusCode"/> so it is preserved rather than collapsed to
    /// 400, matching how the Ed-Fi framework-error paths preserve the status.
    /// </summary>
    public static IResult InvalidRequest(
        string description,
        int statusCode = StatusCodes.Status400BadRequest
    ) => new OAuthErrorResult("invalid_request", description, statusCode);

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
    /// A client error (HTTP 400) returned by the upstream identity provider's token endpoint, such as
    /// invalid_scope or invalid_grant. The provider's OAuth error code is echoed only after validation
    /// against the RFC 6749 section 5.2 token-endpoint error set, so an unexpected or malformed code
    /// cannot be reflected back; unrecognized codes collapse to invalid_request. This keeps a client
    /// mistake a 400 rather than a retryable 503.
    /// </summary>
    public static IResult ClientError(string oauthErrorCode) =>
        new OAuthErrorResult(
            NormalizeTokenEndpointClientError(oauthErrorCode),
            "The authorization server rejected the request.",
            StatusCodes.Status400BadRequest
        );

    /// <summary>
    /// The authorization server cannot currently handle the request, e.g. the identity provider is
    /// unreachable or returned an unexpected failure (HTTP 503).
    /// </summary>
    public static IResult TemporarilyUnavailable(string description) =>
        new OAuthErrorResult("temporarily_unavailable", description, StatusCodes.Status503ServiceUnavailable);

    // Restrict an echoed provider error code to the RFC 6749 section 5.2 token-endpoint set (excluding
    // invalid_client, which is a 401 challenge handled separately). Anything else becomes invalid_request
    // so an unexpected code is never reflected back to the caller.
    private static string NormalizeTokenEndpointClientError(string oauthErrorCode) =>
        oauthErrorCode switch
        {
            "invalid_request"
            or "invalid_grant"
            or "unauthorized_client"
            or "unsupported_grant_type"
            or "invalid_scope" => oauthErrorCode,
            _ => "invalid_request",
        };

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
