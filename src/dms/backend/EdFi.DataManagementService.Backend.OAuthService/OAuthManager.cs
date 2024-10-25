// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;

namespace EdFi.DataManagementService.Backend.OAuthService;

public class OAuthManager : IOAuthManager
{
    // public async Task<HttpResponseMessage> GetAccessTokenAsync(HttpContext httpContext, string upstreamUri)
    public async Task<HttpResponseMessage> GetAccessTokenAsync(HttpClient httpClient, string authHeaderString, string upstreamUri)
    {
        // Generate request message to send upstream
        HttpRequestMessage upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri);

        // Verify the request header contains Authorization Basic.
        if (!authHeaderString.Contains("Basic"))
        {
            throw new OAuthIdentityException($"Malformed Authorization Header", HttpStatusCode.BadRequest);
        }

        // Forward Basic Authentication headers to upstream request.
        upstreamRequest.Headers.Add("Authorization", authHeaderString);

        // TODO(DMS-408): Replace hard-coded with forwarded request body.
        upstreamRequest!.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");

        // In case of 5xx Error, pass 503 Service unavailable to client, otherwise forward response directly to client.
        try
        {
            return await httpClient.SendAsync(upstreamRequest);
        }
        catch (Exception)
        {
            throw new OAuthIdentityException($"Upstream Service Unavailable", HttpStatusCode.ServiceUnavailable);
        }
    }
}

public class OAuthIdentityException : Exception
{
    public OAuthIdentityException(string message) : base(message) { }

    public OAuthIdentityException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
    public HttpStatusCode? StatusCode { get; set; }
}
