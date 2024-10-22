// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Azure;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using System.Net.Http.Headers;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(HttpContext httpContext, IOptions<AppSettings> appSettings)
    {
        // Create Http client to proxy request
        var httpClientFactory = httpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var upstreamAddress = appSettings.Value.AuthenticationService;
        var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamAddress);

        // Verify the header contains Authorization Bearer.
        var authorizationString = httpContext.Request.Headers.Authorization.ToString();
        if (!authorizationString.Contains("Basic"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync("Bad Request: Authorization: Basic");
        }
        else
        {
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorizationString);
            upstreamRequest!.Content = new StreamContent(httpContext.Request.Body);
            var response = await client.SendAsync(upstreamRequest);
            httpContext.Response.StatusCode = (int)response.StatusCode;
            await response.Content.CopyToAsync(httpContext.Response.Body);
        }





    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);
