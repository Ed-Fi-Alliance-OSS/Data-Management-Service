// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.SchemaGenerator.Pgsql
{
    /// <summary>
    /// Provides helper methods for generating PostgreSQL-compliant identifiers,
    /// including truncation and hash suffixing for long names.
    /// </summary>
    public static class PgsqlNamingHelper
    {
        /// <summary>
        /// Generates a PostgreSQL-compliant identifier. If the name exceeds the specified maximum length,
        /// it is truncated and a hash suffix is appended to ensure uniqueness.
        /// </summary>
        /// <param name="name">The original identifier name.</param>
        /// <param name="maxLength">The maximum allowed length for the identifier (default: 63).</param>
        /// <param name="hashLength">The length of the hash suffix to append (default: 8).</param>
        /// <returns>A valid PostgreSQL identifier, truncated and suffixed with a hash if necessary.</returns>
        public static string MakePgsqlIdentifier(string name, int maxLength = 63, int hashLength = 8)
        {
            // Sanitize PostgreSQL identifiers by removing hyphens (which require quoting)
            name = name.Replace("-", "");

            if (name.Length <= maxLength)
            {
                return name;
            }

            using var sha1 = SHA1.Create();
            var hash = BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes(name)))
                .Replace("-", "")
                .ToLowerInvariant()
                .Substring(0, hashLength);
            int baseLen = maxLength - hashLength - 1;
            return name.Substring(0, baseLen) + "_" + hash;
        }
    }
}
