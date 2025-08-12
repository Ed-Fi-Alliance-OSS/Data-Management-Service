// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using Microsoft.Extensions.Configuration;
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
    public class PostgresTokenManager : ITokenManager
    {
        private readonly IOptions<DatabaseOptions> _databaseOptions;
        private readonly ILogger<PostgresTokenManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly JwtSettings _jwtSettings;

        public PostgresTokenManager(
            IOptions<DatabaseOptions> databaseOptions,
            ILogger<PostgresTokenManager> logger,
            IConfiguration configuration)
        {
            _databaseOptions = databaseOptions;
            _logger = logger;
            _configuration = configuration;
            _jwtSettings = new JwtSettings();
            _jwtSettings = EdFi.DmsConfigurationService.Backend.OpenIddict.Token.JwtTokenGenerator.GetJwtSettings(configuration);
            _logger.LogInformation("PostgresTokenManager initialized with JWT settings - Issuer: {Issuer}, Audience: {Audience}",
                _jwtSettings.Issuer, _jwtSettings.Audience);
        }

        /// <summary>
        /// Loads and decrypts the active private key from the database, returning a SecurityKey for JWT signing.
        /// </summary>
        private async Task<(SecurityKey, string)> LoadActiveSigningKey()
        {
            await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync();
            // Fetch and decrypt the private key using pgcrypto
            // Get encryption key from IdentitySettings section in configuration
            var encryptionKey = _configuration.GetValue<string>("IdentitySettings:EncryptionKey") ?? string.Empty;
            if (string.IsNullOrEmpty(encryptionKey))
            {
                throw new InvalidOperationException("No active EncryptionKey found.");
            }
            var keyRecord = await connection.QuerySingleOrDefaultAsync<(string PrivateKey, string KeyId)>(
                "SELECT pgp_sym_decrypt(PrivateKey::bytea, @EncryptionKey) AS PrivateKey, KeyId FROM dmscs.OpenIddictKey WHERE IsActive = TRUE ORDER BY CreatedAt DESC LIMIT 1",
                new { EncryptionKey = encryptionKey });
            if (string.IsNullOrEmpty(keyRecord.PrivateKey) || string.IsNullOrEmpty(keyRecord.KeyId))
            {
                throw new InvalidOperationException("No active private key or key id found in OpenIddictKey table.");
            }
            var signingKey = JwtSigningKeyHelper.GenerateSigningKey(keyRecord.PrivateKey);
            return (signingKey, keyRecord.KeyId);
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
                    @"SELECT a.Id, a.DisplayName, a.Permissions,
                             array_agg(s.Name) as Scopes
                      FROM dmscs.OpenIddictApplication a
                      LEFT JOIN dmscs.OpenIddictApplicationScope aps ON a.Id = aps.ApplicationId
                      LEFT JOIN dmscs.OpenIddictScope s ON aps.ScopeId = s.Id
                      WHERE a.ClientId = @ClientId AND a.ClientSecret = @ClientSecret
                      GROUP BY a.Id, a.DisplayName, a.Permissions";

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
            var (signingKey, keyId) = await LoadActiveSigningKey();
            // Prepare roles from OpenIddictClientRole/OpenIddictRole tables
            var roles = (await connection.QueryAsync<string>(
                    @"SELECT r.Name
                      FROM dmscs.OpenIddictClientRole cr
                      JOIN dmscs.OpenIddictRole r ON cr.RoleId = r.Id
                      WHERE cr.ClientId = @ClientId",
                    new { ClientId = applicationInfo.Id }
                )
            ).ToArray();

            // Use shared JwtTokenGenerator
            var tokenString = EdFi.DmsConfigurationService.Backend.OpenIddict.Token.JwtTokenGenerator.GenerateJwtToken(
                tokenId,
                clientId,
                applicationInfo.DisplayName,
                applicationInfo.Permissions,
                roles,
                scope ?? "",
                now,
                expiration,
                _jwtSettings.Issuer,
                _jwtSettings.Audience,
                signingKey,
                keyId
            );

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
                INSERT INTO dmscs.OpenIddictToken
                (Id, ApplicationId, Subject, Type, Payload, CreationDate, ExpirationDate, Status, ReferenceId)
                VALUES
                (@Id, @ApplicationId, @Subject, @Type, @Payload, @CreationDate, @ExpirationDate, @Status, @ReferenceId)";

            await connection.ExecuteAsync(
                insertSql,
                new
                {
                    Id = tokenId,
                    ApplicationId = applicationId,
                    Subject = subject,
                    Type = "access_token",
                    Payload = payload,
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = expiration,
                    Status = "valid",
                    ReferenceId = tokenId.ToString("N"),
                }
            );

            _logger.LogInformation("JWT token stored in database with ID: {TokenId}", tokenId);
        }

        // Signing key logic moved to JwtSigningKeyHelper

        /// <summary>
        /// Validates a JWT token and checks its status in the database
        /// </summary>
        public async Task<bool> ValidateTokenAsync(string token)
        {
            try
            {
                var signingKey = GetPublicKeys();
                if (!EdFi.DmsConfigurationService.Backend.OpenIddict.Token.JwtTokenValidator.ValidateToken(
                        token,
                        (IDictionary<string, SecurityKey>)signingKey,
                        _jwtSettings.Issuer,
                        _jwtSettings.Audience,
                        out var jwtToken
                    )
                )
                {
                    _logger.LogWarning("Token validation failed (signature, issuer, audience, or lifetime)");
                    return false;
                }

                // Check token status in database
                var jti = jwtToken?.Claims?.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;
                if (!string.IsNullOrEmpty(jti))
                {
                    await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                    await connection.OpenAsync();
                    var status = await connection.QuerySingleOrDefaultAsync<string>(
                        "SELECT Status FROM dmscs.OpenIddictToken WHERE Id = @Id",
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
                        "UPDATE dmscs.OpenIddictToken SET Status = 'revoked', RedemptionDate = CURRENT_TIMESTAMP WHERE Id = @Id",
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
        /// <summary>
        /// Returns all active public keys for JWKS endpoint
        /// </summary>
        public IEnumerable<(RSAParameters RsaParameters, string KeyId)> GetPublicKeys()
        {
            var keys = new List<(RSAParameters, string)>();
            try
            {
                using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                connection.Open();
                var keyRecords = connection.Query<(string KeyId, string PublicKey)>(
                    "SELECT KeyId, PublicKey FROM dmscs.OpenIddictKey WHERE IsActive = TRUE");
                foreach (var record in keyRecords)
                {
                    var keyBytes = Convert.FromBase64String(record.PublicKey);
                    using var rsa = RSA.Create();
                    rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
                    keys.Add((rsa.ExportParameters(false), record.KeyId));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch public keys for JWKS");
            }
            return keys;
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
