// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The request boundary both JwtBearer schemes share (spec D-3, §4.6). Each scheme composes these handlers onto its own
/// <see cref="JwtBearerEvents"/>. A dependency failure (<see cref="AuthenticationDependencyUnavailableException"/>)
/// fails authentication with the typed exception and is challenged with 503 and <c>Retry-After</c>. Any other failure
/// is left to the scheme's existing handling: the <c>Try…</c> methods return <see langword="false"/> and change nothing.
/// </summary>
public sealed class SigningKeyBearerEvents
{
    private const string BearerPrefix = "Bearer ";

    private readonly ISigningKeySnapshotProvider _provider;
    private readonly ITokenManager _tokenManager;
    private readonly ILogger<SigningKeyBearerEvents> _logger;
    private readonly string _retryAfterSeconds;

    /// <exception cref="OptionsValidationException">A signing-key setting is invalid.</exception>
    public SigningKeyBearerEvents(
        ISigningKeySnapshotProvider provider,
        ITokenManager tokenManager,
        IOptions<IdentityOptions> identityOptions,
        ILogger<SigningKeyBearerEvents> logger
    )
    {
        ArgumentNullException.ThrowIfNull(identityOptions);

        _provider = provider;
        _tokenManager = tokenManager;
        _logger = logger;
        _retryAfterSeconds = SigningKeyDependencyResponse.RetryAfterSeconds(
            SigningKeySettings.FromIdentityOptions(identityOptions.Value)
        );
    }

    /// <summary>
    /// Before the handler reads its configuration. With no bearer token it does nothing. Otherwise it obtains a usable
    /// snapshot with the request's abort token. With no usable snapshot it fails with the typed exception, and the
    /// handler returns that result before it asks the configuration manager for anything (V-1). When the token's
    /// <c>kid</c> is not in the snapshot, it makes the one gated unknown-key refresh and then checks usability again,
    /// because the snapshot may have crossed the maximum staleness while a failed refresh ran. A token whose header
    /// cannot be parsed is left to the handler to reject.
    /// </summary>
    public async Task MessageReceivedAsync(MessageReceivedContext context)
    {
        string? token = context.Token ?? BearerToken(context.Request);
        if (token is null)
        {
            return;
        }

        CancellationToken requestAborted = context.HttpContext.RequestAborted;
        SigningKeySnapshot snapshot;
        try
        {
            snapshot = await _provider.GetUsableAsync(requestAborted);
        }
        catch (SigningKeysUnavailableException exception)
        {
            Fail(context, exception);
            return;
        }

        string? keyId = ReadKeyId(token);
        if (string.IsNullOrEmpty(keyId) || snapshot.ContainsKeyId(keyId))
        {
            return;
        }

        SigningKeyUnknownKeyOutcome outcome = await _provider.TryRefreshForUnknownKeyAsync(
            keyId,
            requestAborted
        );
        _logger.LogWarning(
            "Bearer token key id {KeyId} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}",
            LoggingUtility.SanitizeForLog(keyId),
            outcome
        );

        try
        {
            await _provider.GetUsableAsync(requestAborted);
        }
        catch (SigningKeysUnavailableException exception)
        {
            Fail(context, exception);
        }
    }

    /// <summary>
    /// After the signature, issuer, audience and lifetime checks: the per-request token-status check (I-2), uncached.
    /// A revoked or unknown token fails as before; a status store that cannot be read fails with the typed exception.
    /// </summary>
    public async Task TokenValidatedAsync(TokenValidatedContext context)
    {
        string token = context.SecurityToken is JsonWebToken validated
            ? validated.EncodedToken
            : BearerToken(context.Request) ?? string.Empty;

        bool valid;
        try
        {
            valid = await _tokenManager.ValidateTokenAsync(token);
        }
        catch (AuthenticationDependencyUnavailableException exception)
        {
            Fail(context, exception);
            return;
        }

        if (!valid)
        {
            context.Fail("Token has been revoked or is invalid.");
        }
    }

    /// <summary>
    /// When the handler caught an exception, for example from the configuration manager: a dependency failure, alone or
    /// inside an <see cref="AggregateException"/>, fails with the typed exception and returns <see langword="true"/>.
    /// Anything else returns <see langword="false"/> with no result set, so the scheme's existing logging runs and the
    /// handler's existing behavior is unchanged.
    /// </summary>
    public bool TryFailOnDependency(AuthenticationFailedContext context)
    {
        if (DependencyFailureIn(context.Exception) is not { } dependency)
        {
            return false;
        }

        Fail(context, dependency);
        return true;
    }

    /// <summary>
    /// Answers a challenge whose authentication failed on a dependency with 503, <c>Retry-After</c>, and a generic
    /// problem body (I-7), and returns <see langword="true"/>. Any other challenge returns <see langword="false"/>
    /// untouched, so the scheme's existing 401 challenge follows.
    /// </summary>
    public async Task<bool> TryChallengeDependencyAsync(JwtBearerChallengeContext context)
    {
        if (DependencyFailureIn(context.AuthenticateFailure) is null)
        {
            return false;
        }

        context.HandleResponse();
        await SigningKeyDependencyResponse.WriteAsync(context.HttpContext, _retryAfterSeconds);
        return true;
    }

    /// <summary>The bearer token, by the handler's own rule: the <c>Authorization</c> header only.</summary>
    private static string? BearerToken(HttpRequest request)
    {
        string authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = authorization[BearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>The <c>kid</c> header, read without validation, or <see langword="null"/> when the header cannot be parsed.</summary>
    private static string? ReadKeyId(string token)
    {
        try
        {
            return new JsonWebToken(token).Kid;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static AuthenticationDependencyUnavailableException? DependencyFailureIn(Exception? exception) =>
        exception switch
        {
            AuthenticationDependencyUnavailableException dependency => dependency,
            AggregateException aggregate => aggregate
                .Flatten()
                .InnerExceptions.OfType<AuthenticationDependencyUnavailableException>()
                .FirstOrDefault(),
            _ => null,
        };

    /// <summary>Logs the boundary failure once, with its category and the trace id, and fails with the typed exception.</summary>
    private void Fail<TOptions>(
        ResultContext<TOptions> context,
        AuthenticationDependencyUnavailableException exception
    )
        where TOptions : AuthenticationSchemeOptions
    {
        _logger.LogError(
            exception,
            "Authentication could not reach a decision: the {Category} is unavailable (trace {TraceId})",
            exception.Category,
            context.HttpContext.TraceIdentifier
        );
        context.Fail(exception);
    }
}
