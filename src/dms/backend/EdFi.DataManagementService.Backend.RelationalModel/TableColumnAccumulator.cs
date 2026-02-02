// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Accumulates columns and constraints for a table while guarding against column name collisions.
/// </summary>
internal sealed class TableColumnAccumulator
{
    private readonly Dictionary<string, JsonPathExpression?> _columnSources = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an accumulator seeded with the table's existing columns and constraints.
    /// </summary>
    /// <param name="table">The table definition to mutate.</param>
    public TableColumnAccumulator(DbTableModel table)
    {
        Definition = table;
        Columns = new List<DbColumnModel>(table.Columns);
        Constraints = new List<TableConstraint>(table.Constraints);

        foreach (var column in table.Columns)
        {
            _columnSources[column.ColumnName.Value] = column.SourceJsonPath;
        }

        foreach (var keyColumn in table.Key.Columns)
        {
            _columnSources.TryAdd(keyColumn.ColumnName.Value, null);
        }
    }

    /// <summary>
    /// The originating table definition for this accumulator.
    /// </summary>
    public DbTableModel Definition { get; }

    /// <summary>
    /// The accumulated column collection.
    /// </summary>
    public List<DbColumnModel> Columns { get; }

    /// <summary>
    /// The accumulated constraint collection.
    /// </summary>
    public List<TableConstraint> Constraints { get; }

    /// <summary>
    /// Adds a column and throws on name collision.
    /// </summary>
    /// <param name="column">The column to add.</param>
    public void AddColumn(DbColumnModel column)
    {
        if (_columnSources.TryGetValue(column.ColumnName.Value, out var existingSource))
        {
            var tableName = Definition.Table.Name;
            var existingPath = ResolveSourcePath(existingSource);
            var incomingPath = ResolveSourcePath(column.SourceJsonPath);

            throw new InvalidOperationException(
                $"Column name '{column.ColumnName.Value}' is already defined on table '{tableName}'. "
                    + $"Colliding source paths '{existingPath}' and '{incomingPath}'. "
                    + "Use relational.nameOverrides to resolve the collision."
            );
        }

        _columnSources.Add(column.ColumnName.Value, column.SourceJsonPath);
        Columns.Add(column);
    }

    /// <summary>
    /// Adds a constraint to the accumulator.
    /// </summary>
    /// <param name="constraint">The constraint to add.</param>
    public void AddConstraint(TableConstraint constraint)
    {
        Constraints.Add(constraint);
    }

    /// <summary>
    /// Builds the finalized table definition with accumulated columns and constraints.
    /// </summary>
    /// <returns>The updated table model.</returns>
    public DbTableModel Build()
    {
        return Definition with { Columns = Columns.ToArray(), Constraints = Constraints.ToArray() };
    }

    private string ResolveSourcePath(JsonPathExpression? sourcePath)
    {
        return (sourcePath ?? Definition.JsonScope).Canonical;
    }
}
