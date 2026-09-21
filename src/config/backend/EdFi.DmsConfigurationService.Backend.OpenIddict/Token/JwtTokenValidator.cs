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
    /// Why a token failed verification. The distinction exists so that callers and operators can
    /// separate routine occurrences from security signal: clients present expired tokens as a
    /// matter of course, whereas a bad signature, issuer or audience is the shape of an attempted
    /// forgery and is worth alerting on.
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
        /// The token could not be trusted: unparseable, unknown or missing <c>kid</c>, bad
        /// signature, wrong issuer, or wrong audience. Treat as potential forgery.
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
        /// Failure logging is split by severity on purpose. An expired token is logged at Debug
        /// because any client will produce one eventually; everything else is logged at Warning
        /// because it indicates a token that was not issued by this service. Emitting both at the
        /// same level buries the second in the first.
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
                logger?.LogDebug(
                    "JWT token validated successfully. Issuer: {Issuer}, Audience: {Audience}, Subject: {Subject}",
                    jwtToken?.Issuer,
                    jwtToken?.Audiences?.FirstOrDefault(),
                    jwtToken?.Subject
                );
                return true;
            }
            catch (SecurityTokenExpiredException ex)
            {
                // Routine: the token was genuinely issued by this service and simply aged out.
                // A not-yet-valid ("nbf" in the future) token is deliberately NOT treated as
                // routine and falls through to the Warning branch below.
                failure = TokenVerificationFailure.Expired;
                logger?.LogDebug(ex, "JWT token rejected as expired: {ErrorMessage}", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                failure = TokenVerificationFailure.Untrusted;
                logger?.LogWarning(ex, "JWT token validation failed: {ErrorMessage}", ex.Message);
                return false;
            }
        }
    }
}
