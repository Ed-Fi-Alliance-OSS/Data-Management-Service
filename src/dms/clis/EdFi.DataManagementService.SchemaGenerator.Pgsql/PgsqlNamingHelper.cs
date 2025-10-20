// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;

namespace EdFi.DataManagementService.SchemaGenerator.Pgsql
{
    /// <summary>
    /// Provides helper methods for generating PostgreSQL-compliant identifiers,
    /// including truncation and hash suffixing for long names.
    /// </summary>
    public static class PgsqlNamingHelper
    {
        /// <summary>
        /// PostgreSQL identifier maximum length.
        /// </summary>
        private const int PostgreSqlMaxLength = 63;

        /// <summary>
        /// Generates a PostgreSQL-compliant identifier. If the name exceeds the PostgreSQL maximum length (63 characters),
        /// it is truncated and a hash suffix is appended to ensure uniqueness.
        /// </summary>
        /// <param name="name">The original identifier name.</param>
        /// <param name="maxLength">The maximum allowed length for the identifier (default: 63).</param>
        /// <param name="hashLength">The length of the hash suffix to append (default: 8).</param>
        /// <returns>A valid PostgreSQL identifier, truncated and suffixed with a hash if necessary.</returns>
        public static string MakePgsqlIdentifier(string name, int maxLength = PostgreSqlMaxLength, int hashLength = 8)
        {
            return DatabaseIdentifierHelper.MakeIdentifier(name, maxLength, hashLength);
        }
    }
}
