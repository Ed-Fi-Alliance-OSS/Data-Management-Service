// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;

namespace EdFi.DataManagementService.SchemaGenerator.Mssql
{
    /// <summary>
    /// Provides helper methods for generating SQL Server-compliant identifiers.
    /// SQL Server has a 128-character limit, which is more generous than PostgreSQL's 63,
    /// but still needs sanitization and may require truncation for very long Ed-Fi names.
    /// </summary>
    public static class MssqlNamingHelper
    {
        /// <summary>
        /// SQL Server identifier maximum length.
        /// </summary>
        private const int SqlServerMaxLength = 128;

        /// <summary>
        /// Generates a SQL Server-compliant identifier. If the name exceeds the SQL Server maximum length (128 characters),
        /// it is truncated and a hash suffix is appended to ensure uniqueness.
        /// Invalid characters like dots are replaced with underscores.
        /// </summary>
        /// <param name="name">The original identifier name.</param>
        /// <param name="maxLength">The maximum allowed length for the identifier (default: 128).</param>
        /// <param name="hashLength">The length of the hash suffix to append (default: 8).</param>
        /// <returns>A valid SQL Server identifier, sanitized and truncated with hash suffix if necessary.</returns>
        public static string MakeMssqlIdentifier(string name, int maxLength = SqlServerMaxLength, int hashLength = 8)
        {
            return DatabaseIdentifierHelper.MakeIdentifier(name, maxLength, hashLength);
        }

        /// <summary>
        /// Legacy method for simple sanitization (kept for backward compatibility).
        /// For new code, prefer MakeMssqlIdentifier which handles both sanitization and length limits.
        /// </summary>
        /// <param name="name">The original identifier name.</param>
        /// <returns>A sanitized SQL Server identifier with invalid characters replaced.</returns>
        public static string SanitizeIdentifier(string name)
        {
            // For simple cases that don't exceed length limits, just sanitize
            return name.Replace("-", "_").Replace(".", "_");
        }
    }
}
