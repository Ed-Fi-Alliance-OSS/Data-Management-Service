// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class ReadPlanSqlShape
{
    public static IReadOnlyList<string> ExtractSelectedColumnNames(string sql) =>
        ExtractSelectedColumnExpressions(sql).Select(ExtractQualifiedColumnName).ToArray();

    public static IReadOnlyList<string> ExtractOrderByColumnNames(string sql) =>
        ExtractOrderByColumnExpressions(sql).Select(ExtractQualifiedColumnName).ToArray();

    public static IReadOnlyList<string> ExtractSelectedColumnExpressions(string sql) =>
        ExtractSqlSectionExpressions(sql, "SELECT", "FROM");

    public static IReadOnlyList<string> ExtractOrderByColumnExpressions(string sql) =>
        ExtractSqlSectionExpressions(sql, "ORDER BY", ";", " ASC");

    public static string ExtractFromAlias(string sql) => ExtractTrailingAlias(sql, "FROM ");

    public static string ExtractJoinAlias(string sql)
    {
        var joinLine = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Trim())
            .Single(segment => segment.StartsWith("INNER JOIN ", StringComparison.Ordinal));
        var relationWithAlias = joinLine["INNER JOIN ".Length..];
        var onClauseIndex = relationWithAlias.IndexOf(" ON ", StringComparison.Ordinal);

        if (onClauseIndex <= 0)
        {
            throw new InvalidOperationException(
                $"Hydration SQL join line is missing an ON clause: '{joinLine}'."
            );
        }

        var relationTokens = relationWithAlias[..onClauseIndex]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return relationTokens[^1];
    }

    private static IReadOnlyList<string> ExtractSqlSectionExpressions(
        string sql,
        string sectionStart,
        string sectionEnd,
        string trailingSuffixToTrim = ""
    )
    {
        List<string> expressions = [];
        var inSection = false;

        foreach (var rawLine in sql.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (line == sectionStart)
            {
                inSection = true;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            if (line.StartsWith(sectionEnd, StringComparison.Ordinal))
            {
                break;
            }

            var expression = line.TrimEnd(',');

            if (trailingSuffixToTrim.Length > 0)
            {
                if (!expression.EndsWith(trailingSuffixToTrim, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Hydration SQL line '{line}' does not end with expected suffix '{trailingSuffixToTrim}'."
                    );
                }

                expression = expression[..^trailingSuffixToTrim.Length];
            }

            expressions.Add(expression);
        }

        if (expressions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Hydration SQL section '{sectionStart}' did not contain any expressions."
            );
        }

        return expressions;
    }

    private static string ExtractQualifiedColumnName(string expression)
    {
        var qualifierSeparatorIndex = expression.IndexOf('.', StringComparison.Ordinal);

        if (qualifierSeparatorIndex < 0)
        {
            throw new InvalidOperationException(
                $"Hydration SQL expression '{expression}' is missing a qualified column name."
            );
        }

        return expression[(qualifierSeparatorIndex + 1)..].Trim('"', '[', ']');
    }

    private static string ExtractTrailingAlias(string sql, string linePrefix)
    {
        var line = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Trim())
            .Single(segment => segment.StartsWith(linePrefix, StringComparison.Ordinal));
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return tokens[^1];
    }
}
