// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
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
using Microsoft.Extensions.Primitives;
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
        // access token. The ITokenRevocationManager authenticates the caller inside
        // RevokeTokenAsync before deciding whether it owns the token being revoked, so the
        // ASP.NET Core auth pipeline is not involved at all.
        endpoints
            .MapPost("connect/revoke/{**contextPath}", RevokeToken)
            .DisableAntiforgery()
            .WithMetadata(OAuthErrorContractMetadata.Instance);
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
    /// header for /connect/token. Lenient by design and unchanged by DMS-1327: /connect/revoke
    /// uses the strict <see cref="ParseStrictBasicCredentials"/> instead.
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

    private static readonly string[] _revocationParameters =
    [
        "token",
        "token_type_hint",
        "client_id",
        "client_secret",
    ];

    private const string InvalidClientCredentialsDescription = "Invalid client or Invalid client credentials";
    private const string ClientAuthenticationRequiredDescription = "Client authentication is required.";

    /// <summary>
    /// RFC 7009 token revocation. The checks run in the DMS-1327 D-03 order and the first failure
    /// answers: request shape (form body, duplicates, mixed mechanisms, token), then client
    /// authentication, then the manager's outcome. Shape and authentication checks never touch the
    /// token, the database or the identity provider. A malformed form body throws from
    /// <c>ReadFormAsync</c> and is answered in the OAuth format by <c>GlobalExceptionHandler</c>,
    /// selected by the route's <see cref="OAuthErrorContractMetadata"/>.
    /// </summary>
    private static async Task<IResult> RevokeToken(
        [FromServices] ILogger<IdentityModule> logger,
        HttpContext httpContext
    )
    {
        HttpRequest httpRequest = httpContext.Request;

        if (!httpRequest.HasFormContentType)
        {
            return InvalidRequest("The request body must be application/x-www-form-urlencoded.");
        }

        IFormCollection form = await httpRequest.ReadFormAsync(httpContext.RequestAborted);
        StringValues authorization = httpRequest.Headers.Authorization;

        if (authorization.Count > 1 || Array.Exists(_revocationParameters, name => form[name].Count > 1))
        {
            return InvalidRequest("A request parameter or header was included more than once.");
        }

        // RFC 6749 §2.3 allows one mechanism per request. Keyed on the presence of the form fields,
        // whatever their values, and decided before the Basic value is parsed, so a partial or
        // malformed combination is still mixed. Under Keycloak a form client_id would otherwise
        // override the header, letting the caller choose which identity is checked (D-05).
        string? afterBasicScheme = GetTextAfterBasicScheme(authorization);
        bool basicAttempted = afterBasicScheme is not null;
        if (basicAttempted && (form.ContainsKey("client_id") || form.ContainsKey("client_secret")))
        {
            return InvalidRequest("Only one client authentication mechanism may be used.");
        }

        string token = form["token"].ToString();
        if (token.Length == 0)
        {
            return InvalidRequest("The token parameter is missing.");
        }

        string clientId;
        string clientSecret;
        if (afterBasicScheme is not null)
        {
            StrictBasicCredentials parsed = ParseStrictBasicCredentials(afterBasicScheme);
            if (parsed is StrictBasicCredentials.Malformed malformed)
            {
                logger.LogInformation(
                    "Revocation Basic credentials were malformed at the {Stage} stage",
                    malformed.Stage
                );
                return InvalidClient(httpContext, basicAttempted, InvalidClientCredentialsDescription);
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
                return InvalidClient(httpContext, basicAttempted, ClientAuthenticationRequiredDescription);
            }
        }

        // Resolved optionally for now: both identity providers register an ITokenRevocationManager,
        // but until DMS-1327 P4.1 makes the registration required at startup, a host without one
        // answers a bare 200 OK with nothing revoked once the request shape and the presence of
        // credentials have been checked above.
        ITokenRevocationManager? revocationManager =
            httpContext.RequestServices.GetService<ITokenRevocationManager>();
        if (revocationManager is null)
        {
            return Results.Ok();
        }

        TokenRevocationRequest request = new(
            clientId,
            clientSecret,
            token,
            ParseTokenTypeHint(form["token_type_hint"].ToString())
        );

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
    /// malformed escapes. Used by <c>/connect/revoke</c> only; <c>/connect/token</c> keeps the lenient
    /// <see cref="TryParseBasicAuthCredentials"/> (DMS-1327 D-04).
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

    private static IResult InvalidRequest(string errorDescription) =>
        OAuthErrorResponseWriter.Create(StatusCodes.Status400BadRequest, "invalid_request", errorDescription);

    /// <summary>
    /// The RFC 6749 §5.2 response for a revocation caller that failed client authentication. A
    /// caller that attempted Basic authentication gets 401 with the <c>WWW-Authenticate</c> challenge
    /// §5.2 requires; any other caller gets 400, since offering a challenge would invite a scheme it
    /// did not choose.
    ///
    /// Deliberately not routed through <c>FailureResults</c>, which is the right contract for
    /// the Management API's own endpoints but the wrong one here: it emits
    /// <c>application/problem+json</c> and flattens the code into a sentence inside an
    /// <c>errors</c> array, where a conforming OAuth client — which reads <c>error</c> off the
    /// root object — cannot find it. <c>/connect/revoke</c> is an OAuth endpoint and answers in
    /// the OAuth error format.
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

    public class IntrospectionRequest
    {
        public string Token { get; set; } = string.Empty;
        public string? Token_Type_Hint { get; set; }
    }
}
