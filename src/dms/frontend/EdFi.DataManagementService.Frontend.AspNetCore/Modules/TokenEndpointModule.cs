// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using System.Text;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(HttpContext httpContext, IOptions<AppSettings> appSettings)
    {
        // Create HttpClient to proxy request.
        var httpClientFactory = httpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var upstreamAddress = appSettings.Value.AuthenticationService;
        var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamAddress);

        // Verify the request header contains Authorization Basic.
        var authHeader = httpContext.Request.Headers.Authorization;
        if (!authHeader.ToString().Contains("Basic"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync($"Bad Request - Malformed Authorization Header '{authHeader}'");
        }
        else
        {
            // Decode Base64 clientId and clientSecret.
            var base64Credentials = authHeader.ToString().Substring("Basic ".Length).Trim();
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(base64Credentials)).Split(':');
            var clientId = credentials[0];
            var clientSecret = credentials[1];
            var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            // Add Basic Authentication headers and grant_type to upstream request.
            upstreamRequest.Headers.Add("Authorization", $"Basic {encodedCredentials}");
            upstreamRequest!.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");

            // In case of 5xx Error, pass 503 Service unavailable to client, otherwise forward status directly to client.
            var response = new HttpResponseMessage();
            try
            {
                response = await client.SendAsync(upstreamRequest);
                httpContext.Response.StatusCode = (int)response.StatusCode;
            }
            catch (Exception)
            {
                httpContext.Response.StatusCode = (int)System.Net.HttpStatusCode.ServiceUnavailable;
            }
            finally
            {
                await response.Content.CopyToAsync(httpContext.Response.Body);
            }
        }
    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);
