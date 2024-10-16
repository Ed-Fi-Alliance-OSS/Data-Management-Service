// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(HttpContext httpContext)
    {
        // Create Http client to proxy request
        var httpClientFactory = httpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var forwardingAddress = "http://localhost:3000/";
        var request = new HttpRequestMessage(HttpMethod.Post, forwardingAddress);


        request!.Content = new StreamContent(httpContext.Request.Body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(httpContext.Request.ContentType ?? "");

        var response = await client.SendAsync(request);

        httpContext.Response.StatusCode = (int)response.StatusCode;

        await response.Content.CopyToAsync(httpContext.Response.Body);
    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);
