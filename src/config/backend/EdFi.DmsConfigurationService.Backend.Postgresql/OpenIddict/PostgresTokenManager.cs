// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
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
    public class PostgresTokenManager : ITokenManager, ITokenRevocationManager
    {
        private readonly IOptions<DatabaseOptions> _databaseOptions;
        private readonly IOptions<IdentityOptions> _identityOptions;
        private readonly ILogger<PostgresTokenManager> _logger;
        private readonly IClientSecretHasher _secretHasher;

        // Cache for key formats with a maximum size limit to prevent unbounded growth
        private readonly ConcurrentDictionary<string, KeyFormat> _keyFormatCache = new ConcurrentDictionary<string, KeyFormat>();
        private readonly object _cacheLock = new object();

        // Key format enumeration to cache detected formats
        private enum KeyFormat
        {
            SubjectPublicKeyInfo,
            Pkcs1,
            Base64Encoded,
            Unknown
        }

        public PostgresTokenManager(
            IOptions<DatabaseOptions> databaseOptions,
            IOptions<IdentityOptions> identityOptions,
            ILogger<PostgresTokenManager> logger,
            IClientSecretHasher secretHasher)
        {
            _databaseOptions = databaseOptions;
            _identityOptions = identityOptions;
            _logger = logger;
            _secretHasher = secretHasher;
            _logger.LogInformation("PostgresTokenManager initialized");
        }

        /// <summary>
        /// Static helper to validate a JWT token and check its revocation status using a service provider.
        /// This allows easy integration into authentication middleware or endpoint handlers.
        /// </summary>
        public static async Task<bool> ValidateTokenWithRevocationAsync(string token, IServiceProvider serviceProvider)
        {
            var tokenManager = (PostgresTokenManager?)serviceProvider.GetService(typeof(PostgresTokenManager));
            if (tokenManager == null)
            {
                throw new InvalidOperationException("PostgresTokenManager is not registered in the service provider.");
            }
            return await tokenManager.ValidateTokenAsync(token);
        }

        /// <summary>
        /// Loads and decrypts the active private key from the database, returning a SecurityKey for JWT signing.
        /// </summary>
        private async Task<(SecurityKey, string)> LoadActiveSigningKey()
        {
            if (_identityOptions.Value.UseCertificates)
            {
                return await LoadActiveSigningKeyFromCertificatesAsync();
            }
            else
            {
                return await LoadActiveSigningKeyFromDatabaseAsync();
            }
        }

        /// <summary>
        /// Loads signing key from X.509 certificates (existing implementation)
        /// </summary>
        private async Task<(SecurityKey, string)> LoadActiveSigningKeyFromCertificatesAsync()
        {
            if (_identityOptions.Value.UseDevelopmentCertificates)
            {
                var certPath = _identityOptions.Value.DevCertificatePath;
                var certPassword = _identityOptions.Value.DevCertificatePassword;
                X509Certificate2 cert;
                if (!System.IO.File.Exists(certPath))
                {
                    using var rsa = RSA.Create(2048);
                    var certRequest = new CertificateRequest("CN=DevCert", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                    cert = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                    var bytes = cert.Export(X509ContentType.Pfx, certPassword);
                    await System.IO.File.WriteAllBytesAsync(certPath, bytes);
                }
                else
                {
                    cert = new X509Certificate2(certPath, certPassword);
                }
                var signingKey = new X509SecurityKey(cert);
                return await Task.FromResult((signingKey, cert.Thumbprint));
            }
            else
            {
                // Load certificate from configured path
                var certPath = _identityOptions.Value.CertificatePath;
                var certPassword = _identityOptions.Value.CertificatePassword;
                if (string.IsNullOrEmpty(certPath))
                {
                    throw new InvalidOperationException("CertificatePath must be set when not using development certificates.");
                }
                var cert = string.IsNullOrEmpty(certPassword)
                    ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath)
                    : new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, certPassword);
                var signingKey = new X509SecurityKey(cert);
                return await Task.FromResult((signingKey, cert.Thumbprint));
            }
        }

        /// <summary>
        /// Loads signing key from database (OpenIddictKey table)
        /// </summary>
        private async Task<(SecurityKey, string)> LoadActiveSigningKeyFromDatabaseAsync()
        {
            try
            {
                var encryptionKey = _identityOptions.Value.EncryptionKey;
                if (string.IsNullOrEmpty(encryptionKey))
                {
                    throw new InvalidOperationException("IdentitySettings:EncryptionKey must be set when using database keys.");
                }

                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                var query = "SELECT pgp_sym_decrypt(PrivateKey::bytea, @encryptionKey) AS PrivateKey, KeyId FROM dmscs.OpenIddictKey WHERE IsActive = TRUE ORDER BY CreatedAt DESC LIMIT 1";
                var keyRecord = await connection.QuerySingleOrDefaultAsync<(string PrivateKey, string KeyId)>(query, new { encryptionKey });
                if (string.IsNullOrEmpty(keyRecord.PrivateKey) || string.IsNullOrEmpty(keyRecord.KeyId))
                {
                    throw new InvalidOperationException("No active private key or key id found in OpenIddictKey table.");
                }
                var signingKey = JwtSigningKeyHelper.GenerateSigningKey(keyRecord.PrivateKey);
                return (signingKey, keyRecord.KeyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load private key from database");
                throw new InvalidOperationException("Failed to load private key from database. Please check the database connection, OpenIddictKey table, and encryption key.", ex);
            }
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

                // First, get the application by client ID to retrieve the stored (potentially hashed) secret
                string applicationSql =
                    @"SELECT a.Id, a.DisplayName, a.Permissions, a.ClientSecret,
                             array_agg(s.Name) as Scopes
                      FROM dmscs.OpenIddictApplication a
                      LEFT JOIN dmscs.OpenIddictApplicationScope aps ON a.Id = aps.ApplicationId
                      LEFT JOIN dmscs.OpenIddictScope s ON aps.ScopeId = s.Id
                      WHERE a.ClientId = @ClientId
                      GROUP BY a.Id, a.DisplayName, a.Permissions, a.ClientSecret";

                var applicationInfo = await connection.QuerySingleOrDefaultAsync<ApplicationInfo>(
                    applicationSql,
                    new { ClientId = clientId }
                );

                if (applicationInfo == null)
                {
                    _logger.LogWarning("Client not found: {ClientId}", clientId);
                    return new TokenResult.FailureIdentityProvider(
                            new IdentityProviderError.InvalidClient("Invalid client or Invalid client credentials"));
                }
                // Verify the client secret using the hasher (supports both hashed and plain text for backward compatibility)
                var isValidSecret = await _secretHasher.VerifySecretAsync(clientSecret, applicationInfo.ClientSecret ?? string.Empty);
                if (!isValidSecret)
                {
                    _logger.LogWarning("Invalid client secret for client: {ClientId}", clientId);
                    return new TokenResult.FailureIdentityProvider(
                           new IdentityProviderError.Unauthorized("Invalid client or Invalid client credentials"));
                }

                _logger.LogDebug("Application found: {ApplicationId}, Display Name: {DisplayName}",
                    applicationInfo.Id, applicationInfo.DisplayName);
                var listOfScopes = !string.IsNullOrEmpty(scope)
                    ? string.Join(",", scope)
                    : string.Join(",", applicationInfo.Permissions ?? new string[0]);
                // Generate JWT token
                var token = await GenerateJwtTokenAsync(
                    connection,
                    applicationInfo,
                    clientId,
                    listOfScopes
                );
                int tokenExpirationMinutes = _identityOptions.Value.TokenExpirationMinutes;
                // Calculate expires_in (seconds)
                var expiresIn = (tokenExpirationMinutes * 60);
                // Compose the response object
                var response = new
                {
                    access_token = token,
                    expires_in = expiresIn,
                    refresh_expires_in = 0,
                    token_type = "Bearer",
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
            string audience = _identityOptions.Value.Audience;
            string issuer = _identityOptions.Value.Authority;
            int tokenExpirationMinutes = _identityOptions.Value.TokenExpirationMinutes;
            var expiration = now.AddMinutes(tokenExpirationMinutes);
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
            var tokenString = JwtTokenGenerator.GenerateJwtToken(
                tokenId,
                clientId,
                applicationInfo.DisplayName,
                applicationInfo.Permissions,
                roles,
                scope ?? "",
                now,
                expiration,
                issuer,
                audience,
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
                (Id, ApplicationId, Subject, Type, CreationDate, ExpirationDate, Status, ReferenceId)
                VALUES
                (@Id, @ApplicationId, @Subject, @Type, @CreationDate, @ExpirationDate, @Status, @ReferenceId)";

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
        public async Task<bool> ValidateTokenAsync(string rawToken)
        {
            try
            {
                string audience = _identityOptions.Value.Audience;
                string issuer = _identityOptions.Value.Authority;
                var publicKeys = await GetPublicKeysAsync();
                var signingKeys = publicKeys
                    .ToDictionary(
                        k => k.KeyId,
                        k => (SecurityKey)new RsaSecurityKey(k.RsaParameters)
                    );
                if (!JwtTokenValidator.ValidateToken(
                        rawToken,
                        signingKeys,
                        issuer,
                        audience,
                        out var jwtToken,
                        _logger
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
        public async Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>> GetPublicKeysAsync()
        {
            if (_identityOptions.Value.UseCertificates)
            {
                return await GetPublicKeysFromCertificatesAsync();
            }
            else
            {
                return await GetPublicKeysFromDatabaseAsync();
            }
        }

        /// <summary>
        /// Gets public keys from X.509 certificates (existing implementation)
        /// </summary>
        private async Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>> GetPublicKeysFromCertificatesAsync()
        {
            if (_identityOptions.Value.UseDevelopmentCertificates)
            {
                var certPath = _identityOptions.Value.DevCertificatePath;
                var certPassword = _identityOptions.Value.DevCertificatePassword;
                X509Certificate2 cert;
                if (!System.IO.File.Exists(certPath))
                {
                    using var rsa = RSA.Create(2048);
                    var certRequest = new CertificateRequest("CN=DevCert", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                    cert = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                    var bytes = cert.Export(X509ContentType.Pfx, certPassword);
                    await System.IO.File.WriteAllBytesAsync(certPath, bytes);
                }
                else
                {
                    cert = new X509Certificate2(certPath, certPassword);
                }
                using var pubRsa = cert.GetRSAPublicKey();
                return await Task.FromResult(new[] { (pubRsa!.ExportParameters(false), cert.Thumbprint) });
            }
            else
            {
                var certPath = _identityOptions.Value.CertificatePath;
                var certPassword = _identityOptions.Value.CertificatePassword;
                if (string.IsNullOrEmpty(certPath))
                {
                    throw new InvalidOperationException("CertificatePath must be set when not using development certificates.");
                }
                var cert = string.IsNullOrEmpty(certPassword)
                    ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath)
                    : new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, certPassword);
                using var pubRsa = cert.GetRSAPublicKey();
                return await Task.FromResult(new[] { (pubRsa!.ExportParameters(false), cert.Thumbprint) });
            }
        }

        /// <summary>
        /// Gets public keys from database (OpenIddictKey table)
        /// </summary>
        private async Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>> GetPublicKeysFromDatabaseAsync()
        {
            var keys = new List<(RSAParameters, string)>();
            try
            {
                int maxCacheSize = _identityOptions.Value.KeyFormatCacheSize;
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                var keyRecords = await connection.QueryAsync<(string KeyId, byte[] PublicKey)>(
                    "SELECT KeyId, PublicKey FROM dmscs.OpenIddictKey WHERE IsActive = TRUE");
                foreach (var record in keyRecords)
                {
                    try
                    {
                        using var rsa = RSA.Create();

                        // Check if we've already determined the format for this key
                        KeyFormat keyFormat;

                        // Try to get from cache first
                        if (!_keyFormatCache.TryGetValue(record.KeyId, out keyFormat))
                        {
                            // Cache miss, detect format
                            keyFormat = DetectKeyFormat(record.PublicKey);

                            // Check cache size before adding
                            if (_keyFormatCache.Count >= maxCacheSize)
                            {
                                // Cache full, remove a random entry before adding new one
                                lock (_cacheLock)
                                {
                                    if (_keyFormatCache.Count >= maxCacheSize)
                                    {
                                        var keyToRemove = _keyFormatCache.Keys.FirstOrDefault();
                                        if (keyToRemove != null)
                                        {
                                            _keyFormatCache.TryRemove(keyToRemove, out _);
                                        }
                                    }
                                }
                            }

                            // Now add the new entry
                            _keyFormatCache.TryAdd(record.KeyId, keyFormat);
                        }

                        _logger.LogDebug("Key {KeyId} format detected as: {Format}", record.KeyId, keyFormat);

                        // Import the key using the detected format
                        switch (keyFormat)
                        {
                            case KeyFormat.SubjectPublicKeyInfo:
                                rsa.ImportSubjectPublicKeyInfo(record.PublicKey, out _);
                                break;

                            case KeyFormat.Pkcs1:
                                rsa.ImportRSAPublicKey(record.PublicKey, out _);
                                break;

                            case KeyFormat.Base64Encoded:
                                var publicKeyString = System.Text.Encoding.UTF8.GetString(record.PublicKey);
                                var decodedKey = Convert.FromBase64String(publicKeyString);
                                rsa.ImportSubjectPublicKeyInfo(decodedKey, out _);
                                break;

                            default:
                                _logger.LogWarning("Unknown key format for key ID: {KeyId}", record.KeyId);
                                continue; // Skip this key
                        }

                        keys.Add((rsa.ExportParameters(false), record.KeyId));
                        _logger.LogInformation(
                            "Successfully loaded public key with ID: {KeyId}",
                            record.KeyId
                        );
                    }
                    catch (Exception keyEx)
                    {
                        _logger.LogError(keyEx, "Failed to process key with ID {KeyId}", record.KeyId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch public keys for JWKS");
            }
            return keys;
        }

        /// <summary>
        /// Detects the format of a public key
        /// </summary>
        private KeyFormat DetectKeyFormat(byte[] keyData)
        {
            try
            {
                // Try importing as SubjectPublicKeyInfo (X.509) format
                using (var rsa = RSA.Create())
                {
                    try
                    {
                        rsa.ImportSubjectPublicKeyInfo(keyData, out _);
                        return KeyFormat.SubjectPublicKeyInfo;
                    }
                    catch
                    {
                        // Not in SPKI format, continue to next check
                    }

                    // Try importing as PKCS#1 format
                    try
                    {
                        rsa.ImportRSAPublicKey(keyData, out _);
                        return KeyFormat.Pkcs1;
                    }
                    catch
                    {
                        // Not in PKCS#1 format, continue to next check
                    }

                    // Try as Base64 encoded string
                    try
                    {
                        var publicKeyString = System.Text.Encoding.UTF8.GetString(keyData);
                        var decodedKey = Convert.FromBase64String(publicKeyString);
                        rsa.ImportSubjectPublicKeyInfo(decodedKey, out _);
                        return KeyFormat.Base64Encoded;
                    }
                    catch
                    {
                        // Not a Base64 encoded string
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while detecting key format");
            }

            return KeyFormat.Unknown;
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
        public string? ClientSecret { get; set; }
    }

    /// <summary>
    /// Database key information retrieved from OpenIddictKey table
    /// </summary>
    public class DatabaseKeyInfo
    {
        public string KeyId { get; set; } = string.Empty;
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Database private key information retrieved from OpenIddictKey table
    /// </summary>
    public class DatabasePrivateKeyInfo
    {
        public string KeyId { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
    }
}
