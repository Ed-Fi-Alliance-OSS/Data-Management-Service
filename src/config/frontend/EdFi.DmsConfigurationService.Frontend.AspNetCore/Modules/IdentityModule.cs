// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static EdFi.DmsConfigurationService.Backend.IdentityProviderError;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class IdentityModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/connect/register", RegisterClient).DisableAntiforgery();
        endpoints.MapPost("/connect/token", GetClientAccessToken).DisableAntiforgery();
        endpoints.MapPost("/connect/introspect", IntrospectToken).DisableAntiforgery();
        endpoints.MapPost("/connect/revoke", RevokeToken).DisableAntiforgery();
    }

    private async Task<IResult> RegisterClient(
        RegisterRequest.Validator validator,
        [FromForm] RegisterRequest model,
        IIdentityProviderRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        HttpContext httpContext
    )
    {
        bool allowRegistration = identitySettings.Value.AllowRegistration;
        if (allowRegistration)
        {
            await validator.GuardAsync(model);

            var clientResult = await clientRepository.GetAllClientsAsync();
            switch (clientResult)
            {
                case ClientClientsResult.FailureUnknown:
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ClientClientsResult.FailureIdentityProvider failureIdentityProvider:
                    return FailureResults.BadGateway(
                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                        httpContext.TraceIdentifier
                    );
                case ClientClientsResult.Success clientSuccess:
                    if (IsUnique(clientSuccess))
                    {
                        var result = await clientRepository.CreateClientAsync(
                            model.ClientId!,
                            model.ClientSecret!,
                            identitySettings.Value.ConfigServiceRole,
                            model.DisplayName!,
                            AuthorizationScopes.AdminScope.Name,
                            string.Empty,
                            string.Empty
                        );
                        return result switch
                        {
                            ClientCreateResult.Success => Results.Json(
                                new
                                {
                                    Title = $"Registered client {model.ClientId} successfully.",
                                    Status = 200,
                                }
                            ),
                            ClientCreateResult.FailureIdentityProvider failureIdentityProvider =>
                                FailureResults.BadGateway(
                                    failureIdentityProvider.IdentityProviderError.FailureMessage,
                                    httpContext.TraceIdentifier
                                ),
                            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
                        };
                    }
                    break;
            }
            bool IsUnique(ClientClientsResult.Success clientSuccess)
            {
                bool clientExists = clientSuccess.ClientList.Any(c =>
                    c.Equals(model.ClientId!, StringComparison.InvariantCultureIgnoreCase)
                );
                if (clientExists)
                {
                    var validationFailures = new List<ValidationFailure>
                    {
                        new()
                        {
                            PropertyName = "ClientId",
                            ErrorMessage =
                                "Client with the same Client Id already exists. Please provide different Client Id.",
                        },
                    };
                    throw new ValidationException(validationFailures);
                }
                return true;
            }
        }

        return Results.Forbid();
    }

    private static async Task<IResult> GetClientAccessToken(
        TokenRequest.Validator validator,
        [FromForm] TokenRequest model,
        [FromServices] ITokenManager tokenManager,
        [FromServices] IConfiguration configuration,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        var identityProvider =
            configuration.GetValue<string>("AppSettings:IdentityProvider")?.ToLowerInvariant()
            ?? "self-contained";

        // For self-contained mode, support both form and HTTP Basic authentication
        if (string.Equals(identityProvider, "self-contained", StringComparison.OrdinalIgnoreCase))
        {
            // Extract client credentials from either form body or Authorization header
            string clientId = model.client_id ?? string.Empty;
            string clientSecret = model.client_secret ?? string.Empty;
            string grantType = model.grant_type ?? string.Empty;
            string scope = model.scope ?? string.Empty;

            // Check for Authorization header (HTTP Basic auth) - only for self-contained
            httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);
            if (
                !string.IsNullOrEmpty(authHeader.ToString())
                && authHeader.ToString().StartsWith("basic ", StringComparison.OrdinalIgnoreCase)
            )
            {
                try
                {
                    var base64Credentials = authHeader.ToString().Substring(6); // Remove "basic "
                    var credentialBytes = Convert.FromBase64String(base64Credentials);
                    var credentials = System.Text.Encoding.UTF8.GetString(credentialBytes);
                    var parts = credentials.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        clientId = parts[0];
                        clientSecret = parts[1];
                    }
                }
                catch (Exception ex)
                {
                    // Log the exception for debugging purposes
                    logger.LogWarning("Failed to parse Basic Auth credentials: {Exception}", ex);
                }
            }

            // Read form data for other parameters (and as fallback for credentials)
            if (httpContext.Request.HasFormContentType)
            {
                var form = await httpContext.Request.ReadFormAsync();

                // Use form credentials if Basic auth didn't provide them
                if (string.IsNullOrEmpty(clientId))
                {
                    clientId = form["client_id"].ToString();
                }
                if (string.IsNullOrEmpty(clientSecret))
                {
                    clientSecret = form["client_secret"].ToString();
                }

                if (string.IsNullOrEmpty(grantType))
                {
                    grantType = form["grant_type"].ToString();
                }
                if (string.IsNullOrEmpty(scope))
                {
                    scope = form["scope"].ToString();
                }
            }

            // Create updated model for self-contained validation
            model = new TokenRequest
            {
                client_id = clientId,
                client_secret = clientSecret,
                grant_type = grantType,
                scope = scope,
            };
        }

        await validator.GuardAsync(model);

        // Validate grant type (OAuth 2.0 compliance)
        if (
            !string.Equals(
                model.grant_type,
                OpenIddictConstants.GrantTypes.ClientCredentials,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return Results.Json(
                new
                {
                    error = OpenIddictConstants.Errors.UnsupportedGrantType,
                    error_description = "The specified grant type is not supported.",
                },
                statusCode: 400
            );
        }

        var tokenResult = await tokenManager.GetAccessTokenAsync(
            [
                new KeyValuePair<string, string>("client_id", model.client_id),
                new KeyValuePair<string, string>("client_secret", model.client_secret),
                new KeyValuePair<string, string>("grant_type", model.grant_type),
                new KeyValuePair<string, string>("scope", model.scope),
            ]
        );

        return tokenResult switch
        {
            TokenResult.Success tokenSuccess => Results.Ok(
                JsonSerializer.Deserialize<TokenResponse>(tokenSuccess.Token)
            ),
            TokenResult.FailureIdentityProvider failureIdentityProvider =>
                failureIdentityProvider.IdentityProviderError switch
                {
                    InvalidClient unauthorized => FailureResults.InvalidClient(
                        unauthorized.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    Unauthorized unauthorized => FailureResults.Unauthorized(
                        unauthorized.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    Forbidden forbidden => FailureResults.Forbidden(
                        forbidden.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    _ => FailureResults.BadGateway(
                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                },
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> IntrospectToken(
        [FromForm] IntrospectionRequest model,
        [FromServices] IEnhancedTokenValidator? tokenValidator,
        HttpContext httpContext
    )
    {
        if (string.IsNullOrEmpty(model.Token))
        {
            return Results.Json(
                new
                {
                    error = OpenIddictConstants.Errors.InvalidRequest,
                    error_description = "The token parameter is missing.",
                },
                statusCode: 400
            );
        }

        if (tokenValidator == null)
        {
            return Results.Json(new { active = false });
        }

        var validationResult = await tokenValidator.ValidateTokenAsync(model.Token);

        if (!validationResult.IsValid || validationResult.Principal == null)
        {
            return Results.Json(new { active = false });
        }

        // Build introspection response according to RFC 7662
        var response = new
        {
            active = true,
            client_id = validationResult.Principal.FindFirst("client_id")?.Value,
            scope = string.Join(" ", validationResult.Principal.FindAll("scope").Select(c => c.Value)),
            exp = validationResult.Principal.FindFirst("exp")?.Value,
            iat = validationResult.Principal.FindFirst("iat")?.Value,
            sub = validationResult.Principal.FindFirst("sub")?.Value,
            aud = validationResult.Principal.FindFirst("aud")?.Value,
            iss = validationResult.Principal.FindFirst("iss")?.Value,
            token_type = "Bearer",
        };

        return Results.Json(response);
    }

    private static async Task<IResult> RevokeToken(
        [FromForm] RevocationRequest model,
        [FromServices] ITokenManager tokenManager,
        HttpContext httpContext
    )
    {
        if (string.IsNullOrEmpty(model.Token))
        {
            return Results.Json(
                new
                {
                    error = OpenIddictConstants.Errors.InvalidRequest,
                    error_description = "The token parameter is missing.",
                },
                statusCode: 400
            );
        }

        // Check if token manager supports revocation via interface
        if (tokenManager is ITokenRevocationManager revocationManager)
        {
            try
            {
                await revocationManager.RevokeTokenAsync(model.Token);
                return Results.Ok(); // RFC 7009: Always return 200 OK for revocation
            }
            catch
            {
                // Even if revocation fails, return 200 OK (RFC 7009 requirement)
                return Results.Ok();
            }
        }

        // If revocation is not supported, still return 200 OK
        return Results.Ok();
    }

    public class IntrospectionRequest
    {
        public string Token { get; set; } = string.Empty;
        public string? Token_Type_Hint { get; set; }
    }

    public class RevocationRequest
    {
        public string Token { get; set; } = string.Empty;
        public string? Token_Type_Hint { get; set; }
    }
}
