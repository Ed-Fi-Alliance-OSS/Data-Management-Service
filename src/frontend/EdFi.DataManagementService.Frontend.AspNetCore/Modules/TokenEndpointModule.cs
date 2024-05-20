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

    internal async Task GenerateToken(HttpContext httpContext, AuthenticationRequest authenticationRequest)
    {
        if (
            authenticationRequest == null
            || (
                string.IsNullOrEmpty(authenticationRequest.Key)
                || string.IsNullOrEmpty(authenticationRequest.Secret)
            )
        )
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync("Please provide valid key and secret.");
            return;
        }

        var tokenDetails = new TokenResponse("temporary-fake-token", 1800, "bearer");
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        await httpContext.Response.WriteAsSerializedJsonAsync(tokenDetails);
    }
}

public record AuthenticationRequest()
{
    public required string Key { get; set; }
    public required string Secret { get; set; }
}

public record TokenResponse(string access_token, int expires_in, string token_type);
