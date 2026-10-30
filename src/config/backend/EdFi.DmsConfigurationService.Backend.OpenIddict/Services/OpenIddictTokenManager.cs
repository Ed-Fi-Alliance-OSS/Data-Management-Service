// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
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
    /// Verification keys come from the shared signing-key snapshot (DMS-1556 D-1), never from a per-call key read, and the
    /// development certificate is obtained from the shared <see cref="DevelopmentCertificateStore"/> (D-10).
    /// Revocation is the one exception: it verifies against a per-call, read-only key load that fails closed on a
    /// missing certificate or an unusable key row, where the snapshot would create the certificate or discard the row
    /// (DMS-1327 D-07.4). Introspection (<see cref="ITokenIntrospectionManager"/>) uses the snapshot,
    /// like the bearer path.
    /// </summary>
    public class OpenIddictTokenManager(
        IOptions<IdentityOptions> identityOptions,
        ILogger<OpenIddictTokenManager> logger,
        IClientSecretHasher secretHasher,
        IOpenIddictTokenRepository tokenRepository,
        ISigningKeySnapshotProvider signingKeyProvider,
        DevelopmentCertificateStore developmentCertificateStore,
        ClientSecretPlaceholderHash placeholderHash
    ) : ITokenManager, ITokenRevocationManager, ITokenIntrospectionManager
    {
        private readonly IOptions<IdentityOptions> _identityOptions = identityOptions;
        private readonly ILogger<OpenIddictTokenManager> _logger = logger;
        private readonly IClientSecretHasher _secretHasher = secretHasher;
        private readonly IOpenIddictTokenRepository _tokenRepository = tokenRepository;
        private readonly ISigningKeySnapshotProvider _signingKeyProvider = signingKeyProvider;
        private readonly DevelopmentCertificateStore _developmentCertificateStore =
            developmentCertificateStore;

        /// <summary>
        /// The registered hasher's hash of a random secret that nobody holds, which an unknown
        /// client's presented secret is verified against. Read once here, so a manager cannot be
        /// constructed before the host has initialized the placeholder, and no request generates it.
        /// </summary>
        private readonly string _placeholderHash = placeholderHash.Hash;

        /// <summary>
        /// The most UTF-16 code units of a rejected scope value written to the invalid_scope warning.
        /// The form accepts values far larger than any real scope list, so the warning carries this
        /// prefix and the value's original length rather than the whole value.
        /// </summary>
        private const int LoggedScopePrefixLength = 256;

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
                // The shared store is the only code that creates the development certificate, so the key id issued
                // here is always the thumbprint the validation snapshot publishes (I-10), even on a first start where
                // the refresh service and this mint race to create the file.
                X509Certificate2 cert = await _developmentCertificateStore.GetAsync(CancellationToken.None);
                var signingKey = new X509SecurityKey(cert);
                return new SigningKeyResult { SecurityKey = signingKey, KeyId = cert.Thumbprint };
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

                if (
                    await ValidateClientSecretAsync(
                        clientId,
                        clientSecret,
                        preserveVerificationFailures: false
                    )
                    is not { } authenticatedClient
                )
                {
                    return new TokenResult.FailureAuthentication(
                        InvalidClientError,
                        "Invalid client or Invalid client credentials"
                    );
                }

                if (
                    await LoadAuthenticatedApplicationAsync(authenticatedClient, clientId)
                    is not { } applicationInfo
                )
                {
                    // The application changed between authentication and this load, so answer
                    // exactly as an unknown client is answered.
                    return new TokenResult.FailureAuthentication(
                        InvalidClientError,
                        "Invalid client or Invalid client credentials"
                    );
                }

                _logger.LogDebug(
                    "Application found: {ApplicationId}, Display Name: {DisplayName}",
                    applicationInfo.Id,
                    LoggingUtility.SanitizeForLog(applicationInfo.DisplayName)
                );

                // Determine the scopes to grant. An omitted or empty scope receives the application's
                // registered permissions (RFC 6749 section 3.3). A non-empty scope is narrowed to the
                // requested values the application holds, in the requested spelling and order; when
                // that leaves nothing, including a value that names no scope at all, the request is
                // rejected as invalid_scope (section 5.2) rather than widened to the defaults.
                string[] allowedScopes = applicationInfo.Permissions ?? [];
                string[] grantedScopes;
                if (string.IsNullOrEmpty(scope))
                {
                    // No scope requested, use application's default permissions
                    grantedScopes = allowedScopes;
                    _logger.LogInformation(
                        "No scope requested, using application defaults: {ListOfScopes}",
                        LoggingUtility.SanitizeForLog(string.Join(" ", grantedScopes))
                    );
                }
                else
                {
                    string[] requestedScopes = scope.Split(
                        [' ', ','],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    // Only include scopes that are both requested AND allowed
                    grantedScopes = requestedScopes
                        .Where(s => allowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                        .ToArray();

                    if (grantedScopes.Length == 0)
                    {
                        // Returned before the canonical client id, the signing key, the mint and the
                        // store are touched, so a rejected request leaves no token behind.
                        // The prefix is cut before it is sanitized, so a large value is never copied
                        // whole, and its commas become spaces so the separators survive the sanitizer.
                        _logger.LogWarning(
                            "Client {ClientId} requested a scope it does not hold; answering invalid_scope. "
                                + "Requested scope prefix: {RequestedScopePrefix} (sanitized; original length "
                                + "{RequestedScopeOriginalLength})",
                            LoggingUtility.SanitizeForLog(clientId),
                            LoggingUtility.SanitizeForLog(
                                scope[..Math.Min(scope.Length, LoggedScopePrefixLength)].Replace(',', ' ')
                            ),
                            scope.Length
                        );
                        return new TokenResult.FailureInvalidScope();
                    }
                }

                // The access token scope claim and the response report the same granted set space-delimited,
                // as RFC 6749 section 3.3 defines the scope parameter. That definition requires at least
                // one scope token, so an empty granted set is reported as no scope at all, which the
                // response leaves out.
                string listOfScopes = string.Join(" ", grantedScopes);
                string? responseScope = grantedScopes.Length == 0 ? null : listOfScopes;

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
                        InvalidClientError,
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
                    scope = responseScope,
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
        private const string SigningCertificateMissingBoundary = "signing-certificate-missing";
        private const string SigningCertificateNoRsaKeyBoundary = "signing-certificate-no-rsa-key";
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

            AuthenticatedClient? authenticatedClient;
            try
            {
                authenticatedClient = await ValidateClientSecretAsync(
                    request.ClientId,
                    request.ClientSecret,
                    preserveVerificationFailures: true
                );
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                return Unavailable(ClientAuthenticationBoundary, ex);
            }

            if (authenticatedClient is null)
            {
                return new TokenRevocationResult.InvalidClient();
            }

            // Tokens are minted from the stored canonical id, so that is what the ownership
            // comparison must be given, not the caller's own spelling (DMS-1478).
            string callerClientId = authenticatedClient.EffectiveClientId;

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
                // The stored application Id, not the client id, is the token's foreign key; the
                // UPDATE re-checks it so ownership holds even if the claim check were bypassed.
                bool changed = await _tokenRepository.RevokeTokenAsync(
                    tokenId,
                    authenticatedClient.ApplicationId
                );
                _logger.LogDebug(
                    "Revocation for client {CallerClientId} {Outcome}",
                    LoggingUtility.SanitizeForLog(callerClientId),
                    changed
                        ? "revoked the token"
                        : "changed no row (unknown, already revoked, or stored for another application)"
                );
                return new TokenRevocationResult.Completed();
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                return Unavailable(TokenMutationBoundary, ex);
            }
        }

        /// <summary>
        /// RFC 7662 introspection for the self-contained provider (CS-AUTH.md). The order is
        /// authenticate, verify, compare ownership, read status. Authentication runs before the token is
        /// looked at, so a bad token can never turn a failed authentication into an answer, and a failed
        /// client-store dependency is <see cref="TokenIntrospectionResult.TemporarilyUnavailable"/> as it
        /// is for revocation (DMS-1327 D-07). After authentication every token outcome is
        /// <see cref="TokenIntrospectionResult.Inactive"/>: an unverifiable, expired, revoked or unknown
        /// token, another client's token (never an oracle), and a token whose verification or status
        /// dependency failed, which is logged at Error with its category (DMS-1556 §4.7). Verification
        /// uses the signing-key snapshot, like the bearer path and unlike revocation, and only the
        /// verified token's claims are reported. The status read is scoped to the authenticated application,
        /// as revocation's UPDATE is. The comparison and parsing logic between the boundaries
        /// is deliberately not wrapped, so a fault there propagates as an exception (DMS-1327 D-13.1).
        /// Nothing from the caller or a client-store exception reaches a log on the authentication path
        /// except the sanitized canonical client id and exception type names (DMS-1327 D-15).
        /// </summary>
        public async Task<TokenIntrospectionResult> IntrospectTokenAsync(
            TokenIntrospectionRequest request,
            CancellationToken cancellationToken
        )
        {
            if (
                string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret)
            )
            {
                return new TokenIntrospectionResult.InvalidClient();
            }

            AuthenticatedClient? authenticatedClient;
            try
            {
                authenticatedClient = await ValidateClientSecretAsync(
                    request.ClientId,
                    request.ClientSecret,
                    preserveVerificationFailures: true
                );
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                return IntrospectionUnavailable(ClientAuthenticationBoundary, ex);
            }

            if (authenticatedClient is null)
            {
                return new TokenIntrospectionResult.InvalidClient();
            }

            // Tokens are minted from the stored canonical id, so that is what the ownership
            // comparison must be given, not the caller's own spelling (DMS-1478).
            string callerClientId = authenticatedClient.EffectiveClientId;

            TokenVerification verification;
            try
            {
                verification = await VerifyTokenAsync(request.Token);
            }
            catch (AuthenticationDependencyUnavailableException ex)
            {
                return UndecidedToken(ex);
            }

            if (verification.Token is not { } jwtToken)
            {
                LogIntrospectionVerificationFailure(verification.Failure, callerClientId);
                return new TokenIntrospectionResult.Inactive();
            }

            string? tokenClientId = jwtToken
                .Claims.FirstOrDefault(x => x.Type == SecurityConstants.ClientIdClaimType)
                ?.Value;
            if (!TokenBelongsToCaller(tokenClientId, callerClientId))
            {
                // Debug, not Warning: an expected answer any authenticated caller can trigger at
                // will, so a higher level would let them flood a severity operators alert on.
                _logger.LogDebug(
                    "Introspection answered inactive: the supplied token does not belong to the calling client {CallerClientId}",
                    LoggingUtility.SanitizeForLog(callerClientId)
                );
                return new TokenIntrospectionResult.Inactive();
            }

            string? jti = jwtToken.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;
            if (!Guid.TryParse(jti, out Guid tokenId))
            {
                return new TokenIntrospectionResult.Inactive();
            }

            string? status;
            try
            {
                status = await GetTokenStatusForApplicationAsync(tokenId, authenticatedClient.ApplicationId);
            }
            catch (AuthenticationDependencyUnavailableException ex)
            {
                return UndecidedToken(ex);
            }

            if (status != "valid")
            {
                return new TokenIntrospectionResult.Inactive();
            }

            if (
                !long.TryParse(ClaimValue(jwtToken, JwtRegisteredClaimNames.Exp), out long exp)
                || !long.TryParse(ClaimValue(jwtToken, JwtRegisteredClaimNames.Iat), out long iat)
            )
            {
                return new TokenIntrospectionResult.Inactive();
            }

            return new TokenIntrospectionResult.Active(
                new IntrospectedToken(
                    ClientId: tokenClientId ?? string.Empty,
                    Scope: string.Join(
                        " ",
                        jwtToken.Claims.Where(x => x.Type == "scope").Select(x => x.Value)
                    ),
                    Exp: exp,
                    Iat: iat,
                    Sub: ClaimValue(jwtToken, JwtRegisteredClaimNames.Sub),
                    // Every minted token carries exactly one audience (JwtTokenGenerator).
                    Aud: ClaimValue(jwtToken, JwtRegisteredClaimNames.Aud) is { } audience ? [audience] : [],
                    Iss: ClaimValue(jwtToken, JwtRegisteredClaimNames.Iss),
                    TokenType: "Bearer"
                )
            );
        }

        private static string? ClaimValue(JwtSecurityToken token, string claimType) =>
            token.Claims.FirstOrDefault(x => x.Type == claimType)?.Value;

        /// <summary>
        /// A token that could not be decided because a signing-key or token-status dependency failed.
        /// Answered as inactive, the introspection contract DMS-1556 §4.7 kept, so the category is logged
        /// or an unavailable store would be indistinguishable from an invalid token.
        /// </summary>
        private TokenIntrospectionResult UndecidedToken(
            AuthenticationDependencyUnavailableException exception
        )
        {
            _logger.LogError(
                exception,
                "Token validation could not reach a decision: the {Category} is unavailable",
                exception.Category
            );
            return new TokenIntrospectionResult.Inactive();
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

        /// <summary>
        /// The introspection sibling of <see cref="Unavailable"/>: same rule, only the operation named in the
        /// message differs.
        /// </summary>
        private TokenIntrospectionResult IntrospectionUnavailable(string boundary, Exception exception)
        {
            _logger.LogError(
                "Introspection could not be completed: {Boundary} failed ({ExceptionTypes})",
                boundary,
                ExceptionTypeChain(exception)
            );
            return new TokenIntrospectionResult.TemporarilyUnavailable(boundary);
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
        /// Fixed-category logging for an introspected token that failed verification, as for
        /// revocation (DMS-1327 D-07.5): the validator's <c>Detail</c> is not written, because it
        /// incorporates library exception messages and the token's unverified <c>kid</c>. Severities
        /// match <see cref="LogVerificationFailure"/>.
        /// </summary>
        private void LogIntrospectionVerificationFailure(
            TokenVerificationFailure failure,
            string callerClientId
        )
        {
            string sanitizedCaller = LoggingUtility.SanitizeForLog(callerClientId);
            switch (failure)
            {
                case TokenVerificationFailure.Expired:
                    _logger.LogDebug(
                        "Introspection answered inactive: the supplied token failed the lifetime check (token expired); calling client {CallerClientId}",
                        sanitizedCaller
                    );
                    break;

                case TokenVerificationFailure.UntrustedIssuerOrAudience:
                    _logger.LogWarning(
                        "Introspection answered inactive: the supplied token failed the issuer or audience check; verify the configured Authority "
                            + "and Audience if this affects every token; calling client {CallerClientId}",
                        sanitizedCaller
                    );
                    break;

                default:
                    _logger.LogWarning(
                        "Introspection answered inactive: the supplied token failed verification (signature or key id); calling client {CallerClientId}",
                        sanitizedCaller
                    );
                    break;
            }
        }

        /// <summary>
        /// Verification keys for the revocation path, or the reason none can be offered. Unlike
        /// the signing-key snapshot, which serves the JWKS endpoint and the bearer path and
        /// discards individual bad keys, this loader swallows nothing: a
        /// repository or certificate failure propagates to the caller's boundary catch, and an
        /// empty key set or a key record that cannot be imported is reported as unavailable, so a
        /// healthy-looking "unknown token" 200 can never mask a signing-key problem (D-07.4).
        /// </summary>
        private async Task<VerificationKeys> LoadVerificationKeysAsync()
        {
            if (_identityOptions.Value.UseCertificates)
            {
                return LoadCertificateVerificationKeys();
            }

            var keyRecords = (
                await _tokenRepository.GetActivePublicKeysAsync(CancellationToken.None)
            ).ToList();
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
                PublicKeyFormat keyFormat = PublicKeyMaterialParser.DetectFormat(record.PublicKey, _logger);
                if (keyFormat == PublicKeyFormat.Unknown)
                {
                    _logger.LogError(
                        "Revocation could not be completed: active public key {KeyId} is in no recognised format",
                        LoggingUtility.SanitizeForLog(record.KeyId)
                    );
                    return new VerificationKeys(null, SigningKeyImportBoundary);
                }

                try
                {
                    keys[record.KeyId] = new RsaSecurityKey(
                        PublicKeyMaterialParser.ImportPublicParameters(record.PublicKey, keyFormat)
                    );
                }
                catch (Exception ex)
                {
                    return KeyImportUnavailable(record.KeyId, ex);
                }
            }

            return new VerificationKeys(keys, string.Empty);
        }

        /// <summary>
        /// The signing certificate's public key for the revocation path, read-only. Unlike
        /// <see cref="DevelopmentCertificateStore"/>, which the snapshot and issuance use and which
        /// creates the development certificate when the file is missing, this never creates or
        /// replaces anything: a replacement would hold an unrelated key, every outstanding token
        /// would then fail verification against it, and revocation would answer an empty 200 while
        /// revoking nothing (D-07.4). A missing or unconfigured certificate is therefore
        /// <see cref="TokenRevocationResult.TemporarilyUnavailable"/>; a certificate that exists but
        /// cannot be read (wrong password, corrupt file) throws to the caller's boundary catch.
        /// </summary>
        private VerificationKeys LoadCertificateVerificationKeys()
        {
            IdentityOptions options = _identityOptions.Value;
            bool development = options.UseDevelopmentCertificates;
            string certificatePath = development ? options.DevCertificatePath : options.CertificatePath;
            string certificatePassword = development
                ? options.DevCertificatePassword
                : options.CertificatePassword;

            if (string.IsNullOrEmpty(certificatePath) || !System.IO.File.Exists(certificatePath))
            {
                _logger.LogError(
                    "Revocation could not be completed: the configured {CertificateKind} signing certificate does not exist; "
                        + "revocation never creates one",
                    development ? "development" : "production"
                );
                return new VerificationKeys(null, SigningCertificateMissingBoundary);
            }

            // Same loading rules as CertificateSigningKeySource and the signing side.
            using X509Certificate2 certificate =
                development || !string.IsNullOrEmpty(certificatePassword)
                    ? X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword)
                    : X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            using RSA? publicKey = certificate.GetRSAPublicKey();
            if (publicKey is null)
            {
                _logger.LogError(
                    "Revocation could not be completed: the configured signing certificate carries no RSA public key"
                );
                return new VerificationKeys(null, SigningCertificateNoRsaKeyBoundary);
            }

            return new VerificationKeys(
                new Dictionary<string, SecurityKey>
                {
                    [certificate.Thumbprint] = new RsaSecurityKey(publicKey.ExportParameters(false)),
                },
                string.Empty
            );
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
            CanonicalClientId(applicationInfo.ClientId, requestedClientId);

        /// <summary>
        /// The rule of <see cref="CanonicalClientId(ApplicationInfo, string)"/> applied to a stored
        /// client id read without the full application.
        /// </summary>
        private static string CanonicalClientId(string? storedClientId, string requestedClientId) =>
            string.IsNullOrEmpty(storedClientId) ? requestedClientId : storedClientId;

        /// <summary>
        /// The OAuth error code for every failed client authentication, as RFC 6749 section 5.2
        /// requires for invalid client credentials.
        /// </summary>
        private const string InvalidClientError = "invalid_client";

        /// <summary>
        /// A client that has just authenticated, held for one request only. The application id
        /// and canonical client id are what revocation needs; the token endpoint also needs the
        /// full application, which <see cref="LoadAuthenticatedApplicationAsync"/> loads.
        /// </summary>
        /// <param name="ApplicationId">The stored id of the application that authenticated.</param>
        /// <param name="EffectiveClientId">
        /// The stored client id, or the requested one when the stored value is empty.
        /// </param>
        /// <param name="VerifiedStoredSecret">The stored secret the presented secret was verified against.</param>
        private sealed record AuthenticatedClient(
            Guid ApplicationId,
            string EffectiveClientId,
            string VerifiedStoredSecret
        );

        /// <summary>
        /// Reads the client's credential and verifies the secret against the stored (potentially
        /// hashed) value. Returns <c>null</c> for an unknown client, a wrong secret and an
        /// unapproved client alike, so the caller answers all three identically. An unknown
        /// client's secret is still verified, against a placeholder hash, so its rejection does not
        /// return before a secret verification has been performed. Only the credential is read
        /// here; the token endpoint loads the full application after this succeeds.
        /// </summary>
        /// <param name="preserveVerificationFailures">
        /// <c>false</c> for the token endpoint, which keeps the hasher's established behaviour of
        /// answering a verification failure as a mismatch. <c>true</c> for revocation (DMS-1327
        /// D-07 step 2): when the hasher offers <see cref="IFailurePreservingSecretVerifier"/>, a
        /// failure that prevents the comparison propagates to the caller's boundary catch instead
        /// of being reported as wrong credentials. Catching around a dependency is not enough when
        /// the dependency suppresses its own failures.
        /// </param>
        private async Task<AuthenticatedClient?> ValidateClientSecretAsync(
            string clientId,
            string clientSecret,
            bool preserveVerificationFailures
        )
        {
            if (await _tokenRepository.GetClientCredentialAsync(clientId) is not { } credential)
            {
                await VerifyAgainstPlaceholderAsync(clientSecret, preserveVerificationFailures);
                return null;
            }

            if (
                !await VerifyStoredSecretAsync(
                    clientSecret,
                    credential.StoredClientSecret,
                    preserveVerificationFailures
                )
            )
            {
                return null;
            }

            if (!credential.IsApproved)
            {
                return null;
            }

            return new AuthenticatedClient(
                credential.ApplicationId,
                CanonicalClientId(credential.StoredClientId, clientId),
                credential.StoredClientSecret
            );
        }

        /// <summary>
        /// Verifies <paramref name="presentedSecret"/> against a stored secret, or against the
        /// placeholder for an unknown client, with the failure semantics
        /// <paramref name="preserveVerificationFailures"/> selects (see
        /// <see cref="ValidateClientSecretAsync"/>).
        /// </summary>
        private Task<bool> VerifyStoredSecretAsync(
            string presentedSecret,
            string storedSecret,
            bool preserveVerificationFailures
        ) =>
            preserveVerificationFailures && _secretHasher is IFailurePreservingSecretVerifier verifier
                ? verifier.VerifySecretPreservingFailuresAsync(presentedSecret, storedSecret)
                : _secretHasher.VerifySecretAsync(presentedSecret, storedSecret);

        /// <summary>
        /// Loads the full application for a client that authenticated from its credential alone,
        /// under the client id the request named. Returns <c>null</c>
        /// when the application is no longer the one that authenticated: gone, replaced under the
        /// same client id, no longer approved, or holding a different stored secret. The stored
        /// secrets are compared as stored strings; no secret is derived again and neither value
        /// leaves this method. A change after this check is not detected here.
        /// </summary>
        private async Task<ApplicationInfo?> LoadAuthenticatedApplicationAsync(
            AuthenticatedClient authenticatedClient,
            string requestedClientId
        )
        {
            if (
                await _tokenRepository.GetApplicationByClientIdAsync(requestedClientId)
                is not { } applicationInfo
            )
            {
                return null;
            }

            bool isTheAuthenticatedApplication =
                applicationInfo.Id == authenticatedClient.ApplicationId
                && applicationInfo.IsApproved
                && string.Equals(
                    applicationInfo.ClientSecret ?? string.Empty,
                    authenticatedClient.VerifiedStoredSecret,
                    StringComparison.Ordinal
                );
            return isTheAuthenticatedApplication ? applicationInfo : null;
        }

        /// <summary>
        /// Spends on an unknown client the same secret verification that a known client with a
        /// wrong secret costs. The result is discarded: the caller rejects the client whatever this
        /// verification answers, so a placeholder that happened to match could never authenticate.
        /// <para>
        /// The verification is the one a known client gets on the same endpoint, and a failure is
        /// not absorbed either: it reaches the caller's own handling, as it would from a known
        /// client's verification, so an outage is answered the same way whether or not the client
        /// id exists. It arrives as a new exception with a fixed message, the exception type names
        /// and, for a cancellation, the original token, but no inner exception: the hasher's own
        /// exception may carry the secret it was given, and the token endpoint's handling logs and
        /// returns whatever exception reaches it.
        /// </para>
        /// </summary>
        private async Task VerifyAgainstPlaceholderAsync(
            string presentedSecret,
            bool preserveVerificationFailures
        )
        {
            try
            {
                _ = await VerifyStoredSecretAsync(
                    presentedSecret,
                    _placeholderHash,
                    preserveVerificationFailures
                );
            }
            catch (OperationCanceledException ex)
            {
                throw SanitizedCancellation(ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The secret verification for an unknown client failed ({ExceptionTypeChain(ex)})."
                );
            }
        }

        private static OperationCanceledException SanitizedCancellation(
            OperationCanceledException exception
        ) => new("The secret verification for an unknown client was canceled.", exception.CancellationToken);

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

            int maxActiveTokens = applicationInfo.IsTokenLimitExempt
                ? -1
                : _identityOptions.Value.BearerTokenPerClientLimit;
            TokenStoreOutcome outcome = await _tokenRepository.StoreTokenAsync(
                tokenId,
                applicationInfo.Id,
                clientId,
                expiration,
                maxActiveTokens
            );

            return (outcome, tokenString);
        }

        /// <summary>
        /// Verifies a token's signature, issuer, audience and lifetime against the signing-key
        /// snapshot, returning the parsed token, or <c>null</c> when it fails. A token whose
        /// <c>kid</c> the snapshot lacks gets one gated unknown-key refresh first (spec §4.4);
        /// when that refresh is refused or finds nothing, verification fails as for any unknown key.
        ///
        /// The bearer path's verification. <see cref="RevokeTokenAsync"/> shares
        /// <see cref="VerifyToken"/> but not this key source: it verifies against
        /// <see cref="LoadVerificationKeysAsync"/> (DMS-1327 D-07.4). The two apply different
        /// database status gates afterwards — do not move a status check into either.
        /// </summary>
        /// <exception cref="SigningKeysUnavailableException">No usable snapshot exists.</exception>
        private async Task<TokenVerification> VerifyTokenAsync(string rawToken)
        {
            SigningKeySnapshot snapshot = await _signingKeyProvider.GetUsableAsync(CancellationToken.None);

            string? keyId = ReadKeyId(rawToken);
            if (!string.IsNullOrEmpty(keyId) && !snapshot.ContainsKeyId(keyId))
            {
                snapshot = await RefreshForUnknownKeyAsync(keyId);
            }

            var signingKeys = snapshot.Keys.ToDictionary(
                entry => entry.KeyId,
                entry => entry.CreateSecurityKey()
            );

            return VerifyToken(rawToken, signingKeys);
        }

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
        /// Asks the provider for one unknown-key refresh, which it admits at most once per cooldown and only when the
        /// retry gate is open, and returns the snapshot to verify against. That snapshot is obtained through
        /// <see cref="ISigningKeySnapshotProvider.GetUsableAsync"/> again, never taken unchecked: the snapshot usable when
        /// verification started may have crossed the maximum staleness during a refresh that then failed.
        /// </summary>
        /// <exception cref="SigningKeysUnavailableException">No usable snapshot remains after the refresh.</exception>
        private async Task<SigningKeySnapshot> RefreshForUnknownKeyAsync(string keyId)
        {
            SigningKeyUnknownKeyOutcome outcome = await _signingKeyProvider.TryRefreshForUnknownKeyAsync(
                keyId,
                CancellationToken.None
            );

            _logger.LogWarning(
                "Token key id {KeyId} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}",
                LoggingUtility.SanitizeForLog(keyId),
                outcome
            );

            return await _signingKeyProvider.GetUsableAsync(CancellationToken.None);
        }

        /// <summary>
        /// The <c>kid</c> header of a token, read without any validation, or <c>null</c> when the token cannot be
        /// parsed; <see cref="JwtTokenValidator"/> then rejects it.
        /// </summary>
        private static string? ReadKeyId(string rawToken)
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(rawToken))
            {
                return null;
            }

            try
            {
                return handler.ReadJwtToken(rawToken).Header.Kid;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a token's status. A store failure is not a verdict on the token, so it becomes the typed dependency
        /// exception instead of an answer (spec D-7): the driver reports connection and command failures as
        /// <see cref="DbException"/>, and timeouts and cancellation as <see cref="TimeoutException"/> or
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        private async Task<string?> GetTokenStatusAsync(Guid tokenId)
        {
            try
            {
                return await _tokenRepository.GetTokenStatusAsync(tokenId);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or OperationCanceledException)
            {
                throw new AuthenticationDependencyUnavailableException(
                    AuthenticationDependencyCategory.TokenStatusStore,
                    "The token status store could not be read.",
                    ex
                );
            }
        }

        /// <summary>
        /// Reads a token's status through the authenticated application, with the same
        /// dependency-failure classification as <see cref="GetTokenStatusAsync"/>. A row stored for
        /// another application reads as unknown.
        /// </summary>
        private async Task<string?> GetTokenStatusForApplicationAsync(Guid tokenId, Guid applicationId)
        {
            try
            {
                return await _tokenRepository.GetTokenStatusForApplicationAsync(tokenId, applicationId);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or OperationCanceledException)
            {
                throw new AuthenticationDependencyUnavailableException(
                    AuthenticationDependencyCategory.TokenStatusStore,
                    "The token status store could not be read.",
                    ex
                );
            }
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
        /// Validates a JWT token and checks its status in the database. Returns <c>false</c> for a token that is
        /// invalid, revoked, or unknown to the store.
        /// </summary>
        /// <exception cref="AuthenticationDependencyUnavailableException">
        /// The signing keys or the token status could not be read, so no verdict was reached. Never reported as
        /// <c>false</c>, which callers would answer as an invalid token.
        /// </exception>
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
                    var status = await GetTokenStatusAsync(Guid.Parse(jti));
                    return status == "valid";
                }
                return false;
            }
            catch (Exception ex) when (ex is not AuthenticationDependencyUnavailableException)
            {
                _logger.LogWarning(ex, "Token validation failed");
                return false;
            }
        }

        /// <summary>
        /// Returns the public keys of the signing-key snapshot for the JWKS endpoint. An empty result means the store
        /// genuinely holds no active key; a failed retrieval is never reported as an empty set (I-8).
        /// </summary>
        /// <exception cref="SigningKeysUnavailableException">No usable snapshot exists.</exception>
        public async Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>> GetPublicKeysAsync()
        {
            SigningKeySnapshot snapshot = await _signingKeyProvider.GetUsableAsync(CancellationToken.None);
            return [.. snapshot.Keys.Select(entry => (RsaParameters: entry.PublicParameters, entry.KeyId))];
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
