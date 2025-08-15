// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class OpenIdConfigurationModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/openid-configuration", GetOpenIdConfiguration);
    }

    private IResult GetOpenIdConfiguration(
        HttpContext httpContext,
        IOptions<IdentitySettings> identitySettings,
        IConfiguration configuration
    )
    {
        // Build the base URL from the request
        var request = httpContext.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        // If PathBase is configured, include it
        var pathBase = configuration.GetValue<string>("AppSettings:PathBase");
        if (!string.IsNullOrEmpty(pathBase))
        {
            baseUrl = $"{baseUrl}/{pathBase.Trim('/')}";
        }

        // Create OpenID Configuration response
        var openIdConfig = new
        {
            issuer = identitySettings.Value.Authority,
            register_endpoint = $"{baseUrl}/connect/register",
            token_endpoint = $"{baseUrl}/connect/token",
            jwks_uri = $"{baseUrl}/.well-known/jwks.json",
            frontchannel_logout_supported = false,
            frontchannel_logout_session_supported = false,
            backchannel_logout_supported = false,
            backchannel_logout_session_supported = false,
            scopes_supported = new[] { "openid", "profile", "email", "api", "edfi_admin_api/full_access" },
            claims_supported = new[] { "sub", "name", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role" },
            grant_types_supported = new[] { "client_credentials" },
            response_types_supported = new[] { "token" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        };

        return Results.Ok(openIdConfig);
    }
}
