// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Provides canonical ordering rules for columns and constraints within derived relational tables.
/// </summary>
internal static class RelationalModelOrdering
{
    /// <summary>
    /// Returns a copy of the table with columns and constraints ordered deterministically.
    /// </summary>
    public static DbTableModel CanonicalizeTable(DbTableModel table)
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);
        var orderedColumns = OrderColumns(table, keyColumnOrder);

        var orderedConstraints = table
            .Constraints.OrderBy(GetConstraintGroup)
            .ThenBy(GetConstraintName, StringComparer.Ordinal)
            .ToArray();

        return table with
        {
            Columns = orderedColumns,
            Constraints = orderedConstraints,
        };
    }

    /// <summary>
    /// Orders columns deterministically while honoring unified-alias dependency invariants:
    /// canonical/presence columns always precede dependent aliases.
    /// </summary>
    private static DbColumnModel[] OrderColumns(
        DbTableModel table,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        Dictionary<string, DbColumnModel> columnsByName = new(StringComparer.Ordinal);

        foreach (var column in table.Columns)
        {
            if (columnsByName.TryAdd(column.ColumnName.Value, column))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Duplicate column '{column.ColumnName.Value}' encountered while canonicalizing "
                    + $"table '{table.Table}'."
            );
        }

        Dictionary<string, int> dependencyCountByColumn = columnsByName.Keys.ToDictionary(
            columnName => columnName,
            _ => 0,
            StringComparer.Ordinal
        );
        Dictionary<string, List<string>> dependentsByDependencyColumn = new(StringComparer.Ordinal);
        HashSet<(string Dependency, string Dependent)> dependencyEdges = [];

        foreach (var column in table.Columns)
        {
            if (column.Storage is not ColumnStorage.UnifiedAlias unifiedAlias)
            {
                continue;
            }

            AddAliasDependency(
                table.Table,
                column.ColumnName,
                unifiedAlias.CanonicalColumn,
                "canonical column",
                dependencyCountByColumn,
                dependentsByDependencyColumn,
                dependencyEdges
            );

            if (unifiedAlias.PresenceColumn is not { } presenceColumn)
            {
                continue;
            }

            AddAliasDependency(
                table.Table,
                column.ColumnName,
                presenceColumn,
                "presence-gate column",
                dependencyCountByColumn,
                dependentsByDependencyColumn,
                dependencyEdges
            );
        }

        SortedSet<string> availableColumns = new(GetColumnOrderingComparer(columnsByName, keyColumnOrder));

        foreach (var columnName in columnsByName.Keys)
        {
            if (dependencyCountByColumn[columnName] == 0)
            {
                availableColumns.Add(columnName);
            }
        }

        List<DbColumnModel> orderedColumns = new(columnsByName.Count);

        while (availableColumns.Count > 0)
        {
            var nextColumnName = availableColumns.Min!;
            availableColumns.Remove(nextColumnName);
            orderedColumns.Add(columnsByName[nextColumnName]);

            if (!dependentsByDependencyColumn.TryGetValue(nextColumnName, out var dependents))
            {
                continue;
            }

            foreach (var dependent in dependents)
            {
                dependencyCountByColumn[dependent]--;

                if (dependencyCountByColumn[dependent] == 0)
                {
                    availableColumns.Add(dependent);
                }
            }
        }

        if (orderedColumns.Count == table.Columns.Count)
        {
            return orderedColumns.ToArray();
        }

        var blockedColumns = dependencyCountByColumn
            .Where(entry => entry.Value > 0)
            .Select(entry => entry.Key)
            .OrderBy(columnName => columnName, StringComparer.Ordinal);

        throw new InvalidOperationException(
            $"Detected circular unified alias column dependencies while canonicalizing "
                + $"table '{table.Table}': {string.Join(", ", blockedColumns)}."
        );
    }

    /// <summary>
    /// Registers one alias dependency edge from <paramref name="dependencyColumn"/> to
    /// <paramref name="aliasColumn"/>.
    /// </summary>
    private static void AddAliasDependency(
        DbTableName table,
        DbColumnName aliasColumn,
        DbColumnName dependencyColumn,
        string dependencyRole,
        IDictionary<string, int> dependencyCountByColumn,
        IDictionary<string, List<string>> dependentsByDependencyColumn,
        ISet<(string Dependency, string Dependent)> dependencyEdges
    )
    {
        var aliasColumnName = aliasColumn.Value;
        var dependencyColumnName = dependencyColumn.Value;

        if (!dependencyCountByColumn.ContainsKey(dependencyColumnName))
        {
            throw new InvalidOperationException(
                $"Unified alias column '{aliasColumnName}' on table '{table}' references missing "
                    + $"{dependencyRole} '{dependencyColumnName}'."
            );
        }

        if (string.Equals(aliasColumnName, dependencyColumnName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unified alias column '{aliasColumnName}' on table '{table}' cannot depend on itself."
            );
        }

        if (!dependencyEdges.Add((dependencyColumnName, aliasColumnName)))
        {
            return;
        }

        dependencyCountByColumn[aliasColumnName] = dependencyCountByColumn[aliasColumnName] + 1;

        if (!dependentsByDependencyColumn.TryGetValue(dependencyColumnName, out var dependents))
        {
            dependents = [];
            dependentsByDependencyColumn[dependencyColumnName] = dependents;
        }

        dependents.Add(aliasColumnName);
    }

    /// <summary>
    /// Builds a deterministic comparer aligned to existing key/descriptor/scalar grouping rules.
    /// </summary>
    private static IComparer<string> GetColumnOrderingComparer(
        IReadOnlyDictionary<string, DbColumnModel> columnsByName,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return Comparer<string>.Create(
            (left, right) =>
            {
                var leftColumn = columnsByName[left];
                var rightColumn = columnsByName[right];

                var groupComparison = GetColumnGroup(leftColumn, keyColumnOrder)
                    .CompareTo(GetColumnGroup(rightColumn, keyColumnOrder));

                if (groupComparison != 0)
                {
                    return groupComparison;
                }

                var keyIndexComparison = GetColumnKeyIndex(leftColumn, keyColumnOrder)
                    .CompareTo(GetColumnKeyIndex(rightColumn, keyColumnOrder));

                if (keyIndexComparison != 0)
                {
                    return keyIndexComparison;
                }

                return string.Compare(left, right, StringComparison.Ordinal);
            }
        );
    }

    /// <summary>
    /// Builds a lookup that maps key-column names to their ordinal position within the primary key.
    /// </summary>
    private static Dictionary<string, int> BuildKeyColumnOrder(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        Dictionary<string, int> keyOrder = new(StringComparer.Ordinal);

        for (var index = 0; index < keyColumns.Count; index++)
        {
            keyOrder[keyColumns[index].ColumnName.Value] = index;
        }

        return keyOrder;
    }

    /// <summary>
    /// Returns a grouping value to order columns per spec: key → unification support →
    /// reference groups → descriptor FK → scalar → other.
    /// </summary>
    private static int GetColumnGroup(DbColumnModel column, IReadOnlyDictionary<string, int> keyColumnOrder)
    {
        // Group 0: Key columns
        if (keyColumnOrder.ContainsKey(column.ColumnName.Value))
        {
            return 0;
        }

        // Group 1: Unification support columns (canonical storage + presence flags).
        // These are Stored columns with no SourceJsonPath — they hold the canonical unified
        // value or act as presence indicators for key unification.
        if (column.Storage is ColumnStorage.Stored && column.SourceJsonPath is null)
        {
            return 1;
        }

        return column.Kind switch
        {
            // Group 2: Reference groups (DocumentFk + related identity parts)
            ColumnKind.DocumentFk => 2,
            // Group 3: Descriptor FKs
            ColumnKind.DescriptorFk => 3,
            // Group 4: Scalar columns
            ColumnKind.Scalar => 4,
            // Group 5: Other (Ordinal, ParentKeyPart, etc.)
            _ => 5,
        };
    }

    /// <summary>
    /// Returns the primary-key ordinal index for key columns and <see cref="int.MaxValue"/> for others.
    /// </summary>
    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    /// <summary>
    /// Returns a grouping value used to order constraints deterministically.
    /// </summary>
    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            TableConstraint.AllOrNoneNullability => 3,
            TableConstraint.NullOrTrue => 4,
            _ => 99,
        };
    }

    /// <summary>
    /// Returns the constraint name used as a secondary ordering key.
    /// </summary>
    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            TableConstraint.NullOrTrue nullOrTrue => nullOrTrue.Name,
            _ => string.Empty,
        };
    }
}
