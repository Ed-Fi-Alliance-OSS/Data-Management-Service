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
    /// <summary>
    /// Returns <see langword="true" /> when the table carries explicit stable-identity metadata.
    /// </summary>
    public static bool UsesExplicitIdentityMetadata(DbTableModel tableModel)
    {
        ArgumentNullException.ThrowIfNull(tableModel);

        return tableModel.IdentityMetadata.TableKind is not DbTableKind.Unspecified;
    }

    /// <summary>
    /// Resolves the single root-scope table model and verifies it matches <see cref="RelationalResourceModel.Root" />.
    /// </summary>
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

    /// <summary>
    /// Validates the deterministic key-shape contract shared by read and write plan compilation.
    /// </summary>
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

    /// <summary>
    /// Resolves the single root-document locator column used for page-keyset joins.
    /// </summary>
    public static DbColumnName ResolveRootScopeLocatorColumnOrThrow(
        DbTableModel tableModel,
        string planKind,
        Action<DbColumnName>? validateColumn = null
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);

        if (!UsesExplicitIdentityMetadata(tableModel))
        {
            ValidateDeterministicTableKeyShapeOrThrow(tableModel, planKind);

            var rootScopeLocatorColumn = tableModel.Key.Columns[0].ColumnName;
            validateColumn?.Invoke(rootScopeLocatorColumn);

            return rootScopeLocatorColumn;
        }

        if (tableModel.IdentityMetadata.RootScopeLocatorColumns.Count != 1)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': expected exactly one explicit root-scope locator column, "
                    + $"but found {tableModel.IdentityMetadata.RootScopeLocatorColumns.Count}."
            );
        }

        var explicitRootScopeLocatorColumn = tableModel.IdentityMetadata.RootScopeLocatorColumns[0];

        if (!RelationalNameConventions.IsDocumentIdColumn(explicitRootScopeLocatorColumn))
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': explicit root-scope locator column "
                    + $"'{explicitRootScopeLocatorColumn.Value}' is not a document-id locator."
            );
        }

        validateColumn?.Invoke(explicitRootScopeLocatorColumn);

        return explicitRootScopeLocatorColumn;
    }

    /// <summary>
    /// Resolves the immediate-parent scope locator columns used for non-root plan operations.
    /// </summary>
    public static IReadOnlyList<DbColumnName> ResolveImmediateParentScopeLocatorColumnsOrThrow(
        DbTableModel tableModel,
        string planKind,
        Action<DbColumnName>? validateColumn = null
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);

        if (!UsesExplicitIdentityMetadata(tableModel))
        {
            ValidateDeterministicTableKeyShapeOrThrow(tableModel, planKind);

            var inferredImmediateParentScopeLocatorColumns = tableModel
                .Key.Columns.Where(static keyColumn => keyColumn.Kind is ColumnKind.ParentKeyPart)
                .Select(static keyColumn => keyColumn.ColumnName)
                .ToArray();

            foreach (var column in inferredImmediateParentScopeLocatorColumns)
            {
                validateColumn?.Invoke(column);
            }

            return inferredImmediateParentScopeLocatorColumns;
        }

        if (tableModel.IdentityMetadata.TableKind is DbTableKind.Root)
        {
            return [];
        }

        if (tableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for '{tableModel.Table}': explicit immediate-parent scope locator metadata is empty for non-root table kind "
                    + $"'{tableModel.IdentityMetadata.TableKind}'."
            );
        }

        var explicitImmediateParentScopeLocatorColumns =
            tableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.ToArray();

        foreach (var column in explicitImmediateParentScopeLocatorColumns)
        {
            validateColumn?.Invoke(column);
        }

        return explicitImmediateParentScopeLocatorColumns;
    }

    /// <summary>
    /// Resolves the deterministic hydration ordering columns for one table.
    /// </summary>
    public static IReadOnlyList<DbColumnName> ResolveHydrationOrderingColumnsOrThrow(
        DbTableModel tableModel,
        string planKind,
        Action<DbColumnName>? validateColumn = null
    )
    {
        ArgumentNullException.ThrowIfNull(tableModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);

        if (!UsesExplicitIdentityMetadata(tableModel))
        {
            ValidateDeterministicTableKeyShapeOrThrow(
                tableModel,
                planKind,
                keyColumn => validateColumn?.Invoke(keyColumn.ColumnName)
            );

            return tableModel.Key.Columns.Select(static keyColumn => keyColumn.ColumnName).ToArray();
        }

        var rootScopeLocatorColumn = ResolveRootScopeLocatorColumnOrThrow(
            tableModel,
            planKind,
            validateColumn
        );
        var immediateParentScopeLocatorColumns = ResolveImmediateParentScopeLocatorColumnsOrThrow(
            tableModel,
            planKind,
            validateColumn
        );
        var ordinalColumns = tableModel
            .Columns.Where(static column => column.Kind is ColumnKind.Ordinal)
            .Select(static column => column.ColumnName)
            .ToArray();

        List<DbColumnName> hydrationOrderingColumns = [];
        HashSet<DbColumnName> seenColumns = [];

        AppendDistinctColumn(rootScopeLocatorColumn);

        foreach (var column in immediateParentScopeLocatorColumns)
        {
            AppendDistinctColumn(column);
        }

        foreach (var column in ordinalColumns)
        {
            AppendDistinctColumn(column);
        }

        return hydrationOrderingColumns;

        void AppendDistinctColumn(DbColumnName column)
        {
            validateColumn?.Invoke(column);

            if (seenColumns.Add(column))
            {
                hydrationOrderingColumns.Add(column);
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true" /> when the JSON scope is the resource root path <c>$</c>.
    /// </summary>
    private static bool IsRootJsonScope(JsonPathExpression jsonScope)
    {
        return jsonScope.Canonical == "$" && jsonScope.Segments.Count == 0;
    }

    /// <summary>
    /// Formats a resource model name as <c>{ProjectName}.{ResourceName}</c> for diagnostics.
    /// </summary>
    private static string GetResourceDisplayName(RelationalResourceModel resourceModel)
    {
        return $"{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}";
    }

    /// <summary>
    /// Formats table key columns as <c>{Column}:{Kind}</c> pairs for deterministic error messages.
    /// </summary>
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
