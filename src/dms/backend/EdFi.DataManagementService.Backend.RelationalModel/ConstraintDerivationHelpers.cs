// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Helper methods used by set-level constraint derivation passes to build deterministic constraint names,
/// look up column bindings, and apply resource-level mutations.
/// </summary>
internal static class ConstraintDerivationHelpers
{
    /// <summary>
    /// Builds a deterministic unique-constraint name for a table from the supplied column list.
    /// </summary>
    internal static string BuildUniqueConstraintName(string tableName, IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Unique constraint must include at least one column.");
        }

        return $"UX_{tableName}_{string.Join("_", columns.Select(column => column.Value))}";
    }

    /// <summary>
    /// Builds a deterministic "all-or-none" check-constraint name for a reference FK column.
    /// </summary>
    internal static string BuildAllOrNoneConstraintName(string tableName, DbColumnName fkColumn)
    {
        return $"CK_{tableName}_{fkColumn.Value}_AllOrNone";
    }

    /// <summary>
    /// Returns true when the constraint set already contains a unique constraint with the given name.
    /// </summary>
    internal static bool ContainsUniqueConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.Unique>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true when the constraint set already contains a foreign-key constraint with the given name.
    /// </summary>
    internal static bool ContainsForeignKeyConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.ForeignKey>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true when the constraint set already contains an all-or-none nullability check with the given
    /// name.
    /// </summary>
    internal static bool ContainsAllOrNoneConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.AllOrNoneNullability>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds a lookup from identity-path canonical JSONPath to the <see cref="DocumentReferenceBinding"/> that
    /// supplies that identity component.
    /// </summary>
    internal static IReadOnlyDictionary<string, DocumentReferenceBinding> BuildReferenceIdentityBindings(
        IReadOnlyList<DocumentReferenceBinding> bindings,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DocumentReferenceBinding> lookup = new(StringComparer.Ordinal);

        foreach (var binding in bindings)
        {
            foreach (var identityBinding in binding.IdentityBindings)
            {
                if (!lookup.TryAdd(identityBinding.ReferenceJsonPath.Canonical, binding))
                {
                    var existing = lookup[identityBinding.ReferenceJsonPath.Canonical];

                    if (existing.ReferenceObjectPath.Canonical == binding.ReferenceObjectPath.Canonical)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Identity path '{identityBinding.ReferenceJsonPath.Canonical}' on resource "
                            + $"'{FormatResource(resource)}' was bound to multiple references."
                    );
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Builds a lookup from source JSONPath canonical string to physical column name, throwing when a single
    /// source path maps to multiple column kinds.
    /// </summary>
    internal static IReadOnlyDictionary<string, DbColumnName> BuildColumnNameLookupBySourceJsonPath(
        DbTableModel table,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, DbColumnName> lookup = new(StringComparer.Ordinal);

        foreach (
            var group in table
                .Columns.Where(column => column.SourceJsonPath is not null)
                .GroupBy(column => column.SourceJsonPath!.Value.Canonical, StringComparer.Ordinal)
        )
        {
            var ordered = group
                .OrderBy(candidate => candidate.ColumnName.Value, StringComparer.Ordinal)
                .ToArray();

            if (ordered.Select(column => column.Kind).Distinct().Skip(1).Any())
            {
                var columnDetails = string.Join(
                    ", ",
                    ordered.Select(column => $"{column.ColumnName.Value} ({column.Kind})")
                );

                throw new InvalidOperationException(
                    $"Table '{table.Table}' on resource '{FormatResource(resource)}' has multiple column "
                        + $"kinds for source path '{group.Key}': {columnDetails}."
                );
            }

            lookup[group.Key] = ordered[0].ColumnName;
        }

        return lookup;
    }

    /// <summary>
    /// Adds a column to a constraint column list exactly once, preserving the first-seen order.
    /// </summary>
    internal static void AddUniqueColumn(
        DbColumnName columnName,
        ICollection<DbColumnName> columns,
        ISet<string> seenColumns
    )
    {
        if (!seenColumns.Add(columnName.Value))
        {
            return;
        }

        columns.Add(columnName);
    }

    /// <summary>
    /// Applies a set of accumulated table mutations to a resource model and updates its root-table pointer.
    /// </summary>
    internal static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        ResourceMutation mutation
    )
    {
        var updatedTables = resourceModel
            .TablesInDependencyOrder.Select(table => mutation.BuildTable(table))
            .ToArray();
        var updatedRoot = updatedTables.Single(table => table.Table.Equals(resourceModel.Root.Table));

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
        };
    }

    /// <summary>
    /// Gets a mutation accumulator for a resource or creates one when it does not exist yet.
    /// </summary>
    internal static ResourceMutation GetOrCreateMutation(
        QualifiedResourceName resource,
        ResourceEntry entry,
        IDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        if (mutations.TryGetValue(resource, out var existing))
        {
            return existing;
        }

        var mutation = new ResourceMutation(entry);
        mutations[resource] = mutation;
        return mutation;
    }

    /// <summary>
    /// Captures a concrete resource model and its index within the builder's canonical resource ordering.
    /// </summary>
    internal sealed record ResourceEntry(int Index, ConcreteResourceModel Model);

    /// <summary>
    /// Accumulates per-table mutations for a concrete resource and produces updated table models.
    /// </summary>
    internal sealed class ResourceMutation
    {
        private readonly HashSet<TableKey> _mutatedTables = new();

        /// <summary>
        /// Creates a mutation accumulator for the resource entry.
        /// </summary>
        /// <param name="entry">The resource entry being mutated.</param>
        public ResourceMutation(ResourceEntry entry)
        {
            Entry = entry;
            TableAccumulators = new Dictionary<TableKey, TableColumnAccumulator>();

            foreach (var table in entry.Model.RelationalModel.TablesInDependencyOrder)
            {
                var key = new TableKey(table.Table, table.JsonScope.Canonical);
                TableAccumulators.TryAdd(key, new TableColumnAccumulator(table));
            }
        }

        /// <summary>
        /// The resource entry being mutated.
        /// </summary>
        public ResourceEntry Entry { get; }

        /// <summary>
        /// Table mutation accumulators keyed by physical table and JSON scope.
        /// </summary>
        private Dictionary<TableKey, TableColumnAccumulator> TableAccumulators { get; }

        /// <summary>
        /// True when at least one table in the resource has been marked as mutated.
        /// </summary>
        public bool HasChanges => _mutatedTables.Count > 0;

        /// <summary>
        /// Returns the accumulator for a given table, throwing when the table is not present in the resource.
        /// </summary>
        public TableColumnAccumulator GetTableAccumulator(DbTableModel table, QualifiedResourceName resource)
        {
            var key = new TableKey(table.Table, table.JsonScope.Canonical);

            if (TableAccumulators.TryGetValue(key, out var builder))
            {
                return builder;
            }

            throw new InvalidOperationException(
                $"Table '{table.Table}' scope '{table.JsonScope.Canonical}' for resource "
                    + $"'{FormatResource(resource)}' was not found for constraint derivation."
            );
        }

        /// <summary>
        /// Marks a table as mutated so canonical ordering is re-applied when rebuilding.
        /// </summary>
        public void MarkTableMutated(DbTableModel table)
        {
            _mutatedTables.Add(new TableKey(table.Table, table.JsonScope.Canonical));
        }

        /// <summary>
        /// Builds an updated table model from the accumulator, canonicalizing the table when it was mutated.
        /// </summary>
        public DbTableModel BuildTable(DbTableModel original)
        {
            var key = new TableKey(original.Table, original.JsonScope.Canonical);

            if (!TableAccumulators.TryGetValue(key, out var builder))
            {
                throw new InvalidOperationException(
                    $"Table '{original.Table}' scope '{original.JsonScope.Canonical}' was not found "
                        + "for constraint derivation."
                );
            }

            var built = builder.Build();
            return _mutatedTables.Contains(key) ? RelationalModelOrdering.CanonicalizeTable(built) : built;
        }

        /// <summary>
        /// A stable key for locating a table accumulator within a resource mutation.
        /// </summary>
        private sealed record TableKey(DbTableName Table, string Scope);
    }
}
