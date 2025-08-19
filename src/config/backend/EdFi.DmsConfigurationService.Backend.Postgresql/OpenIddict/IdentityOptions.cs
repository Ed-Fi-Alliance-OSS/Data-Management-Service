// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
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
    }
}
