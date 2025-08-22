// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Configuration
{
    /// <summary>
    /// Enhanced OpenID Connect configuration that's more compliant with OpenID Connect specifications
    /// </summary>
    public interface IOpenIdConnectConfigurationProvider
    {
        Task<OpenIdConnectConfiguration> GetConfigurationAsync(string baseUrl, CancellationToken cancellationToken = default);
        Task<JwksDocument> GetJwksDocumentAsync(CancellationToken cancellationToken = default);
    }

    public class OpenIdConnectConfiguration
    {
        public string Issuer { get; set; } = string.Empty;
        public string AuthorizationEndpoint { get; set; } = string.Empty;
        public string TokenEndpoint { get; set; } = string.Empty;
        public string UserinfoEndpoint { get; set; } = string.Empty;
        public string JwksUri { get; set; } = string.Empty;
        public string RegistrationEndpoint { get; set; } = string.Empty;
        public string IntrospectionEndpoint { get; set; } = string.Empty;
        public string RevocationEndpoint { get; set; } = string.Empty;
        public string EndSessionEndpoint { get; set; } = string.Empty;

        // Supported features
        public string[] ScopesSupported { get; set; } = Array.Empty<string>();
        public string[] ResponseTypesSupported { get; set; } = Array.Empty<string>();
        public string[] ResponseModesSupported { get; set; } = Array.Empty<string>();
        public string[] GrantTypesSupported { get; set; } = Array.Empty<string>();
        public string[] TokenEndpointAuthMethodsSupported { get; set; } = Array.Empty<string>();
        public string[] TokenEndpointAuthSigningAlgValuesSupported { get; set; } = Array.Empty<string>();
        public string[] IdTokenSigningAlgValuesSupported { get; set; } = Array.Empty<string>();
        public string[] ClaimsSupported { get; set; } = Array.Empty<string>();
        public string[] SubjectTypesSupported { get; set; } = Array.Empty<string>();
        public string[] CodeChallengeMethodsSupported { get; set; } = Array.Empty<string>();

        // Logout support
        public bool FrontchannelLogoutSupported { get; set; }
        public bool FrontchannelLogoutSessionSupported { get; set; }
        public bool BackchannelLogoutSupported { get; set; }
        public bool BackchannelLogoutSessionSupported { get; set; }

        // Additional capabilities
        public bool RequestParameterSupported { get; set; }
        public bool RequestUriParameterSupported { get; set; }
        public bool RequireRequestUriRegistration { get; set; }
        public bool ClaimsParameterSupported { get; set; }
        public bool IntrospectionEndpointAuthMethodsSupported { get; set; }
        public bool RevocationEndpointAuthMethodsSupported { get; set; }
    }

    public class JwksDocument
    {
        public JsonWebKey[] Keys { get; set; } = Array.Empty<JsonWebKey>();
    }

    public class JsonWebKey
    {
        public string Kty { get; set; } = string.Empty;
        public string Use { get; set; } = string.Empty;
        public string Kid { get; set; } = string.Empty;
        public string Alg { get; set; } = string.Empty;
        public string N { get; set; } = string.Empty;
        public string E { get; set; } = string.Empty;
        public string[] X5c { get; set; } = Array.Empty<string>();
        public string X5t { get; set; } = string.Empty;
        public string X5tS256 { get; set; } = string.Empty;
    }

    public class OpenIdConnectConfigurationProvider : IOpenIdConnectConfigurationProvider
    {
        private readonly ITokenManager _tokenManager;
        private readonly IConfiguration _configuration;

        public OpenIdConnectConfigurationProvider(
            ITokenManager tokenManager,
            IConfiguration configuration)
        {
            _tokenManager = tokenManager;
            _configuration = configuration;
        }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string baseUrl, CancellationToken cancellationToken = default)
        {
            var authority = _configuration["IdentitySettings:Authority"] ?? baseUrl;
            var config = new OpenIdConnectConfiguration
            {
                Issuer = authority,
                TokenEndpoint = $"{baseUrl}/connect/token",
                RegistrationEndpoint = $"{baseUrl}/connect/register",
                JwksUri = $"{baseUrl}/.well-known/jwks.json",
                IntrospectionEndpoint = $"{baseUrl}/connect/introspect",
                RevocationEndpoint = $"{baseUrl}/connect/revoke",

                // Supported grant types (OAuth 2.0 / OpenID Connect)
                GrantTypesSupported = new[]
                {
                    OpenIddictConstants.GrantTypes.ClientCredentials,
                    OpenIddictConstants.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.GrantTypes.RefreshToken
                },

                // Supported response types
                ResponseTypesSupported = new[]
                {
                    OpenIddictConstants.ResponseTypes.Code,
                    OpenIddictConstants.ResponseTypes.Token
                },

                // Supported response modes
                ResponseModesSupported = new[]
                {
                    OpenIddictConstants.ResponseModes.Query,
                    OpenIddictConstants.ResponseModes.Fragment,
                    OpenIddictConstants.ResponseModes.FormPost
                },

                // Supported scopes
                ScopesSupported = new[]
                {
                    OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Scopes.Profile,
                    OpenIddictConstants.Scopes.Email,
                    "api",
                    "edfi_admin_api/full_access"
                },

                // Token endpoint authentication methods
                TokenEndpointAuthMethodsSupported = new[]
                {
                    OpenIddictConstants.ClientAuthenticationMethods.ClientSecretPost,
                    OpenIddictConstants.ClientAuthenticationMethods.ClientSecretBasic
                },

                // Signing algorithms
                IdTokenSigningAlgValuesSupported = new[]
                {
                    OpenIddictConstants.Algorithms.RsaSha256
                },

                TokenEndpointAuthSigningAlgValuesSupported = new[]
                {
                    OpenIddictConstants.Algorithms.RsaSha256
                },

                // Supported claims
                ClaimsSupported = new[]
                {
                    OpenIddictConstants.Claims.Subject,
                    OpenIddictConstants.Claims.Name,
                    OpenIddictConstants.Claims.Role,
                    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                    "namespacePrefixes",
                    "educationOrganizationIds"
                },

                // Subject types
                SubjectTypesSupported = new[]
                {
                    OpenIddictConstants.SubjectTypes.Public
                },

                // PKCE support
                CodeChallengeMethodsSupported = new[]
                {
                    OpenIddictConstants.CodeChallengeMethods.Sha256
                },

                // Logout support (disabled for now as this is primarily an API)
                FrontchannelLogoutSupported = false,
                FrontchannelLogoutSessionSupported = false,
                BackchannelLogoutSupported = false,
                BackchannelLogoutSessionSupported = false,

                // Additional parameter support
                RequestParameterSupported = false,
                RequestUriParameterSupported = false,
                RequireRequestUriRegistration = false,
                ClaimsParameterSupported = false,
                IntrospectionEndpointAuthMethodsSupported = true,
                RevocationEndpointAuthMethodsSupported = true
            };

            return Task.FromResult(config);
        }

        public async Task<JwksDocument> GetJwksDocumentAsync(CancellationToken cancellationToken = default)
        {
            var publicKeys = await _tokenManager.GetPublicKeysAsync();

            var keys = publicKeys.Select(keyInfo =>
            {
                var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportParameters(keyInfo.RsaParameters);

                return new JsonWebKey
                {
                    Kty = "RSA",
                    Use = "sig",
                    Kid = keyInfo.KeyId,
                    Alg = "RS256",
                    N = Convert.ToBase64String(keyInfo.RsaParameters.Modulus!),
                    E = Convert.ToBase64String(keyInfo.RsaParameters.Exponent!)
                };
            }).ToArray();

            return new JwksDocument { Keys = keys };
        }
    }
}
