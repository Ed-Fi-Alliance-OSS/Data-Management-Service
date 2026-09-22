// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Validation;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
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
        // Support dynamic route contexts: allows zero or more path segments after the endpoint
        // Examples: /connect/token, /connect/token/{districtId}, /connect/token/{districtId}/{schoolYear}
        endpoints.MapPost("connect/register/{**contextPath}", RegisterClient).DisableAntiforgery();
        endpoints.MapPost("connect/token/{**contextPath}", GetClientAccessToken).DisableAntiforgery();
        endpoints.MapPost("connect/introspect/{**contextPath}", IntrospectToken).DisableAntiforgery();
        // No RequireAuthorization() here: RFC 7009 §2.1 requires the caller to authenticate with
        // client credentials (RFC 6749 §2.3), the same as at /connect/token, not with a bearer
        // access token. RevokeToken authenticates the caller itself via
        // ITokenRevocationManager.ValidateClientCredentialsAsync before deciding whether it owns
        // the token being revoked, so the ASP.NET Core auth pipeline is not involved at all.
        endpoints.MapPost("connect/revoke/{**contextPath}", RevokeToken).DisableAntiforgery();
    }

    private async Task<IResult> RegisterClient(
        RegisterRequest.Validator validator,
        IIdentityProviderRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        HttpContext httpContext
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        // (Minimal API [FromForm] binding returns 400 with empty body before handler is invoked)
        RegisterRequest model = new();
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync();
            model = new RegisterRequest
            {
                ClientId = form["ClientId"].ToString(),
                ClientSecret = form["ClientSecret"].ToString(),
                DisplayName = form["DisplayName"].ToString(),
            };
        }

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

        return FailureResults.Authorization(httpContext.TraceIdentifier, ["Registration is disabled."]);
    }

    /// <summary>
    /// Parses HTTP Basic auth credentials (client_id:client_secret) from the Authorization
    /// header, shared by /connect/token and /connect/revoke since both authenticate the caller
    /// with client credentials rather than a bearer token.
    /// </summary>
    private static void TryParseBasicAuthCredentials(
        HttpContext httpContext,
        ILogger logger,
        out string clientId,
        out string clientSecret
    )
    {
        clientId = string.Empty;
        clientSecret = string.Empty;

        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);
        if (
            string.IsNullOrEmpty(authHeader.ToString())
            || !authHeader.ToString().StartsWith("basic ", StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        try
        {
            var base64Credentials = authHeader.ToString().Substring(6); // Remove "basic "
            var credentialBytes = Convert.FromBase64String(base64Credentials);
            var credentials = System.Text.Encoding.UTF8.GetString(credentialBytes);
            var parts = credentials.Split(':', 2);
            if (parts.Length == 2)
            {
                clientId = Uri.UnescapeDataString(parts[0]);
                clientSecret = Uri.UnescapeDataString(parts[1]);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Basic Auth credentials");
        }
    }

    private static async Task<IResult> GetClientAccessToken(
        TokenRequest.Validator validator,
        [FromServices] ITokenManager tokenManager,
        [FromServices] IConfiguration configuration,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        var identityProvider =
            configuration.GetValue<string>("AppSettings:IdentityProvider")?.ToLowerInvariant()
            ?? "self-contained";

        // Manually read form data to handle empty form bodies in .NET 10
        // (Minimal API [FromForm] binding returns 400 with empty body before handler is invoked)
        string clientId = string.Empty;
        string clientSecret = string.Empty;
        string grantType = string.Empty;
        string scope = string.Empty;

        // For self-contained mode, support HTTP Basic authentication
        if (string.Equals(identityProvider, "self-contained", StringComparison.OrdinalIgnoreCase))
        {
            TryParseBasicAuthCredentials(httpContext, logger, out clientId, out clientSecret);
        }

        // Read form data for all parameters (and as fallback for credentials in self-contained mode)
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

        var model = new TokenRequest
        {
            client_id = clientId,
            client_secret = clientSecret,
            grant_type = grantType,
            scope = scope,
        };

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
            return FailureResults.BadRequest(
                "The specified grant type is not supported.",
                httpContext.TraceIdentifier
            );
        }

        var tokenResult = await tokenManager.GetAccessTokenAsync([
            new KeyValuePair<string, string>("client_id", model.client_id),
            new KeyValuePair<string, string>("client_secret", model.client_secret),
            new KeyValuePair<string, string>("grant_type", model.grant_type),
            new KeyValuePair<string, string>("scope", model.scope),
        ]);

        return tokenResult switch
        {
            TokenResult.Success tokenSuccess => Results.Ok(
                JsonSerializer.Deserialize<TokenResponse>(tokenSuccess.Token)
            ),
            TokenResult.FailureAuthentication authenticationFailure => FailureResults.Authentication(
                authenticationFailure.Error,
                authenticationFailure.ErrorDescription,
                httpContext.TraceIdentifier
            ),
            TokenResult.FailureTokenLimitExceeded tokenLimitExceeded => FailureResults.TooManyTokens(
                tokenLimitExceeded.Limit,
                httpContext.TraceIdentifier
            ),
            TokenResult.FailureLockTimeout => FailureResults.ConcurrentModification(
                httpContext.TraceIdentifier
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
        [FromServices] IEnhancedTokenValidator? tokenValidator,
        HttpContext httpContext
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        IntrospectionRequest model = new();
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync();
            model = new IntrospectionRequest
            {
                Token = form["token"].ToString(),
                Token_Type_Hint = form["token_type_hint"].ToString(),
            };
        }

        if (string.IsNullOrEmpty(model.Token))
        {
            return FailureResults.BadRequest("The token parameter is missing.", httpContext.TraceIdentifier);
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
            client_id = validationResult.Principal.GetClientId(),
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
        [FromServices] ITokenManager tokenManager,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        // RFC 7009 §2.1 requires the caller to authenticate with client credentials (RFC 6749
        // §2.3), the same as at /connect/token — not with a bearer access token, since the token
        // being revoked is the subject of the request, not proof of who is calling.
        TryParseBasicAuthCredentials(httpContext, logger, out var clientId, out var clientSecret);

        // Manually read form data to handle empty form bodies in .NET 10
        RevocationRequest model = new();
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync();
            model = new RevocationRequest
            {
                Token = form["token"].ToString(),
                Token_Type_Hint = form["token_type_hint"].ToString(),
            };

            // RFC 6749 §2.3 also permits credentials in the request body for clients that
            // cannot use HTTP Basic auth, mirroring the fallback GetClientAccessToken uses.
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = form["client_id"].ToString();
            }
            if (string.IsNullOrEmpty(clientSecret))
            {
                clientSecret = form["client_secret"].ToString();
            }
        }

        if (string.IsNullOrEmpty(model.Token))
        {
            return FailureResults.BadRequest("The token parameter is missing.", httpContext.TraceIdentifier);
        }

        // Check if token manager supports revocation via interface. In Keycloak mode no
        // ITokenRevocationManager is registered, so this falls through to the bare 200 OK below
        // and nothing is revoked — revocation is the external IdP's responsibility there.
        if (tokenManager is not ITokenRevocationManager revocationManager)
        {
            return Results.Ok();
        }

        // Client-authentication failures are reported, not masked as 200 OK: RFC 7009 only
        // requires hiding whether a *token* is valid/owned, not whether the *caller* authenticated.
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return FailureResults.InvalidClient(
                "Client authentication is required.",
                httpContext.TraceIdentifier
            );
        }

        if (!await revocationManager.ValidateClientCredentialsAsync(clientId, clientSecret))
        {
            return FailureResults.InvalidClient(
                "Invalid client or Invalid client credentials",
                httpContext.TraceIdentifier
            );
        }

        try
        {
            // A token belonging to another client is left alone by the manager and still
            // reported as 200 OK, so nothing is leaked about whether it exists or who owns it.
            await revocationManager.RevokeTokenAsync(model.Token, clientId);
            return Results.Ok(); // RFC 7009: Always return 200 OK for revocation
        }
        catch (Exception ex)
        {
            // Even if revocation fails, return 200 OK (RFC 7009 requirement). The 200 hides
            // the failure from the caller by design, so log it here or it is lost entirely.
            logger.LogError(
                ex,
                "Revocation failed for client {ClientId}; returning 200 OK per RFC 7009",
                LoggingUtility.SanitizeForLog(clientId)
            );
            return Results.Ok();
        }
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
