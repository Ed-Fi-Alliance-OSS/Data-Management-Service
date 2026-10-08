// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.IdentityModel.Tokens.Jwt;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    /// <summary>
    /// Why a token failed verification. The categories exist so that operators can tell three
    /// different situations apart, because they look identical at the point of failure but call
    /// for completely different responses.
    /// </summary>
    public enum TokenVerificationFailure
    {
        /// <summary>The token verified successfully.</summary>
        None,

        /// <summary>
        /// The token is well-formed and correctly signed by a trusted key, but is past its
        /// expiration plus <see cref="JwtTokenValidator.TokenValidationClockSkew"/>. Routine.
        /// </summary>
        Expired,

        /// <summary>
        /// The token is well-formed and correctly signed, but names an issuer or audience this
        /// service does not accept. Far more often a deployment problem than an attack: an
        /// <c>Authority</c> or <c>Audience</c> typo makes every token in the environment fail
        /// this way at once. Kept separate from <see cref="Untrusted"/> so that a configuration
        /// mistake cannot bury genuine forgery signal under a storm of identical warnings.
        /// </summary>
        UntrustedIssuerOrAudience,

        /// <summary>
        /// The token could not be trusted on its own terms: unparseable, unknown or missing
        /// <c>kid</c>, or a bad signature. No amount of misconfiguration produces a validly
        /// signed token from a key this service does not hold, so treat as potential forgery.
        /// </summary>
        Untrusted,
    }

    /// <summary>
    /// The outcome of one verification attempt. <see cref="Token"/> is non-null exactly when
    /// <see cref="Failure"/> is <see cref="TokenVerificationFailure.None"/>.
    /// </summary>
    /// <param name="Token">The parsed token, or <c>null</c> when verification failed.</param>
    /// <param name="Failure">Why verification failed, or <c>None</c> on success.</param>
    /// <param name="Detail">
    /// Sanitized diagnostic text for the caller to log, empty on success. Already passed through
    /// <see cref="LoggingUtility.SanitizeForLog(string?)"/>, because it is built from exception
    /// messages that embed claim values taken from the token — attacker input on this path.
    /// </param>
    public sealed record TokenVerification(
        JwtSecurityToken? Token,
        TokenVerificationFailure Failure,
        string Detail
    );

    public static class JwtTokenValidator
    {
        /// <summary>
        /// Grace period accepted past a token's stated expiration to absorb clock drift.
        /// Anything that removes token state (such as the expired-token cleanup sweep) must
        /// keep rows until expiration plus this skew, or tokens the validator still accepts
        /// would fail their status lookup.
        /// </summary>
        public static readonly TimeSpan TokenValidationClockSkew = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Validates a JWT token using a dictionary of public keys, selecting the correct key by 'kid' in the JWT header.
        /// </summary>
        /// <param name="token">JWT token string</param>
        /// <param name="publicKeys">Dictionary of KeyId to SecurityKey</param>
        /// <param name="issuer">Expected issuer</param>
        /// <param name="audience">Expected audience</param>
        /// <returns>The parsed token on success, or the category and detail of the failure.</returns>
        /// <remarks>
        /// Deliberately silent: it categorizes failures but never logs them. Whoever calls this
        /// knows which operation was being attempted and logs once with that context, which is
        /// the only way the same failure does not appear twice at two severities.
        ///
        /// Failures are categorized rather than collapsed because they are operationally
        /// distinct — routine expiry, a deployment-wide Authority/Audience mismatch, and a
        /// genuinely untrusted token each call for a different response, and one shared
        /// category lets the first two bury the third.
        /// </remarks>
        public static TokenVerification ValidateToken(
            string token,
            IDictionary<string, SecurityKey> publicKeys,
            string issuer,
            string audience
        )
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var parsedToken = tokenHandler.ReadJwtToken(token);
                var kid = parsedToken.Header.TryGetValue("kid", out var kidObj) ? kidObj?.ToString() : null;
                if (string.IsNullOrEmpty(kid) || !publicKeys.TryGetValue(kid, out var signingKey))
                {
                    // No kid or key not found
                    return new TokenVerification(
                        null,
                        TokenVerificationFailure.Untrusted,
                        $"missing or unknown 'kid' header ({LoggingUtility.SanitizeForLog(kid)}); "
                            + $"available keys: {string.Join(", ", publicKeys.Keys)}"
                    );
                }
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TokenValidationClockSkew,
                };
                tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                return new TokenVerification(
                    validatedToken as JwtSecurityToken,
                    TokenVerificationFailure.None,
                    string.Empty
                );
            }
            catch (SecurityTokenExpiredException ex)
            {
                // Routine: the token was genuinely issued by this service and simply aged out.
                // A not-yet-valid ("nbf" in the future) token is deliberately NOT treated as
                // routine and falls through to the forgery-suspicious branch below.
                return new TokenVerification(
                    null,
                    TokenVerificationFailure.Expired,
                    LoggingUtility.SanitizeForLog(ex.Message)
                );
            }
            catch (Exception ex)
                when (ex is SecurityTokenInvalidIssuerException or SecurityTokenInvalidAudienceException)
            {
                // The signature already verified, so this token was minted by a key this service
                // trusts; only the issuer or audience is unacceptable. That is usually an
                // Authority/Audience misconfiguration, which fails every token at once, so it is
                // reported separately to keep the forgery bucket below meaningful.
                return new TokenVerification(
                    null,
                    TokenVerificationFailure.UntrustedIssuerOrAudience,
                    LoggingUtility.SanitizeForLog(ex.Message)
                );
            }
            catch (Exception ex)
            {
                // The type and stack trace are carried too, because this bucket is where an
                // unanticipated failure lands and the category alone would not identify it.
                return new TokenVerification(
                    null,
                    TokenVerificationFailure.Untrusted,
                    $"{LoggingUtility.SanitizeForLog(ex.GetType().Name)}, "
                        + $"{LoggingUtility.SanitizeForLog(ex.Message)}\n"
                        + LoggingUtility.SanitizeForLog(ex.StackTrace)
                );
            }
        }
    }
}
