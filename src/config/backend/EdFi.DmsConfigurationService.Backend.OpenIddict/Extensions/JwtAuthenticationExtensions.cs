// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions
{
    /// <summary>
    /// JWT Authentication configuration for validating tokens issued by ITokenManager implementations
    /// </summary>
    public static class JwtAuthenticationExtensions
    {
        public const string JwtSchemeName = "DmsJwtBearer";

        public static IServiceCollection AddJwtAuthentication(
            this IServiceCollection services,
            JwtSettings jwtSettings,
            Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            var signingKey = configuration["IdentitySettings:SigningKey"];
            if (string.IsNullOrEmpty(signingKey))
            {
                throw new InvalidOperationException(
                    "JWT signing key is not configured in IdentitySettings:SigningKey."
                );
            }
            var key = Encoding.UTF8.GetBytes(signingKey);

            // Add authentication without setting a default scheme to avoid conflicts
            services.AddAuthentication()

                .AddJwtBearer(JwtSchemeName, options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(key),
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
                            // Additional validation against database using any ITokenManager implementation
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
