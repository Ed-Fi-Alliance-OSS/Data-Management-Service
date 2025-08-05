// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    /// <summary>
    /// PostgreSQL implementation of ITokenManager that generates and validates JWT tokens using OpenIddict standards.
    /// Stores tokens in dmscs.openiddict_token table and includes custom scope claims.
    /// </summary>
    public class PostgresTokenManager : ITokenManager //, ITokenRevocationManager, ITokenValidationManager
    {
        private readonly IOptions<DatabaseOptions> _databaseOptions;
        private readonly ILogger<PostgresTokenManager> _logger;
        private readonly JwtSettings _jwtSettings;
        private readonly SecurityKey _signingKey;

        public PostgresTokenManager(
            IOptions<DatabaseOptions> databaseOptions,
            ILogger<PostgresTokenManager> logger)
        {
            _databaseOptions = databaseOptions;
            _logger = logger;
            _jwtSettings = new JwtSettings();
            _signingKey = GenerateSigningKey();

            _logger.LogInformation("PostgresTokenManager initialized with JWT settings - Issuer: {Issuer}, Audience: {Audience}",
                _jwtSettings.Issuer, _jwtSettings.Audience);
        }

        public async Task<TokenResult> GetAccessTokenAsync(
            IEnumerable<KeyValuePair<string, string>> credentials)
        {
            try
            {
                string? clientId = null;
                string? clientSecret = null;
                string? scope = null;

                foreach (var kvp in credentials)
                {
                    if (kvp.Key.Equals("client_id", StringComparison.OrdinalIgnoreCase))
                    {
                        clientId = kvp.Value;
                    }
                    else if (kvp.Key.Equals("client_secret", StringComparison.OrdinalIgnoreCase))
                    {
                        clientSecret = kvp.Value;
                    }
                    else if (kvp.Key.Equals("scope", StringComparison.OrdinalIgnoreCase))
                    {
                        scope = kvp.Value;
                    }
                }

                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                {
                    _logger.LogWarning("Missing client credentials in token request");
                    return new TokenResult.FailureUnknown("Missing client_id or client_secret");
                }

                _logger.LogDebug("Attempting to generate token for client: {ClientId}", clientId);

                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();

                // Validate client credentials and get application details
                string applicationSql =
                    @"SELECT a.id, a.display_name, a.permissions,
                             array_agg(s.name) as scopes
                      FROM dmscs.openiddict_application a
                      LEFT JOIN dmscs.openiddict_application_scope aps ON a.id = aps.application_id
                      LEFT JOIN dmscs.openiddict_scope s ON aps.scope_id = s.id
                      WHERE a.client_id = @ClientId AND a.client_secret = @ClientSecret
                      GROUP BY a.id, a.display_name, a.permissions";

                var applicationInfo = await connection.QuerySingleOrDefaultAsync<ApplicationInfo>(
                    applicationSql,
                    new { ClientId = clientId, ClientSecret = clientSecret }
                );

                if (applicationInfo == null)
                {
                    _logger.LogWarning("Invalid client credentials for client: {ClientId}", clientId);
                    return new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.Unauthorized("Invalid client credentials")
                    );
                }

                _logger.LogDebug("Application found: {ApplicationId}, Display Name: {DisplayName}",
                    applicationInfo.Id, applicationInfo.DisplayName);

                // Generate JWT token
                var token = await GenerateJwtTokenAsync(
                    connection,
                    applicationInfo,
                    clientId,
                    scope ?? string.Join(",", applicationInfo.Scopes ?? new string[0])
                );

                // Calculate expires_in (seconds)
                var expiresIn = (_jwtSettings.ExpirationHours * 3600);
                // Compose the response object
                var response = new
                {
                    access_token = token,
                    expires_in = expiresIn,
                    refresh_expires_in = 0,
                    token_type = "Bearer",
                    // ["not-before-policy"] = 0,
                    scope = scope ?? string.Join(",", applicationInfo.Scopes ?? new string[0])
                };

                // Return as JSON string
                var json = System.Text.Json.JsonSerializer.Serialize(response);
                return new TokenResult.Success(json);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "Database error while retrieving access token");
                return new TokenResult.FailureIdentityProvider(
                    new IdentityProviderError.Unreachable(ex.Message)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unknown error while retrieving access token");
                return new TokenResult.FailureUnknown(ex.Message);
            }
        }

        private async Task<string> GenerateJwtTokenAsync(
            NpgsqlConnection connection,
            ApplicationInfo applicationInfo,
            string clientId,
            string scope)
        {
            var tokenId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var expiration = now.AddHours(_jwtSettings.ExpirationHours);

            // Create claims
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Jti, tokenId.ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, clientId),
                new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim(JwtRegisteredClaimNames.Exp, expiration.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim("client_id", clientId),
                new Claim("token_type", "access_token")
            };

            // Add display name if available
            if (!string.IsNullOrEmpty(applicationInfo.DisplayName))
            {
                claims.Add(new Claim("client_name", applicationInfo.DisplayName));
            }

            // Add scope claims
            var scopes = scope?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
            foreach (var s in scopes)
            {
                claims.Add(new Claim("scope", s));
            }

            // Add custom scope array claim
            if (scopes.Length > 0)
            {
                claims.Add(new Claim("uri://myuri.org/scopes", JsonSerializer.Serialize(scopes)));
            }

            // Add permissions if available
            if (applicationInfo.Permissions != null && applicationInfo.Permissions.Length > 0)
            {
                foreach (var permission in applicationInfo.Permissions)
                {
                    claims.Add(new Claim("permission", permission));
                }
            }

            // Create JWT token with validation
            _logger.LogDebug("Creating JWT token descriptor with {ClaimCount} claims", claims.Count);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = expiration.DateTime,
                SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            SecurityToken securityToken;

            try
            {
                securityToken = tokenHandler.CreateToken(tokenDescriptor);
                _logger.LogDebug("SecurityToken created successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create SecurityToken");
                throw new InvalidOperationException("Failed to create JWT SecurityToken", ex);
            }

            string tokenString;
            try
            {
                tokenString = tokenHandler.WriteToken(securityToken);
                _logger.LogDebug("Token written successfully, length: {Length}", tokenString?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write token to string");
                throw new InvalidOperationException("Failed to serialize JWT token to string", ex);
            }

            // Validate the generated token format
            if (string.IsNullOrEmpty(tokenString))
            {
                _logger.LogError("Generated JWT token is null or empty");
                throw new InvalidOperationException("JWT token generation failed - token is null or empty");
            }

            if (!tokenString.Contains('.'))
            {
                _logger.LogError("Generated JWT token does not contain dots. Token: {Token}", tokenString);
                throw new InvalidOperationException("JWT token generation failed - token does not contain required separators");
            }

            var tokenParts = tokenString.Split('.');
            if (tokenParts.Length != 3)
            {
                _logger.LogError("Generated JWT token has {PartCount} parts instead of 3. Token: {Token}",
                    tokenParts.Length, tokenString);
                throw new InvalidOperationException($"Generated JWT token is not in the correct format. Expected 3 parts (header.payload.signature), got {tokenParts.Length} parts");
            }

            _logger.LogInformation("Successfully generated JWT token with {HeaderLength}/{PayloadLength}/{SignatureLength} character parts",
                tokenParts[0].Length, tokenParts[1].Length, tokenParts[2].Length);

            // Store token in database
            await StoreTokenInDatabaseAsync(connection, tokenId, applicationInfo.Id, clientId, tokenString, expiration);

            return tokenString;
        }

        private async Task StoreTokenInDatabaseAsync(
            NpgsqlConnection connection,
            Guid tokenId,
            Guid applicationId,
            string subject,
            string payload,
            DateTimeOffset expiration)
        {
            string insertSql = @"
                INSERT INTO dmscs.openiddict_token
                (id, application_id, subject, type, payload, creation_date, expiration_date, status, reference_id)
                VALUES
                (@Id, @ApplicationId, @Subject, @Type, @Payload, @CreationDate, @ExpirationDate, @Status, @ReferenceId)";

            await connection.ExecuteAsync(insertSql, new
            {
                Id = tokenId,
                ApplicationId = applicationId,
                Subject = subject,
                Type = "access_token",
                Payload = payload,
                CreationDate = DateTimeOffset.UtcNow,
                ExpirationDate = expiration,
                Status = "valid",
                ReferenceId = tokenId.ToString("N")
            });

            _logger.LogInformation("JWT token stored in database with ID: {TokenId}", tokenId);
        }

        private static SecurityKey GenerateSigningKey()
        {
            // Retrieve the key from a secure configuration source or key vault
            var key = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY") ?? "YourSecretKeyForJWTWhichMustBeLongEnoughForSecurity123456789";

            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("JWT signing key is not configured.");
            }

            // Ensure key is long enough for HMAC-SHA256 (at least 256 bits / 32 bytes)
            var keyBytes = Encoding.UTF8.GetBytes(key);
            if (keyBytes.Length < 32)
            {
                throw new InvalidOperationException($"JWT signing key must be at least 32 bytes long. Current length: {keyBytes.Length}");
            }

            return new SymmetricSecurityKey(keyBytes);
        }

        /// <summary>
        /// Validates a JWT token and checks its status in the database
        /// </summary>
        public async Task<bool> ValidateTokenAsync(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = _signingKey,
                    ValidateIssuer = true,
                    ValidIssuer = _jwtSettings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = _jwtSettings.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                // Validate the token
                tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

                // Check token status in database
                var jwtToken = validatedToken as JwtSecurityToken;
                var jti = jwtToken?.Claims?.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;

                if (!string.IsNullOrEmpty(jti))
                {
                    await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                    await connection.OpenAsync();

                    var status = await connection.QuerySingleOrDefaultAsync<string>(
                        "SELECT status FROM dmscs.openiddict_token WHERE id = @Id",
                        new { Id = Guid.Parse(jti) }
                    );

                    return status == "valid";
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token validation failed");
                return false;
            }
        }

        /// <summary>
        /// Revokes a token by setting its status to 'revoked'
        /// </summary>
        public async Task<bool> RevokeTokenAsync(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var jwtToken = tokenHandler.ReadJwtToken(token);
                var jti = jwtToken.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;

                if (!string.IsNullOrEmpty(jti))
                {
                    await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                    await connection.OpenAsync();

                    var result = await connection.ExecuteAsync(
                        "UPDATE dmscs.openiddict_token SET status = 'revoked', redemption_date = CURRENT_TIMESTAMP WHERE id = @Id",
                        new { Id = Guid.Parse(jti) }
                    );

                    return result > 0;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke token");
                return false;
            }
        }
    }

    /// <summary>
    /// Application information retrieved from database
    /// </summary>
    public class ApplicationInfo
    {
        public Guid Id { get; set; }
        public string? DisplayName { get; set; }
        public string[]? Permissions { get; set; }
        public string[]? Scopes { get; set; }
    }
}
