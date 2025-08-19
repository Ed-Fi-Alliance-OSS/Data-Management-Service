// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions
{
    /// <summary>
    /// Enhanced JWT Authentication configuration using OpenIddict validation components
    /// </summary>
    public static class JwtAuthenticationExtensions
    {
        public const string JwtSchemeName = "DmsJwtBearer";
        public const string OpenIddictValidationSchemeName = "DmsOpenIddictValidation";

        public static IServiceCollection AddJwtAuthentication(
            this IServiceCollection services,
            JwtSettings jwtSettings,
            Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            // Add enhanced token validator
            services.AddScoped<IEnhancedTokenValidator, EnhancedTokenValidator>();

            // Add OpenIddict validation support
            services.AddOpenIddict()
                .AddValidation(options =>
                {
                    // Configure the OpenIddict validation component to use the local
                    // token validation endpoint (for future extensibility)
                    options.SetIssuer(jwtSettings.Issuer);

                    // Register the System.Net.Http integration
                    options.UseSystemNetHttp();

                    // Register the ASP.NET Core host
                    options.UseAspNetCore();
                });

            // Add authentication with both schemes
            services.AddAuthentication()
                .AddJwtBearer(JwtSchemeName, options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        // IssuerSigningKeys will be set via options pattern below
                        ValidateIssuer = true,
                        ValidIssuer = jwtSettings.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwtSettings.Audience,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(5),
                        RequireExpirationTime = true,
                        RequireSignedTokens = true,
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = async context =>
                        {
                            // Use enhanced token validator for additional validation
                            var enhancedValidator = context.HttpContext.RequestServices.GetService<IEnhancedTokenValidator>();

                            if (enhancedValidator != null)
                            {
                                var token = context.Request.Headers["Authorization"]
                                    .ToString().Replace("Bearer ", "");

                                var validationResult = await enhancedValidator.ValidateTokenAsync(token);
                                if (!validationResult.IsValid)
                                {
                                    context.Fail($"Enhanced validation failed: {validationResult.ErrorDescription}");
                                }
                            }

                            // Additional validation using existing token manager (backward compatibility)
                            var tokenManager = context.HttpContext.RequestServices.GetService<ITokenManager>();
                            if (tokenManager != null)
                            {
                                var token = context.Request.Headers["Authorization"]
                                    .ToString().Replace("Bearer ", "");

                                // Check if the token manager supports validation
                                var validationMethod = tokenManager.GetType().GetMethod("ValidateTokenAsync");
                                if (validationMethod != null)
                                {
                                    var result = await (Task<bool>)validationMethod.Invoke(tokenManager, new object[] { token })!;
                                    if (!result)
                                    {
                                        context.Fail("Token has been revoked or is invalid");
                                    }
                                }
                            }
                        },
                        OnChallenge = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerHandler>>();
                            logger.LogWarning("JWT authentication challenge: {Error} - {Description}",
                                context.Error, context.ErrorDescription);
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<JwtBearerHandler>>();
                            logger.LogError(context.Exception, "JWT authentication failed");
                            return Task.CompletedTask;
                        }
                    };
                });

            // Configure JWT options post-configuration to resolve signing keys at runtime
            services.AddOptions<JwtBearerOptions>(JwtSchemeName)
                .Configure<ITokenManager>(
                    (options, tokenManager) =>
                    {
                        // Create a static cache for public keys that loads only once
                        var keysCache = new Lazy<IEnumerable<SecurityKey>>(FetchPublicKeys);

                        // Function to fetch keys synchronously but only once during initialization
                        IEnumerable<SecurityKey> FetchPublicKeys()
                        {
                            try
                            {
                                // This still blocks, but only happens once at startup
                                var keys = tokenManager.GetPublicKeysAsync()
                                    .ConfigureAwait(false)
                                    .GetAwaiter()
                                    .GetResult()
                                    .Select(rsaParams => new RsaSecurityKey(rsaParams.RsaParameters)
                                    {
                                        KeyId = rsaParams.KeyId
                                    });

                                return keys.Cast<SecurityKey>().ToList();
                            }
                            catch
                            {
                                return new List<SecurityKey>();
                            }
                        }

                        // Use the cached keys for all token validations
                        options.TokenValidationParameters.IssuerSigningKeyResolver =
                            (token, securityToken, kid, validationParameters) => keysCache.Value;
                    }
                );

            return services;
        }

        /// <summary>
        /// Extracts scope claims from JWT token
        /// </summary>
        public static string[] GetScopes(this ClaimsPrincipal principal)
        {
            var scopesClaim = principal.FindFirst("uri://myuri.org/scopes")?.Value;
            if (string.IsNullOrEmpty(scopesClaim))
            {
                return Array.Empty<string>();
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(scopesClaim) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Checks if principal has required scope
        /// </summary>
        public static bool HasScope(this ClaimsPrincipal principal, string requiredScope)
        {
            var scopes = principal.GetScopes();
            return Array.Exists(scopes, scope => scope.Equals(requiredScope, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets client ID from JWT token
        /// </summary>
        public static string? GetClientId(this ClaimsPrincipal principal)
        {
            return principal.FindFirst("client_id")?.Value;
        }

        /// <summary>
        /// Gets permissions from JWT token
        /// </summary>
        public static string[] GetPermissions(this ClaimsPrincipal principal)
        {
            return principal.FindAll("permission").Select(c => c.Value).ToArray();
        }

        /// <summary>
        /// Creates an AuthorizeAttribute configured for the DMS JWT scheme
        /// </summary>
        public static Microsoft.AspNetCore.Authorization.AuthorizeAttribute CreateJwtAuthorizeAttribute()
        {
            return new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = JwtSchemeName
            };
        }
    }
}
