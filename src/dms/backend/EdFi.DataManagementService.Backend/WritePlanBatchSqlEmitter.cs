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
public sealed class WritePlanBatchSqlEmitter(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;

    public string EmitInsertBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ValidateRowCount(rowCount);

        var orderedColumns = tableWritePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName)
            .ToArray();
        var orderedParameterNames = tableWritePlan
            .ColumnBindings.Select(static binding => binding.ParameterName)
            .ToArray();
        var orderedParameterNamesByRow = Enumerable
            .Range(0, rowCount)
            .Select(rowIndex => SuffixParameterNames(orderedParameterNames, rowIndex))
            .ToArray();

        return EmitInsertSql(tableWritePlan.TableModel.Table, orderedColumns, orderedParameterNamesByRow);
    }

    public string EmitUpdateBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ValidateRowCount(rowCount);

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
                    SuffixParameterNames(orderedSetParameterNames, rowIndex),
                    keyColumnsInKeyOrder,
                    SuffixParameterNames(orderedKeyParameterNames, rowIndex)
                )
        );
    }

    public string EmitDeleteByParentBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ValidateRowCount(rowCount);

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
                    SuffixParameterNames(orderedKeyParameterNames, rowIndex)
                )
        );
    }

    public string EmitCollectionUpdateByStableRowIdentityBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ValidateRowCount(rowCount);

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
                    SuffixParameterNames(orderedSetParameterNames, rowIndex),
                    orderedKeyColumns,
                    SuffixParameterNames(orderedKeyParameterNames, rowIndex)
                )
        );
    }

    public string EmitCollectionDeleteByStableRowIdentityBatch(TableWritePlan tableWritePlan, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ValidateRowCount(rowCount);

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
                    SuffixParameterNames([stableRowIdentityBinding.ParameterName], rowIndex)
                )
        );
    }

    private string EmitInsertSql(
        DbTableName table,
        IReadOnlyList<DbColumnName> orderedColumns,
        IReadOnlyList<IReadOnlyList<string>> orderedParameterNamesByRow
    )
    {
        StringBuilder builder = new();

        builder.Append("INSERT INTO ");
        AppendQualifiedTable(builder, table);
        builder.Append('\n');
        AppendParenthesizedLines(
            builder,
            orderedColumns.Count,
            index => builder.Append(QuoteIdentifier(orderedColumns[index].Value))
        );
        builder.Append("VALUES\n");

        for (var rowIndex = 0; rowIndex < orderedParameterNamesByRow.Count; rowIndex++)
        {
            var orderedParameterNames = orderedParameterNamesByRow[rowIndex];

            AppendParenthesizedValueLines(
                builder,
                orderedParameterNames.Count,
                index => builder.Append('@').Append(orderedParameterNames[index]),
                appendTrailingComma: rowIndex + 1 < orderedParameterNamesByRow.Count
            );
        }

        builder.Append(";\n");

        return builder.ToString();
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

    private static string[] SuffixParameterNames(IReadOnlyList<string> orderedParameterNames, int rowIndex)
    {
        var suffixedParameterNames = new string[orderedParameterNames.Count];

        for (var index = 0; index < orderedParameterNames.Count; index++)
        {
            suffixedParameterNames[index] = BuildBatchParameterName(orderedParameterNames[index], rowIndex);
        }

        return suffixedParameterNames;
    }

    private static string BuildBatchParameterName(string parameterName, int rowIndex)
    {
        var bareParameterName = parameterName.TrimStart('@');
        return $"{bareParameterName}_{rowIndex}";
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

    private static void AppendParenthesizedLines(StringBuilder builder, int itemCount, Action<int> appendItem)
    {
        builder.Append("(\n");

        for (var index = 0; index < itemCount; index++)
        {
            builder.Append("    ");
            appendItem(index);
            builder.Append(index + 1 < itemCount ? ",\n" : "\n");
        }

        builder.Append(')');
        builder.Append('\n');
    }

    private static void AppendParenthesizedValueLines(
        StringBuilder builder,
        int itemCount,
        Action<int> appendItem,
        bool appendTrailingComma
    )
    {
        builder.Append("(\n");

        for (var index = 0; index < itemCount; index++)
        {
            builder.Append("    ");
            appendItem(index);
            builder.Append(index + 1 < itemCount ? ",\n" : "\n");
        }

        builder.Append(appendTrailingComma ? "),\n" : ")\n");
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

    private static void ValidateRowCount(int rowCount)
    {
        if (rowCount >= 1)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "Row count must be at least 1.");
    }
}
