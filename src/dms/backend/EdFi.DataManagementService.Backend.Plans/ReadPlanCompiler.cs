// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles hydration read plans for all relational-table resource tables in dependency order.
/// </summary>
public sealed class ReadPlanCompiler(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    /// <summary>
    /// Returns <see langword="true" /> when the resource uses relational-table storage.
    /// </summary>
    public static bool IsSupported(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        return resourceModel.StorageKind == ResourceStorageKind.RelationalTables;
    }

    /// <summary>
    /// Attempts to compile a hydration read plan, returning <see langword="false" /> when unsupported.
    /// </summary>
    public bool TryCompile(
        RelationalResourceModel resourceModel,
        [NotNullWhen(true)] out ResourceReadPlan? readPlan
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        if (!IsSupported(resourceModel))
        {
            readPlan = null;
            return false;
        }

        readPlan = CompileCore(resourceModel);
        return true;
    }

    /// <summary>
    /// Compiles a hydration read plan for a single relational-table resource.
    /// </summary>
    public ResourceReadPlan Compile(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        if (!IsSupported(resourceModel))
        {
            throw new NotSupportedException(
                "Only relational-table resources are supported by hydration read plan compilation. "
                    + $"Resource: {resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}, "
                    + $"StorageKind: {resourceModel.StorageKind}."
            );
        }

        return CompileCore(resourceModel);
    }

    /// <summary>
    /// Compiles one table read plan per <see cref="RelationalResourceModel.TablesInDependencyOrder" /> entry.
    /// </summary>
    private ResourceReadPlan CompileCore(RelationalResourceModel resourceModel)
    {
        var keysetTable = KeysetTableConventions.GetKeysetTableContract(_dialect);
        var tablePlans = new TableReadPlan[resourceModel.TablesInDependencyOrder.Count];

        for (var index = 0; index < resourceModel.TablesInDependencyOrder.Count; index++)
        {
            var tableModel = resourceModel.TablesInDependencyOrder[index];
            var tableAlias =
                index == 0
                    ? PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root)
                    : PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Table);

            tablePlans[index] = new TableReadPlan(
                TableModel: tableModel,
                SelectByKeysetSql: EmitSelectByKeysetSql(tableModel, keysetTable, tableAlias)
            );
        }

        return new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: keysetTable,
            TablePlansInDependencyOrder: tablePlans,
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }

    /// <summary>
    /// Emits canonical keyset-join hydration SQL for a single table.
    /// </summary>
    private string EmitSelectByKeysetSql(
        DbTableModel tableModel,
        KeysetTableContract keysetTable,
        string tableAlias
    )
    {
        if (tableModel.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile read plan for '{tableModel.Table}': no columns were found."
            );
        }

        if (tableModel.Key.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile read plan for '{tableModel.Table}': no key columns were found."
            );
        }

        var keysetAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset);
        var rootDocumentIdKeyColumn = ResolveRootDocumentIdKeyColumn(tableModel);
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            for (var index = 0; index < tableModel.Columns.Count; index++)
            {
                AppendQualifiedColumn(writer, tableAlias, tableModel.Columns[index].ColumnName);

                if (index + 1 < tableModel.Columns.Count)
                {
                    writer.AppendLine(",");
                }
                else
                {
                    writer.AppendLine();
                }
            }
        }

        writer.Append("FROM ").AppendTable(tableModel.Table).AppendLine($" {tableAlias}");

        writer.Append("INNER JOIN ").AppendRelation(keysetTable.Table).Append($" {keysetAlias} ON ");
        AppendQualifiedColumn(writer, tableAlias, rootDocumentIdKeyColumn);
        writer.Append(" = ");
        AppendQualifiedColumn(writer, keysetAlias, keysetTable.DocumentIdColumnName);
        writer.AppendLine();

        writer.AppendLine("ORDER BY");
        var orderByKeyColumns = GetOrderByKeyColumns(tableModel, rootDocumentIdKeyColumn);

        using (writer.Indent())
        {
            for (var index = 0; index < orderByKeyColumns.Count; index++)
            {
                AppendQualifiedColumn(writer, tableAlias, orderByKeyColumns[index]);
                writer.Append(" ASC");

                if (index + 1 < orderByKeyColumns.Count)
                {
                    writer.AppendLine(",");
                }
                else
                {
                    writer.AppendLine();
                }
            }
        }

        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Resolves the single root document-id key column used to join the table to the page keyset.
    /// </summary>
    private static DbColumnName ResolveRootDocumentIdKeyColumn(DbTableModel tableModel)
    {
        var rootDocumentIdKeyColumns = tableModel
            .Key.Columns.Where(column => RelationalNameConventions.IsDocumentIdColumn(column.ColumnName))
            .Select(static column => column.ColumnName)
            .ToArray();

        if (rootDocumentIdKeyColumns.Length == 1)
        {
            return rootDocumentIdKeyColumns[0];
        }

        var keyColumnList = string.Join(
            ", ",
            tableModel.Key.Columns.Select(column => column.ColumnName.Value)
        );

        throw new InvalidOperationException(
            $"Cannot compile read plan for '{tableModel.Table}': expected exactly one root document-id key column but found {rootDocumentIdKeyColumns.Length}. "
                + $"Key columns: [{keyColumnList}]."
        );
    }

    /// <summary>
    /// Produces a deterministic <c>ORDER BY</c> key column list with the root document-id key first.
    /// </summary>
    private static List<DbColumnName> GetOrderByKeyColumns(
        DbTableModel tableModel,
        DbColumnName rootDocumentIdKeyColumn
    )
    {
        List<DbColumnName> orderByKeyColumns = [rootDocumentIdKeyColumn];

        foreach (var keyColumn in tableModel.Key.Columns)
        {
            if (keyColumn.ColumnName == rootDocumentIdKeyColumn)
            {
                continue;
            }

            orderByKeyColumns.Add(keyColumn.ColumnName);
        }

        return orderByKeyColumns;
    }

    /// <summary>
    /// Appends a qualified column reference (<c>{alias}."Column"</c>) using dialect quoting rules.
    /// </summary>
    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }
}
