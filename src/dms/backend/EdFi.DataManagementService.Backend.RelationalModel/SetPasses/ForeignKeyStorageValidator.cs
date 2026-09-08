// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Shared validator for foreign-key endpoint storage invariants.
/// </summary>
internal static class ForeignKeyStorageValidator
{
    /// <summary>
    /// Validates that foreign-key endpoint columns are stored columns and not synthetic presence flags.
    /// </summary>
    public static void ValidateEndpointColumns(
        TableConstraint.ForeignKey foreignKey,
        DbTableName referencingTable,
        DbTableName referencedTable,
        string columnRole,
        IReadOnlyList<DbColumnName> columns,
        UnifiedAliasStorageResolver.TableMetadata tableMetadata
    )
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table "
                    + $"'{referencedTable}' must define at least one {columnRole} column."
            );
        }

        List<string> offendingColumns = [];

        foreach (var column in columns)
        {
            if (!tableMetadata.ColumnsByName.TryGetValue(column, out var columnModel))
            {
                offendingColumns.Add($"'{column.Value}' (missing)");
                continue;
            }

            if (tableMetadata.SyntheticScalarPresenceColumns.Contains(columnModel.ColumnName))
            {
                offendingColumns.Add($"'{column.Value}' (synthetic presence column)");
                continue;
            }

            switch (columnModel.Storage)
            {
                case ColumnStorage.Stored:
                    continue;
                case ColumnStorage.UnifiedAlias unifiedAlias:
                    offendingColumns.Add(
                        $"'{column.Value}' (UnifiedAlias; canonical storage column "
                            + $"'{unifiedAlias.CanonicalColumn.Value}')"
                    );
                    continue;
                default:
                    offendingColumns.Add(
                        $"'{column.Value}' (unsupported storage '{columnModel.Storage.GetType().Name}')"
                    );
                    continue;
            }
        }

        if (offendingColumns.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table '{referencedTable}' "
                + $"contains invalid {columnRole} column(s): {string.Join(", ", offendingColumns)}. Foreign key "
                + "columns must reference stored columns and cannot use synthetic presence columns."
        );
    }

    /// <summary>
    /// Validates that dms.Document foreign-key targets include only DocumentId.
    /// </summary>
    public static void ValidateDocumentTargetColumns(
        TableConstraint.ForeignKey foreignKey,
        DbTableName referencingTable,
        DbTableName referencedTable,
        IReadOnlyList<DbColumnName> targetColumns
    )
    {
        if (targetColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table "
                    + $"'{referencedTable}' must define at least one target column."
            );
        }

        var invalidColumns = targetColumns
            .Where(column => !column.Equals(RelationalNameConventions.DocumentIdColumnName))
            .Select(column => $"'{column.Value}'")
            .ToArray();

        if (invalidColumns.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Foreign key '{foreignKey.Name}' from table '{referencingTable}' to table '{referencedTable}' "
                + $"contains invalid target column(s): {string.Join(", ", invalidColumns)}. Foreign keys "
                + "targeting dms.Document must reference only DocumentId."
        );
    }
}
