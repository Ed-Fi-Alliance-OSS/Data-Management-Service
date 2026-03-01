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
/// Compiles thin-slice root-table read plans for root-only relational resources.
/// </summary>
public sealed class RootOnlyReadPlanCompiler(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    /// <summary>
    /// Returns <see langword="true" /> when a resource is supported by thin-slice root-only read compilation.
    /// </summary>
    public static bool IsSupported(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        return resourceModel.StorageKind == ResourceStorageKind.RelationalTables
            && resourceModel.TablesInDependencyOrder.Count == 1;
    }

    /// <summary>
    /// Attempts to compile a root-only read plan, returning <see langword="false" /> when unsupported.
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

        readPlan = Compile(resourceModel);
        return true;
    }

    /// <summary>
    /// Compiles a root-only read plan for a single supported resource.
    /// </summary>
    public ResourceReadPlan Compile(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        if (!IsSupported(resourceModel))
        {
            throw new NotSupportedException(
                "Only root-only relational-table resources are supported. "
                    + $"Resource: {resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}, "
                    + $"StorageKind: {resourceModel.StorageKind}, "
                    + $"TableCount: {resourceModel.TablesInDependencyOrder.Count}."
            );
        }

        var rootTable = resourceModel.TablesInDependencyOrder[0];
        var keysetTable = KeysetTableConventions.GetKeysetTableContract(_dialect);

        var tablePlan = new TableReadPlan(
            TableModel: rootTable,
            SelectByKeysetSql: EmitSelectByKeysetSql(rootTable, keysetTable)
        );

        return new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: keysetTable,
            TablePlansInDependencyOrder: [tablePlan],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }

    private string EmitSelectByKeysetSql(DbTableModel rootTable, KeysetTableContract keysetTable)
    {
        if (rootTable.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile read plan for '{rootTable.Table}': no columns were found."
            );
        }

        if (rootTable.Key.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile read plan for '{rootTable.Table}': no key columns were found."
            );
        }

        var tableAlias = PlanNamingConventions.CreateTableAliasAllocator().AllocateNext();
        var keysetAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset);
        var rootDocumentIdKeyColumn = ResolveRootDocumentIdKeyColumn(rootTable);

        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            for (var index = 0; index < rootTable.Columns.Count; index++)
            {
                AppendQualifiedColumn(writer, tableAlias, rootTable.Columns[index].ColumnName);

                if (index + 1 < rootTable.Columns.Count)
                {
                    writer.AppendLine(",");
                }
                else
                {
                    writer.AppendLine();
                }
            }
        }

        writer.Append("FROM ").AppendTable(rootTable.Table).AppendLine($" {tableAlias}");

        writer.Append("INNER JOIN ").AppendRelation(keysetTable.Table).Append($" {keysetAlias} ON ");
        AppendQualifiedColumn(writer, tableAlias, rootDocumentIdKeyColumn);
        writer.Append(" = ");
        AppendQualifiedColumn(writer, keysetAlias, keysetTable.DocumentIdColumnName);
        writer.AppendLine();

        writer.AppendLine("ORDER BY");

        using (writer.Indent())
        {
            for (var index = 0; index < rootTable.Key.Columns.Count; index++)
            {
                AppendQualifiedColumn(writer, tableAlias, rootTable.Key.Columns[index].ColumnName);
                writer.Append(" ASC");

                if (index + 1 < rootTable.Key.Columns.Count)
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

    private static DbColumnName ResolveRootDocumentIdKeyColumn(DbTableModel rootTable)
    {
        var rootDocumentIdKeyColumns = rootTable
            .Key.Columns.Where(column => RelationalNameConventions.IsDocumentIdColumn(column.ColumnName))
            .Select(static column => column.ColumnName)
            .ToArray();

        if (rootDocumentIdKeyColumns.Length == 1)
        {
            return rootDocumentIdKeyColumns[0];
        }

        var keyColumnList = string.Join(
            ", ",
            rootTable.Key.Columns.Select(column => column.ColumnName.Value)
        );

        throw new InvalidOperationException(
            $"Cannot compile read plan for '{rootTable.Table}': expected exactly one root document-id key column but found {rootDocumentIdKeyColumns.Length}. "
                + $"Key columns: [{keyColumnList}]."
        );
    }

    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }
}
