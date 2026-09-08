// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.External;

internal static class WriteBatchSqlSupport
{
    public static string EmitInsertSql(
        SqlDialect dialect,
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyList<IReadOnlyList<string>> orderedParameterNamesByRow
    )
    {
        ArgumentNullException.ThrowIfNull(orderedColumns);
        ArgumentNullException.ThrowIfNull(orderedParameterNamesByRow);

        ValidateInsertShape(orderedColumns, orderedParameterNamesByRow);

        StringBuilder builder = new();

        builder.Append("INSERT INTO ");
        AppendQualifiedTable(builder, dialect, table);
        builder.Append('\n');
        AppendParenthesizedLines(
            builder,
            orderedColumns.Count,
            index => builder.Append(QuoteIdentifier(dialect, orderedColumns[index].Value))
        );
        builder.Append("VALUES\n");

        for (var rowIndex = 0; rowIndex < orderedParameterNamesByRow.Count; rowIndex++)
        {
            var orderedParameterNames = orderedParameterNamesByRow[rowIndex];

            AppendParenthesizedValueLines(
                builder,
                orderedParameterNames.Count,
                index => builder.Append('@').Append(orderedParameterNames[index]),
                appendTrailingComma: rowIndex + 1 < orderedParameterNamesByRow.Count
            );
        }

        builder.Append(";\n");

        return builder.ToString();
    }

    public static string EmitSuffixedInsertSql(
        SqlDialect dialect,
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyList<string> orderedParameterNames,
        int rowCount
    )
    {
        ArgumentNullException.ThrowIfNull(orderedColumns);
        ArgumentNullException.ThrowIfNull(orderedParameterNames);

        ValidateRowCount(rowCount);

        var orderedParameterNamesByRow = Enumerable
            .Range(0, rowCount)
            .Select(rowIndex => SuffixParameterNames(orderedParameterNames, rowIndex))
            .ToArray();

        return EmitInsertSql(dialect, table, orderedColumns, orderedParameterNamesByRow);
    }

    public static string[] SuffixParameterNames(IReadOnlyList<string> orderedParameterNames, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(orderedParameterNames);

        var suffixedParameterNames = new string[orderedParameterNames.Count];

        for (var index = 0; index < orderedParameterNames.Count; index++)
        {
            suffixedParameterNames[index] = BuildBatchParameterName(orderedParameterNames[index], rowIndex);
        }

        return suffixedParameterNames;
    }

    public static string BuildBatchParameterName(string parameterName, int rowIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

        if (rowIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowIndex),
                rowIndex,
                "Row index cannot be negative."
            );
        }

        var bareParameterName = parameterName.TrimStart('@');

        if (bareParameterName.Length == 0)
        {
            throw new ArgumentException(
                "Parameter name cannot be only the parameter prefix.",
                nameof(parameterName)
            );
        }

        return $"{bareParameterName}_{rowIndex}";
    }

    public static void ValidateRowCount(int rowCount)
    {
        if (rowCount >= 1)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be at least 1.");
    }

    private static void ValidateInsertShape(
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyList<IReadOnlyList<string>> orderedParameterNamesByRow
    )
    {
        if (orderedColumns.Count == 0)
        {
            throw new ArgumentException("At least one column must be supplied.", nameof(orderedColumns));
        }

        if (orderedParameterNamesByRow.Count == 0)
        {
            throw new ArgumentException(
                "At least one parameter row must be supplied.",
                nameof(orderedParameterNamesByRow)
            );
        }

        for (var rowIndex = 0; rowIndex < orderedParameterNamesByRow.Count; rowIndex++)
        {
            var orderedParameterNames = orderedParameterNamesByRow[rowIndex];

            if (orderedParameterNames is null)
            {
                throw new ArgumentException(
                    $"Parameter row at index {rowIndex} cannot be null.",
                    nameof(orderedParameterNamesByRow)
                );
            }

            if (orderedColumns.Count == orderedParameterNames.Count)
            {
                continue;
            }

            throw new ArgumentException(
                $"Column and parameter counts must match for row {rowIndex}. Column count: {orderedColumns.Count}. Parameter count: {orderedParameterNames.Count}.",
                nameof(orderedParameterNamesByRow)
            );
        }
    }

    private static void AppendQualifiedTable(StringBuilder builder, SqlDialect dialect, DbTableName table)
    {
        builder.Append(QuoteIdentifier(dialect, table.Schema.Value));
        builder.Append('.');
        builder.Append(QuoteIdentifier(dialect, table.Name));
    }

    private static void AppendParenthesizedLines(StringBuilder builder, int itemCount, Action<int> appendItem)
    {
        builder.Append("(\n");

        for (var index = 0; index < itemCount; index++)
        {
            builder.Append("    ");
            appendItem(index);
            builder.Append(index + 1 < itemCount ? ",\n" : "\n");
        }

        builder.Append(")\n");
    }

    private static void AppendParenthesizedValueLines(
        StringBuilder builder,
        int itemCount,
        Action<int> appendItem,
        bool appendTrailingComma
    )
    {
        builder.Append("(\n");

        for (var index = 0; index < itemCount; index++)
        {
            builder.Append("    ");
            appendItem(index);
            builder.Append(index + 1 < itemCount ? ",\n" : "\n");
        }

        builder.Append(appendTrailingComma ? "),\n" : ")\n");
    }

    private static string QuoteIdentifier(SqlDialect dialect, string identifier)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
            SqlDialect.Mssql => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }
}
