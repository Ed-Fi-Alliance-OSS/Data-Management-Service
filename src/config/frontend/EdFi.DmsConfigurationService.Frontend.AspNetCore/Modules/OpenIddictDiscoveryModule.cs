// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// Provides OpenIddict server discovery endpoints when enabled
/// </summary>
public class OpenIddictDiscoveryModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/openiddict-status", GetOpenIddictStatus);
    }

    private static IResult GetOpenIddictStatus(IOptions<IdentitySettings> identitySettings)
    {
        var settings = identitySettings.Value;
        var enableOpenIddictServer = false;

        return Results.Json(new
        {
            OpenIddictServerEnabled = enableOpenIddictServer,
            Authority = settings.Authority,
            DiscoveryEndpoint = $"{settings.Authority}.well-known/openid-configuration",
            JwksEndpoint = $"{settings.Authority}.well-known/jwks",
            TokenEndpoint = $"{settings.Authority}connect/token"
        });
    }
}
