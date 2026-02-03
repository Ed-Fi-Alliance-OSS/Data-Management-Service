// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal static class ConstraintDerivationHelpers
{
    internal static string BuildUniqueConstraintName(string tableName, IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Unique constraint must include at least one column.");
        }

        return $"UX_{tableName}_{string.Join("_", columns.Select(column => column.Value))}";
    }

    internal static string BuildAllOrNoneConstraintName(string tableName, DbColumnName fkColumn)
    {
        return $"CK_{tableName}_{fkColumn.Value}_AllOrNone";
    }

    internal static bool ContainsUniqueConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.Unique>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    internal static bool ContainsForeignKeyConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.ForeignKey>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

    internal static bool ContainsAllOrNoneConstraint(IReadOnlyList<TableConstraint> constraints, string name)
    {
        return constraints
            .OfType<TableConstraint.AllOrNoneNullability>()
            .Any(constraint => string.Equals(constraint.Name, name, StringComparison.Ordinal));
    }

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

    internal static RelationalResourceModel UpdateResourceModel(
        RelationalResourceModel resourceModel,
        ResourceMutation mutation
    )
    {
        var updatedTables = resourceModel
            .TablesInReadDependencyOrder.Select(table => mutation.BuildTable(table))
            .ToArray();
        var updatedRoot = updatedTables.Single(table => table.Table.Equals(resourceModel.Root.Table));

        return resourceModel with
        {
            Root = updatedRoot,
            TablesInReadDependencyOrder = updatedTables,
        };
    }

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

    internal sealed record ResourceEntry(int Index, ConcreteResourceModel Model);

    internal sealed class ResourceMutation
    {
        private readonly HashSet<TableKey> _mutatedTables = new();

        public ResourceMutation(ResourceEntry entry)
        {
            Entry = entry;
            TableAccumulators = new Dictionary<TableKey, TableColumnAccumulator>();

            foreach (var table in entry.Model.RelationalModel.TablesInReadDependencyOrder)
            {
                var key = new TableKey(table.Table, table.JsonScope.Canonical);
                TableAccumulators.TryAdd(key, new TableColumnAccumulator(table));
            }
        }

        public ResourceEntry Entry { get; }

        private Dictionary<TableKey, TableColumnAccumulator> TableAccumulators { get; }

        public bool HasChanges => _mutatedTables.Count > 0;

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

        public void MarkTableMutated(DbTableModel table)
        {
            _mutatedTables.Add(new TableKey(table.Table, table.JsonScope.Canonical));
        }

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

        private sealed record TableKey(DbTableName Table, string Scope);
    }
}
