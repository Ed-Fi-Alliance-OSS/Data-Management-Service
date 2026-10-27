// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Backend;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using static EdFi.DmsConfigurationService.Backend.IdentityProviderError;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class IdentityModule(IConfiguration configuration) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Support dynamic route contexts: allows zero or more path segments after the endpoint
        // Examples: /connect/token, /connect/token/{districtId}, /connect/token/{districtId}/{schoolYear}
        endpoints
            .MapPost("connect/register/{**contextPath}", RegisterClient)
            .DisableAntiforgery()
            .WithMetadata(OAuthEndpointMetadata.Instance, ConnectRateLimitMetadata.Instance);
        endpoints
            .MapPost("connect/token/{**contextPath}", GetClientAccessToken)
            .DisableAntiforgery()
            .WithMetadata(OAuthEndpointMetadata.Instance, ConnectRateLimitMetadata.Instance);
        // No RequireAuthorization() here: RFC 7009 §2.1 requires the caller to authenticate with
        // client credentials (RFC 6749 §2.3), the same as at /connect/token, not with a bearer
        // access token. The ITokenRevocationManager authenticates the caller inside
        // RevokeTokenAsync before deciding whether it owns the token being revoked. Leaving out
        // RequireAuthorization() does not keep UseAuthentication from processing a bearer header,
        // so the route is also exempt from bearer authentication: its key loading would recreate a
        // missing development certificate that revocation must report (D-07.4). A failure is
        // answered in the OAuth format and logged by exception type names only: the handler
        // resolves a manager whose construction can run plugin code, so an exception's text is not
        // under CMS control (DMS-1327 D-15, D-17).
        endpoints
            .MapPost("connect/revoke/{**contextPath}", RevokeToken)
            .DisableAntiforgery()
            .WithMetadata(
                OAuthErrorContractMetadata.Revocation,
                ExceptionTypeOnlyLoggingMetadata.Instance,
                BearerAuthenticationExemptMetadata.Instance,
                ClientCredentialAuthenticationMetadata.Instance,
                OAuthEndpointMetadata.Instance,
                ConnectRateLimitMetadata.Instance
            );
        // The same contract as /connect/revoke. RFC 7662 §2.1 requires some form of authorization
        // to call the endpoint; in self-contained mode CMS uses client credentials (RFC 6749 §2.3),
        // as at /connect/revoke. Keycloak mode registers no introspection manager, and the endpoint
        // then makes no credential check. In both modes a bearer header neither authenticates the
        // caller nor reaches bearer processing.
        // Failures are answered in the OAuth error format. The manager the handler resolves can be
        // constructed by a plugin, so failures are logged by exception type names only
        // (DMS-1327 D-03, D-15, D-17, CS-AUTH.md). The OpenAPI document describes the client
        // credentials only in self-contained mode, the mode that checks them.
        RouteHandlerBuilder introspection = endpoints
            .MapPost("connect/introspect/{**contextPath}", IntrospectToken)
            .DisableAntiforgery()
            .WithMetadata(
                OAuthErrorContractMetadata.Introspection,
                ExceptionTypeOnlyLoggingMetadata.Instance,
                BearerAuthenticationExemptMetadata.Instance,
                OAuthEndpointMetadata.Instance,
                ConnectRateLimitMetadata.Instance
            );
        if (
            string.Equals(
                configuration["AppSettings:IdentityProvider"],
                "self-contained",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            introspection.WithMetadata(ClientCredentialAuthenticationMetadata.Instance);
        }
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
                            // The uniqueness precheck above is not atomic: a client registered
                            // between it and the creation reaches the provider, which refuses the
                            // key. That is the condition the precheck reports, so it is answered
                            // with the same validation failure rather than the insert endpoints'
                            // 409 (DMS-1529 D-5).
                            ClientCreateResult.FailureDuplicateClientId => throw DuplicateClientId(),
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
                    throw DuplicateClientId();
                }
                return true;
            }
        }

        return FailureResults.Authorization(httpContext.TraceIdentifier, ["Registration is disabled."]);
    }

    /// <summary>
    /// The registration endpoint's duplicate-client answer, shared by the uniqueness precheck and
    /// the provider-detected conflict so both paths produce the same 400 body.
    /// </summary>
    private static ValidationException DuplicateClientId() =>
        new([
            new ValidationFailure
            {
                PropertyName = "ClientId",
                ErrorMessage =
                    "Client with the same Client Id already exists. Please provide different Client Id.",
            },
        ]);

    private const string BasicAuthScheme = "basic ";

    /// <summary>
    /// Protection space named in the <c>WWW-Authenticate</c> challenge. A single value covers
    /// client-credential authentication service-wide, since one registered client uses the same
    /// secret wherever it authenticates.
    /// </summary>
    private const string ClientCredentialRealm = "EdFi.DmsConfigurationService";

    /// <summary>
    /// Parses HTTP Basic auth credentials (client_id:client_secret) from the Authorization
    /// header for /connect/token. Lenient by design and unchanged by DMS-1327: /connect/revoke and
    /// /connect/introspect use the strict <see cref="ParseStrictBasicCredentials"/> instead.
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
        [FromServices] ConnectRateLimiter rateLimiter,
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        // Manually read form data to handle empty form bodies in .NET 10
        // (Minimal API [FromForm] binding returns 400 with empty body before handler is invoked)
        string clientId = string.Empty;
        string clientSecret = string.Empty;
        string grantType = string.Empty;
        string scope = string.Empty;

        // Support HTTP Basic authentication in every identity provider mode
        TryParseBasicAuthCredentials(httpContext, logger, out clientId, out clientSecret);

        // Read form data for all parameters (and for any credential the Basic header did not supply)
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

        // DMS-1482 client gate: after the request is known to be well formed and before the manager
        // verifies the secret. The budget is spent whether or not the client exists, so a 429 says
        // nothing about existence; a malformed request keeps its answer and spends nothing.
        using RateLimitLease clientLease = rateLimiter.AttemptClient(httpContext, model.client_id);
        if (!clientLease.IsAcquired)
        {
            return ConnectRateLimitRejection.Create(
                httpContext,
                clientLease,
                ConnectRateLimitRejection.ClientGate,
                logger
            );
        }

        // RFC 6749 section 3.2: a parameter sent without a value is treated as omitted
        List<KeyValuePair<string, string>> tokenRequestParameters =
        [
            new("client_id", model.client_id),
            new("client_secret", model.client_secret),
            new("grant_type", model.grant_type),
        ];
        if (!string.IsNullOrEmpty(model.scope))
        {
            tokenRequestParameters.Add(new("scope", model.scope));
        }

        var tokenResult = await tokenManager.GetAccessTokenAsync(tokenRequestParameters);

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
                    InvalidScope invalidScope => FailureResults.InvalidScope(
                        invalidScope.FailureMessage,
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

    /// <summary>
    /// RFC 7662 token introspection. Under the self-contained provider the request shape and client
    /// credentials are read by <see cref="ReadClientAuthenticatedFormAsync"/>, the same seam as
    /// <c>/connect/revoke</c>, and the manager's outcome maps to the response. Without a manager
    /// (Keycloak), the endpoint keeps its long-standing behavior of answering
    /// <c>{"active":false}</c> for any token, with no credential checks.
    /// </summary>
    private static async Task<IResult> IntrospectToken(
        [FromServices] ILogger<IdentityModule> logger,
        [FromServices] ITokenIntrospectionManager? introspectionManager,
        [FromServices] ConnectRateLimiter rateLimiter,
        HttpContext httpContext
    )
    {
        if (introspectionManager is null)
        {
            // Manually read form data to handle empty form bodies in .NET 10
            string formToken = httpContext.Request.HasFormContentType
                ? (await httpContext.Request.ReadFormAsync(httpContext.RequestAborted))["token"].ToString()
                : string.Empty;

            return formToken.Length == 0
                ? InvalidRequest("The token parameter is missing.")
                : Results.Json(new { active = false });
        }

        ClientAuthenticatedForm form = await ReadClientAuthenticatedFormAsync(
            logger,
            httpContext,
            ClientAuthenticatedEndpoint.Introspection
        );
        if (form is ClientAuthenticatedForm.Rejected rejected)
        {
            return rejected.Result;
        }

        (
            string clientId,
            string clientSecret,
            string token,
            TokenTypeHint tokenTypeHint,
            bool basicAttempted
        ) = (ClientAuthenticatedForm.Accepted)form;

        // Bound client-key processing after the shared parser's shape checks, as on revoke.
        if (clientId.Length > TokenRequest.MaxClientIdLength)
        {
            return InvalidRequest(
                $"The client_id must not exceed {TokenRequest.MaxClientIdLength} characters."
            );
        }

        using RateLimitLease clientLease = rateLimiter.AttemptClient(httpContext, clientId);
        if (!clientLease.IsAcquired)
        {
            return ConnectRateLimitRejection.Create(
                httpContext,
                clientLease,
                ConnectRateLimitRejection.ClientGate,
                logger
            );
        }

        TokenIntrospectionResult result = await introspectionManager.IntrospectTokenAsync(
            new TokenIntrospectionRequest(clientId, clientSecret, token, tokenTypeHint),
            httpContext.RequestAborted
        );

        return result switch
        {
            // RFC 7662 §2.2: the claims of a live token the caller owns. exp and iat are JSON numbers.
            TokenIntrospectionResult.Active active => Results.Json(
                new
                {
                    active = true,
                    client_id = active.Token.ClientId,
                    scope = active.Token.Scope,
                    exp = active.Token.Exp,
                    iat = active.Token.Iat,
                    sub = active.Token.Sub,
                    aud = active.Token.Aud,
                    iss = active.Token.Iss,
                    token_type = "Bearer",
                }
            ),
            // Every other token outcome, another client's token included, is the same answer.
            TokenIntrospectionResult.Inactive => Results.Json(new { active = false }),
            TokenIntrospectionResult.InvalidClient => InvalidClient(
                httpContext,
                basicAttempted,
                InvalidClientCredentialsDescription
            ),
            TokenIntrospectionResult.TemporarilyUnavailable unavailable => IntrospectionUnavailable(
                logger,
                clientId,
                unavailable
            ),
            _ => throw new InvalidOperationException("The introspection manager returned an unknown result."),
        };
    }

    private static readonly string[] _clientAuthenticatedFormParameters =
    [
        "token",
        "token_type_hint",
        "client_id",
        "client_secret",
    ];

    private const string InvalidClientCredentialsDescription = "Invalid client or Invalid client credentials";
    private const string ClientAuthenticationRequiredDescription = "Client authentication is required.";
    private const string MalformedFormDescription = "The request form payload is malformed.";
    private const string UnsupportedCharsetDescription = "The request form payload must be encoded as UTF-8.";

    /// <summary>
    /// The outcome of <see cref="ReadClientAuthenticatedFormAsync"/>: the response that answers the
    /// request, or the client credentials and token parameters the endpoint goes on to use.
    /// <c>BasicAttempted</c> says whether the caller attempted Basic authentication, which
    /// decides the shape of a later <c>invalid_client</c> answer.
    /// </summary>
    private abstract record ClientAuthenticatedForm
    {
        public sealed record Rejected(IResult Result) : ClientAuthenticatedForm;

        public sealed record Accepted(
            string ClientId,
            string ClientSecret,
            string Token,
            TokenTypeHint TokenTypeHint,
            bool BasicAttempted
        ) : ClientAuthenticatedForm;
    }

    /// <summary>
    /// RFC 7009 token revocation. The request shape and client credentials are read by
    /// <see cref="ReadClientAuthenticatedFormAsync"/> and its first failure answers, then the
    /// manager's outcome maps to the response (DMS-1327 D-03 order).
    /// </summary>
    private static async Task<IResult> RevokeToken(
        [FromServices] ILogger<IdentityModule> logger,
        [FromServices] ITokenRevocationManager revocationManager,
        [FromServices] ConnectRateLimiter rateLimiter,
        HttpContext httpContext
    )
    {
        ClientAuthenticatedForm form = await ReadClientAuthenticatedFormAsync(
            logger,
            httpContext,
            ClientAuthenticatedEndpoint.Revocation
        );
        if (form is ClientAuthenticatedForm.Rejected rejected)
        {
            return rejected.Result;
        }

        (
            string clientId,
            string clientSecret,
            string token,
            TokenTypeHint tokenTypeHint,
            bool basicAttempted
        ) = (ClientAuthenticatedForm.Accepted)form;

        // Reject oversized aliases before generating a rate-limit partition key.
        if (clientId.Length > TokenRequest.MaxClientIdLength)
        {
            return InvalidRequest(
                $"The client_id must not exceed {TokenRequest.MaxClientIdLength} characters."
            );
        }

        // DMS-1482 client gate: after the D-03 shape checks and credential parsing, before the manager
        // authenticates the caller. Spent whether or not the client exists; rate-limit rejections use
        // Ed-Fi problem details, while this route's other errors retain their OAuth contract.
        using RateLimitLease clientLease = rateLimiter.AttemptClient(httpContext, clientId);
        if (!clientLease.IsAcquired)
        {
            return ConnectRateLimitRejection.Create(
                httpContext,
                clientLease,
                ConnectRateLimitRejection.ClientGate,
                logger
            );
        }
        TokenRevocationRequest request = new(clientId, clientSecret, token, tokenTypeHint);

        // The manager owns authenticate → ownership → mutate and classifies every dependency
        // failure; an exception reaching this point is a programming fault, answered 500
        // server_error in the OAuth format by the global exception handler (DMS-1327 D-13, D-17).
        TokenRevocationResult result = await revocationManager.RevokeTokenAsync(
            request,
            httpContext.RequestAborted
        );

        return result switch
        {
            // RFC 7009: revoked, unknown, invalid, expired, already revoked and another client's
            // token are all the same empty 200, so nothing leaks about a token's existence or owner.
            TokenRevocationResult.Completed => Results.Ok(),
            TokenRevocationResult.InvalidClient => InvalidClient(
                httpContext,
                basicAttempted,
                InvalidClientCredentialsDescription
            ),
            TokenRevocationResult.UnsupportedTokenType => OAuthErrorResponseWriter.Create(
                StatusCodes.Status400BadRequest,
                "unsupported_token_type",
                "The token type is not supported by the identity provider."
            ),
            TokenRevocationResult.InvalidRequest => InvalidRequest(
                "The identity provider rejected the revocation request."
            ),
            TokenRevocationResult.TemporarilyUnavailable unavailable => TemporarilyUnavailable(
                logger,
                clientId,
                unavailable
            ),
            _ => throw new InvalidOperationException("The revocation manager returned an unknown result."),
        };
    }

    /// <summary>
    /// The client-authenticated form endpoints, which each log with their own fixed message templates.
    /// </summary>
    private enum ClientAuthenticatedEndpoint
    {
        Revocation,
        Introspection,
    }

    /// <summary>
    /// The request-shape and client-credential checks of a client-authenticated form endpoint, in the
    /// DMS-1327 D-03 order, rows 1-7. The first failure answers: content type, charset and encoding,
    /// duplicates, mixed mechanisms, the token, then the client credentials. None of them touches the
    /// token, the database or the identity provider. The body must be
    /// <c>application/x-www-form-urlencoded</c>, the request format RFC 7009 §2.1 and RFC 7662 §2.1
    /// define; any other content type, <c>multipart/form-data</c> included, is refused before the body
    /// is read. A body <c>ReadFormAsync</c> cannot parse throws and is answered in the OAuth format by
    /// <c>GlobalExceptionHandler</c>, selected by the route's <see cref="OAuthErrorContractMetadata"/>.
    /// <c>ReadFormAsync</c> does not reject malformed URL encoding, though: it keeps an invalid escape
    /// such as <c>%GG</c> as literal text and leaves percent-encoded bytes that are not UTF-8
    /// undecoded, so that is checked here. The check is UTF-8 and <c>ReadFormAsync</c> decodes with
    /// the declared charset, so the body may declare no charset or UTF-8 only; the two then always
    /// decode the same way. <paramref name="endpoint"/> selects the malformed-Basic log line.
    /// </summary>
    private static async Task<ClientAuthenticatedForm> ReadClientAuthenticatedFormAsync(
        ILogger<IdentityModule> logger,
        HttpContext httpContext,
        ClientAuthenticatedEndpoint endpoint
    )
    {
        HttpRequest httpRequest = httpContext.Request;

        MediaTypeHeaderValue? contentType = httpRequest.GetTypedHeaders().ContentType;
        if (
            contentType?.MediaType.Equals(
                "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase
            )
            is not true
        )
        {
            return new ClientAuthenticatedForm.Rejected(
                InvalidRequest("The request body must be application/x-www-form-urlencoded.")
            );
        }

        if (!IsAbsentOrUtf8(contentType.Charset))
        {
            return new ClientAuthenticatedForm.Rejected(InvalidRequest(UnsupportedCharsetDescription));
        }

        UrlEncodedFormValidatingStream encodingCheck = new(httpRequest.Body);
        httpRequest.Body = encodingCheck;

        IFormCollection form = await httpRequest.ReadFormAsync(httpContext.RequestAborted);
        if (encodingCheck.IsMalformed())
        {
            return new ClientAuthenticatedForm.Rejected(InvalidRequest(MalformedFormDescription));
        }

        StringValues authorization = httpRequest.Headers.Authorization;

        if (
            authorization.Count > 1
            || Array.Exists(_clientAuthenticatedFormParameters, name => form[name].Count > 1)
        )
        {
            return new ClientAuthenticatedForm.Rejected(
                InvalidRequest("A request parameter or header was included more than once.")
            );
        }

        // RFC 6749 §2.3 allows one mechanism per request. Keyed on the presence of the form fields,
        // whatever their values, and decided before the Basic value is parsed, so a partial or
        // malformed combination is still mixed. Under Keycloak a form client_id would otherwise
        // override the header, letting the caller choose which identity is checked (D-05).
        string? afterBasicScheme = GetTextAfterBasicScheme(authorization);
        bool basicAttempted = afterBasicScheme is not null;
        if (basicAttempted && (form.ContainsKey("client_id") || form.ContainsKey("client_secret")))
        {
            return new ClientAuthenticatedForm.Rejected(
                InvalidRequest("Only one client authentication mechanism may be used.")
            );
        }

        string token = form["token"].ToString();
        if (token.Length == 0)
        {
            return new ClientAuthenticatedForm.Rejected(InvalidRequest("The token parameter is missing."));
        }

        string clientId;
        string clientSecret;
        if (afterBasicScheme is not null)
        {
            StrictBasicCredentials parsed = ParseStrictBasicCredentials(afterBasicScheme);
            if (parsed is StrictBasicCredentials.Malformed malformed)
            {
                if (endpoint == ClientAuthenticatedEndpoint.Revocation)
                {
                    logger.LogInformation(
                        "Revocation Basic credentials were malformed at the {Stage} stage",
                        malformed.Stage
                    );
                }
                else
                {
                    logger.LogInformation(
                        "Introspection Basic credentials were malformed at the {Stage} stage",
                        malformed.Stage
                    );
                }
                return new ClientAuthenticatedForm.Rejected(
                    InvalidClient(httpContext, basicAttempted, InvalidClientCredentialsDescription)
                );
            }

            (clientId, clientSecret) = (StrictBasicCredentials.Parsed)parsed;
        }
        else
        {
            // A non-Basic Authorization header (Bearer, say) is not client authentication here and
            // grants nothing; the form credentials must authenticate on their own (Q-03).
            clientId = form["client_id"].ToString();
            clientSecret = form["client_secret"].ToString();
            if (clientId.Length == 0 || clientSecret.Length == 0)
            {
                return new ClientAuthenticatedForm.Rejected(
                    InvalidClient(httpContext, basicAttempted, ClientAuthenticationRequiredDescription)
                );
            }
        }

        return new ClientAuthenticatedForm.Accepted(
            clientId,
            clientSecret,
            token,
            ParseTokenTypeHint(form["token_type_hint"].ToString()),
            basicAttempted
        );
    }

    private static TokenTypeHint ParseTokenTypeHint(string hint) =>
        hint switch
        {
            "access_token" => TokenTypeHint.AccessToken,
            "refresh_token" => TokenTypeHint.RefreshToken,
            _ => TokenTypeHint.None,
        };

    private const string BasicScheme = "Basic";

    /// <summary>
    /// Returns everything after the scheme token when the caller attempted Basic client
    /// authentication, otherwise <c>null</c>. Attempted means exactly one Authorization value whose
    /// leading scheme token, the longest run of RFC 7230 <c>tchar</c> characters, is <c>Basic</c>
    /// (case-insensitive). Recognition stops there: whatever follows the token, including a tab or
    /// any other invalid separator, is validated by <see cref="ParseStrictBasicCredentials"/> and
    /// makes the attempt malformed, never a request without Basic. <c>Basicx</c> or <c>Bearer</c> is
    /// a different scheme and is not an attempt.
    /// </summary>
    private static string? GetTextAfterBasicScheme(StringValues authorization)
    {
        if (authorization.Count != 1)
        {
            return null;
        }

        string value = authorization[0] ?? string.Empty;
        if (
            !value.StartsWith(BasicScheme, StringComparison.OrdinalIgnoreCase)
            || (value.Length > BasicScheme.Length && IsTokenChar(value[BasicScheme.Length]))
        )
        {
            return null;
        }

        return value[BasicScheme.Length..];
    }

    /// <summary>RFC 7230 §3.2.6 <c>tchar</c>.</summary>
    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private abstract record StrictBasicCredentials
    {
        /// <summary><paramref name="Stage"/> is a fixed label, never caller input.</summary>
        public sealed record Malformed(string Stage) : StrictBasicCredentials;

        public sealed record Parsed(string ClientId, string ClientSecret) : StrictBasicCredentials;
    }

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Validates what follows the <c>Basic</c> scheme token: one or more spaces (RFC 7235 §2.1
    /// <c>1*SP</c>) and then the credentials. Any other separator is malformed. The credentials are
    /// decoded exactly as RFC 6749 §2.3.1 defines them: base64 of
    /// <c>form-urlencode(client_id) ":" form-urlencode(client_secret)</c>. Each stage is validated
    /// explicitly because the framework decoders are lenient: <c>Convert.FromBase64String</c> skips
    /// whitespace, default UTF-8 decoding substitutes U+FFFD, and <c>WebUtility.UrlDecode</c> keeps
    /// malformed escapes. Used by <c>/connect/revoke</c> and <c>/connect/introspect</c>;
    /// <c>/connect/token</c> keeps the lenient <see cref="TryParseBasicAuthCredentials"/> (DMS-1327 D-04).
    /// </summary>
    private static StrictBasicCredentials ParseStrictBasicCredentials(string afterScheme)
    {
        if (afterScheme.Length > 0 && afterScheme[0] != ' ')
        {
            return new StrictBasicCredentials.Malformed("scheme-separator");
        }

        string base64Credentials = afterScheme.TrimStart(' ');
        if (!IsStrictBase64(base64Credentials))
        {
            return new StrictBasicCredentials.Malformed("base64");
        }

        string decoded;
        try
        {
            decoded = _strictUtf8.GetString(Convert.FromBase64String(base64Credentials));
        }
        catch (DecoderFallbackException)
        {
            return new StrictBasicCredentials.Malformed("utf8");
        }

        int colon = decoded.IndexOf(':');
        if (colon < 0)
        {
            return new StrictBasicCredentials.Malformed("separator");
        }

        if (
            !TryFormDecode(decoded[..colon], out string clientId)
            || !TryFormDecode(decoded[(colon + 1)..], out string clientSecret)
        )
        {
            return new StrictBasicCredentials.Malformed("form-decoding");
        }

        if (clientId.Length == 0 || clientSecret.Length == 0)
        {
            return new StrictBasicCredentials.Malformed("empty");
        }

        return new StrictBasicCredentials.Parsed(clientId, clientSecret);
    }

    /// <summary>
    /// Non-empty, a multiple of four characters, only the base64 alphabet, and <c>=</c> padding in
    /// the last two positions at most.
    /// </summary>
    private static bool IsStrictBase64(string value)
    {
        if (value.Length == 0 || value.Length % 4 != 0)
        {
            return false;
        }

        int padding = value.Length - value.TrimEnd('=').Length;
        if (padding > 2)
        {
            return false;
        }

        for (int i = 0; i < value.Length - padding; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '/')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>application/x-www-form-urlencoded</c> decoding: <c>+</c> is a space, <c>%</c> must be
    /// followed by exactly two hex digits and yields that byte, and any other character contributes
    /// its UTF-8 bytes. The resulting bytes must be valid UTF-8.
    /// </summary>
    private static bool TryFormDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        List<byte> bytes = new(value.Length);
        int literalStart = 0;
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c != '+' && c != '%')
            {
                i++;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(value[literalStart..i]));
            if (c == '+')
            {
                bytes.Add((byte)' ');
                i++;
            }
            else
            {
                if (
                    i + 2 >= value.Length
                    || !char.IsAsciiHexDigit(value[i + 1])
                    || !char.IsAsciiHexDigit(value[i + 2])
                )
                {
                    return false;
                }

                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 3;
            }
            literalStart = i;
        }
        bytes.AddRange(Encoding.UTF8.GetBytes(value[literalStart..]));

        try
        {
            decoded = _strictUtf8.GetString([.. bytes]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for no <c>charset</c> parameter, an empty one, or one naming UTF-8 in any casing,
    /// quoted or not; false for any other value, an unknown charset included.
    /// </summary>
    private static bool IsAbsentOrUtf8(StringSegment charset)
    {
        StringSegment name = HeaderUtilities.RemoveQuotes(charset);
        if (StringSegment.IsNullOrEmpty(name))
        {
            return true;
        }

        try
        {
            return Encoding.GetEncoding(name.ToString()).CodePage == Encoding.UTF8.CodePage;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Passes the request body through unchanged and checks it, as <c>ReadFormAsync</c> reads it,
    /// against <c>application/x-www-form-urlencoded</c> as RFC 6749 Appendix B uses it: every
    /// <c>%</c> starts an escape of exactly two hex digits, and every name and value decodes to
    /// valid UTF-8. Nothing is buffered and nothing is decoded for use, so the request's size
    /// limits, its cancellation and the form reader's own limits apply as before, and the form
    /// reader remains the only decoder of the values the endpoint uses.
    /// </summary>
    private sealed class UrlEncodedFormValidatingStream(Stream inner) : Stream
    {
        private readonly Decoder _utf8 = _strictUtf8.GetDecoder();
        private int _escapeDigitsPending;
        private int _escapedByte;
        private bool _malformed;

        /// <summary>
        /// Whether the body is malformed, an escape or UTF-8 sequence left unfinished at its end
        /// included. Called once the form has been read.
        /// </summary>
        public bool IsMalformed()
        {
            EndSegment();
            return _malformed;
        }

        private void Inspect(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                if (_malformed)
                {
                    return;
                }

                if (_escapeDigitsPending > 0)
                {
                    int digit = HexDigitValue(value);
                    if (digit < 0)
                    {
                        _malformed = true;
                        return;
                    }

                    _escapedByte = (_escapedByte << 4) | digit;
                    if (--_escapeDigitsPending == 0)
                    {
                        AddDecodedByte((byte)_escapedByte);
                    }
                    continue;
                }

                switch (value)
                {
                    case (byte)'%':
                        _escapeDigitsPending = 2;
                        _escapedByte = 0;
                        break;
                    case (byte)'&' or (byte)'=':
                        EndSegment();
                        break;
                    case (byte)'+':
                        AddDecodedByte((byte)' ');
                        break;
                    default:
                        AddDecodedByte(value);
                        break;
                }
            }
        }

        private static int HexDigitValue(byte value) =>
            value switch
            {
                >= (byte)'0' and <= (byte)'9' => value - '0',
                >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
                _ => -1,
            };

        /// <summary>
        /// Feeds one decoded byte to the stateful decoder; <c>GetChars</c> rather than
        /// <c>GetCharCount</c>, which does not carry a partial sequence into the next call.
        /// </summary>
        private void AddDecodedByte(byte value)
        {
            ReadOnlySpan<byte> decoded = [value];
            Span<char> chars = stackalloc char[2];
            try
            {
                _utf8.GetChars(decoded, chars, flush: false);
            }
            catch (DecoderFallbackException)
            {
                _malformed = true;
            }
        }

        /// <summary>Ends a name or value: no escape or UTF-8 sequence may be left unfinished.</summary>
        private void EndSegment()
        {
            if (_malformed)
            {
                return;
            }

            if (_escapeDigitsPending > 0)
            {
                _malformed = true;
                return;
            }

            Span<char> chars = stackalloc char[2];
            try
            {
                _utf8.GetChars([], chars, flush: true);
            }
            catch (DecoderFallbackException)
            {
                _malformed = true;
            }
            _utf8.Reset();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Inspect(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Inspect(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A dependency the revocation needed (database, signing keys, identity provider) failed, so
    /// the outcome is unknown: the mutation may or may not have happened. The text therefore says
    /// "could not be confirmed" and never claims the token is still live. Retrying is safe (DMS-1327
    /// D-13.3). The manager has already logged the boundary and exception types at Error; this
    /// records only the sanitized caller and the fixed boundary label.
    /// </summary>
    private static IResult TemporarilyUnavailable(
        ILogger<IdentityModule> logger,
        string clientId,
        TokenRevocationResult.TemporarilyUnavailable unavailable
    )
    {
        logger.LogWarning(
            "Revocation for client {ClientId} could not be confirmed ({Reason}); answering 503",
            LoggingUtility.SanitizeForLog(clientId),
            unavailable.Reason
        );
        return OAuthErrorResponseWriter.Create(
            StatusCodes.Status503ServiceUnavailable,
            "temporarily_unavailable",
            "Token revocation could not be confirmed. Retry the request and confirm the token's state through the provider's validation path."
        );
    }

    /// <summary>
    /// The caller could not be authenticated because a dependency failed, so the request was not
    /// judged. This answers 503, not <c>invalid_client</c>, so an outage cannot read as rejected
    /// credentials. A dependency failure after authentication, while deciding the token, still
    /// answers <c>{"active": false}</c> (CS-AUTH.md). The manager has already logged the boundary and
    /// exception types at Error; this records only the sanitized caller and the fixed boundary label,
    /// at Warning like the revocation endpoint's line, so one outage request is one Error event.
    /// </summary>
    private static IResult IntrospectionUnavailable(
        ILogger<IdentityModule> logger,
        string clientId,
        TokenIntrospectionResult.TemporarilyUnavailable unavailable
    )
    {
        logger.LogWarning(
            "Introspection for client {ClientId} could not be completed ({Reason}); answering 503",
            LoggingUtility.SanitizeForLog(clientId),
            unavailable.Reason
        );
        return OAuthErrorResponseWriter.Create(
            StatusCodes.Status503ServiceUnavailable,
            "temporarily_unavailable",
            "Token introspection is temporarily unavailable. Retry the request."
        );
    }

    private static IResult InvalidRequest(string errorDescription) =>
        OAuthErrorResponseWriter.Create(StatusCodes.Status400BadRequest, "invalid_request", errorDescription);

    /// <summary>
    /// The RFC 6749 §5.2 response for a revocation or introspection caller that failed client
    /// authentication. A caller that attempted Basic authentication gets 401 with the
    /// <c>WWW-Authenticate</c> challenge
    /// §5.2 requires; any other caller gets 400, since offering a challenge would invite a scheme it
    /// did not choose.
    ///
    /// Deliberately not routed through <c>FailureResults</c>, which is the right contract for
    /// the Management API's own endpoints but the wrong one here: it emits
    /// <c>application/problem+json</c> and flattens the code into a sentence inside an
    /// <c>errors</c> array, where a conforming OAuth client, which reads <c>error</c> off the
    /// root object, cannot find it. <c>/connect/revoke</c> and <c>/connect/introspect</c> are OAuth
    /// endpoints and answer in the OAuth error format.
    /// </summary>
    private static IResult InvalidClient(
        HttpContext httpContext,
        bool basicAttempted,
        string errorDescription
    )
    {
        if (!basicAttempted)
        {
            return OAuthErrorResponseWriter.Create(
                StatusCodes.Status400BadRequest,
                "invalid_client",
                errorDescription
            );
        }

        httpContext.Response.Headers.WWWAuthenticate = $"Basic realm=\"{ClientCredentialRealm}\"";
        return OAuthErrorResponseWriter.Create(
            StatusCodes.Status401Unauthorized,
            "invalid_client",
            errorDescription
        );
    }
}
