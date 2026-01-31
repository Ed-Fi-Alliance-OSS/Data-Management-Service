// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Normalizes ordering of tables, columns, constraints, and related metadata to ensure deterministic
/// relational model output regardless of source enumeration order.
/// </summary>
public sealed class CanonicalizeOrderingStep : IRelationalModelBuilderStep
{
    /// <summary>
    /// Applies canonical ordering rules to the current <see cref="RelationalModelBuilderContext"/>,
    /// including table ordering, column and constraint ordering, document reference bindings,
    /// descriptor edges, and extension sites.
    /// </summary>
    /// <param name="context">The relational model builder context to canonicalize.</param>
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourceModel =
            context.ResourceModel
            ?? throw new InvalidOperationException(
                "Resource model must be provided before canonicalizing ordering."
            );

        var canonicalTables = resourceModel
            .TablesInReadDependencyOrder.Select(CanonicalizeTable)
            .OrderBy(table => CountArrayDepth(table.JsonScope))
            .ThenBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ThenBy(table => table.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(table => table.Table.Name, StringComparer.Ordinal)
            .ToArray();

        var rootTable = canonicalTables.FirstOrDefault(table =>
            string.Equals(table.JsonScope.Canonical, "$", StringComparison.Ordinal)
        );

        if (rootTable is null)
        {
            throw new InvalidOperationException("Root table scope '$' was not found.");
        }

        var orderedDocumentReferences = resourceModel
            .DocumentReferenceBindings.OrderBy(
                binding => binding.ReferenceObjectPath.Canonical,
                StringComparer.Ordinal
            )
            .ThenBy(binding => binding.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(binding => binding.Table.Name, StringComparer.Ordinal)
            .ThenBy(binding => binding.FkColumn.Value, StringComparer.Ordinal)
            .ThenBy(binding => binding.TargetResource.ProjectName, StringComparer.Ordinal)
            .ThenBy(binding => binding.TargetResource.ResourceName, StringComparer.Ordinal)
            .ThenBy(binding => binding.IsIdentityComponent)
            .ToArray();

        var orderedDescriptorEdges = resourceModel
            .DescriptorEdgeSources.OrderBy(edge => edge.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Table.Name, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorValuePath.Canonical, StringComparer.Ordinal)
            .ThenBy(edge => edge.FkColumn.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorResource.ProjectName, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorResource.ResourceName, StringComparer.Ordinal)
            .ThenBy(edge => edge.IsIdentityComponent)
            .ToArray();

        var orderedExtensionSites = CanonicalizeExtensionSites(context.ExtensionSites);

        context.ResourceModel = resourceModel with
        {
            Root = rootTable,
            TablesInReadDependencyOrder = canonicalTables,
            TablesInWriteDependencyOrder = canonicalTables,
            DocumentReferenceBindings = orderedDocumentReferences,
            DescriptorEdgeSources = orderedDescriptorEdges,
        };

        context.ExtensionSites.Clear();
        context.ExtensionSites.AddRange(orderedExtensionSites);
    }

    /// <summary>
    /// Reorders table columns and constraints into a stable, predictable sequence.
    /// </summary>
    /// <param name="table">The table model to canonicalize.</param>
    /// <returns>A copy of the table model with canonical column and constraint order.</returns>
    private static DbTableModel CanonicalizeTable(DbTableModel table)
    {
        var keyColumnOrder = BuildKeyColumnOrder(table.Key.Columns);

        var orderedColumns = table
            .Columns.OrderBy(column => GetColumnGroup(column, keyColumnOrder))
            .ThenBy(column => GetColumnKeyIndex(column, keyColumnOrder))
            .ThenBy(column => column.ColumnName.Value, StringComparer.Ordinal)
            .ToArray();

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
    /// Builds a lookup from key column name to its ordinal position within the key definition.
    /// </summary>
    /// <param name="keyColumns">The key columns to index.</param>
    /// <returns>A mapping of column name to key position.</returns>
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
    /// Assigns a grouping bucket used to order columns such that key columns come first,
    /// followed by descriptor foreign keys, scalar columns, and then all remaining kinds.
    /// </summary>
    /// <param name="column">The column to classify.</param>
    /// <param name="keyColumnOrder">Lookup of key column names to their key order.</param>
    /// <returns>A numeric group value used for ordering.</returns>
    private static int GetColumnGroup(DbColumnModel column, IReadOnlyDictionary<string, int> keyColumnOrder)
    {
        if (keyColumnOrder.ContainsKey(column.ColumnName.Value))
        {
            return 0;
        }

        return column.Kind switch
        {
            ColumnKind.DescriptorFk => 1,
            ColumnKind.Scalar => 2,
            _ => 3,
        };
    }

    /// <summary>
    /// Returns the ordinal position of a column within the key, or <see cref="int.MaxValue"/>
    /// when the column is not part of the key.
    /// </summary>
    /// <param name="column">The column whose key index is requested.</param>
    /// <param name="keyColumnOrder">Lookup of key column names to their key order.</param>
    /// <returns>The key ordinal index, or <see cref="int.MaxValue"/> when not a key column.</returns>
    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    /// <summary>
    /// Assigns a grouping bucket used to order constraints with uniques first, then foreign keys,
    /// then check constraints, and finally any other constraint types.
    /// </summary>
    /// <param name="constraint">The constraint to classify.</param>
    /// <returns>A numeric group value used for ordering.</returns>
    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            TableConstraint.AllOrNoneNullability => 3,
            _ => 99,
        };
    }

    /// <summary>
    /// Extracts the constraint name used as a stable tiebreaker during ordering.
    /// </summary>
    /// <param name="constraint">The constraint whose name should be returned.</param>
    /// <returns>The constraint name, or an empty string when unnamed.</returns>
    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Counts the number of array segments within a JSON path, which is used to ensure
    /// parent scopes are ordered ahead of deeper array scopes.
    /// </summary>
    /// <param name="scope">The JSON scope to inspect.</param>
    /// <returns>The number of array-depth segments in the scope.</returns>
    private static int CountArrayDepth(JsonPathExpression scope)
    {
        var depth = 0;

        foreach (var segment in scope.Segments)
        {
            if (segment is JsonPathSegment.AnyArrayElement)
            {
                depth++;
            }
        }

        return depth;
    }

    /// <summary>
    /// Produces a stable ordering for extension sites by normalizing project key ordering and then
    /// ordering by owning scope, extension path, and project keys.
    /// </summary>
    /// <param name="extensionSites">The extension sites to canonicalize.</param>
    /// <returns>An ordered collection of extension sites.</returns>
    private static IReadOnlyList<ExtensionSite> CanonicalizeExtensionSites(
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        return extensionSites
            .Select(site =>
                site with
                {
                    ProjectKeys = site.ProjectKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                }
            )
            .OrderBy(site => site.OwningScope.Canonical, StringComparer.Ordinal)
            .ThenBy(site => site.ExtensionPath.Canonical, StringComparer.Ordinal)
            .ThenBy(site => string.Join("|", site.ProjectKeys), StringComparer.Ordinal)
            .ToArray();
    }
}
