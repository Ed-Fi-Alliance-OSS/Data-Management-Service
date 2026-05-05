// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Single source of truth for converting an identity column value to its canonical
/// text form inside generated SQL. Used by both ReferentialIdentity trigger emission
/// (Backend.Ddl) and reference-lookup verification (Backend.Postgresql / Backend.Mssql)
/// so the trigger-stored ReferentialId text and the runtime-computed verification key
/// cannot drift.
///
/// Output must match Core's <c>IdentityValueCanonicalizer</c>: fixed-point decimals
/// with no trailing fractional zeros, no trailing decimal point, ISO 8601 'Z'-suffixed
/// UTC datetimes, and 'true'/'false' booleans.
/// </summary>
public static class DialectIdentityTextFormatter
{
    /// <summary>
    /// Returns a PostgreSQL expression that converts <paramref name="columnExpression"/>
    /// (e.g. <c>NEW."Foo"</c>, <c>s."Foo"</c>) to its canonical identity text form.
    /// </summary>
    public static string PgsqlColumnToText(string columnExpression, RelationalScalarType scalarType)
    {
        ArgumentNullException.ThrowIfNull(columnExpression);
        ArgumentNullException.ThrowIfNull(scalarType);

        return scalarType.Kind switch
        {
            // PG timestamp::text gives 'YYYY-MM-DD HH:MM:SS' (space, no T). Use to_char with
            // AT TIME ZONE 'UTC' to produce ISO 8601 with the T separator and a literal Z,
            // independent of the session timezone. Core normalizes incoming DateTime values
            // to UTC before persistence, so AT TIME ZONE 'UTC' here reproduces that canonical
            // UTC form.
            ScalarKind.DateTime =>
                $"to_char({columnExpression} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')",

            // Decimal columns carry column-scale trailing zeros (e.g. decimal(9,2) renders
            // 1.50, 2.00) that Core never hashes. Two nested regexp_replace calls strip
            // trailing fractional zeros and then a lone trailing decimal point so the SQL
            // output matches Core's canonical decimal form.
            //   regexp_replace(<col>::text, '(\.[0-9]*?)0+$', '\1')  → 1.50→1.5, 2.00→2.
            //   outer regexp_replace(..., '\.$', '')                 → 2.→2, 100→100.
            // Cases verified: 1.50→1.5, 2.00→2, 100.00→100, 1.5→1.5, 100→100, 0→0, -1.50→-1.5.
            ScalarKind.Decimal =>
                $"""regexp_replace(regexp_replace({columnExpression}::text, '(\.[0-9]*?)0+$', '\1'), '\.$', '')""",

            // String, Int32, Int64, Date, Time, Boolean — ::text is deterministic.
            _ => $"{columnExpression}::text",
        };
    }

    /// <summary>
    /// Returns a SQL Server expression that converts <paramref name="columnExpression"/>
    /// (e.g. <c>i.[Foo]</c>, <c>s.[Foo]</c>) to its canonical identity nvarchar form.
    /// </summary>
    public static string MssqlColumnToNvarchar(string columnExpression, RelationalScalarType scalarType)
    {
        ArgumentNullException.ThrowIfNull(columnExpression);
        ArgumentNullException.ThrowIfNull(scalarType);

        return scalarType.Kind switch
        {
            // Already nvarchar — no cast needed.
            ScalarKind.String => columnExpression,

            // ISO 8601: YYYY-MM-DD (CONVERT style 23).
            ScalarKind.Date => $"CONVERT(nvarchar(10), {columnExpression}, 23)",

            // ISO 8601: YYYY-MM-DDTHH:mm:ssZ (CONVERT style 126, truncated to 19 chars,
            // with UTC Z suffix). Truncation to whole seconds matches PG to_char()'s
            // omission of fractional seconds. datetime2 is timezone-naive because Core
            // normalizes incoming offsets to UTC before persistence, so appending Z is
            // sufficient.
            ScalarKind.DateTime => $"CONVERT(nvarchar(19), {columnExpression}, 126) + N'Z'",

            // HH:mm:ss (CONVERT style 108).
            ScalarKind.Time => $"CONVERT(nvarchar(8), {columnExpression}, 108)",

            // CAST(bit AS nvarchar) produces '1'/'0', but Core's JsonValue.ToString() and
            // PG bool::text both produce 'true'/'false'. Use CASE to match.
            ScalarKind.Boolean => $"CASE WHEN {columnExpression} = 1 THEN N'true' ELSE N'false' END",

            // Trim trailing fractional zeros and a trailing decimal point to match Core's
            // canonical form. Deterministic CAST/CHARINDEX/PATINDEX/LEFT/REVERSE — no FORMAT().
            // Example transformations: 1.50 → 1.5, 2.00 → 2, 100.00 → 100, 1.5 → 1.5.
            ScalarKind.Decimal => BuildMssqlDecimalText(columnExpression),

            // Int32, Int64 — CAST is deterministic and matches Core/PG formatting.
            _ => $"CAST({columnExpression} AS nvarchar(max))",
        };
    }

    private static string BuildMssqlDecimalText(string columnExpression)
    {
        var cast = $"CAST({columnExpression} AS nvarchar(max))";
        return $"CASE WHEN CHARINDEX(N'.', {cast}) = 0 THEN {cast} "
            + $"ELSE CASE WHEN RIGHT(LEFT({cast}, LEN({cast}) - PATINDEX('%[^0]%', REVERSE({cast})) + 1), 1) = N'.' "
            + $"THEN LEFT({cast}, LEN({cast}) - PATINDEX('%[^0]%', REVERSE({cast}))) "
            + $"ELSE LEFT({cast}, LEN({cast}) - PATINDEX('%[^0]%', REVERSE({cast})) + 1) END END";
    }
}
