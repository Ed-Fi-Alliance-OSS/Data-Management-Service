// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Emits deterministic parameterized <c>UPDATE</c> SQL for 1:1 table write plans (no <c>Ordinal</c> key column).
/// </summary>
public sealed class SimpleUpdateSqlEmitter(SqlDialect dialect)
{
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    /// <summary>
    /// Emits canonical multi-line <c>UPDATE</c> SQL using ordered SET columns and key-ordered WHERE columns.
    /// </summary>
    /// <param name="table">Target table.</param>
    /// <param name="orderedSetColumns">Ordered non-key stored columns to set.</param>
    /// <param name="orderedSetParameterNames">Ordered bare parameter-name list for <paramref name="orderedSetColumns" />.</param>
    /// <param name="orderedKeyColumns">Key columns in key order.</param>
    /// <param name="orderedKeyParameterNames">Ordered bare parameter-name list for <paramref name="orderedKeyColumns" />.</param>
    /// <returns>Canonical SQL ending with <c>;\n</c>.</returns>
    public string Emit(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedSetColumns,
        IReadOnlyList<string> orderedSetParameterNames,
        IReadOnlyList<DbColumnName> orderedKeyColumns,
        IReadOnlyList<string> orderedKeyParameterNames
    )
    {
        ArgumentNullException.ThrowIfNull(orderedSetColumns);
        ArgumentNullException.ThrowIfNull(orderedSetParameterNames);
        ArgumentNullException.ThrowIfNull(orderedKeyColumns);
        ArgumentNullException.ThrowIfNull(orderedKeyParameterNames);

        if (orderedSetColumns.Count == 0)
        {
            throw new ArgumentException(
                "At least one set column must be supplied.",
                nameof(orderedSetColumns)
            );
        }

        if (orderedSetColumns.Count != orderedSetParameterNames.Count)
        {
            throw new ArgumentException(
                $"Set-column and parameter counts must match. Set-column count: {orderedSetColumns.Count}. Parameter count: {orderedSetParameterNames.Count}.",
                nameof(orderedSetParameterNames)
            );
        }

        if (orderedKeyColumns.Count == 0)
        {
            throw new ArgumentException(
                "At least one key column must be supplied.",
                nameof(orderedKeyColumns)
            );
        }

        if (orderedKeyColumns.Count != orderedKeyParameterNames.Count)
        {
            throw new ArgumentException(
                $"Key-column and parameter counts must match. Key-column count: {orderedKeyColumns.Count}. Parameter count: {orderedKeyParameterNames.Count}.",
                nameof(orderedKeyParameterNames)
            );
        }

        var writer = new SqlWriter(_sqlDialect);

        writer.Append("UPDATE ").AppendTable(table).AppendLine();
        writer.AppendLine("SET");

        using (writer.Indent())
        {
            for (var index = 0; index < orderedSetColumns.Count; index++)
            {
                writer
                    .AppendQuoted(orderedSetColumns[index].Value)
                    .Append(" = ")
                    .AppendParameter(orderedSetParameterNames[index]);

                if (index + 1 < orderedSetColumns.Count)
                {
                    writer.AppendLine(",");
                }
                else
                {
                    writer.AppendLine();
                }
            }
        }

        writer.AppendWhereClause(
            orderedKeyColumns.Count,
            (predicateWriter, index) =>
            {
                predicateWriter
                    .AppendQuoted(orderedKeyColumns[index].Value)
                    .Append(" = ")
                    .AppendParameter(orderedKeyParameterNames[index]);
            }
        );
        writer.AppendLine(";");

        return writer.ToString();
    }
}
