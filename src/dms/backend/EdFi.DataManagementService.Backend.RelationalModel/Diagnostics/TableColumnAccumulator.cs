// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Diagnostics;

/// <summary>
/// Accumulates columns and constraints for a table while guarding against column name collisions.
/// This is the primary per-table collision guard for derived columns.
/// </summary>
internal sealed class TableColumnAccumulator
{
    private readonly Dictionary<string, ColumnCollisionInfo> _columnOrigins = new(StringComparer.Ordinal);
    private readonly string? _resourceLabel;

    /// <summary>
    /// Creates an accumulator seeded with the table's existing columns and constraints.
    /// </summary>
    /// <param name="table">The table definition to mutate.</param>
    /// <param name="resourceLabel">Optional resource label for diagnostics.</param>
    public TableColumnAccumulator(DbTableModel table, string? resourceLabel = null)
    {
        Definition = table;
        _resourceLabel = resourceLabel;
        Columns = new List<DbColumnModel>(table.Columns);
        Constraints = new List<TableConstraint>(table.Constraints);

        foreach (var column in table.Columns)
        {
            _columnOrigins[column.ColumnName.Value] = new ColumnCollisionInfo(
                column.ColumnName.Value,
                BuildOrigin(column.ColumnName, column.SourceJsonPath)
            );
        }

        foreach (var keyColumn in table.Key.Columns)
        {
            _columnOrigins.TryAdd(
                keyColumn.ColumnName.Value,
                new ColumnCollisionInfo(keyColumn.ColumnName.Value, BuildOrigin(keyColumn.ColumnName, null))
            );
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
    /// <param name="originalName">The pre-override name for collision reporting.</param>
    /// <param name="origin">The origin details used for collision reporting.</param>
    public void AddColumn(
        DbColumnModel column,
        string? originalName = null,
        IdentifierCollisionOrigin? origin = null
    )
    {
        if (_columnOrigins.TryGetValue(column.ColumnName.Value, out var existing))
        {
            var resolvedOriginal = string.IsNullOrWhiteSpace(originalName)
                ? column.ColumnName.Value
                : originalName;
            var resolvedOrigin = origin ?? BuildOrigin(column.ColumnName, column.SourceJsonPath);
            var scope = new IdentifierCollisionScope(
                IdentifierCollisionKind.Column,
                Definition.Table.Schema.Value,
                Definition.Table.Name
            );
            IdentifierCollisionSource[] sources =
            [
                new IdentifierCollisionSource(
                    existing.OriginalName,
                    column.ColumnName.Value,
                    existing.Origin
                ),
                new IdentifierCollisionSource(resolvedOriginal, column.ColumnName.Value, resolvedOrigin),
            ];

            var orderedSources = sources
                .OrderBy(source => source.OriginalIdentifier, StringComparer.Ordinal)
                .ThenBy(source => source.Origin.Description, StringComparer.Ordinal)
                .ThenBy(source => source.Origin.ResourceLabel ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(source => source.Origin.JsonPath ?? string.Empty, StringComparer.Ordinal)
                .ToArray();

            var record = new IdentifierCollisionRecord(
                IdentifierCollisionStage.AfterOverrideNormalization,
                scope,
                orderedSources
            );

            throw new InvalidOperationException(
                "Identifier override collisions detected: " + record.Format()
            );
        }

        var finalOriginal = string.IsNullOrWhiteSpace(originalName) ? column.ColumnName.Value : originalName;
        var finalOrigin = origin ?? BuildOrigin(column.ColumnName, column.SourceJsonPath);

        _columnOrigins.Add(column.ColumnName.Value, new ColumnCollisionInfo(finalOriginal, finalOrigin));
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

    /// <summary>
    /// Builds collision origin details for a column.
    /// </summary>
    private IdentifierCollisionOrigin BuildOrigin(DbColumnName columnName, JsonPathExpression? sourcePath)
    {
        var description =
            $"column {Definition.Table.Schema.Value}.{Definition.Table.Name}.{columnName.Value}";
        var resolvedPath = sourcePath ?? Definition.JsonScope;

        return new IdentifierCollisionOrigin(description, _resourceLabel, resolvedPath.Canonical);
    }

    /// <summary>
    /// Captures the original identifier and origin metadata for collision diagnostics.
    /// </summary>
    private sealed record ColumnCollisionInfo(string OriginalName, IdentifierCollisionOrigin Origin);
}
