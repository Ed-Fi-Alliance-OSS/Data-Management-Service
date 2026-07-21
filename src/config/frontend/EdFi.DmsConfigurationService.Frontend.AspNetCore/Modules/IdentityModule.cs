// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
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

/// <summary>
/// Endpoints for OAuth 2.0 / OpenID Connect: client registration plus the token, introspection, and
/// revocation endpoints.
/// </summary>
/// <remarks>
/// The token, introspection, and revocation endpoints are OAuth 2.0 / OpenID Connect protocol
/// endpoints. Their error responses use the standard { error, error_description } shape (RFC 6749
/// section 5.2, RFC 7662, and RFC 7009) rather than the Ed-Fi Problem Details contract, so that
/// standards-compliant OAuth clients can interpret them. The /connect/register endpoint is not an OAuth
/// protocol endpoint and returns the Ed-Fi contract.
/// </remarks>
public class IdentityModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Support dynamic route contexts: allows zero or more path segments after the endpoint
        // Examples: /connect/token, /connect/token/{districtId}, /connect/token/{districtId}/{schoolYear}
        endpoints.MapPost("connect/register/{**contextPath}", RegisterClient).DisableAntiforgery();
        endpoints.MapPost("connect/token/{**contextPath}", GetClientAccessToken).DisableAntiforgery();
        endpoints.MapPost("connect/introspect/{**contextPath}", IntrospectToken).DisableAntiforgery();
        endpoints.MapPost("connect/revoke/{**contextPath}", RevokeToken).DisableAntiforgery();
    }

    private async Task<IResult> RegisterClient(
        RegisterRequest.Validator validator,
        IIdentityProviderRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        HttpContext httpContext,
        ILogger<IdentityModule> logger
    )
    {
        // Registration disabled is answered before any request body is read, so a disabled endpoint
        // returns the exact Ed-Fi authorization response regardless of the posted body — a malformed or
        // form-limit-exceeding body must not defeat it by throwing out of the form read below.
        if (!identitySettings.Value.AllowRegistration)
        {
            return FailureResults.AuthorizationFailed(
                ["Registration is disabled."],
                httpContext.TraceIdentifier
            );
        }

        // Manually read form data to handle empty form bodies in .NET 10
        // (Minimal API [FromForm] binding returns 400 with empty body before handler is invoked).
        RegisterRequest model = new();
        if (httpContext.Request.HasFormContentType)
        {
            IFormCollection form;
            try
            {
                form = await httpContext.Request.ReadFormAsync();
            }
            catch (InvalidDataException exception)
            {
                // A form-limit breach or malformed form throws InvalidDataException, which the global
                // exception handler would answer with a 500. /connect/register is not an OAuth endpoint,
                // so it uses the Ed-Fi contract: an unreadable form is a client bad request. The framework
                // message and raw request values are never surfaced; the failure is logged server-side.
                logger.LogWarning(exception, "Failed to read the form body on the registration endpoint.");
                return FailureResults.BadRequest("The request was invalid.", httpContext.TraceIdentifier);
            }
            model = new RegisterRequest
            {
                ClientId = form["ClientId"].ToString(),
                ClientSecret = form["ClientSecret"].ToString(),
                DisplayName = form["DisplayName"].ToString(),
            };
        }

        await validator.GuardAsync(model);

        var clientResult = await clientRepository.GetAllClientsAsync();
        switch (clientResult)
        {
            case ClientClientsResult.FailureUnknown:
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            case ClientClientsResult.FailureIdentityProvider failureIdentityProvider:
                return UpstreamRegistrationError(
                    logger,
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
                            new { Title = $"Registered client {model.ClientId} successfully.", Status = 200 }
                        ),
                        ClientCreateResult.FailureIdentityProvider failureIdentityProvider =>
                            UpstreamRegistrationError(
                                logger,
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

        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    // Reports an identity-provider failure raised while registering a client. The raw provider message is
    // built from the underlying HTTP client exception and can carry provider URLs and status detail, so it
    // is recorded server-side only, sanitized for safe logging; the caller receives a fixed generic
    // response. This keeps provider, database, and exception details from ever being surfaced.
    private static IResult UpstreamRegistrationError(
        ILogger logger,
        string failureMessage,
        string correlationId
    )
    {
        logger.LogError(
            "Identity provider error during client registration: {FailureMessage}",
            LoggingUtility.SanitizeForLog(failureMessage)
        );
        return FailureResults.BadGateway("Identity provider error during client registration", correlationId);
    }

    // Reads the posted form for an OAuth protocol endpoint (token, introspection, revocation). A failure to
    // read the form — the request body exceeds a form or overall size limit, or the form is otherwise
    // malformed — must not escape to the global exception handler, which answers with the Ed-Fi Problem
    // Details contract (and with a 500 for a form-limit InvalidDataException). These endpoints answer with
    // the OAuth error contract (RFC 6749 section 5.2), so an unreadable form is reported as invalid_request.
    // The framework message and raw request values are never surfaced; the failure is logged server-side
    // only. Returns a null form and null error when the request carries no form content type, matching the
    // endpoints' empty-body handling.
    private static async Task<(IFormCollection? Form, IResult? Error)> TryReadOAuthFormAsync(
        HttpContext httpContext,
        ILogger logger
    )
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return (null, null);
        }

        try
        {
            return (await httpContext.Request.ReadFormAsync(), null);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
        {
            logger.LogWarning(exception, "Failed to read the form body on the OAuth endpoint.");
            // A BadHttpRequestException carries the framework request status (for example 413 when the body
            // exceeds the size limit); preserve it rather than collapsing every form-read failure to 400, so
            // an oversized body is not reported as a plain 400. This mirrors the framework-error handling on
            // the Ed-Fi paths, which preserve the status. An InvalidDataException (a form key/value/count
            // limit breach) has no status and is a malformed form, so it stays 400. The OAuth error shape
            // and generic description are kept in every case; the framework message and raw request values
            // are never surfaced.
            int statusCode = exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status400BadRequest;
            return (
                null,
                OAuthErrorResults.InvalidRequest(
                    "The request is missing a required parameter or is otherwise malformed.",
                    statusCode
                )
            );
        }
    }

    // The token-request parameters read from the posted form. RFC 6749 §3.1 forbids repeating any of
    // them; a repeated value is rejected before extraction (see GetClientAccessToken) because
    // StringValues.ToString() would otherwise comma-join the values.
    private static readonly string[] OAuthTokenFormParameters =
    [
        "grant_type",
        "scope",
        "client_id",
        "client_secret",
    ];

    private static async Task<IResult> GetClientAccessToken(
        TokenRequest.Validator validator,
        [FromServices] ITokenManager tokenManager,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        // (Minimal API [FromForm] binding returns 400 with empty body before handler is invoked). The
        // grant type and scope always come from the form; the client credentials come from either an HTTP
        // Basic authorization header or the form body.
        string grantType = string.Empty;
        string scope = string.Empty;
        string formClientId = string.Empty;
        string formClientSecret = string.Empty;

        var (form, formError) = await TryReadOAuthFormAsync(httpContext, logger);
        if (formError is not null)
        {
            return formError;
        }
        if (form is not null)
        {
            // RFC 6749 §3.1: a token-request parameter must not be included more than once. A repeated
            // parameter is rejected as invalid_request before any value is extracted or the token manager
            // is called, so a comma-joined StringValues never corrupts the request. A duplicated
            // credential is therefore a malformed request (invalid_request), not a client-authentication
            // failure (invalid_client).
            if (Array.Exists(OAuthTokenFormParameters, name => form[name].Count > 1))
            {
                return OAuthErrorResults.InvalidRequest(
                    "The request is missing a required parameter or is otherwise malformed."
                );
            }

            grantType = form["grant_type"].ToString();
            scope = form["scope"].ToString();
            formClientId = form["client_id"].ToString();
            formClientSecret = form["client_secret"].ToString();
        }

        // Client authentication (RFC 6749 section 2.3), supported in every identity-provider mode. Any
        // Authorization header is an attempt to authenticate the client via the request header. If it is
        // not a valid HTTP Basic credential — an unsupported scheme (e.g. Digest) or a malformed Basic
        // value — it is a failed client authentication (section 5.2): respond 401 invalid_client with the
        // Basic challenge and never fall back to the form credentials. With no Authorization header, the
        // client may authenticate with form credentials, which flow through the same token-manager call.
        httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader);
        string authorization = authHeader.ToString();

        string clientId;
        string clientSecret;
        if (!string.IsNullOrEmpty(authorization))
        {
            if (!TryParseBasicCredentials(authorization, out clientId, out clientSecret))
            {
                // Unsupported scheme or malformed Basic credentials. The header contents are never logged.
                logger.LogWarning(
                    "Rejected an unsupported or malformed Authorization header on the token endpoint."
                );
                return OAuthErrorResults.InvalidClient("Client authentication failed.");
            }

            // A client must not use more than one authentication mechanism in a single request
            // (RFC 6749 section 2.3): a Basic header combined with form credentials is invalid_request.
            if (!string.IsNullOrEmpty(formClientId) || !string.IsNullOrEmpty(formClientSecret))
            {
                return OAuthErrorResults.InvalidRequest(
                    "The request is missing a required parameter or is otherwise malformed."
                );
            }
        }
        else
        {
            clientId = formClientId;
            clientSecret = formClientSecret;
        }

        var model = new TokenRequest
        {
            client_id = clientId,
            client_secret = clientSecret,
            grant_type = grantType,
            scope = scope,
        };

        // RFC 6749 §5.2: absent or incomplete client authentication (a missing client_id or
        // client_secret) is a failed client authentication, reported as invalid_client (401 with the
        // Basic challenge), not invalid_request. This applies to the form path as well as the Basic
        // header, so it is checked before the ordinary-parameter validation below.
        if (string.IsNullOrEmpty(model.client_id) || string.IsNullOrEmpty(model.client_secret))
        {
            return OAuthErrorResults.InvalidClient("Client authentication failed.");
        }

        // Validate the remaining request parameters without throwing: a token-endpoint validation failure
        // is reported as the OAuth invalid_request error, not the Ed-Fi Problem Details contract the
        // global exception handler produces for a thrown ValidationException.
        var validationResult = await validator.ValidateAsync(model);
        if (!validationResult.IsValid)
        {
            return OAuthErrorResults.InvalidRequest(
                "The request is missing a required parameter or is otherwise malformed."
            );
        }

        // Only the client_credentials grant is supported.
        if (
            !string.Equals(
                model.grant_type,
                OpenIddictConstants.GrantTypes.ClientCredentials,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return OAuthErrorResults.UnsupportedGrantType("The specified grant type is not supported.");
        }

        var tokenResult = await tokenManager.GetAccessTokenAsync([
            new KeyValuePair<string, string>("client_id", model.client_id),
            new KeyValuePair<string, string>("client_secret", model.client_secret),
            new KeyValuePair<string, string>("grant_type", model.grant_type),
            new KeyValuePair<string, string>("scope", model.scope),
        ]);

        return tokenResult switch
        {
            TokenResult.Success tokenSuccess => CreateTokenResponse(tokenSuccess.Token, logger),
            TokenResult.FailureIdentityProvider failure => MapIdentityProviderError(
                failure.IdentityProviderError,
                logger
            ),
            TokenResult.FailureUnknown unknown => UpstreamUnavailable(unknown.FailureMessage, logger),
            _ => UpstreamUnavailable("Unexpected token result.", logger),
        };
    }

    // Parses HTTP Basic client credentials (RFC 6749 section 2.3.1). Returns false — never throws — for
    // anything that is not a well-formed Basic credential: an unsupported scheme, a "Basic" value with no
    // credentials, invalid Base64, or a value with no "id:secret" pair. Per section 2.3.1 the client id
    // and secret are application/x-www-form-urlencoded, so each half is form-decoded (which converts '+'
    // to a space) with WebUtility.UrlDecode rather than Uri.UnescapeDataString.
    private static bool TryParseBasicCredentials(
        string authorizationHeader,
        out string clientId,
        out string clientSecret
    )
    {
        clientId = string.Empty;
        clientSecret = string.Empty;

        const string scheme = "Basic ";
        if (!authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string encoded = authorizationHeader[scheme.Length..].Trim();
        if (encoded.Length == 0)
        {
            return false;
        }

        string credentials;
        try
        {
            credentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        int separator = credentials.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        clientId = WebUtility.UrlDecode(credentials[..separator]);
        clientSecret = WebUtility.UrlDecode(credentials[(separator + 1)..]);
        return true;
    }

    // Deserializes the identity provider's success payload. The provider returns its raw token response
    // body unvalidated, so a malformed payload (invalid JSON, a JSON null, or one missing a field RFC 6749
    // §5.1 requires — access_token or token_type) must not reach the caller as a successful token or escape
    // to the global exception handler as an Ed-Fi Problem Details response. Any such payload is logged
    // server-side (without its contents) and reported as the same upstream-unavailable OAuth failure used
    // for other provider problems.
    private static IResult CreateTokenResponse(string token, ILogger logger)
    {
        TokenResponse? tokenResponse;
        try
        {
            tokenResponse = JsonSerializer.Deserialize<TokenResponse>(token);
        }
        catch (JsonException)
        {
            // Deliberately omit the exception: its message can embed fragments of the invalid payload.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception as a parameter
            logger.LogError("The identity provider returned a token response that is not valid JSON.");
#pragma warning restore S6667
            return OAuthErrorResults.TemporarilyUnavailable(
                "The authorization server is temporarily unable to handle the request."
            );
        }

        // RFC 6749 §5.1 requires both access_token and token_type in a successful token response.
        if (
            tokenResponse is null
            || string.IsNullOrEmpty(tokenResponse.AccessToken)
            || string.IsNullOrEmpty(tokenResponse.TokenType)
        )
        {
            logger.LogError(
                "The identity provider returned a successful token response missing a required field."
            );
            return OAuthErrorResults.TemporarilyUnavailable(
                "The authorization server is temporarily unable to handle the request."
            );
        }

        return new TokenSuccessResult(tokenResponse);
    }

    // A successful token response (HTTP 200). RFC 6749 §5.1 requires the authorization server to include
    // Cache-Control: no-store and Pragma: no-cache so the issued token is not retained by clients or
    // intermediaries. The headers are applied only on this success path, never on the OAuth error
    // responses. The body, status, and content type are those of the standard Results.Ok JSON response.
    private sealed class TokenSuccessResult(TokenResponse tokenResponse) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            return Results.Ok(tokenResponse).ExecuteAsync(httpContext);
        }
    }

    // Maps an identity-provider token failure to the OAuth error contract. Bad-credential failures become
    // invalid_client (401 with a WWW-Authenticate challenge); a provider-side OAuth client error (e.g.
    // invalid_scope, invalid_grant) is returned as that error with 400 so a client mistake is not
    // retried; unreachable/not-found/other upstream failures become temporarily_unavailable (503). The
    // provider message is logged server-side only and never surfaced to the caller.
    private static IResult MapIdentityProviderError(IdentityProviderError error, ILogger logger)
    {
        logger.LogWarning(
            "Token request rejected by the identity provider ({ErrorType}): {FailureMessage}",
            error.GetType().Name,
            error.FailureMessage
        );

        return error switch
        {
            InvalidClient or Unauthorized or Forbidden => OAuthErrorResults.InvalidClient(
                "Client authentication failed."
            ),
            BadRequest badRequest => OAuthErrorResults.ClientError(badRequest.Error),
            _ => OAuthErrorResults.TemporarilyUnavailable(
                "The authorization server is temporarily unable to handle the request."
            ),
        };
    }

    private static IResult UpstreamUnavailable(string failureMessage, ILogger logger)
    {
        logger.LogError("Token request failed: {FailureMessage}", failureMessage);
        return OAuthErrorResults.TemporarilyUnavailable(
            "The authorization server is temporarily unable to handle the request."
        );
    }

    private static async Task<IResult> IntrospectToken(
        [FromServices] IEnhancedTokenValidator? tokenValidator,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        IntrospectionRequest model = new();
        var (form, formError) = await TryReadOAuthFormAsync(httpContext, logger);
        if (formError is not null)
        {
            return formError;
        }
        if (form is not null)
        {
            model = new IntrospectionRequest
            {
                Token = form["token"].ToString(),
                Token_Type_Hint = form["token_type_hint"].ToString(),
            };
        }

        if (string.IsNullOrEmpty(model.Token))
        {
            return OAuthErrorResults.InvalidRequest("The token parameter is missing.");
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
        [FromServices] ITokenManager tokenManager,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        RevocationRequest model = new();
        var (form, formError) = await TryReadOAuthFormAsync(httpContext, logger);
        if (formError is not null)
        {
            return formError;
        }
        if (form is not null)
        {
            model = new RevocationRequest
            {
                Token = form["token"].ToString(),
                Token_Type_Hint = form["token_type_hint"].ToString(),
            };
        }

        if (string.IsNullOrEmpty(model.Token))
        {
            return OAuthErrorResults.InvalidRequest("The token parameter is missing.");
        }

        // RFC 7009 §2.2: respond 200 for a successful revocation or an invalid/unknown token, but 503
        // when the revocation service is unavailable — a provider that does not implement revocation, or
        // an unexpected failure — so the client knows the token may still exist and can retry.
        if (tokenManager is not ITokenRevocationManager revocationManager)
        {
            logger.LogWarning("The configured identity provider does not support token revocation.");
            return OAuthErrorResults.TemporarilyUnavailable(
                "The authorization server is temporarily unable to handle the request."
            );
        }

        try
        {
            // A revoked token and an invalid or unknown token both succeed under RFC 7009 (both 200).
            await revocationManager.RevokeTokenAsync(model.Token);
            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Token revocation failed.");
            return OAuthErrorResults.TemporarilyUnavailable(
                "The authorization server is temporarily unable to handle the request."
            );
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
