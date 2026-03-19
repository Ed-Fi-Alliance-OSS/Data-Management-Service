// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Identifies a column's membership in a reference group for ordering purposes.
/// </summary>
/// <param name="GroupKey">The FK column name anchoring the group (for inter-group sort).</param>
/// <param name="Position">0 = the FK itself, 1+ = identity parts in binding order.</param>
internal readonly record struct ReferenceGroupMembership(string GroupKey, int Position);

/// <summary>
/// Provides canonical ordering rules for columns and constraints within derived relational tables.
/// </summary>
internal static class RelationalModelOrdering
{
    /// <summary>
    /// Returns a copy of the table with columns and constraints ordered deterministically.
    /// </summary>
    public static DbTableModel CanonicalizeTable(
        DbTableModel table,
        IReadOnlyDictionary<string, ReferenceGroupMembership>? referenceGroups = null
    )
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);
        var orderedColumns = OrderColumns(table, keyColumnOrder, referenceGroups);
        var columnOrderLookup = BuildColumnOrderLookup(orderedColumns);

        var orderedConstraints = table
            .Constraints.OrderBy(GetConstraintGroup)
            .ThenBy(GetConstraintName, StringComparer.Ordinal)
            .ToArray();
        var canonicalIdentityMetadata = CanonicalizeIdentityMetadata(
            table.Table,
            table.IdentityMetadata,
            columnOrderLookup
        );

        return table with
        {
            Columns = orderedColumns,
            Constraints = orderedConstraints,
            IdentityMetadata = canonicalIdentityMetadata,
        };
    }

    /// <summary>
    /// Orders columns deterministically while honoring unified-alias dependency invariants:
    /// canonical/presence columns always precede dependent aliases.
    /// </summary>
    private static DbColumnModel[] OrderColumns(
        DbTableModel table,
        IReadOnlyDictionary<string, int> keyColumnOrder,
        IReadOnlyDictionary<string, ReferenceGroupMembership>? referenceGroups
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

        SortedSet<string> availableColumns = new(
            GetColumnOrderingComparer(columnsByName, keyColumnOrder, referenceGroups)
        );

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
    /// Builds a lookup from column name to canonical ordinal within the ordered table column list.
    /// </summary>
    private static IReadOnlyDictionary<string, int> BuildColumnOrderLookup(
        IReadOnlyList<DbColumnModel> orderedColumns
    )
    {
        Dictionary<string, int> orderLookup = new(StringComparer.Ordinal);

        for (var index = 0; index < orderedColumns.Count; index++)
        {
            orderLookup[orderedColumns[index].ColumnName.Value] = index;
        }

        return orderLookup;
    }

    /// <summary>
    /// Canonicalizes stable-identity metadata collections to match final table column ordering while preserving
    /// the compiled semantic-binding sequence.
    /// </summary>
    private static DbTableIdentityMetadata CanonicalizeIdentityMetadata(
        DbTableName table,
        DbTableIdentityMetadata identityMetadata,
        IReadOnlyDictionary<string, int> columnOrderLookup
    )
    {
        var orderedPhysicalRowIdentityColumns = CanonicalizeMetadataColumns(
            table,
            "physical row identity",
            identityMetadata.PhysicalRowIdentityColumns,
            columnOrderLookup,
            out var physicalRowIdentityColumnsChanged
        );
        var orderedRootScopeLocatorColumns = CanonicalizeMetadataColumns(
            table,
            "root scope locator",
            identityMetadata.RootScopeLocatorColumns,
            columnOrderLookup,
            out var rootScopeLocatorColumnsChanged
        );
        var orderedImmediateParentScopeLocatorColumns = CanonicalizeMetadataColumns(
            table,
            "immediate parent scope locator",
            identityMetadata.ImmediateParentScopeLocatorColumns,
            columnOrderLookup,
            out var immediateParentScopeLocatorColumnsChanged
        );
        var canonicalSemanticIdentityBindings = CanonicalizeSemanticIdentityBindings(
            table,
            identityMetadata.SemanticIdentityBindings,
            columnOrderLookup
        );

        if (
            !physicalRowIdentityColumnsChanged
            && !rootScopeLocatorColumnsChanged
            && !immediateParentScopeLocatorColumnsChanged
        )
        {
            return identityMetadata;
        }

        return identityMetadata with
        {
            PhysicalRowIdentityColumns = orderedPhysicalRowIdentityColumns,
            RootScopeLocatorColumns = orderedRootScopeLocatorColumns,
            ImmediateParentScopeLocatorColumns = orderedImmediateParentScopeLocatorColumns,
            SemanticIdentityBindings = canonicalSemanticIdentityBindings,
        };
    }

    /// <summary>
    /// Orders one identity-metadata column collection to match the table's canonical column order.
    /// </summary>
    private static IReadOnlyList<DbColumnName> CanonicalizeMetadataColumns(
        DbTableName table,
        string metadataRole,
        IReadOnlyList<DbColumnName> columns,
        IReadOnlyDictionary<string, int> columnOrderLookup,
        out bool changed
    )
    {
        changed = false;

        if (columns.Count == 0)
        {
            return columns;
        }

        foreach (var column in columns)
        {
            GetCanonicalColumnOrdinal(table, metadataRole, column, columnOrderLookup);
        }

        if (columns.Count == 1)
        {
            return columns;
        }

        var orderedColumns = columns
            .OrderBy(column => GetCanonicalColumnOrdinal(table, metadataRole, column, columnOrderLookup))
            .ThenBy(column => column.Value, StringComparer.Ordinal)
            .ToArray();

        changed = !orderedColumns.SequenceEqual(columns);

        return changed ? orderedColumns : columns;
    }

    /// <summary>
    /// Validates semantic-identity binding column references against the canonical table columns while
    /// preserving the compiled binding order.
    /// </summary>
    private static IReadOnlyList<CollectionSemanticIdentityBinding> CanonicalizeSemanticIdentityBindings(
        DbTableName table,
        IReadOnlyList<CollectionSemanticIdentityBinding> semanticIdentityBindings,
        IReadOnlyDictionary<string, int> columnOrderLookup
    )
    {
        if (semanticIdentityBindings.Count == 0)
        {
            return semanticIdentityBindings;
        }

        foreach (var binding in semanticIdentityBindings)
        {
            GetCanonicalColumnOrdinal(
                table,
                $"semantic identity binding '{binding.RelativePath.Canonical}'",
                binding.ColumnName,
                columnOrderLookup
            );
        }

        return semanticIdentityBindings;
    }

    /// <summary>
    /// Returns the canonical ordered-column ordinal for one metadata column reference.
    /// </summary>
    private static int GetCanonicalColumnOrdinal(
        DbTableName table,
        string metadataRole,
        DbColumnName column,
        IReadOnlyDictionary<string, int> columnOrderLookup
    )
    {
        if (columnOrderLookup.TryGetValue(column.Value, out var ordinal))
        {
            return ordinal;
        }

        throw new InvalidOperationException(
            $"Stable identity metadata {metadataRole} column '{column.Value}' on table '{table}' "
                + "does not exist in the table column list."
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
        IReadOnlyDictionary<string, int> keyColumnOrder,
        IReadOnlyDictionary<string, ReferenceGroupMembership>? referenceGroups
    )
    {
        return Comparer<string>.Create(
            (left, right) =>
            {
                var leftColumn = columnsByName[left];
                var rightColumn = columnsByName[right];

                var groupComparison = GetColumnGroup(leftColumn, keyColumnOrder, referenceGroups)
                    .CompareTo(GetColumnGroup(rightColumn, keyColumnOrder, referenceGroups));

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

                // Sub-sort within reference groups: order by GroupKey then Position.
                if (referenceGroups is not null)
                {
                    var leftInGroup = referenceGroups.TryGetValue(left, out var leftMembership);
                    var rightInGroup = referenceGroups.TryGetValue(right, out var rightMembership);

                    if (leftInGroup && rightInGroup)
                    {
                        var groupKeyComparison = string.Compare(
                            leftMembership.GroupKey,
                            rightMembership.GroupKey,
                            StringComparison.Ordinal
                        );

                        if (groupKeyComparison != 0)
                        {
                            return groupKeyComparison;
                        }

                        var positionComparison = leftMembership.Position.CompareTo(rightMembership.Position);

                        if (positionComparison != 0)
                        {
                            return positionComparison;
                        }
                    }
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
    private static int GetColumnGroup(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder,
        IReadOnlyDictionary<string, ReferenceGroupMembership>? referenceGroups = null
    )
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

        // Reference group members (identity-part columns) are promoted to group 2
        // so they stay adjacent to their DocumentFk column.
        if (referenceGroups is not null && referenceGroups.ContainsKey(column.ColumnName.Value))
        {
            return 2;
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
