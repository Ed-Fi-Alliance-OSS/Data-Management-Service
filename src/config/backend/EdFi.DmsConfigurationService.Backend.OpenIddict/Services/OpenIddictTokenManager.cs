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
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Secrets;
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
                    cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
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
                    ? X509CertificateLoader.LoadCertificateFromFile(certPath)
                    : X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
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

                var (applicationInfo, credentialErrorCode) = await ValidateClientSecretAsync(
                    clientId,
                    clientSecret
                );

                if (applicationInfo == null)
                {
                    return new TokenResult.FailureAuthentication(
                        credentialErrorCode,
                        "Invalid client or Invalid client credentials"
                    );
                }

                _logger.LogDebug(
                    "Application found: {ApplicationId}, Display Name: {DisplayName}",
                    applicationInfo.Id,
                    LoggingUtility.SanitizeForLog(applicationInfo.DisplayName)
                );

                // Determine scopes to include in token
                string listOfScopes;
                if (!string.IsNullOrEmpty(scope))
                {
                    // Validate requested scopes against application's permitted scopes
                    var requestedScopes = scope.Split(
                        new[] { ' ', ',' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    var allowedScopes = applicationInfo.Permissions ?? Array.Empty<string>();
                    // Only include scopes that are both requested AND allowed
                    var validScopes = requestedScopes
                        .Where(s => allowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                        .ToArray();

                    if (validScopes.Length == 0)
                    {
                        // If no valid scopes, use the application's default permissions
                        listOfScopes = string.Join(",", allowedScopes);
                        _logger.LogWarning(
                            "No valid requested scopes found, using application defaults: {ListOfScopes}",
                            LoggingUtility.SanitizeForLog(listOfScopes)
                        );
                    }
                    else
                    {
                        listOfScopes = string.Join(",", validScopes);
                    }
                }
                else
                {
                    // No scope requested, use application's default permissions
                    listOfScopes = string.Join(",", applicationInfo.Permissions ?? Array.Empty<string>());
                    _logger.LogInformation(
                        "No scope requested, using application defaults: {ListOfScopes}",
                        LoggingUtility.SanitizeForLog(listOfScopes)
                    );
                }

                // Mint from the stored client id, not the one the caller typed, so that every
                // token issued to a client carries one identity. This single argument feeds the
                // "sub", "client_id" and "azp" claims plus the stored token's client id, so all
                // four become canonical together.
                string canonicalClientId = CanonicalClientId(applicationInfo, clientId);

                // Generate and store the JWT token. The limit is enforced by the same
                // statement that stores the token, so the token is minted before the outcome is
                // known; one that is not stored is never returned to the caller.
                (TokenStoreOutcome outcome, string token) = await GenerateJwtTokenAsync(
                    applicationInfo,
                    canonicalClientId,
                    listOfScopes
                );

                if (outcome == TokenStoreOutcome.LimitExceeded)
                {
                    int bearerTokenPerClientLimit = _identityOptions.Value.BearerTokenPerClientLimit;
                    _logger.LogWarning(
                        "Client {ClientId} already holds the maximum of {TokenLimit} active access tokens",
                        LoggingUtility.SanitizeForLog(clientId),
                        bearerTokenPerClientLimit
                    );
                    return new TokenResult.FailureTokenLimitExceeded(bearerTokenPerClientLimit);
                }

                if (outcome == TokenStoreOutcome.LockTimeout)
                {
                    // Not routed through the catch below: contention is a retriable concurrency
                    // condition, and reporting it as an unknown failure would answer a transient
                    // queue with a server error.
                    _logger.LogWarning(
                        "Timed out waiting for, or deadlocked on, a database lock while storing a token grant for client {ClientId}",
                        LoggingUtility.SanitizeForLog(clientId)
                    );
                    return new TokenResult.FailureLockTimeout();
                }

                if (outcome == TokenStoreOutcome.ClientNotFound)
                {
                    // The application row was deleted between the lookup above and the store, so
                    // answer exactly as the unknown-client case above answers.
                    return new TokenResult.FailureAuthentication(
                        "invalid_client",
                        "Invalid client or Invalid client credentials"
                    );
                }

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

        private const string ClientAuthenticationBoundary = "client-authentication";
        private const string SigningKeyRetrievalBoundary = "signing-key-retrieval";
        private const string SigningKeyImportBoundary = "signing-key-import";
        private const string NoActiveSigningKeysBoundary = "no-active-signing-keys";
        private const string TokenMutationBoundary = "token-mutation";

        /// <summary>
        /// RFC 7009 revocation for the self-contained provider (DMS-1327 D-07). The order is
        /// authenticate, load verification keys, verify, compare ownership, mutate. Authentication
        /// runs before the token is looked at, so an invalid or unknown token can never hide an
        /// authentication failure. Each dependency call is wrapped at its own boundary and a
        /// failure there is classified as <see cref="TokenRevocationResult.TemporarilyUnavailable"/>,
        /// never answered as a token outcome; the comparison and parsing logic between the
        /// boundaries is deliberately not wrapped, so a fault there propagates as an exception
        /// rather than being relabelled as an outage (D-13.1). Nothing from the caller or a
        /// dependency exception reaches a log on this path except the sanitized canonical client
        /// id and exception type names (D-15).
        /// </summary>
        public async Task<TokenRevocationResult> RevokeTokenAsync(
            TokenRevocationRequest request,
            CancellationToken cancellationToken
        )
        {
            if (
                string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret)
            )
            {
                return new TokenRevocationResult.InvalidClient();
            }

            ApplicationInfo? applicationInfo;
            try
            {
                (applicationInfo, _) = await ValidateClientSecretAsync(
                    request.ClientId,
                    request.ClientSecret
                );
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                return Unavailable(ClientAuthenticationBoundary, ex);
            }

            if (applicationInfo is null)
            {
                return new TokenRevocationResult.InvalidClient();
            }

            // Tokens are minted from the stored canonical id, so that is what the ownership
            // comparison must be given, not the caller's own spelling (DMS-1478).
            string callerClientId = CanonicalClientId(applicationInfo, request.ClientId);

            IDictionary<string, SecurityKey> verificationKeys;
            try
            {
                VerificationKeys loaded = await LoadVerificationKeysAsync();
                if (loaded.Keys is null)
                {
                    return new TokenRevocationResult.TemporarilyUnavailable(loaded.UnavailableReason);
                }

                verificationKeys = loaded.Keys;
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                return Unavailable(SigningKeyRetrievalBoundary, ex);
            }

            // Verify before trusting any claim: an unverified client_id could be forged to name
            // the caller while carrying a victim's jti. ValidateTokenAsync is not reused because
            // its "valid" status gate would break RFC 7009 re-revocation idempotency.
            TokenVerification verification = VerifyToken(request.Token, verificationKeys);
            if (verification.Token is not { } jwtToken)
            {
                LogRevocationVerificationFailure(verification.Failure, callerClientId);
                return new TokenRevocationResult.Completed();
            }

            string? tokenClientId = jwtToken
                .Claims.FirstOrDefault(x => x.Type == SecurityConstants.ClientIdClaimType)
                ?.Value;
            if (!TokenBelongsToCaller(tokenClientId, callerClientId))
            {
                // Debug, not Warning: an expected no-op any authenticated caller can trigger at
                // will, so a higher level would let them flood a severity operators alert on.
                _logger.LogDebug(
                    "Revocation ignored: the supplied token does not belong to the calling client {CallerClientId}",
                    LoggingUtility.SanitizeForLog(callerClientId)
                );
                return new TokenRevocationResult.Completed();
            }

            string? jti = jwtToken.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;
            if (!Guid.TryParse(jti, out Guid tokenId))
            {
                _logger.LogDebug(
                    "Revocation ignored: the supplied token carries no usable jti; calling client {CallerClientId}",
                    LoggingUtility.SanitizeForLog(callerClientId)
                );
                return new TokenRevocationResult.Completed();
            }

            try
            {
                bool changed = await _tokenRepository.RevokeTokenAsync(tokenId);
                _logger.LogDebug(
                    "Revocation for client {CallerClientId} {Outcome}",
                    LoggingUtility.SanitizeForLog(callerClientId),
                    changed ? "revoked the token" : "changed no row (unknown or already revoked)"
                );
                return new TokenRevocationResult.Completed();
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                return Unavailable(TokenMutationBoundary, ex);
            }
        }

        /// <summary>
        /// Ordinal (case-sensitive) on purpose: a case-insensitive comparison would make the
        /// ownership boundary depend on the deployed engine's collation, since Postgres is
        /// case-sensitive and SQL Server is not by default (DMS-1478). Virtual only so a test can
        /// inject a fault here and prove that comparison logic is not wrapped as an outage.
        /// </summary>
        protected virtual bool TokenBelongsToCaller(string? tokenClientId, string callerClientId) =>
            string.Equals(tokenClientId, callerClientId, StringComparison.Ordinal);

        private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

        /// <summary>
        /// Classifies a dependency failure at one boundary. The exception object is deliberately
        /// not handed to the logger: its message may carry anything a dependency put there, so only
        /// the chain of exception type names is recorded (D-15 rule 1).
        /// </summary>
        private TokenRevocationResult Unavailable(string boundary, Exception exception)
        {
            _logger.LogError(
                "Revocation could not be completed: {Boundary} failed ({ExceptionTypes})",
                boundary,
                ExceptionTypeChain(exception)
            );
            return new TokenRevocationResult.TemporarilyUnavailable(boundary);
        }

        private static string ExceptionTypeChain(Exception exception)
        {
            var names = new List<string>();
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                names.Add(current.GetType().FullName ?? current.GetType().Name);
            }

            return string.Join(" -> ", names);
        }

        /// <summary>
        /// Fixed-category logging for a revocation target that failed verification (D-07.5). The
        /// validator's <c>Detail</c> is not written here because it incorporates library exception
        /// messages and the token's unverified <c>kid</c>, neither of which is known to be free of
        /// attacker-chosen content. Severities match <see cref="LogVerificationFailure"/>.
        /// </summary>
        private void LogRevocationVerificationFailure(TokenVerificationFailure failure, string callerClientId)
        {
            string sanitizedCaller = LoggingUtility.SanitizeForLog(callerClientId);
            switch (failure)
            {
                case TokenVerificationFailure.Expired:
                    _logger.LogDebug(
                        "Revocation ignored: the supplied token failed the lifetime check (token expired); calling client {CallerClientId}",
                        sanitizedCaller
                    );
                    break;

                case TokenVerificationFailure.UntrustedIssuerOrAudience:
                    _logger.LogWarning(
                        "Revocation ignored: the supplied token failed the issuer or audience check; verify the configured Authority "
                            + "and Audience if this affects every token; calling client {CallerClientId}",
                        sanitizedCaller
                    );
                    break;

                default:
                    _logger.LogWarning(
                        "Revocation ignored: the supplied token failed verification (signature or key id); calling client {CallerClientId}",
                        sanitizedCaller
                    );
                    break;
            }
        }

        /// <summary>
        /// Verification keys for the revocation path, or the reason none can be offered. Unlike
        /// <see cref="GetPublicKeysFromDatabaseAsync"/>, which serves the JWKS endpoint and the
        /// bearer path and tolerates individual bad keys, this loader swallows nothing: a
        /// repository or certificate failure propagates to the caller's boundary catch, and an
        /// empty key set or a key record that cannot be imported is reported as unavailable, so a
        /// healthy-looking "unknown token" 200 can never mask a signing-key problem (D-07.4).
        /// </summary>
        private async Task<VerificationKeys> LoadVerificationKeysAsync()
        {
            if (_identityOptions.Value.UseCertificates)
            {
                var certificateKeys = await GetPublicKeysFromCertificatesAsync();
                return new VerificationKeys(ToSecurityKeys(certificateKeys), string.Empty);
            }

            var keyRecords = (await _tokenRepository.GetActivePublicKeysAsync()).ToList();
            if (keyRecords.Count == 0)
            {
                _logger.LogError(
                    "Revocation could not be completed: the OpenIddictKey table holds no active public key"
                );
                return new VerificationKeys(null, NoActiveSigningKeysBoundary);
            }

            var keys = new Dictionary<string, SecurityKey>();
            foreach (var record in keyRecords)
            {
                KeyFormat keyFormat = ResolveKeyFormat(record);
                if (keyFormat == KeyFormat.Unknown)
                {
                    _logger.LogError(
                        "Revocation could not be completed: active public key {KeyId} is in no recognised format",
                        LoggingUtility.SanitizeForLog(record.KeyId)
                    );
                    return new VerificationKeys(null, SigningKeyImportBoundary);
                }

                try
                {
                    using var rsa = RSA.Create();
                    ImportPublicKey(rsa, keyFormat, record.PublicKey);
                    keys[record.KeyId] = new RsaSecurityKey(rsa.ExportParameters(false));
                }
                catch (Exception ex)
                {
                    return KeyImportUnavailable(record.KeyId, ex);
                }
            }

            return new VerificationKeys(keys, string.Empty);
        }

        /// <summary>
        /// A stored key that could not be imported. As in <see cref="Unavailable"/>, the exception
        /// object is not handed to the logger; only its type chain and the sanitized key id are.
        /// </summary>
        private VerificationKeys KeyImportUnavailable(string keyId, Exception exception)
        {
            _logger.LogError(
                "Revocation could not be completed: active public key {KeyId} could not be imported ({ExceptionTypes})",
                LoggingUtility.SanitizeForLog(keyId),
                ExceptionTypeChain(exception)
            );
            return new VerificationKeys(null, SigningKeyImportBoundary);
        }

        /// <summary>Either a usable key set, or the boundary label explaining why there is none.</summary>
        private sealed record VerificationKeys(
            IDictionary<string, SecurityKey>? Keys,
            string UnavailableReason
        );

        private static Dictionary<string, SecurityKey> ToSecurityKeys(
            IEnumerable<(RSAParameters RsaParameters, string KeyId)> publicKeys
        ) => publicKeys.ToDictionary(k => k.KeyId, k => (SecurityKey)new RsaSecurityKey(k.RsaParameters));

        /// <summary>
        /// The client's stored spelling of its own id, falling back to the requested spelling
        /// only when the stored value is empty — minting an empty subject would be worse than
        /// minting a non-canonical one.
        ///
        /// Everything identifying a client is derived from this, so that a client which
        /// authenticates under a spelling the engine accepts but did not store — SQL Server's
        /// default collation resolves a mis-cased id; Postgres does not — is still the same
        /// client everywhere downstream. Without it, the claims minted at the token endpoint and
        /// the caller id reaching the ownership check disagree, and revocation silently no-ops.
        /// </summary>
        private static string CanonicalClientId(ApplicationInfo applicationInfo, string requestedClientId) =>
            string.IsNullOrEmpty(applicationInfo.ClientId) ? requestedClientId : applicationInfo.ClientId;

        /// <summary>
        /// Looks up the application by client id and verifies the secret against the stored
        /// (potentially hashed) value. Returns a null application on failure along with the OAuth
        /// error code that describes why.
        /// </summary>
        private async Task<(ApplicationInfo? Application, string ErrorCode)> ValidateClientSecretAsync(
            string clientId,
            string clientSecret
        )
        {
            var applicationInfo = await _tokenRepository.GetApplicationByClientIdAsync(clientId);
            if (applicationInfo == null)
            {
                return (null, "invalid_client");
            }

            var isValidSecret = await _secretHasher.VerifySecretAsync(
                clientSecret,
                applicationInfo.ClientSecret ?? string.Empty
            );
            if (!isValidSecret)
            {
                return (null, "unauthorized_client");
            }

            if (!applicationInfo.IsApproved)
            {
                return (null, "invalid_client");
            }

            return (applicationInfo, string.Empty);
        }

        /// <summary>
        /// Mints a JWT and attempts to store it, subject to the configured per-client token limit.
        /// </summary>
        /// <returns>
        /// The outcome of storing the token, and the token itself, which is meaningful only when
        /// the outcome is <see cref="TokenStoreOutcome.Stored"/>.
        /// </returns>
        private async Task<(TokenStoreOutcome Outcome, string Token)> GenerateJwtTokenAsync(
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
                signingKeyResult.KeyId,
                dataStoreIds: applicationInfo.DataStoreIds
            );

            // Store token in database. The repository owns the disable semantics of the limit,
            // so a value below 1 is passed through rather than special-cased here.
            TokenStoreOutcome outcome = await _tokenRepository.StoreTokenAsync(
                tokenId,
                applicationInfo.Id,
                clientId,
                expiration,
                _identityOptions.Value.BearerTokenPerClientLimit
            );

            return (outcome, tokenString);
        }

        /// <summary>
        /// Verifies a token's signature, issuer, audience and lifetime against the currently
        /// active public keys, returning the parsed token, or <c>null</c> when it fails.
        ///
        /// Shared by <see cref="ValidateTokenAsync"/> and <see cref="RevokeTokenAsync"/>, which
        /// apply different database status gates afterwards — do not move a status check in here.
        /// The two callers share this validator but not the key loader: the bearer path keeps
        /// <see cref="GetPublicKeysAsync"/>, revocation uses <see cref="LoadVerificationKeysAsync"/>.
        /// </summary>
        private async Task<TokenVerification> VerifyTokenAsync(string rawToken) =>
            VerifyToken(rawToken, ToSecurityKeys(await GetPublicKeysAsync()));

        private TokenVerification VerifyToken(string rawToken, IDictionary<string, SecurityKey> signingKeys)
        {
            var verification = JwtTokenValidator.ValidateToken(
                rawToken,
                signingKeys,
                _identityOptions.Value.Authority,
                _identityOptions.Value.Audience
            );

            if (verification.Token is not null)
            {
                // Issuer and audience are logged unsanitized deliberately: validation has just
                // proved them equal to the configured ValidIssuer/ValidAudience, so what is
                // written is a server-controlled configuration value, not caller input. Subject
                // is different — nothing constrains it to a known value, and it originates from
                // a client id supplied at registration — so it is sanitized.
                _logger.LogDebug(
                    "JWT token validated successfully. Issuer: {Issuer}, Audience: {Audience}, Subject: {Subject}",
                    verification.Token.Issuer,
                    verification.Token.Audiences?.FirstOrDefault(),
                    LoggingUtility.SanitizeForLog(verification.Token.Subject)
                );
            }

            return verification;
        }

        /// <summary>
        /// The single place a verification failure is logged. <see cref="JwtTokenValidator"/>
        /// categorizes but never logs, so the same rejection cannot appear twice at two
        /// severities; in exchange this has to supply the operation context the validator does
        /// not know.
        ///
        /// An expired token is an ordinary fact of life and goes to Debug. The two untrusted
        /// cases both go to Warning but say different things, because an issuer/audience
        /// mismatch is usually a deployment misconfiguration that fails every token at once, and
        /// letting that share a message with genuine forgery signal would make the latter
        /// impossible to pick out.
        /// </summary>
        private void LogVerificationFailure(string context, TokenVerification verification)
        {
            switch (verification.Failure)
            {
                case TokenVerificationFailure.Expired:
                    _logger.LogDebug(
                        "{Context} the lifetime check (token expired): {Detail}",
                        context,
                        verification.Detail
                    );
                    break;

                case TokenVerificationFailure.UntrustedIssuerOrAudience:
                    _logger.LogWarning(
                        "{Context} the issuer or audience check; verify the configured Authority "
                            + "and Audience if this affects every token: {Detail}",
                        context,
                        verification.Detail
                    );
                    break;

                default:
                    _logger.LogWarning(
                        "{Context} verification (signature or key id): {Detail}",
                        context,
                        verification.Detail
                    );
                    break;
            }
        }

        /// <summary>
        /// Validates a JWT token and checks its status in the database
        /// </summary>
        public async Task<bool> ValidateTokenAsync(string rawToken)
        {
            try
            {
                var verification = await VerifyTokenAsync(rawToken);
                if (verification.Token is not { } jwtToken)
                {
                    LogVerificationFailure("Token validation failed", verification);
                    return false;
                }

                // Check token status in repository
                var jti = jwtToken.Claims?.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;
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
                    cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
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
                    ? X509CertificateLoader.LoadCertificateFromFile(certPath)
                    : X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
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
                var keyRecords = await _tokenRepository.GetActivePublicKeysAsync();
                foreach (var record in keyRecords)
                {
                    try
                    {
                        using var rsa = RSA.Create();

                        KeyFormat keyFormat = ResolveKeyFormat(record);
                        if (keyFormat == KeyFormat.Unknown)
                        {
                            _logger.LogWarning(
                                "Unknown key format for key ID: {KeyId}",
                                LoggingUtility.SanitizeForLog(record.KeyId)
                            );
                            continue; // Skip this key
                        }

                        ImportPublicKey(rsa, keyFormat, record.PublicKey);
                        keys.Add((rsa.ExportParameters(false), record.KeyId));
                    }
                    catch (Exception keyEx)
                    {
                        _logger.LogError(
                            keyEx,
                            "Failed to process key with ID {KeyId}",
                            LoggingUtility.SanitizeForLog(record.KeyId)
                        );
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
        /// The stored key's format, from the bounded format cache or by detection on a miss.
        /// </summary>
        private KeyFormat ResolveKeyFormat(PublicKeyInfo record)
        {
            if (!_keyFormatCache.TryGetValue(record.KeyId, out KeyFormat keyFormat))
            {
                keyFormat = DetectKeyFormat(record.PublicKey);

                // Atomically check cache size, remove if needed, and add new entry
                lock (_cacheLock)
                {
                    if (_keyFormatCache.Count >= _identityOptions.Value.KeyFormatCacheSize)
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

            _logger.LogDebug(
                "Key {KeyId} format detected as: {Format}",
                LoggingUtility.SanitizeForLog(record.KeyId),
                keyFormat
            );
            return keyFormat;
        }

        /// <summary>Imports a stored public key of a known format; throws for bytes that do not match it.</summary>
        private static void ImportPublicKey(RSA rsa, KeyFormat keyFormat, byte[] publicKey)
        {
            switch (keyFormat)
            {
                case KeyFormat.SubjectPublicKeyInfo:
                    rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                    break;

                case KeyFormat.Pkcs1:
                    rsa.ImportRSAPublicKey(publicKey, out _);
                    break;

                case KeyFormat.Base64Encoded:
                    var publicKeyString = System.Text.Encoding.UTF8.GetString(publicKey);
                    var decodedKey = Convert.FromBase64String(publicKeyString);
                    rsa.ImportSubjectPublicKeyInfo(decodedKey, out _);
                    break;

                default:
                    throw new InvalidOperationException("The public key format is not importable.");
            }
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
