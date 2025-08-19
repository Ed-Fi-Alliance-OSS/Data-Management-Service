// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class OpenIdConfigurationModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/openid-configuration", GetOpenIdConfiguration);
    }

    private async Task<IResult> GetOpenIdConfiguration(
        HttpContext httpContext,
        IOptions<IdentitySettings> identitySettings,
        IConfiguration configuration,
        IOpenIdConnectConfigurationProvider? configurationProvider = null
    )
    {
        // Build the base URL from the request
        var request = httpContext.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        // If PathBase is configured, include it
        var pathBase = configuration.GetValue<string>("AppSettings:PathBase");
        var uriBuilder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = request.Host.Host,
            Port = request.Host.Port ?? (request.Scheme == "https" ? 443 : 80),
            Path = string.IsNullOrEmpty(pathBase) ? "" : pathBase
        };
        baseUrl = uriBuilder.Uri.ToString().TrimEnd('/');

        // Use enhanced configuration provider if available, otherwise fallback to basic implementation
        if (configurationProvider != null)
        {
            var enhancedConfig = await configurationProvider.GetConfigurationAsync(baseUrl);

            // Convert to anonymous object for JSON serialization
            var openIdConfig = new
            {
                issuer = enhancedConfig.Issuer,
                authorization_endpoint = string.IsNullOrEmpty(enhancedConfig.AuthorizationEndpoint) ? null : enhancedConfig.AuthorizationEndpoint,
                token_endpoint = enhancedConfig.TokenEndpoint,
                userinfo_endpoint = string.IsNullOrEmpty(enhancedConfig.UserinfoEndpoint) ? null : enhancedConfig.UserinfoEndpoint,
                jwks_uri = enhancedConfig.JwksUri,
                registration_endpoint = enhancedConfig.RegistrationEndpoint,
                introspection_endpoint = enhancedConfig.IntrospectionEndpoint,
                revocation_endpoint = enhancedConfig.RevocationEndpoint,
                end_session_endpoint = string.IsNullOrEmpty(enhancedConfig.EndSessionEndpoint) ? null : enhancedConfig.EndSessionEndpoint,

                // Capabilities
                scopes_supported = enhancedConfig.ScopesSupported,
                response_types_supported = enhancedConfig.ResponseTypesSupported,
                response_modes_supported = enhancedConfig.ResponseModesSupported,
                grant_types_supported = enhancedConfig.GrantTypesSupported,
                token_endpoint_auth_methods_supported = enhancedConfig.TokenEndpointAuthMethodsSupported,
                token_endpoint_auth_signing_alg_values_supported = enhancedConfig.TokenEndpointAuthSigningAlgValuesSupported,
                id_token_signing_alg_values_supported = enhancedConfig.IdTokenSigningAlgValuesSupported,
                claims_supported = enhancedConfig.ClaimsSupported,
                subject_types_supported = enhancedConfig.SubjectTypesSupported,
                code_challenge_methods_supported = enhancedConfig.CodeChallengeMethodsSupported,

                // Logout support
                frontchannel_logout_supported = enhancedConfig.FrontchannelLogoutSupported,
                frontchannel_logout_session_supported = enhancedConfig.FrontchannelLogoutSessionSupported,
                backchannel_logout_supported = enhancedConfig.BackchannelLogoutSupported,
                backchannel_logout_session_supported = enhancedConfig.BackchannelLogoutSessionSupported,

                // Additional capabilities
                request_parameter_supported = enhancedConfig.RequestParameterSupported,
                request_uri_parameter_supported = enhancedConfig.RequestUriParameterSupported,
                require_request_uri_registration = enhancedConfig.RequireRequestUriRegistration,
                claims_parameter_supported = enhancedConfig.ClaimsParameterSupported,
                introspection_endpoint_auth_methods_supported = new[] { "client_secret_basic" },
                revocation_endpoint_auth_methods_supported = new[] { "client_secret_basic" }
            };

            return Results.Ok(openIdConfig);
        }

        // Fallback to basic implementation
        var basicConfig = new
        {
            issuer = identitySettings.Value.Authority,
            registration_endpoint = $"{baseUrl}/connect/register",
            token_endpoint = $"{baseUrl}/connect/token",
            revocation_endpoint = $"{baseUrl}/connect/revoke",
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

        return Results.Ok(basicConfig);
    }
}
