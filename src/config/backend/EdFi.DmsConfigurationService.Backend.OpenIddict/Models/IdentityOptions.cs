// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models
{
    /// <summary>
    /// Options for OpenIddict identity settings.
    /// </summary>
    public class IdentityOptions
    {
        /// <summary>
        /// The audience for JWT tokens.
        /// </summary>
        public string Audience { get; set; } = string.Empty;

        /// <summary>
        /// The authority (issuer) for JWT tokens.
        /// </summary>
        public string Authority { get; set; } = string.Empty;

        /// <summary>
        /// Token expiration time in minutes.
        /// </summary>
        public int TokenExpirationMinutes { get; set; } = 30;

        /// <summary>
        /// Whether to use certificates for JWT signing.
        /// </summary>
        public bool UseCertificates { get; set; } = false;

        /// <summary>
        /// Whether to use development certificates.
        /// </summary>
        public bool UseDevelopmentCertificates { get; set; } = false;

        /// <summary>
        /// Path to the development certificate.
        /// </summary>
        public string DevCertificatePath { get; set; } = "devcert.pfx";

        /// <summary>
        /// Password for the development certificate.
        /// </summary>
        public string DevCertificatePassword { get; set; } = "password";

        /// <summary>
        /// Path to the production certificate.
        /// </summary>
        public string CertificatePath { get; set; } = string.Empty;

        /// <summary>
        /// Password for the production certificate.
        /// </summary>
        public string CertificatePassword { get; set; } = string.Empty;

        /// <summary>
        /// Encryption key for database private keys.
        /// </summary>
        public string EncryptionKey { get; set; } = string.Empty;

        /// <summary>
        /// Maximum size of key format cache.
        /// </summary>
        public int KeyFormatCacheSize { get; set; } = 100;

        /// <summary>
        /// Number of PBKDF2 iterations for client secret hashing.
        /// Higher values increase security but also increase computation time.
        /// Recommended minimum: 100,000 for SHA-256.
        /// </summary>
        public int HashingIterations { get; set; } = 210000;

        /// <summary>
        /// Whether the background sweep that deletes expired OpenIddict tokens runs.
        /// </summary>
        public bool TokenCleanupEnabled { get; set; } = true;

        /// <summary>
        /// Interval, in minutes, between expired-token cleanup sweeps.
        /// </summary>
        public int TokenCleanupIntervalMinutes { get; set; } = 30;

        /// <summary>
        /// Maximum number of simultaneously active access tokens a single API client may hold.
        /// Any value below 1 disables enforcement, allowing an unlimited number of tokens. A
        /// disabling value also skips the application-existence check the enforcing path makes under
        /// the client's row lock, so a client deleted mid-grant may still receive a usable token,
        /// exactly as it could before this limit existed.
        /// Applies to the self-contained identity provider only: the Keycloak provider persists
        /// no tokens, so the setting is inert there.
        /// </summary>
        public int BearerTokenPerClientLimit { get; set; } = 5;

        /// <summary>
        /// Seconds between scheduled reloads of the signing-key snapshot. Bounds how long a
        /// healthy instance takes to see a key-table change without request-driven refresh.
        /// </summary>
        public int SigningKeyRefreshIntervalSeconds { get; set; } = 300;

        /// <summary>
        /// Seconds after the last successful key retrieval beyond which the snapshot is no longer
        /// trusted and every authenticated request fails closed. Bounds how long a key retired
        /// during a key-store outage can still be accepted.
        /// </summary>
        public int SigningKeyMaxStalenessSeconds { get; set; } = 3600;

        /// <summary>
        /// Minimum seconds between the end of one completed key load and an unknown-kid refresh,
        /// shared by all requests, so tokens carrying arbitrary key ids cannot drive key-store load.
        /// </summary>
        public int SigningKeyUnknownKeyRefreshCooldownSeconds { get; set; } = 30;

        /// <summary>
        /// Seconds a single signing-key load may run before it is canceled and counted as failed.
        /// </summary>
        public int SigningKeyLoadTimeoutSeconds { get; set; } = 10;
    }
}
