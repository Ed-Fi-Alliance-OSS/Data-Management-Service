// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

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
            Microsoft.Extensions.Configuration.IConfiguration configuration
        )
        {
            // Add enhanced token validator
            services.AddScoped<IEnhancedTokenValidator, EnhancedTokenValidator>();

            // Add OpenIddict validation support
            services
                .AddOpenIddict()
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
            services
                .AddAuthentication()
                .AddJwtBearer(
                    JwtSchemeName,
                    options =>
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
                            ClockSkew = JwtTokenValidator.TokenValidationClockSkew,
                            RequireExpirationTime = true,
                            RequireSignedTokens = true,
                        };

                        options.Events = new JwtBearerEvents
                        {
                            OnChallenge = context =>
                            {
                                var logger = context.HttpContext.RequestServices.GetRequiredService<
                                    ILogger<JwtBearerHandler>
                                >();
                                logger.LogWarning(
                                    "JWT authentication challenge: {Error} - {Description}",
                                    context.Error,
                                    context.ErrorDescription
                                );
                                return Task.CompletedTask;
                            },
                            OnAuthenticationFailed = context =>
                            {
                                var logger = context.HttpContext.RequestServices.GetRequiredService<
                                    ILogger<JwtBearerHandler>
                                >();
                                logger.LogError(context.Exception, "JWT authentication failed");
                                return Task.CompletedTask;
                            },
                        };
                    }
                );

            // Validation keys come from the shared signing-key snapshot through its configuration manager, and the
            // shared request boundary classifies dependency failures as 503, as on the default Bearer scheme (spec
            // D-2, D-3, step 3.2). The shared token-validated handler is the one uncached token-status check, in place
            // of the enhanced-validator pre-check and the reflective ValidateTokenAsync call this scheme made before.
            // The challenge and authentication-failed logging above still runs for every non-dependency failure. The
            // manager and events come from AddSigningKeyServices, which every caller of this method registers.
            services
                .AddOptions<JwtBearerOptions>(JwtSchemeName)
                .Configure<SigningKeyConfigurationManager, SigningKeyBearerEvents>(
                    (options, configurationManager, bearerEvents) =>
                        options.UseSigningKeySnapshot(configurationManager, bearerEvents)
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
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(scopesClaim)
                    ?? Array.Empty<string>();
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
            return Array.Exists(
                scopes,
                scope => scope.Equals(requiredScope, StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// Gets client ID from JWT token
        /// </summary>
        public static string? GetClientId(this ClaimsPrincipal principal)
        {
            return principal.FindFirst(DataModel.SecurityConstants.ClientIdClaimType)?.Value;
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
                AuthenticationSchemes = JwtSchemeName,
            };
        }
    }
}
