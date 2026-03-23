// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Creates seeded physical columns for system-managed key and locator roles.
/// </summary>
internal static class RelationalModelSystemColumnFactory
{
    /// <summary>
    /// Builds physical column models for a table key definition.
    /// </summary>
    internal static DbColumnModel[] BuildKeyColumns(IReadOnlyList<DbKeyColumn> keyColumns)
    {
        DbColumnModel[] columns = new DbColumnModel[keyColumns.Count];

        for (var index = 0; index < keyColumns.Count; index++)
        {
            var keyColumn = keyColumns[index];
            columns[index] = CreateKeyColumn(keyColumn.ColumnName, keyColumn.Kind);
        }

        return columns;
    }

    /// <summary>
    /// Creates one seeded system column using the standard scalar-type conventions for key and locator roles.
    /// </summary>
    internal static DbColumnModel CreateKeyColumn(DbColumnName columnName, ColumnKind columnKind)
    {
        var keyColumn = new DbKeyColumn(columnName, columnKind);

        return new DbColumnModel(
            columnName,
            columnKind,
            ResolveKeyColumnScalarType(keyColumn),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    /// <summary>
    /// Resolves the scalar type used for seeded key and locator columns.
    /// </summary>
    internal static RelationalScalarType ResolveKeyColumnScalarType(DbKeyColumn keyColumn)
    {
        return keyColumn.Kind switch
        {
            ColumnKind.Ordinal => new RelationalScalarType(ScalarKind.Int32),
            ColumnKind.CollectionKey => new RelationalScalarType(ScalarKind.Int64),
            ColumnKind.ParentKeyPart => RelationalNameConventions.IsDocumentIdColumn(keyColumn.ColumnName)
            || RelationalNameConventions.IsCollectionIdentityColumn(keyColumn.ColumnName)
                ? new RelationalScalarType(ScalarKind.Int64)
                : new RelationalScalarType(ScalarKind.Int32),
            ColumnKind.DocumentFk => new RelationalScalarType(ScalarKind.Int64),
            _ => throw new InvalidOperationException(
                $"Unsupported key column kind '{keyColumn.Kind}' for {keyColumn.ColumnName.Value}."
            ),
        };
    }
}
