// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.OAuth;

/// <summary>
/// Retrieves an access token via upstream OAuth Service.
/// </summary>
/// <param name="logger"></param>
public class OAuthManager(ILogger<OAuthManager> logger) : IOAuthManager
{
    private readonly ILogger<OAuthManager> _logger = logger;

    public async Task<HttpResponseMessage> GetAccessTokenAsync(
        IHttpClientWrapper httpClient,
        string authHeaderString,
        string upstreamUri,
        TraceId traceId
    )
    {
        _logger.LogInformation("GetAccessTokenAsync - {TraceId}", traceId.Value);

        if (!authHeaderString.Contains("basic", StringComparison.InvariantCultureIgnoreCase))
        {
            return GenerateProblemDetailResponse(
                HttpStatusCode.BadRequest,
                FailureResponse.ForBadRequest("Malformed Authorization header", traceId, [], [])
            );
        }

        HttpRequestMessage upstreamRequest = new(HttpMethod.Post, upstreamUri);
        upstreamRequest.Headers.Add("Authorization", authHeaderString);

        // TODO(DMS-408): Replace hard-coded with forwarded request body.
        upstreamRequest.Content = new StringContent(
            "grant_type=client_credentials",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );

        // In case of 5xx Error, pass 503 Service unavailable to client, otherwise forward response directly to client.
        try
        {
            _logger.LogInformation("Forwarding token request to upstream service - {TraceId}", traceId.Value);
            var response = await httpClient.SendAsync(upstreamRequest);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return response;
                case HttpStatusCode.Unauthorized:
                    return await GenerateUnauthorizedResponse(traceId, response);
                default:
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Error from upstream identity service - {TraceId} - {Content}",
                        traceId,
                        content
                    );
                    return GenerateProblemDetailResponse(
                        HttpStatusCode.BadGateway,
                        FailureResponse.ForGatewayError(traceId, content)
                    );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error from upstream identity service - {TraceId}", traceId.Value);
            return GenerateProblemDetailResponse(
                HttpStatusCode.BadGateway,
                FailureResponse.ForGatewayError(traceId)
            );
        }

        // Attempts to read `{ "error": "...", "error_description": "..."}` from the response
        // body, with sensible fallback mechanism if the response is in a different format.
        static async Task<HttpResponseMessage> GenerateUnauthorizedResponse(
            TraceId traceId,
            HttpResponseMessage response
        )
        {
            var body = await response.Content.ReadAsStringAsync();
            var error = "Unauthorized";
            var errorDescription = body;

            JsonNode? parsed = JsonNode.Parse(body);
            if (parsed is not null)
            {
                var obj = parsed.AsObject();
                if (obj.ContainsKey("error"))
                {
                    error = obj["error"]!.ToString();
                }
                if (obj.ContainsKey("error_description"))
                {
                    errorDescription = obj["error_description"]!.ToString();
                }
            }

            return GenerateProblemDetailResponse(
                HttpStatusCode.Unauthorized,
                FailureResponse.ForUnauthorized(traceId, error, errorDescription)
            );
        }

        static HttpResponseMessage GenerateProblemDetailResponse(
            HttpStatusCode statusCode,
            JsonNode failureResponse
        )
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    failureResponse.ToString(),
                    Encoding.UTF8,
                    "application/problem+json"
                ),
            };
        }
    }
}
