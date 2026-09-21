// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.IdentityModel.Tokens.Jwt;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;
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
        /// <param name="jwtToken">Out: parsed JwtSecurityToken</param>
        /// <param name="failure">Out: why verification failed, or <c>None</c> on success</param>
        /// <param name="logger">Optional logger for diagnostic information</param>
        /// <returns>True if valid, false otherwise</returns>
        /// <remarks>
        /// Failures are reported in three categories on purpose, because they are operationally
        /// distinct. An expired token is logged at Debug, since any client will produce one
        /// eventually. An issuer or audience mismatch is logged at Warning against its own
        /// message, since it usually means the deployment's Authority/Audience is wrong and will
        /// affect every token at once. Everything else — unparseable, unknown <c>kid</c>, bad
        /// signature — is logged at Warning as potential forgery. Collapsing these into one
        /// level and message lets routine expiry and misconfiguration bury real signal.
        /// </remarks>
        public static bool ValidateToken(
            string token,
            IDictionary<string, SecurityKey> publicKeys,
            string issuer,
            string audience,
            out JwtSecurityToken? jwtToken,
            out TokenVerificationFailure failure,
            ILogger? logger = null
        )
        {
            jwtToken = null;
            failure = TokenVerificationFailure.None;
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var parsedToken = tokenHandler.ReadJwtToken(token);
                var kid = parsedToken.Header.TryGetValue("kid", out var kidObj) ? kidObj?.ToString() : null;
                if (string.IsNullOrEmpty(kid) || !publicKeys.TryGetValue(kid, out var signingKey))
                {
                    // No kid or key not found
                    failure = TokenVerificationFailure.Untrusted;
                    logger?.LogWarning(
                        "JWT validation failed: Missing or invalid 'kid' header. Kid: {Kid}, Available keys: {AvailableKeys}",
                        LoggingUtility.SanitizeForLog(kid),
                        string.Join(", ", publicKeys.Keys)
                    );
                    return false;
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
                jwtToken = validatedToken as JwtSecurityToken;
                // Issuer and audience are logged unsanitized deliberately: validation has just
                // proved them equal to the configured ValidIssuer/ValidAudience, so what is
                // written is a server-controlled configuration value, not caller input. Subject
                // is different — nothing constrains it to a known value, and it originates from
                // a client id supplied at registration — so it is sanitized.
                logger?.LogDebug(
                    "JWT token validated successfully. Issuer: {Issuer}, Audience: {Audience}, Subject: {Subject}",
                    jwtToken?.Issuer,
                    jwtToken?.Audiences?.FirstOrDefault(),
                    LoggingUtility.SanitizeForLog(jwtToken?.Subject)
                );
                return true;
            }
            catch (SecurityTokenExpiredException ex)
            {
                // Routine: the token was genuinely issued by this service and simply aged out.
                // A not-yet-valid ("nbf" in the future) token is deliberately NOT treated as
                // routine and falls through to the forgery-suspicious branch below.
                failure = TokenVerificationFailure.Expired;
                logger?.LogDebug(
                    ex,
                    "JWT token rejected as expired: {ErrorMessage}",
                    LoggingUtility.SanitizeForLog(ex.Message)
                );
                return false;
            }
            catch (Exception ex)
                when (ex is SecurityTokenInvalidIssuerException or SecurityTokenInvalidAudienceException)
            {
                // The signature already verified, so this token was minted by a key this service
                // trusts; only the issuer or audience is unacceptable. That is usually an
                // Authority/Audience misconfiguration, which fails every token at once, so it is
                // reported separately to keep the forgery bucket below meaningful.
                failure = TokenVerificationFailure.UntrustedIssuerOrAudience;
                logger?.LogWarning(
                    ex,
                    "JWT token rejected: issuer or audience not accepted. Check the configured "
                        + "Authority and Audience if this affects every token. {ErrorMessage}",
                    LoggingUtility.SanitizeForLog(ex.Message)
                );
                return false;
            }
            catch (Exception ex)
            {
                // Exception messages embed claim values taken from the token, which is attacker
                // input on exactly this path, so they are sanitized before logging.
                failure = TokenVerificationFailure.Untrusted;
                logger?.LogWarning(
                    ex,
                    "JWT token validation failed: {ErrorMessage}",
                    LoggingUtility.SanitizeForLog(ex.Message)
                );
                return false;
            }
        }
    }
}
