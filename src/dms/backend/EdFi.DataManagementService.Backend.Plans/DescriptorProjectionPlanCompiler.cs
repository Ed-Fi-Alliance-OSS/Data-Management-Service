// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles page-batched descriptor URI projection plans from
/// <see cref="RelationalResourceModel.DescriptorEdgeSources" />.
/// </summary>
internal sealed class DescriptorProjectionPlanCompiler(SqlDialect dialect)
{
    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");
    private static readonly DbColumnName _descriptorDocumentIdColumn = new("DocumentId");
    private static readonly DbColumnName _descriptorIdProjectionColumn = new("DescriptorId");
    private static readonly DbColumnName _uriColumn = new("Uri");

    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    public IReadOnlyList<DescriptorProjectionPlan> Compile(
        RelationalResourceModel resourceModel,
        KeysetTableContract keysetTable,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentNullException.ThrowIfNull(keysetTable);
        ArgumentNullException.ThrowIfNull(tablesByName);
        ArgumentNullException.ThrowIfNull(columnOrdinalsByTable);

        if (resourceModel.DescriptorEdgeSources.Count == 0)
        {
            return [];
        }

        var deduplicatedSqlSources = CompileDeduplicatedSqlSources(
            resourceModel,
            tablesByName,
            columnOrdinalsByTable
        );

        var compiledSources = resourceModel
            .DescriptorEdgeSources.Select(edgeSource =>
            {
                var columnOrdinals = ResolveColumnOrdinalsOrThrow(columnOrdinalsByTable, edgeSource.Table);

                return new DescriptorProjectionSource(
                    DescriptorValuePath: edgeSource.DescriptorValuePath,
                    Table: edgeSource.Table,
                    DescriptorResource: edgeSource.DescriptorResource,
                    DescriptorIdColumnOrdinal: ResolveColumnOrdinalOrThrow(
                        columnOrdinals,
                        edgeSource.Table,
                        edgeSource.FkColumn,
                        $"descriptor edge source '{edgeSource.DescriptorValuePath.Canonical}' FK column"
                    )
                );
            })
            .ToArray();

        return
        [
            new DescriptorProjectionPlan(
                SelectByKeysetSql: EmitSelectByKeysetSql(deduplicatedSqlSources, keysetTable),
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: compiledSources
            ),
        ];
    }

    private static IReadOnlyList<DescriptorProjectionSqlSource> CompileDeduplicatedSqlSources(
        RelationalResourceModel resourceModel,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable
    )
    {
        var tableDependencyOrder = resourceModel
            .TablesInDependencyOrder.Select((table, index) => (table.Table, index))
            .ToDictionary(entry => entry.Table, entry => entry.index);
        Dictionary<DescriptorProjectionSqlSourceKey, DescriptorProjectionSqlSource> sqlSourcesByKey = [];

        foreach (var edgeSource in resourceModel.DescriptorEdgeSources)
        {
            var tableModel = ResolveTableModelOrThrow(tablesByName, edgeSource.Table);
            var tableOrdinals = ResolveColumnOrdinalsOrThrow(columnOrdinalsByTable, edgeSource.Table);
            var storageColumn = ResolveStorageColumnOrThrow(tableModel, edgeSource);
            var storageColumnOrdinal = ResolveColumnOrdinalOrThrow(
                tableOrdinals,
                edgeSource.Table,
                storageColumn,
                $"descriptor edge source '{edgeSource.DescriptorValuePath.Canonical}' storage column"
            );
            var sqlSource = new DescriptorProjectionSqlSource(
                TableModel: tableModel,
                StorageColumn: storageColumn,
                TableDependencyOrdinal: tableDependencyOrder[edgeSource.Table],
                StorageColumnOrdinal: storageColumnOrdinal
            );

            sqlSourcesByKey.TryAdd(
                new DescriptorProjectionSqlSourceKey(edgeSource.Table, storageColumn),
                sqlSource
            );
        }

        return sqlSourcesByKey
            .Values.OrderBy(source => source.TableDependencyOrdinal)
            .ThenBy(source => source.StorageColumnOrdinal)
            .ToArray();
    }

