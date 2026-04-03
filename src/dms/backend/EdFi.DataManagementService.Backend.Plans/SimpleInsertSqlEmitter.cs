// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Emits deterministic parameterized <c>INSERT</c> SQL for plan-compilation foundations.
/// </summary>
public sealed class SimpleInsertSqlEmitter(SqlDialect dialect)
{
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
            throw new ArgumentException(
                $"Column and parameter counts must match. Column count: {orderedColumns.Count}. Parameter count: {orderedParameterNames.Count}.",
                nameof(orderedParameterNames)
            );
        }

        return EmitBatch(table, orderedColumns, [orderedParameterNames]);
    }

    /// <summary>
    /// Emits canonical multi-line multi-row <c>INSERT</c> SQL using ordered columns and per-row ordered bare
    /// parameter names.
    /// </summary>
    /// <param name="table">Target table.</param>
    /// <param name="orderedColumns">Ordered column list.</param>
    /// <param name="orderedParameterNamesByRow">
    /// Ordered bare parameter-name lists for each row, aligned to <paramref name="orderedColumns" />.
    /// </param>
    /// <returns>Canonical SQL ending with <c>;\n</c>.</returns>
    public string EmitBatch(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyList<IReadOnlyList<string>> orderedParameterNamesByRow
    )
    {
        ArgumentNullException.ThrowIfNull(orderedColumns);
        ArgumentNullException.ThrowIfNull(orderedParameterNamesByRow);

        for (var rowIndex = 0; rowIndex < orderedParameterNamesByRow.Count; rowIndex++)
        {
            var orderedParameterNames = orderedParameterNamesByRow[rowIndex];

            if (orderedParameterNames is null)
            {
                continue;
            }

            foreach (var bareName in orderedParameterNames)
            {
                PlanSqlWriterExtensions.ValidateBareParameterName(bareName, nameof(bareName));
            }
        }

        return WriteBatchSqlSupport.EmitInsertSql(dialect, table, orderedColumns, orderedParameterNamesByRow);
    }
}
