// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Emits deterministic batched DML SQL from compiled write-plan metadata so runtime batching stays aligned to the
/// authoritative compiled plan surface instead of hand-rewriting SQL text in the persister.
/// </summary>
internal sealed class WritePlanBatchSqlEmitter(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;

    public string EmitInsertBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        WriteBatchSqlSupport.ValidateRowCount(rowCount);

        var orderedColumns = tableWritePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName)
            .ToArray();
        var orderedParameterNames = tableWritePlan
            .ColumnBindings.Select(static binding => binding.ParameterName)
            .ToArray();

        return WriteBatchSqlSupport.EmitSuffixedInsertSql(
            _dialect,
            tableWritePlan.TableModel.Table,
            orderedColumns,
            orderedParameterNames,
            rowCount
        );
    }

    public string EmitUpdateBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        WriteBatchSqlSupport.ValidateRowCount(rowCount);

        if (tableWritePlan.UpdateSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' does not expose {nameof(TableWritePlan.UpdateSql)} for batched update emission."
            );
        }

        var keyColumnsInKeyOrder = tableWritePlan
            .TableModel.Key.Columns.Select(static keyColumn => keyColumn.ColumnName)
            .ToArray();
        var keyColumnNames = keyColumnsInKeyOrder.ToHashSet();
        var writableNonKeyBindingsInOrder = tableWritePlan
            .ColumnBindings.Where(binding => !keyColumnNames.Contains(binding.Column.ColumnName))
            .ToArray();

        if (writableNonKeyBindingsInOrder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' does not expose writable non-key bindings for batched update emission."
            );
        }

        var parameterNameByColumn = BuildParameterNameByColumnMap(tableWritePlan);
        var orderedSetColumns = writableNonKeyBindingsInOrder
            .Select(static binding => binding.Column.ColumnName)
            .ToArray();
        var orderedSetParameterNames = writableNonKeyBindingsInOrder
            .Select(static binding => binding.ParameterName)
            .ToArray();
        var orderedKeyParameterNames = ResolveRequiredParameterNamesInOrder(
            tableWritePlan,
            keyColumnsInKeyOrder,
            parameterNameByColumn,
            sqlOperation: "update batch"
        );

        return EmitStatementBatch(
            rowCount,
            rowIndex =>
                EmitUpdateSql(
                    tableWritePlan.TableModel.Table,
                    orderedSetColumns,
                    WriteBatchSqlSupport.SuffixParameterNames(orderedSetParameterNames, rowIndex),
                    keyColumnsInKeyOrder,
                    WriteBatchSqlSupport.SuffixParameterNames(orderedKeyParameterNames, rowIndex)
                )
        );
    }

    public string EmitDeleteByParentBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        WriteBatchSqlSupport.ValidateRowCount(rowCount);

        if (tableWritePlan.DeleteByParentSql is null)
        {
            throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' does not expose {nameof(TableWritePlan.DeleteByParentSql)} for batched delete emission."
            );
        }

        var keyColumnsInOrder = DeriveDeleteByParentKeyColumnsInOrder(tableWritePlan);
        var parameterNameByColumn = BuildParameterNameByColumnMap(tableWritePlan);
        var orderedKeyParameterNames = ResolveRequiredParameterNamesInOrder(
            tableWritePlan,
            keyColumnsInOrder,
            parameterNameByColumn,
            sqlOperation: "delete-by-parent batch"
        );

        return EmitStatementBatch(
            rowCount,
            rowIndex =>
                EmitDeleteSql(
                    tableWritePlan.TableModel.Table,
                    keyColumnsInOrder,
                    WriteBatchSqlSupport.SuffixParameterNames(orderedKeyParameterNames, rowIndex)
                )
        );
    }

    public string EmitCollectionUpdateByStableRowIdentityBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        WriteBatchSqlSupport.ValidateRowCount(rowCount);

        var collectionMergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' does not expose {nameof(TableWritePlan.CollectionMergePlan)} for batched collection-update emission."
            );
        var stableRowIdentityBinding = tableWritePlan.ColumnBindings[
            collectionMergePlan.StableRowIdentityBindingIndex
        ];
        var locatorColumns = DeriveCollectionLocatorColumns(tableWritePlan);
        var bindingsToUpdateInOrder = tableWritePlan
            .ColumnBindings.Where(
                (binding, index) =>
                    index != collectionMergePlan.StableRowIdentityBindingIndex
                    && !locatorColumns.Contains(binding.Column.ColumnName)
            )
            .ToArray();

        if (bindingsToUpdateInOrder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' does not expose any writable collection bindings for batched stable-row updates."
            );
        }

        var orderedSetColumns = bindingsToUpdateInOrder
            .Select(static binding => binding.Column.ColumnName)
            .ToArray();
        var orderedSetParameterNames = bindingsToUpdateInOrder
            .Select(static binding => binding.ParameterName)
            .ToArray();
        var orderedKeyColumns = new[] { stableRowIdentityBinding.Column.ColumnName };
        var orderedKeyParameterNames = new[] { stableRowIdentityBinding.ParameterName };

        return EmitStatementBatch(
            rowCount,
            rowIndex =>
                EmitUpdateSql(
                    tableWritePlan.TableModel.Table,
                    orderedSetColumns,
                    WriteBatchSqlSupport.SuffixParameterNames(orderedSetParameterNames, rowIndex),
                    orderedKeyColumns,
                    WriteBatchSqlSupport.SuffixParameterNames(orderedKeyParameterNames, rowIndex)
                )
        );
    }

    public string EmitCollectionDeleteByStableRowIdentityBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        WriteBatchSqlSupport.ValidateRowCount(rowCount);

        var collectionMergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' does not expose {nameof(TableWritePlan.CollectionMergePlan)} for batched collection-delete emission."
            );
        var stableRowIdentityBinding = tableWritePlan.ColumnBindings[
            collectionMergePlan.StableRowIdentityBindingIndex
        ];

        return EmitStatementBatch(
            rowCount,
            rowIndex =>
                EmitDeleteSql(
                    tableWritePlan.TableModel.Table,
                    [stableRowIdentityBinding.Column.ColumnName],
                    WriteBatchSqlSupport.SuffixParameterNames(
                        [stableRowIdentityBinding.ParameterName],
                        rowIndex
                    )
                )
        );
    }

    private string EmitUpdateSql(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedSetColumns,
        IReadOnlyList<string> orderedSetParameterNames,
        IReadOnlyList<DbColumnName> orderedKeyColumns,
        IReadOnlyList<string> orderedKeyParameterNames
    )
    {
        StringBuilder builder = new();

        builder.Append("UPDATE ");
        AppendQualifiedTable(builder, table);
        builder.Append('\n');
        builder.Append("SET\n");

        for (var index = 0; index < orderedSetColumns.Count; index++)
        {
            builder.Append("    ");
            builder.Append(QuoteIdentifier(orderedSetColumns[index].Value));
            builder.Append(" = @");
            builder.Append(orderedSetParameterNames[index]);
            builder.Append(index + 1 < orderedSetColumns.Count ? ",\n" : "\n");
        }

        AppendWhereClause(builder, orderedKeyColumns, orderedKeyParameterNames);
        builder.Append(";\n");

        return builder.ToString();
    }

    private string EmitDeleteSql(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedWhereColumns,
        IReadOnlyList<string> orderedWhereParameterNames
    )
    {
        StringBuilder builder = new();

        builder.Append("DELETE FROM ");
        AppendQualifiedTable(builder, table);
        builder.Append('\n');
        AppendWhereClause(builder, orderedWhereColumns, orderedWhereParameterNames);
        builder.Append(";\n");

        return builder.ToString();
    }

    private static IReadOnlyDictionary<DbColumnName, string> BuildParameterNameByColumnMap(
        TableWritePlan tableWritePlan
    )
    {
        Dictionary<DbColumnName, string> parameterNameByColumn = [];

        foreach (var binding in tableWritePlan.ColumnBindings)
        {
            if (parameterNameByColumn.TryAdd(binding.Column.ColumnName, binding.ParameterName))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Table '{tableWritePlan.TableModel.Table}' exposes duplicate write bindings for column '{binding.Column.ColumnName.Value}'."
            );
        }

        return parameterNameByColumn;
    }

    private static string[] ResolveRequiredParameterNamesInOrder(
        TableWritePlan tableWritePlan,
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyDictionary<DbColumnName, string> parameterNameByColumn,
        string sqlOperation
    )
    {
        var orderedParameterNames = new string[orderedColumns.Count];

        for (var index = 0; index < orderedColumns.Count; index++)
        {
            var orderedColumn = orderedColumns[index];

            if (parameterNameByColumn.TryGetValue(orderedColumn, out var parameterName))
            {
                orderedParameterNames[index] = parameterName;
                continue;
            }

            throw new InvalidOperationException(
                $"Cannot emit {sqlOperation} SQL for '{tableWritePlan.TableModel.Table}': column '{orderedColumn.Value}' does not have a compiled write binding parameter."
            );
        }

        return orderedParameterNames;
    }

    private static DbColumnName[] DeriveDeleteByParentKeyColumnsInOrder(TableWritePlan tableWritePlan)
    {
        var tableModel = tableWritePlan.TableModel;

        if (!UsesExplicitIdentityMetadata(tableModel))
        {
            return tableModel
                .Key.Columns.Where(static keyColumn => keyColumn.Kind is ColumnKind.ParentKeyPart)
                .Select(static keyColumn => keyColumn.ColumnName)
                .ToArray();
        }

        return tableModel.IdentityMetadata.TableKind is DbTableKind.Root
            ? []
            : tableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.ToArray();
    }

    private static HashSet<DbColumnName> DeriveCollectionLocatorColumns(TableWritePlan tableWritePlan)
    {
        return tableWritePlan
            .TableModel.IdentityMetadata.RootScopeLocatorColumns.Concat(
                tableWritePlan.TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns
            )
            .ToHashSet();
    }

    private static bool UsesExplicitIdentityMetadata(DbTableModel tableModel)
    {
        return tableModel.IdentityMetadata.TableKind is not DbTableKind.Unspecified;
    }

    private static string EmitStatementBatch(int rowCount, Func<int, string> emitStatement)
    {
        StringBuilder builder = new();

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (rowIndex > 0)
            {
                builder.Append('\n');
            }

            builder.Append(emitStatement(rowIndex));
        }

        return builder.ToString();
    }

    private void AppendQualifiedTable(StringBuilder builder, DbTableName table)
    {
        builder.Append(QuoteIdentifier(table.Schema.Value));
        builder.Append('.');
        builder.Append(QuoteIdentifier(table.Name));
    }

    private void AppendWhereClause(
        StringBuilder builder,
        IReadOnlyList<DbColumnName> orderedWhereColumns,
        IReadOnlyList<string> orderedWhereParameterNames
    )
    {
        if (orderedWhereColumns.Count == 0)
        {
            return;
        }

        builder.Append("WHERE\n");

        for (var index = 0; index < orderedWhereColumns.Count; index++)
        {
            builder.Append(index == 0 ? "    (" : "    AND (");
            builder.Append(QuoteIdentifier(orderedWhereColumns[index].Value));
            builder.Append(" = @");
            builder.Append(orderedWhereParameterNames[index]);
            builder.Append(")\n");
        }
    }

    private string QuoteIdentifier(string identifier)
    {
        return _dialect switch
        {
            SqlDialect.Pgsql => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
            SqlDialect.Mssql => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), _dialect, null),
        };
    }
}
