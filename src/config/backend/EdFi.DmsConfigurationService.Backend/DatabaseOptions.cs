// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend
{
    public class DatabaseOptions
    {
        public required string DatabaseConnection { get; set; }
        public required string EncryptionKey { get; set; }
    }

    public class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
    {
        /// <summary>
        /// Only the first 32 characters of the configured key contribute to the AES-256 key, so the
        /// rules below govern that prefix.
        /// </summary>
        private const int RequiredEncryptionKeyLength = 32;

        /// <summary>
        /// The value formerly shipped in appsettings.json. It is public in an open-source repository,
        /// so any deployment left on it has decryptable connection strings. Only its significant
        /// 32-character prefix is compared: every value sharing that prefix derives the same key, so
        /// rejecting the full string alone would leave the published key reachable.
        /// </summary>
        private const string ShippedDefaultEncryptionKey = "YourSecureEncryptionKey32Characters";

        public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.DatabaseConnection))
            {
                return ValidateOptionsResult.Fail(
                    "Missing required DatabaseSettings value: DatabaseConnection"
                );
            }

            if (string.IsNullOrWhiteSpace(options.EncryptionKey))
            {
                return ValidateOptionsResult.Fail("Missing required DatabaseSettings value: EncryptionKey");
            }

            // Checked before the known-default rule so the comparison below always has 32 characters
            // to read.
            if (options.EncryptionKey.Length < RequiredEncryptionKeyLength)
            {
                return ValidateOptionsResult.Fail(
                    "DatabaseSettings:EncryptionKey must be at least 32 characters of ASCII key material."
                );
            }

            // Compares significant prefixes, not whole strings: the known default truncated to 32
            // characters, or that prefix with any suffix, derives the same publicly known key.
            if (
                options
                    .EncryptionKey.AsSpan(0, RequiredEncryptionKeyLength)
                    .SequenceEqual(ShippedDefaultEncryptionKey.AsSpan(0, RequiredEncryptionKeyLength))
            )
            {
                return ValidateOptionsResult.Fail(
                    "DatabaseSettings:EncryptionKey must not be the known default value; provide a unique key."
                );
            }

            if (!Ascii.IsValid(options.EncryptionKey.AsSpan(0, RequiredEncryptionKeyLength)))
            {
                return ValidateOptionsResult.Fail(
                    "DatabaseSettings:EncryptionKey must use ASCII characters in its first 32 characters."
                );
            }

            return ValidateOptionsResult.Success;
        }
    }
}
