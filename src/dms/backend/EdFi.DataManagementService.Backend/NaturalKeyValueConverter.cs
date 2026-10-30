// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Converts request-side natural-key identity strings into the typed values a natural-key match binds,
/// shared by the reference resolver's batch lookup and the POST target capture. A value that cannot be
/// stored in its key column converts to <see langword="null"/>: no stored row can match it, so callers
/// treat it as a definite miss instead of binding it.
/// </summary>
internal static class NaturalKeyValueConverter
{
    public static object? ConvertScalar(SqlDialect dialect, string rawValue, RelationalScalarType scalarType)
    {
        if (!RelationalScalarLiteralParser.TryParse(rawValue, scalarType, out var value))
        {
            return null;
        }

        return
            scalarType.Kind is ScalarKind.String
            && (
                // PostgreSQL text cannot hold NUL, so binding it would fail the whole lookup.
                (dialect == SqlDialect.Pgsql && rawValue.Contains('\0'))
                || (scalarType.MaxLength is { } maxLength && StoredLength(dialect, rawValue) > maxLength)
            )
            ? null
            : value;
    }

    /// <summary>
    /// Whether a descriptor URI can match a stored one. SQL Server's identity collation ignores unweighted
    /// characters, so there the database decides any URI its lookup can read.
    /// </summary>
    public static bool FitsDescriptorUri(SqlDialect dialect, string uri) =>
        StoredLength(dialect, uri)
        <= (
            dialect == SqlDialect.Mssql
                ? NaturalKeyLookupLimits.MaxMssqlDescriptorUriProbeLength
                : NaturalKeyLookupLimits.MaxDescriptorUriLength
        );

    /// <summary>
    /// The length a string column measures: SQL Server <c>nvarchar(n)</c> counts UTF-16 code units, and
    /// PostgreSQL <c>varchar(n)</c> counts characters.
    /// </summary>
    public static int StoredLength(SqlDialect dialect, string value) =>
        dialect == SqlDialect.Mssql ? value.Length : value.EnumerateRunes().Count();
}
