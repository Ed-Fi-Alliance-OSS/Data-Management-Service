// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Emits deterministic parameterized <c>DELETE</c> SQL for replace semantics by parent key.
/// </summary>
public sealed class SimpleDeleteSqlEmitter(SqlDialect dialect)
{
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    /// <summary>
    /// Emits canonical multi-line <c>DELETE</c> SQL using ordered <c>WHERE</c> predicate columns.
    /// </summary>
    /// <param name="table">Target table.</param>
    /// <param name="orderedWhereColumns">Ordered columns to predicate in <c>WHERE</c>.</param>
    /// <param name="orderedWhereParameterNames">Ordered bare parameter names for <paramref name="orderedWhereColumns" />.</param>
    /// <returns>Canonical SQL ending with <c>;\n</c>.</returns>
    public string Emit(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedWhereColumns,
        IReadOnlyList<string> orderedWhereParameterNames
    )
    {
        ArgumentNullException.ThrowIfNull(orderedWhereColumns);
        ArgumentNullException.ThrowIfNull(orderedWhereParameterNames);

        if (orderedWhereColumns.Count == 0)
        {
            throw new ArgumentException(
                "At least one where column must be supplied.",
                nameof(orderedWhereColumns)
            );
        }

        if (orderedWhereColumns.Count != orderedWhereParameterNames.Count)
        {
            throw new ArgumentException(
                $"Where-column and parameter counts must match. Where-column count: {orderedWhereColumns.Count}. Parameter count: {orderedWhereParameterNames.Count}.",
                nameof(orderedWhereParameterNames)
            );
        }

        var writer = new SqlWriter(_sqlDialect);

        writer.Append("DELETE FROM ").AppendTable(table).AppendLine();
        writer.AppendWhereClause(
            orderedWhereColumns.Count,
            (predicateWriter, index) =>
            {
                predicateWriter
                    .AppendQuoted(orderedWhereColumns[index].Value)
                    .Append(" = ")
                    .AppendParameter(orderedWhereParameterNames[index]);
            }
        );
        writer.AppendLine(";");

        return writer.ToString();
    }
}
