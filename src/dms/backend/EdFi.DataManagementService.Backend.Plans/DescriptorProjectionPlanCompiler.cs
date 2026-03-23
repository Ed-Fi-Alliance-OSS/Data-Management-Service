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

        var deduplicatedSqlSources = CompileDeduplicatedSqlSources(resourceModel, tablesByName);

        var compiledSources = resourceModel
            .DescriptorEdgeSources.Select(edgeSource =>
            {
                var columnOrdinals = ProjectionMetadataResolver.ResolveHydrationColumnOrdinalsOrThrow(
                    edgeSource.Table,
                    columnOrdinalsByTable,
                    missingTable => new InvalidOperationException(
                        $"Cannot compile descriptor projection plan for '{missingTable}': owning table is not present in TablesInDependencyOrder."
                    )
                );

                return new DescriptorProjectionSource(
                    DescriptorValuePath: edgeSource.DescriptorValuePath,
                    Table: edgeSource.Table,
                    DescriptorResource: edgeSource.DescriptorResource,
                    DescriptorIdColumnOrdinal: ProjectionMetadataResolver.ResolveHydrationColumnOrdinalOrThrow(
                        columnOrdinals,
                        edgeSource.FkColumn,
                        missingColumn => new InvalidOperationException(
                            $"Cannot compile descriptor projection plan for '{edgeSource.Table}': descriptor edge source '{edgeSource.DescriptorValuePath.Canonical}' FK column '{missingColumn.Value}' does not exist in hydration select-list columns."
                        )
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
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        var tableDependencyOrder = resourceModel
            .TablesInDependencyOrder.Select((table, index) => (table.Table, index))
            .ToDictionary(entry => entry.Table, entry => entry.index);
        Dictionary<DescriptorProjectionSqlSourceKey, DescriptorProjectionSqlSource> sqlSourcesByKey = [];

        foreach (var edgeSource in resourceModel.DescriptorEdgeSources)
        {
            var tableModel = ProjectionMetadataResolver.ResolveTableModelOrThrow(
                edgeSource.Table,
                tablesByName,
                missingTable => new InvalidOperationException(
                    $"Cannot compile descriptor projection plan for '{missingTable}': owning table is not present in TablesInDependencyOrder."
                )
            );
            var storageColumn = ResolveStorageColumnOrThrow(tableModel, edgeSource);
            var sqlSource = new DescriptorProjectionSqlSource(
                TableModel: tableModel,
                StorageColumn: storageColumn,
                TableDependencyOrdinal: ResolveTableDependencyOrdinalOrThrow(
                    tableDependencyOrder,
                    edgeSource.Table
                ),
                StorageColumnOrdinal: ProjectionMetadataResolver.ResolveTableColumnOrdinalOrThrow(
                    tableModel,
                    storageColumn,
                    missingColumn => new InvalidOperationException(
                        $"Cannot compile descriptor projection plan for '{tableModel.Table}': descriptor edge source '{edgeSource.DescriptorValuePath.Canonical}' storage column '{missingColumn.Value}' does not exist in table columns."
                    )
                )
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
                    var rootScopeLocatorColumn =
                        RelationalResourceModelCompileValidator.ResolveRootScopeLocatorColumnOrThrow(
                            tableModel,
                            "descriptor projection plan"
                        );

                    writer.Append("SELECT ");

                    if (sqlSources.Count == 1)
                    {
                        writer.Append("DISTINCT ");
                    }

                    AppendQualifiedColumn(writer, tableAlias, sqlSource.StorageColumn);
                    writer.Append(" AS ").AppendQuoted(_descriptorIdProjectionColumn.Value).AppendLine();
                    writer.Append("FROM ").AppendTable(tableModel.Table).AppendLine($" {tableAlias}");
                    writer
                        .Append("INNER JOIN ")
                        .AppendRelation(keysetTable.Table)
                        .Append($" {keysetAlias} ON ");
                    AppendQualifiedColumn(writer, tableAlias, rootScopeLocatorColumn);
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
        var bindingColumn = ProjectionMetadataResolver.ResolveTableColumnOrThrow(
            tableModel,
            edgeSource.FkColumn,
            missingColumn => new InvalidOperationException(
                $"{contextDescription} '{missingColumn.Value}' does not exist in table columns."
            )
        );

        ValidateDescriptorFkColumnKindOrThrow(bindingColumn, contextDescription, edgeSource.FkColumn);

        return bindingColumn.Storage switch
        {
            ColumnStorage.Stored => bindingColumn.ColumnName,
            ColumnStorage.UnifiedAlias unifiedAlias => ValidateStoredStorageColumnOrThrow(
                ProjectionMetadataResolver.ResolveTableColumnOrThrow(
                    tableModel,
                    unifiedAlias.CanonicalColumn,
                    missingColumn => new InvalidOperationException(
                        $"{contextDescription} '{edgeSource.FkColumn.Value}' resolved to missing canonical storage column '{missingColumn.Value}' does not exist in table columns."
                    )
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

    private static void ValidateDescriptorFkColumnKindOrThrow(
        DbColumnModel columnModel,
        string contextDescription,
        DbColumnName resolvedColumnName
    )
    {
        if (columnModel.Kind is ColumnKind.DescriptorFk)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{contextDescription} '{resolvedColumnName.Value}' has kind '{columnModel.Kind}'. "
                + $"Expected '{ColumnKind.DescriptorFk}'."
        );
    }

    private static DbColumnName ValidateStoredStorageColumnOrThrow(
        DbColumnModel columnModel,
        string contextDescription,
        DbColumnName resolvedColumnName
    )
    {
        ValidateDescriptorFkColumnKindOrThrow(columnModel, contextDescription, resolvedColumnName);

        if (columnModel.Storage is not ColumnStorage.Stored)
        {
            throw new InvalidOperationException(
                $"{contextDescription} '{resolvedColumnName.Value}' is not stored."
            );
        }

        return columnModel.ColumnName;
    }

    private static int ResolveTableDependencyOrdinalOrThrow(
        IReadOnlyDictionary<DbTableName, int> tableDependencyOrder,
        DbTableName table
    )
    {
        if (tableDependencyOrder.TryGetValue(table, out var tableOrdinal))
        {
            return tableOrdinal;
        }

        throw new InvalidOperationException(
            $"Cannot compile descriptor projection plan for '{table}': owning table is not present in TablesInDependencyOrder."
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
