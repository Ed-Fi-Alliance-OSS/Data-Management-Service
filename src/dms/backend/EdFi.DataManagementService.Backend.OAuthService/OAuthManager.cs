// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace EdFi.DataManagementService.Backend.OAuthService;

public class OAuthManager : IOAuthManager
{
    public async Task GetAccessTokenAsync(HttpContext httpContext, string upstreamUri)
    {
        // Create HttpClient to proxy request.
        var httpClientFactory = httpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        HttpRequestMessage upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri);

        // Verify the request header contains Authorization Basic.
        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);
        if (!authHeader.ToString().Contains("Basic"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync($"Bad Request - Malformed Authorization Header '{authHeader}'");
        }
        else
        {

            // Forward Basic Authentication headers to upstream request.
            upstreamRequest.Headers.Add("Authorization", authHeader.ToString());

            // TODO(DMS-408): Replace hard-coded with forwarded request body.
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
