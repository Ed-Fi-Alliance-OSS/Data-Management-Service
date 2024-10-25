// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace EdFi.DataManagementService.Core;

public interface IOAuthManager
{
    public Task<HttpResponseMessage> GetAccessTokenAsync(HttpClient httpClient, string authHeaderString, string upstreamUri);
}
public class OAuthManager(ILogger<OAuthManager> logger) : IOAuthManager
{
    private readonly ILogger<OAuthManager> _logger = logger;
    public async Task<HttpResponseMessage> GetAccessTokenAsync(HttpClient httpClient, string authHeaderString, string upstreamUri)
    {
        // Generate request message to send upstream
        _logger.LogInformation("Generating Request message to send upstream");
        HttpRequestMessage upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri);

        // Verify the request header contains Authorization Basic.
        _logger.LogInformation("Verifying Authorization Header contains 'Basic' Authentication formatting");
        if (!authHeaderString.Contains("Basic"))
        {
            _logger.LogError("Malformed Authorization Header");
            throw new OAuthIdentityException($"Malformed Authorization Header", HttpStatusCode.BadRequest);
        }

        // Forward Basic Authentication headers to upstream request.
        upstreamRequest.Headers.Add("Authorization", authHeaderString);

        // TODO(DMS-408): Replace hard-coded with forwarded request body.
        upstreamRequest!.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");
        _logger.LogInformation("Forwarding Authorization Header and Body to upstream service");

        // In case of 5xx Error, pass 503 Service unavailable to client, otherwise forward response directly to client.
        try
        {
            return await httpClient.SendAsync(upstreamRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error from Upstream Service, notifying client...");
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
