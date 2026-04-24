// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Resolves a column name to its canonical storage-backed column, stripping <see cref="ColumnStorage.UnifiedAlias"/>
/// indirection so that downstream consumers always bind to the stored column.
/// </summary>
internal static class UnifiedAliasColumnResolver
{
    /// <summary>
    /// Resolves a column name to its storage-backed canonical column. If the column has
    /// <see cref="ColumnStorage.UnifiedAlias"/> storage, returns the aliased
    /// <see cref="ColumnStorage.UnifiedAlias.CanonicalColumn"/>; otherwise returns the column name
    /// unchanged. Defensively passes through unknown columns so the missing-column condition surfaces
    /// as the downstream diagnostic.
    /// </summary>
    internal static DbColumnName ResolveStorageColumnName(DbTableModel table, DbColumnName columnName)
    {
        var column = table.Columns.FirstOrDefault(c => c.ColumnName.Equals(columnName));

        if (column is null)
        {
            // Defensive: leave untouched — missing column will surface downstream as a diagnostic.
            return columnName;
        }

        if (column.Storage is ColumnStorage.UnifiedAlias unifiedAlias)
        {
            return unifiedAlias.CanonicalColumn;
        }

        return columnName;
    }
}
