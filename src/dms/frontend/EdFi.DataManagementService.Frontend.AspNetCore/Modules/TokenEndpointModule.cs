// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Backend.OAuthService;
using Microsoft.Extensions.Options;
using System.Text;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TokenEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/oauth/token", GenerateToken);
    }

    internal static async Task GenerateToken(HttpContext httpContext, IOptions<AppSettings> appSettings, IOAuthManager oAuthManager)
    {
        await oAuthManager.GetAccessTokenAsync(httpContext, appSettings.Value.AuthenticationService);
    }
}

public record TokenResponse(string access_token, int expires_in, string token_type);