    private string EmitSelectByKeysetSql(
        IReadOnlyList<DescriptorProjectionSqlSource> sqlSources,
        KeysetTableContract keysetTable
    )
    {
        const string projectionAlias = "p";

        var keysetAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset);
        var descriptorAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Descriptor);
        var tableAliasAllocator = PlanNamingConventions.CreateTableAliasAllocator();
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            writer
                .Append($"{projectionAlias}.")
                .AppendQuoted(_descriptorIdProjectionColumn.Value)
                .AppendLine(",");
            writer.Append($"{descriptorAlias}.").AppendQuoted(_uriColumn.Value).AppendLine();
        }

        writer.AppendLine("FROM");

        using (writer.Indent())
        {
            writer.AppendLine("(");

            using (writer.Indent())
            {
                for (var index = 0; index < sqlSources.Count; index++)
                {
                    var sqlSource = sqlSources[index];
                    var tableModel = sqlSource.TableModel;
                    var tableAlias = tableAliasAllocator.AllocateNext();
                    var rootDocumentIdKeyColumn = tableModel.Key.Columns[0].ColumnName;

                    writer.Append("SELECT ");
                    AppendQualifiedColumn(writer, tableAlias, sqlSource.StorageColumn);
                    writer.Append(" AS ").AppendQuoted(_descriptorIdProjectionColumn.Value).AppendLine();
                    writer.Append("FROM ").AppendTable(tableModel.Table).AppendLine($" {tableAlias}");
                    writer
                        .Append("INNER JOIN ")
                        .AppendRelation(keysetTable.Table)
                        .Append($" {keysetAlias} ON ");
                    AppendQualifiedColumn(writer, tableAlias, rootDocumentIdKeyColumn);
                    writer.Append(" = ");
                    AppendQualifiedColumn(writer, keysetAlias, keysetTable.DocumentIdColumnName);
                    writer.AppendLine();
                    writer.Append("WHERE ");
                    AppendQualifiedColumn(writer, tableAlias, sqlSource.StorageColumn);
                    writer.AppendLine(" IS NOT NULL");

                    if (index + 1 < sqlSources.Count)
                    {
                        writer.AppendLine("UNION");
                    }
                }
            }

            writer.AppendLine($") {projectionAlias}");
        }

        writer.Append("INNER JOIN ").AppendTable(_descriptorTable).Append($" {descriptorAlias} ON ");
        AppendQualifiedColumn(writer, descriptorAlias, _descriptorDocumentIdColumn);
        writer.Append(" = ");
        writer.Append($"{projectionAlias}.").AppendQuoted(_descriptorIdProjectionColumn.Value).AppendLine();
        writer.AppendLine("ORDER BY");

        using (writer.Indent())
        {
            writer.Append($"{projectionAlias}.").AppendQuoted(_descriptorIdProjectionColumn.Value);
            writer.AppendLine(" ASC");
        }

        writer.AppendLine(";");

        return writer.ToString();
    }

    private static DbColumnName ResolveStorageColumnOrThrow(
        DbTableModel tableModel,
        DescriptorEdgeSource edgeSource
    )
    {
        var contextDescription =
            $"Cannot compile descriptor projection plan for '{tableModel.Table}': descriptor edge source "
            + $"'{edgeSource.DescriptorValuePath.Canonical}' FK column";
        var bindingColumn = ResolveTableColumnOrThrow(
            tableModel,
            edgeSource.FkColumn,
            contextDescription,
            edgeSource.FkColumn
        );

        return bindingColumn.Storage switch
        {
            ColumnStorage.Stored => bindingColumn.ColumnName,
            ColumnStorage.UnifiedAlias unifiedAlias => ValidateStoredStorageColumnOrThrow(
                tableModel,
                ResolveTableColumnOrThrow(
                    tableModel,
                    unifiedAlias.CanonicalColumn,
                    $"{contextDescription} '{edgeSource.FkColumn.Value}' resolved to missing canonical storage column",
                    unifiedAlias.CanonicalColumn
                ),
                $"{contextDescription} '{edgeSource.FkColumn.Value}' resolved to canonical storage column",
                unifiedAlias.CanonicalColumn
            ),
            _ => throw new InvalidOperationException(
                $"{contextDescription} '{edgeSource.FkColumn.Value}' uses unsupported storage metadata "
                    + $"'{bindingColumn.Storage.GetType().Name}'."
            ),
        };
    }

    private static DbColumnName ValidateStoredStorageColumnOrThrow(
        DbTableModel tableModel,
        DbColumnModel columnModel,
        string contextDescription,
        DbColumnName resolvedColumnName
    )
    {
        if (columnModel.Storage is not ColumnStorage.Stored)
        {
            throw new InvalidOperationException(
                $"{contextDescription} '{resolvedColumnName.Value}' is not stored."
            );
        }

        return columnModel.ColumnName;
    }

    private static DbTableModel ResolveTableModelOrThrow(
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        DbTableName table
    )
    {
        if (tablesByName.TryGetValue(table, out var tableModel))
        {
            return tableModel;
        }

        throw new InvalidOperationException(
            $"Cannot compile descriptor projection plan for '{table}': owning table is not present in TablesInDependencyOrder."
        );
    }

    private static DbColumnModel ResolveTableColumnOrThrow(
        DbTableModel tableModel,
        DbColumnName column,
        string contextDescription,
        DbColumnName missingColumn
    )
    {
        var columnModel = tableModel.Columns.SingleOrDefault(candidate =>
            candidate.ColumnName.Equals(column)
        );

        if (columnModel is not null)
        {
            return columnModel;
        }

        throw new InvalidOperationException(
            $"{contextDescription} '{missingColumn.Value}' does not exist in table columns."
        );
    }

    private static IReadOnlyDictionary<DbColumnName, int> ResolveColumnOrdinalsOrThrow(
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable,
        DbTableName table
    )
    {
        if (columnOrdinalsByTable.TryGetValue(table, out var columnOrdinals))
        {
            return columnOrdinals;
        }

        throw new InvalidOperationException(
            $"Cannot compile descriptor projection plan for '{table}': owning table is not present in TablesInDependencyOrder."
        );
    }

    private static int ResolveColumnOrdinalOrThrow(
        IReadOnlyDictionary<DbColumnName, int> columnOrdinals,
        DbTableName table,
        DbColumnName column,
        string dependencyDescription
    )
    {
        if (columnOrdinals.TryGetValue(column, out var columnOrdinal))
        {
            return columnOrdinal;
        }

        throw new InvalidOperationException(
            $"Cannot compile descriptor projection plan for '{table}': {dependencyDescription} '{column.Value}' does not exist in hydration select-list columns."
        );
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }

    private readonly record struct DescriptorProjectionSqlSourceKey(
        DbTableName Table,
        DbColumnName StorageColumn
    );

    private sealed record DescriptorProjectionSqlSource(
        DbTableModel TableModel,
        DbColumnName StorageColumn,
        int TableDependencyOrdinal,
        int StorageColumnOrdinal
    );
}
