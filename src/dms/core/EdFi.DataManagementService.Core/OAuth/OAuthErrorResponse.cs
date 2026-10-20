// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.OAuth;

/// <summary>
/// Creates the token endpoint's OAuth error response without depending on ASP.NET Core.
/// </summary>
public static class OAuthErrorResponse
{
    public const string CorrelationHeaderName = "X-Correlation-ID";

    public const string BasicChallenge = "Basic realm=\"EdFi.DataManagementService\"";

    public const string MissingCredentialsDescription =
        "Client credentials must be supplied in an Authorization: Basic header or as client_id and "
        + "client_secret in the request body";

    public const string NonBasicSchemeDescription = "The Authorization header must use the Basic scheme";

    public const string EmptyAuthorizationDescription = "The Authorization header must not be empty";

    public const string MalformedAuthorizationDescription = "Malformed Authorization header";

    public const string MixedMechanismsDescription = "Only one client authentication mechanism may be used";

    public const string MissingGrantDescription = "The grant_type request parameter must be supplied";

    public const string ProviderRejectedDescription =
        "The upstream identity service rejected the token request.";

    public const string UnauthorizedFallbackDescription =
        "The upstream identity service rejected the request credentials.";

    public const string RateLimitDescription =
        "The number of allowed requests has been exceeded. Retry the request later.";

    public const string TemporarilyUnavailableDescription =
        "The service is temporarily unable to handle the request. Retry the request later.";

    public const string GatewayDescription =
        "The upstream identity service did not return a usable response. Contact your system "
        + "administrator with the X-Correlation-ID response header, decoded once using UTF-8 "
        + "percent decoding, which identifies the corresponding server log entry.";

    /// <summary>
    /// Serializes exactly the two OAuth members owned by DMS and attaches its token error headers.
    /// </summary>
    public static HttpResponseMessage Create(
        HttpStatusCode statusCode,
        string error,
        string description,
        TraceId traceId
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        ArgumentException.ThrowIfNullOrEmpty(description);

        HttpResponseMessage response = new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error, error_description = description }),
                Encoding.UTF8,
                "application/json"
            ),
        };

        response.Headers.Add(CorrelationHeaderName, Uri.EscapeDataString(traceId.Value));

        if (statusCode is HttpStatusCode.Unauthorized)
        {
            response.Headers.WwwAuthenticate.ParseAdd(BasicChallenge);
        }

        return response;
    }
}
