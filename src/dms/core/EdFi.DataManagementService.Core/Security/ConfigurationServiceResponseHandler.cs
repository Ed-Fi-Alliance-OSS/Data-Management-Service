// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Maps a failed protected Configuration Service read to an <see cref="HttpRequestException" /> that
/// carries the numeric status. The response body, the request URI and response headers stay out of
/// the log and the exception, because the Configuration Service may echo request content in them.
/// A fixed route label identifies the endpoint without including identifiers or query strings.
/// A request may opt in to receiving a 404 as a response instead.
/// </summary>
public class ConfigurationServiceResponseHandler(ILogger<ConfigurationServiceResponseHandler> logger)
    : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<bool> AllowNotFoundResponse = new("AllowNotFoundResponse");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var response = await base.SendAsync(request, cancellationToken);

        bool allowsNotFoundResponse =
            request.Options.TryGetValue(AllowNotFoundResponse, out bool allowNotFoundResponse)
            && allowNotFoundResponse;

        if (
            !response.IsSuccessStatusCode
            && !(response.StatusCode is HttpStatusCode.NotFound && allowsNotFoundResponse)
        )
        {
            HttpStatusCode statusCode = response.StatusCode;
            int numericStatus = (int)statusCode;
            response.Dispose();

            // A 401 starts one recovery in the shared send path and often succeeds there, so it is a
            // warning here; a 401 that survives recovery reaches the caller, which reports the failure.
            logger.Log(
                statusCode is HttpStatusCode.Unauthorized ? LogLevel.Warning : LogLevel.Error,
                "Configuration Service endpoint {Endpoint} request failed with status code {StatusCode}",
                EndpointForLogging(request),
                numericStatus
            );

            throw new HttpRequestException(
                HttpRequestError.Unknown,
                $"Configuration Service request failed with status code {numericStatus}.",
                inner: null,
                statusCode
            );
        }

        return response;
    }

    private static string EndpointForLogging(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [.., "v3", "tenants"] => "v3/tenants",
            [.., "v3", "dataStores"] => "v3/dataStores",
            [.., "v3", "apiClients", _] => "v3/apiClients/{id}",
            [.., "v3", "authorizationMetadata"] => "v3/authorizationMetadata",
            [.., "v3", "applications", _] => "v3/applications/{id}",
            [.., "v3", "profiles", _] => "v3/profiles/{id}",
            [.., "v3", "profiles"] => "v3/profiles",
            _ => "unrecognized",
        };
}
