// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Extracts the shared candidate region of compiled page SQL so traditional, cursor, and unpaged
/// plans can be compared on everything except the clauses their mode owns.
/// </summary>
/// <remarks>
/// The shared region runs from the <c>FROM</c> line through the end of the <c>WHERE</c> clause. The
/// cursor bound lines are removed by exact match against the text the compiler emits for the plan's
/// own parameter names, so this never parses SQL heuristically: it only subtracts text it can
/// regenerate.
/// </remarks>
internal static class CandidateSqlRegions
{
    /// <summary>
    /// Returns the candidate root, joins, filter predicates, and authorization fragment, excluding the
    /// mode-owned select prefix, range predicates, ordering clause, and size clause.
    /// </summary>
    public static string SharedCandidateRegion(string sql, PageCandidateMode mode, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(mode);

        var lines = sql.Split('\n');
        var fromIndex = Array.FindIndex(
            lines,
            static line => line.StartsWith("FROM ", StringComparison.Ordinal)
        );

        if (fromIndex < 0)
        {
            throw new InvalidOperationException($"Compiled page SQL has no FROM clause:\n{sql}");
        }

        var endExclusiveIndex = Array.FindIndex(
            lines,
            fromIndex,
            static line =>
                line.StartsWith("ORDER BY ", StringComparison.Ordinal)
                || string.Equals(line, ";", StringComparison.Ordinal)
        );

        if (endExclusiveIndex < 0)
        {
            throw new InvalidOperationException(
                $"Compiled page SQL has neither an ORDER BY clause nor a statement terminator:\n{sql}"
            );
        }

        var regionLines = lines[fromIndex..endExclusiveIndex];

        if (mode is PageCandidateMode.Cursor cursor)
        {
            var cursorBoundLines = BuildCursorBoundLines(cursor, dialect);
            regionLines =
            [
                .. regionLines.Where(line => !cursorBoundLines.Contains(line, StringComparer.Ordinal)),
            ];
        }

        return string.Join("\n", regionLines);
    }

    /// <summary>
    /// Returns the plan's filter-role parameters, preserving inventory order.
    /// </summary>
    public static IReadOnlyList<QuerySqlParameter> FilterParameters(PageDocumentIdSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return
        [
            .. plan.PageParametersInOrder.Where(static parameter =>
                parameter.Role is QuerySqlParameterRole.Filter
            ),
        ];
    }

    private static IReadOnlyList<string> BuildCursorBoundLines(
        PageCandidateMode.Cursor cursor,
        SqlDialect dialect
    )
    {
        var quotedDocumentId = dialect switch
        {
            SqlDialect.Pgsql => "\"DocumentId\"",
            SqlDialect.Mssql => "[DocumentId]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

        // Both spellings: the first predicate in a WHERE clause is emitted without the AND prefix, which
        // is what a cursor bound looks like when no filter or authorization predicate precedes it.
        return
        [
            $"    (r.{quotedDocumentId} >= @{cursor.InclusiveMinimumParameterName})",
            $"    AND (r.{quotedDocumentId} >= @{cursor.InclusiveMinimumParameterName})",
            $"    (r.{quotedDocumentId} <= @{cursor.InclusiveMaximumParameterName})",
            $"    AND (r.{quotedDocumentId} <= @{cursor.InclusiveMaximumParameterName})",
        ];
    }
}
