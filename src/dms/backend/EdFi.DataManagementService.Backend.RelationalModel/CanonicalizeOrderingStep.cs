// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class CanonicalizeOrderingStep : IRelationalModelBuilderStep
{
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

        context.ResourceModel = resourceModel with
        {
            Root = rootTable,
            TablesInReadDependencyOrder = canonicalTables,
            TablesInWriteDependencyOrder = canonicalTables,
            DocumentReferenceBindings = orderedDocumentReferences,
            DescriptorEdgeSources = orderedDescriptorEdges,
        };
    }

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

    private static Dictionary<string, int> BuildKeyColumnOrder(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        Dictionary<string, int> keyOrder = new(StringComparer.Ordinal);

        for (var index = 0; index < keyColumns.Count; index++)
        {
            keyOrder[keyColumns[index].ColumnName.Value] = index;
        }

        return keyOrder;
    }

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

    private static int GetColumnKeyIndex(
        DbColumnModel column,
        IReadOnlyDictionary<string, int> keyColumnOrder
    )
    {
        return keyColumnOrder.TryGetValue(column.ColumnName.Value, out var index) ? index : int.MaxValue;
    }

    private static int GetConstraintGroup(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique => 1,
            TableConstraint.ForeignKey => 2,
            _ => 99,
        };
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            _ => string.Empty,
        };
    }

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
}
