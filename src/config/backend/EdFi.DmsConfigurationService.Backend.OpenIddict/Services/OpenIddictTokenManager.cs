// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services
{
    /// <summary>
    /// Database-agnostic implementation of ITokenManager that generates and validates JWT tokens using OpenIddict standards.
    /// Uses IOpenIddictTokenRepository for database operations to support multiple database providers.
    /// </summary>
    public class OpenIddictTokenManager(
        IOptions<IdentityOptions> identityOptions,
        ILogger<OpenIddictTokenManager> logger,
        IClientSecretHasher secretHasher,
        IOpenIddictTokenRepository tokenRepository
    ) : ITokenManager, ITokenRevocationManager
    {
        private readonly IOptions<IdentityOptions> _identityOptions = identityOptions;
        private readonly ILogger<OpenIddictTokenManager> _logger = logger;
        private readonly IClientSecretHasher _secretHasher = secretHasher;
        private readonly IOpenIddictTokenRepository _tokenRepository = tokenRepository;

        // Cache for key formats with a maximum size limit to prevent unbounded growth
        private readonly ConcurrentDictionary<string, KeyFormat> _keyFormatCache = new();
        private readonly object _cacheLock = new object();

        // Key format enumeration to cache detected formats
        private enum KeyFormat
        {
            SubjectPublicKeyInfo,
            Pkcs1,
            Base64Encoded,
            Unknown,
        }

        /// <summary>
        /// Static helper to validate a JWT token and check its revocation status using a service provider.
        /// This allows easy integration into authentication middleware or endpoint handlers.
        /// </summary>
        public static async Task<bool> ValidateTokenWithRevocationAsync(
            string token,
            IServiceProvider serviceProvider
        )
        {
            var tokenManager = serviceProvider.GetService<OpenIddictTokenManager>();
            if (tokenManager == null)
            {
                throw new InvalidOperationException(
                    "OpenIddictTokenManager is not registered in the service provider."
                );
            }
            return await tokenManager.ValidateTokenAsync(token);
        }

        /// <summary>
        /// Loads and decrypts the active private key from the database, returning a SecurityKey for JWT signing.
        /// </summary>
        private async Task<SigningKeyResult> LoadActiveSigningKey()
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
        private async Task<SigningKeyResult> LoadActiveSigningKeyFromCertificatesAsync()
        {
            if (_identityOptions.Value.UseDevelopmentCertificates)
            {
                var certPath = _identityOptions.Value.DevCertificatePath;
                var certPassword = _identityOptions.Value.DevCertificatePassword;
                X509Certificate2 cert;
                if (!System.IO.File.Exists(certPath))
                {
                    using var rsa = RSA.Create(2048);
                    var certRequest = new CertificateRequest(
                        "CN=DevCert",
                        rsa,
                        System.Security.Cryptography.HashAlgorithmName.SHA256,
                        System.Security.Cryptography.RSASignaturePadding.Pkcs1
                    );
                    cert = certRequest.CreateSelfSigned(
                        DateTimeOffset.UtcNow.AddDays(-1),
                        DateTimeOffset.UtcNow.AddYears(1)
                    );
                    var bytes = cert.Export(X509ContentType.Pfx, certPassword);
                    await System.IO.File.WriteAllBytesAsync(certPath, bytes);
                }
                else
                {
                    cert = new X509Certificate2(certPath, certPassword);
                }
                var signingKey = new X509SecurityKey(cert);
                return await Task.FromResult(
                    new SigningKeyResult { SecurityKey = signingKey, KeyId = cert.Thumbprint }
                );
            }
            else
            {
                // Load certificate from configured path
                var certPath = _identityOptions.Value.CertificatePath;
                var certPassword = _identityOptions.Value.CertificatePassword;
                if (string.IsNullOrEmpty(certPath))
                {
                    throw new InvalidOperationException(
                        "CertificatePath must be set when not using development certificates."
                    );
                }
                var cert = string.IsNullOrEmpty(certPassword)
                    ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath)
                    : new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        certPath,
                        certPassword
                    );
                var signingKey = new X509SecurityKey(cert);
                return await Task.FromResult(
                    new SigningKeyResult { SecurityKey = signingKey, KeyId = cert.Thumbprint }
                );
            }
        }

        /// <summary>
        /// Loads signing key from database (OpenIddictKey table)
        /// </summary>
        private async Task<SigningKeyResult> LoadActiveSigningKeyFromDatabaseAsync()
        {
            try
            {
                var encryptionKey = _identityOptions.Value.EncryptionKey;
                if (string.IsNullOrEmpty(encryptionKey))
                {
                    throw new InvalidOperationException(
                        "IdentitySettings:EncryptionKey must be set when using database keys."
                    );
                }

                var keyRecord = await _tokenRepository.GetActivePrivateKeyAsync(encryptionKey);
                if (keyRecord == null)
                {
                    throw new InvalidOperationException(
                        "No active private key or key id found in OpenIddictKey table."
                    );
                }

                var signingKey = JwtSigningKeyHelper.GenerateSigningKey(keyRecord.PrivateKey);
                return new SigningKeyResult { SecurityKey = signingKey, KeyId = keyRecord.KeyId };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load private key from database");
                throw new InvalidOperationException(
                    "Failed to load private key from database. Please check the database connection, OpenIddictKey table, and encryption key.",
                    ex
                );
            }
        }

        public async Task<TokenResult> GetAccessTokenAsync(
            IEnumerable<KeyValuePair<string, string>> credentials
        )
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

                // First, get the application by client ID to retrieve the stored (potentially hashed) secret
                var applicationInfo = await _tokenRepository.GetApplicationByClientIdAsync(clientId);

                if (applicationInfo == null)
                {
                    return new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.InvalidClient(
                            "Invalid client or Invalid client credentials"
                        )
                    );
                }
                // Verify the client secret using the hasher
                var isValidSecret = await _secretHasher.VerifySecretAsync(
                    clientSecret,
                    applicationInfo.ClientSecret ?? string.Empty
                );
                if (!isValidSecret)
                {
                    return new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.Unauthorized("Invalid client or Invalid client credentials")
                    );
                }

                _logger.LogDebug(
                    "Application found: {ApplicationId}, Display Name: {DisplayName}",
                    applicationInfo.Id,
                    applicationInfo.DisplayName
                );
                var listOfScopes = !string.IsNullOrEmpty(scope)
                    ? string.Join(",", scope)
                    : string.Join(",", applicationInfo.Permissions ?? new string[0]);

                // Generate JWT token
                var token = await GenerateJwtTokenAsync(applicationInfo, clientId, listOfScopes);
                int tokenExpirationMinutes = _identityOptions.Value.TokenExpirationMinutes;
                // Calculate expires_in (seconds)
                var expiresIn = tokenExpirationMinutes * 60;
                // Compose the response object
                var response = new
                {
                    access_token = token,
                    expires_in = expiresIn,
                    refresh_expires_in = 0,
                    token_type = "Bearer",
                    scope = listOfScopes,
                };

                // Return as JSON string
                var json = System.Text.Json.JsonSerializer.Serialize(response);
                return new TokenResult.Success(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unknown error while retrieving access token");
                return new TokenResult.FailureUnknown(ex.Message);
            }
        }

        private async Task<string> GenerateJwtTokenAsync(
            ApplicationInfo applicationInfo,
            string clientId,
            string scope
        )
        {
            var tokenId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            string audience = _identityOptions.Value.Audience;
            string issuer = _identityOptions.Value.Authority;
            int tokenExpirationMinutes = _identityOptions.Value.TokenExpirationMinutes;
            var expiration = now.AddMinutes(tokenExpirationMinutes);
            var signingKeyResult = await LoadActiveSigningKey();

            // Prepare roles from OpenIddictClientRole/OpenIddictRole tables
            var roles = (await _tokenRepository.GetClientRolesAsync(applicationInfo.Id)).ToArray();

            // Use shared JwtTokenGenerator
            var tokenString = JwtTokenGenerator.GenerateJwtToken(
                tokenId,
                clientId,
                applicationInfo.DisplayName,
                applicationInfo.Permissions,
                roles,
                scope ?? "",
                applicationInfo.ProtocolMappers ?? "[]",
                now,
                expiration,
                issuer,
                audience,
                signingKeyResult.SecurityKey,
                signingKeyResult.KeyId
            );

            // Store token in database
            await _tokenRepository.StoreTokenAsync(
                tokenId,
                applicationInfo.Id,
                clientId,
                tokenString,
                expiration
            );

            return tokenString;
        }

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
                var signingKeys = publicKeys.ToDictionary(
                    k => k.KeyId,
                    k => (SecurityKey)new RsaSecurityKey(k.RsaParameters)
                );
                if (
                    !JwtTokenValidator.ValidateToken(
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

                // Check token status in repository
                var jti = jwtToken?.Claims?.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;
                if (!string.IsNullOrEmpty(jti))
                {
                    var status = await _tokenRepository.GetTokenStatusAsync(Guid.Parse(jti));
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
                    return await _tokenRepository.RevokeTokenAsync(Guid.Parse(jti));
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
        private async Task<
            IEnumerable<(RSAParameters RsaParameters, string KeyId)>
        > GetPublicKeysFromCertificatesAsync()
        {
            if (_identityOptions.Value.UseDevelopmentCertificates)
            {
                var certPath = _identityOptions.Value.DevCertificatePath;
                var certPassword = _identityOptions.Value.DevCertificatePassword;
                X509Certificate2 cert;
                if (!System.IO.File.Exists(certPath))
                {
                    using var rsa = RSA.Create(2048);
                    var certRequest = new CertificateRequest(
                        "CN=DevCert",
                        rsa,
                        System.Security.Cryptography.HashAlgorithmName.SHA256,
                        System.Security.Cryptography.RSASignaturePadding.Pkcs1
                    );
                    cert = certRequest.CreateSelfSigned(
                        DateTimeOffset.UtcNow.AddDays(-1),
                        DateTimeOffset.UtcNow.AddYears(1)
                    );
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
                    throw new InvalidOperationException(
                        "CertificatePath must be set when not using development certificates."
                    );
                }
                var cert = string.IsNullOrEmpty(certPassword)
                    ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath)
                    : new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        certPath,
                        certPassword
                    );
                using var pubRsa = cert.GetRSAPublicKey();
                return await Task.FromResult(new[] { (pubRsa!.ExportParameters(false), cert.Thumbprint) });
            }
        }

        /// <summary>
        /// Gets public keys from database (OpenIddictKey table)
        /// </summary>
        private async Task<
            IEnumerable<(RSAParameters RsaParameters, string KeyId)>
        > GetPublicKeysFromDatabaseAsync()
        {
            var keys = new List<(RSAParameters, string)>();
            try
            {
                int maxCacheSize = _identityOptions.Value.KeyFormatCacheSize;
                var keyRecords = await _tokenRepository.GetActivePublicKeysAsync();
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

                            // Atomically check cache size, remove if needed, and add new entry
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
                                _keyFormatCache.TryAdd(record.KeyId, keyFormat);
                            }
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

    /// <summary>
    /// Represents a signing key with its associated key identifier
    /// </summary>
    public class SigningKeyResult
    {
        public SecurityKey SecurityKey { get; set; } = null!;
        public string KeyId { get; set; } = string.Empty;
    }
}
