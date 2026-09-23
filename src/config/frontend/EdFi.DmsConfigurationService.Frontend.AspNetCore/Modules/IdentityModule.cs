// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
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
        // ITokenRevocationManager.AuthenticateClientAsync before deciding whether it owns the
        // token being revoked, so the ASP.NET Core auth pipeline is not involved at all.
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

    private const string BasicAuthScheme = "basic ";

    /// <summary>
    /// Protection space named in the <c>WWW-Authenticate</c> challenge. A single value covers
    /// client-credential authentication service-wide, since one registered client uses the same
    /// secret wherever it authenticates.
    /// </summary>
    private const string ClientCredentialRealm = "EdFi.DmsConfigurationService";

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
            || !authHeader.ToString().StartsWith(BasicAuthScheme, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        try
        {
            var base64Credentials = authHeader.ToString()[BasicAuthScheme.Length..];
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
            // The Authorization header is attacker-controlled input, and Convert.FromBase64String
            // / UTF8-decoding exceptions can embed fragments of the invalid input in ex.Message.
            // Passing the raw exception to the logger risks leaking that content, since most
            // sinks render its unsanitized Message/ToString() independently of the sanitized
            // template arguments below, so log sanitized fields instead.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "Failed to parse Basic Auth credentials: ({ExceptionType}, {ErrorMessage}\n{StackTrace})",
                LoggingUtility.SanitizeForLog(ex.GetType().Name),
                LoggingUtility.SanitizeForLog(ex.Message),
                LoggingUtility.SanitizeForLog(ex.StackTrace)
            );
#pragma warning restore S6667
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

        // This request-shape check deliberately runs before client authentication and before the
        // provider-mode branch below: a structurally invalid request — missing the required
        // `token` parameter — always gets 400, regardless of who is asking or which identity
        // provider is configured, mirroring how GetClientAccessToken validates `grant_type` before
        // authenticating. One consequence: an unauthenticated caller who also omits `token` gets
        // 400, not 401 — the malformed request is reported before authentication is attempted.
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
            return InvalidClient(httpContext, "Client authentication is required.");
        }

        // Authentication hands back the client's stored canonical client_id rather than a bare
        // success flag, and that is what the ownership check must be given. Tokens are minted
        // from the canonical value, so passing the caller's own spelling would silently fail
        // the comparison wherever the engine authenticated a mis-cased id — SQL Server's default
        // collation does — leaving the caller with 200 OK and a still-live token.
        string? canonicalClientId = await revocationManager.AuthenticateClientAsync(clientId, clientSecret);
        if (canonicalClientId is null)
        {
            return InvalidClient(httpContext, "Invalid client or Invalid client credentials");
        }

        try
        {
            // A token belonging to another client is left alone by the manager and still
            // reported as 200 OK, so nothing is leaked about whether it exists or who owns it.
            await revocationManager.RevokeTokenAsync(model.Token, canonicalClientId);
            return Results.Ok(); // RFC 7009: Always return 200 OK for revocation
        }
        catch (Exception ex)
        {
            // Even if revocation fails, return 200 OK (RFC 7009 requirement). The 200 hides
            // the failure from the caller by design, so log it here or it is lost entirely.
            logger.LogError(
                ex,
                "Revocation failed for client {ClientId}; returning 200 OK per RFC 7009",
                LoggingUtility.SanitizeForLog(canonicalClientId)
            );
            return Results.Ok();
        }
    }

    /// <summary>
    /// The RFC 6749 §5.2 error response for a revocation caller that failed client
    /// authentication: a JSON object whose <c>error</c> and <c>error_description</c> are
    /// top-level members, plus the <c>WWW-Authenticate</c> challenge §5.2 requires when the
    /// client attempted to authenticate through the Authorization header.
    ///
    /// Deliberately not routed through <c>FailureResults</c>, which is the right contract for
    /// the Management API's own endpoints but the wrong one here: it emits
    /// <c>application/problem+json</c> and flattens the code into a sentence inside an
    /// <c>errors</c> array, where a conforming OAuth client — which reads <c>error</c> off the
    /// root object — cannot find it. <c>/connect/revoke</c> is an OAuth endpoint and answers in
    /// the OAuth error format.
    /// </summary>
    private static IResult InvalidClient(HttpContext httpContext, string errorDescription)
    {
        if (
            httpContext
                .Request.Headers.Authorization.ToString()
                .StartsWith(BasicAuthScheme, StringComparison.OrdinalIgnoreCase)
        )
        {
            httpContext.Response.Headers.WWWAuthenticate = $"Basic realm=\"{ClientCredentialRealm}\"";
        }

        return Results.Json(
            new OAuthErrorResponse("invalid_client", errorDescription),
            contentType: "application/json",
            statusCode: StatusCodes.Status401Unauthorized
        );
    }

    /// <summary>
    /// An RFC 6749 §5.2 error response body. The property names are the wire names the
    /// specification fixes, so they are spelled that way rather than renamed by a serializer
    /// policy that a future configuration change could alter.
    /// </summary>
    private sealed record OAuthErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription
    );

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
