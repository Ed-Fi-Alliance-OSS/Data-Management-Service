// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Emits deterministic parameterized <c>INSERT</c> SQL for plan-compilation foundations.
/// </summary>
public sealed class SimpleInsertSqlEmitter(SqlDialect dialect)
{
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    /// <summary>
    /// Emits canonical multi-line <c>INSERT</c> SQL using ordered columns and ordered bare parameter names.
    /// </summary>
    /// <param name="table">Target table.</param>
    /// <param name="orderedColumns">Ordered column list.</param>
    /// <param name="orderedParameterNames">Ordered bare parameter-name list.</param>
    /// <returns>Canonical SQL ending with <c>;\n</c>.</returns>
    public string Emit(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyList<string> orderedParameterNames
    )
    {
        ArgumentNullException.ThrowIfNull(orderedColumns);
        ArgumentNullException.ThrowIfNull(orderedParameterNames);

        if (orderedColumns.Count == 0)
        {
            throw new ArgumentException("At least one column must be supplied.", nameof(orderedColumns));
        }

        if (orderedColumns.Count != orderedParameterNames.Count)
        {
            throw new InvalidOperationException(
                $"Column and parameter counts must match. Column count: {orderedColumns.Count}. Parameter count: {orderedParameterNames.Count}."
            );
        }

        var writer = new SqlWriter(_sqlDialect);

        writer.Append("INSERT INTO ").AppendTable(table).AppendLine();
        AppendParenthesizedLines(
            writer,
            orderedColumns.Count,
            index => writer.AppendQuoted(orderedColumns[index].Value)
        );
        writer.AppendLine("VALUES");
        AppendParenthesizedLines(
            writer,
            orderedParameterNames.Count,
            index => writer.AppendParameter(orderedParameterNames[index])
        );
        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Emits a parenthesized, one-item-per-line block with deterministic comma placement.
    /// </summary>
    private static void AppendParenthesizedLines(SqlWriter writer, int itemCount, Action<int> appendItem)
    {
        writer.AppendLine("(");

        using (writer.Indent())
        {
            for (var index = 0; index < itemCount; index++)
            {
                appendItem(index);

                if (index + 1 < itemCount)
                {
                    writer.AppendLine(",");
                }
                else
                {
                    writer.AppendLine();
                }
            }
        }

        writer.AppendLine(")");
    }
}
