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
                SelectByKeysetSql: EmitSelectByKeysetSql(
                    resourceModel.DescriptorEdgeSources,
                    keysetTable,
                    tablesByName
                ),
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: compiledSources
            ),
        ];
    }

    private string EmitSelectByKeysetSql(
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources,
        KeysetTableContract keysetTable,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
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
                for (var index = 0; index < descriptorEdgeSources.Count; index++)
                {
                    var edgeSource = descriptorEdgeSources[index];
                    var tableModel = ResolveTableModelOrThrow(tablesByName, edgeSource.Table);
                    var tableAlias = tableAliasAllocator.AllocateNext();
                    var rootDocumentIdKeyColumn = tableModel.Key.Columns[0].ColumnName;

                    writer.Append("SELECT ");
                    AppendQualifiedColumn(writer, tableAlias, edgeSource.FkColumn);
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
                    AppendQualifiedColumn(writer, tableAlias, edgeSource.FkColumn);
                    writer.AppendLine(" IS NOT NULL");

                    if (index + 1 < descriptorEdgeSources.Count)
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
}
