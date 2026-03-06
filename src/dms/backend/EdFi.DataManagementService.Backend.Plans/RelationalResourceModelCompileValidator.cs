// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates baseline structural invariants shared by relational plan compilers.
/// </summary>
internal static class RelationalResourceModelCompileValidator
{
    public static DbTableModel ResolveRootScopeTableModelOrThrow(
        RelationalResourceModel resourceModel,
        string planKind
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);

        if (resourceModel.TablesInDependencyOrder.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': no tables were found in dependency order."
            );
        }

        if (!IsRootJsonScope(resourceModel.Root.JsonScope))
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': resourceModel.Root must have JsonScope '$', but was '{resourceModel.Root.JsonScope.Canonical}'."
            );
        }

        var rootScopeTables = resourceModel
            .TablesInDependencyOrder.Where(static tableModel => IsRootJsonScope(tableModel.JsonScope))
            .ToArray();

        if (rootScopeTables.Length != 1)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found {rootScopeTables.Length}."
            );
        }

        var rootScopeTable = rootScopeTables[0];

        if (!rootScopeTable.Table.Equals(resourceModel.Root.Table))
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': root-scope table '{rootScopeTable.Table}' does not match resourceModel.Root table '{resourceModel.Root.Table}'."
            );
        }

        return rootScopeTable;
    }

    public static void ValidateDeterministicTableKeyShapeOrThrow(
        DbTableModel tableModel,
        string planKind,
        Action<DbKeyColumn>? validateKeyColumn = null
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);

        if (tableModel.Key.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': table key contains no columns."
            );
        }

        var documentIdParentKeyPartCount = 0;
        var ordinalKeyColumnCount = 0;

        foreach (var keyColumn in tableModel.Key.Columns)
        {
            if (keyColumn.Kind is not ColumnKind.ParentKeyPart and not ColumnKind.Ordinal)
            {
                throw new InvalidOperationException(
                    $"Cannot compile {planKind} for '{tableModel.Table}': key column '{keyColumn.ColumnName.Value}' has unsupported kind '{keyColumn.Kind}'. "
                        + $"Supported key kinds are {nameof(ColumnKind.ParentKeyPart)} and {nameof(ColumnKind.Ordinal)}."
                );
            }

            validateKeyColumn?.Invoke(keyColumn);

            if (
                keyColumn.Kind is ColumnKind.ParentKeyPart
                && RelationalNameConventions.IsDocumentIdColumn(keyColumn.ColumnName)
            )
            {
                documentIdParentKeyPartCount++;
            }

            if (keyColumn.Kind is ColumnKind.Ordinal)
            {
                ordinalKeyColumnCount++;
            }
        }

        if (documentIdParentKeyPartCount != 1)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': expected exactly one ParentKeyPart document-id key column "
                    + $"('{RelationalNameConventions.DocumentIdColumnName.Value}' or '*_{RelationalNameConventions.DocumentIdColumnName.Value}'), "
                    + $"but found {documentIdParentKeyPartCount}. Key columns: [{FormatKeyColumnSummary(tableModel)}]."
            );
        }

        var firstKeyColumn = tableModel.Key.Columns[0];

        if (
            firstKeyColumn.Kind is not ColumnKind.ParentKeyPart
            || !RelationalNameConventions.IsDocumentIdColumn(firstKeyColumn.ColumnName)
        )
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': expected document-id ParentKeyPart key column "
                    + $"('{RelationalNameConventions.DocumentIdColumnName.Value}' or '*_{RelationalNameConventions.DocumentIdColumnName.Value}') "
                    + $"to be first in key order, but found '{firstKeyColumn.ColumnName.Value}:{firstKeyColumn.Kind}'. "
                    + $"Key columns: [{FormatKeyColumnSummary(tableModel)}]."
            );
        }

        if (ordinalKeyColumnCount > 1)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': expected at most one Ordinal key column, but found {ordinalKeyColumnCount}. "
                    + $"Key columns: [{FormatKeyColumnSummary(tableModel)}]."
            );
        }

        if (ordinalKeyColumnCount == 1 && tableModel.Key.Columns[^1].Kind is not ColumnKind.Ordinal)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': expected Ordinal key column to be last in key order. "
                    + $"Key columns: [{FormatKeyColumnSummary(tableModel)}]."
            );
        }
    }

    private static bool IsRootJsonScope(JsonPathExpression jsonScope)
    {
        return jsonScope.Canonical == "$" && jsonScope.Segments.Count == 0;
    }

    private static string GetResourceDisplayName(RelationalResourceModel resourceModel)
    {
        return $"{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}";
    }

    private static string FormatKeyColumnSummary(DbTableModel tableModel)
    {
        return string.Join(
            ", ",
            tableModel.Key.Columns.Select(static keyColumn =>
                $"{keyColumn.ColumnName.Value}:{keyColumn.Kind}"
            )
        );
    }
}
