// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Puts a JwtBearer scheme on the shared signing-key snapshot (spec D-2, D-3, §4.6).
/// </summary>
public static class SigningKeyJwtBearerOptionsExtensions
{
    /// <summary>
    /// Supplies <paramref name="configurationManager"/> as the scheme's configuration manager, turns
    /// <see cref="JwtBearerOptions.RefreshOnIssuerKeyNotFound"/> off (Q14: the request boundary owns the unknown-key
    /// refresh), and removes any <c>IssuerSigningKeyResolver</c>, so validation keys come only from the snapshot and no
    /// request thread blocks on a key read. With a manager supplied, post-configuration creates neither an HTTP
    /// configuration manager nor a backchannel (V-5). Issuer, audience, lifetime and signature validation are left as
    /// the scheme configured them.
    /// <para>
    /// The shared <paramref name="bearerEvents"/> are composed onto the scheme's existing events: message-received
    /// runs the boundary, and token-validated runs the shared, uncached token-status check in place of the scheme's
    /// own. A dependency failure in authentication-failed fails with the typed exception, and one in challenge is
    /// answered with 503. Every other failure and challenge goes to the scheme's existing handler, so its logging and
    /// its 401 are unchanged. Call this once per scheme, after the scheme has set its events.
    /// </para>
    /// </summary>
    public static JwtBearerOptions UseSigningKeySnapshot(
        this JwtBearerOptions options,
        SigningKeyConfigurationManager configurationManager,
        SigningKeyBearerEvents bearerEvents
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configurationManager);
        ArgumentNullException.ThrowIfNull(bearerEvents);

        options.ConfigurationManager = configurationManager;
        options.RefreshOnIssuerKeyNotFound = false;
        options.TokenValidationParameters.IssuerSigningKeyResolver = null;

        JwtBearerEvents events = options.Events ?? new JwtBearerEvents();
        Func<AuthenticationFailedContext, Task> schemeAuthenticationFailed = events.OnAuthenticationFailed;
        Func<JwtBearerChallengeContext, Task> schemeChallenge = events.OnChallenge;

        events.OnMessageReceived = bearerEvents.MessageReceivedAsync;
        events.OnTokenValidated = bearerEvents.TokenValidatedAsync;
        events.OnAuthenticationFailed = context =>
            bearerEvents.TryFailOnDependency(context)
                ? Task.CompletedTask
                : schemeAuthenticationFailed(context);
        events.OnChallenge = async context =>
        {
            if (!await bearerEvents.TryChallengeDependencyAsync(context))
            {
                await schemeChallenge(context);
            }
        };

        options.Events = events;
        return options;
    }
}
