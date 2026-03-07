// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles hydration read plans for all relational-table resource tables in dependency order.
/// </summary>
public sealed class ReadPlanCompiler(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly ReferenceIdentityProjectionPlanCompiler _referenceIdentityProjectionPlanCompiler = new();
    private readonly DescriptorProjectionPlanCompiler _descriptorProjectionPlanCompiler = new(dialect);

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
        var rootScopeTableModel = RelationalResourceModelCompileValidator.ResolveRootScopeTableModelOrThrow(
            resourceModel,
            "read plan"
        );
        var hydrationPlanMetadata = BuildHydrationPlanMetadata(resourceModel);

        var keysetTable = KeysetTableConventions.GetKeysetTableContract(_dialect);
        var tablePlans = new TableReadPlan[resourceModel.TablesInDependencyOrder.Count];

        for (var index = 0; index < resourceModel.TablesInDependencyOrder.Count; index++)
        {
            var tableModel = resourceModel.TablesInDependencyOrder[index];
            var tableAlias = tableModel.Equals(rootScopeTableModel)
                ? PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root)
                : PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Table);

            tablePlans[index] = new TableReadPlan(
                TableModel: tableModel,
                SelectByKeysetSql: EmitSelectByKeysetSql(tableModel, keysetTable, tableAlias)
            );
        }

        var referenceIdentityProjectionPlans = _referenceIdentityProjectionPlanCompiler.Compile(
            resourceModel,
            hydrationPlanMetadata.ColumnOrdinalsByTable
        );
        var descriptorProjectionPlans = _descriptorProjectionPlanCompiler.Compile(
            resourceModel,
            keysetTable,
            hydrationPlanMetadata.TablesByName,
            hydrationPlanMetadata.ColumnOrdinalsByTable
        );

        var readPlan = new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: keysetTable,
            TablePlansInDependencyOrder: tablePlans,
            ReferenceIdentityProjectionPlansInDependencyOrder: referenceIdentityProjectionPlans,
            DescriptorProjectionPlansInOrder: descriptorProjectionPlans
        );

        ReadPlanProjectionContractValidator.ValidateOrThrow(
            readPlan,
            reason => new InvalidOperationException(
                $"Cannot compile read plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': "
                    + $"compiled projection metadata is invalid. {reason}. This indicates a read-plan compiler bug."
            )
        );

        return readPlan;
    }

    /// <summary>
    /// Validates key ordering metadata and builds deterministic hydration metadata used by projection compilers.
    /// </summary>
    private static HydrationPlanMetadata BuildHydrationPlanMetadata(RelationalResourceModel resourceModel)
    {
        var tablesByName = new Dictionary<DbTableName, DbTableModel>();
        var columnOrdinalByTable = new Dictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>>();

        foreach (var tableModel in resourceModel.TablesInDependencyOrder)
        {
            var columnOrdinalByName = BuildHydrationColumnOrdinalMap(tableModel);

            if (!tablesByName.TryAdd(tableModel.Table, tableModel))
            {
                throw new InvalidOperationException(
                    $"Cannot compile read plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': duplicate table '{tableModel.Table}' encountered while building hydration table metadata."
                );
            }

            if (!columnOrdinalByTable.TryAdd(tableModel.Table, columnOrdinalByName))
            {
                throw new InvalidOperationException(
                    $"Cannot compile read plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': duplicate table '{tableModel.Table}' encountered while building hydration table metadata."
                );
            }

            ValidateHydrationKeyColumns(tableModel, columnOrdinalByName);
        }

        foreach (var binding in resourceModel.DocumentReferenceBindings)
        {
            ValidateDocumentReferenceBinding(binding, tablesByName, columnOrdinalByTable);
        }

        foreach (var edgeSource in resourceModel.DescriptorEdgeSources)
        {
            ValidateDescriptorEdgeSource(edgeSource, tablesByName, columnOrdinalByTable);
        }

        return new HydrationPlanMetadata(tablesByName, columnOrdinalByTable);
    }

    /// <summary>
    /// Builds a deterministic hydration select-list ordinal map and rejects duplicate physical column names.
    /// </summary>
    private static IReadOnlyDictionary<DbColumnName, int> BuildHydrationColumnOrdinalMap(
        DbTableModel tableModel
    )
    {
        var columnOrdinalByName = new Dictionary<DbColumnName, int>();

        for (var index = 0; index < tableModel.Columns.Count; index++)
        {
            var columnName = tableModel.Columns[index].ColumnName;

            if (!columnOrdinalByName.TryAdd(columnName, index))
            {
                throw new InvalidOperationException(
                    $"Cannot compile read plan for '{tableModel.Table}': duplicate column name '{columnName.Value}' encountered while building hydration select-list ordinal map."
                );
            }
        }

        return columnOrdinalByName;
    }

    /// <summary>
    /// Validates that read-plan ordering metadata uses a deterministic key shape.
    /// </summary>
    private static void ValidateHydrationKeyColumns(
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, int> columnOrdinalByName
    )
    {
        RelationalResourceModelCompileValidator.ValidateDeterministicTableKeyShapeOrThrow(
            tableModel,
            "read plan",
            keyColumn =>
            {
                if (!columnOrdinalByName.ContainsKey(keyColumn.ColumnName))
                {
                    throw new InvalidOperationException(
                        $"Cannot compile read plan for '{tableModel.Table}': key column '{keyColumn.ColumnName.Value}' does not exist in table columns."
                    );
                }
            }
        );
    }

    /// <summary>
    /// Validates that future reference-identity projection dependencies resolve to hydration select-list ordinals.
    /// </summary>
    private static void ValidateDocumentReferenceBinding(
        DocumentReferenceBinding binding,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalByTable
    )
    {
        var tableModel = ResolveHydrationTableModel(binding.Table, tablesByName);
        var columnOrdinalByName = ResolveHydrationColumnOrdinals(binding.Table, columnOrdinalByTable);
        var fkColumn = ResolveHydrationProjectionColumn(
            tableModel,
            binding.FkColumn,
            columnOrdinalByName,
            $"document-reference binding '{binding.ReferenceObjectPath.Canonical}' FK column"
        );

        ReadPlanProjectionContractValidator.ValidateDocumentReferenceBindingPathOrThrow(
            tableModel.Table,
            fkColumn,
            binding,
            reason => new InvalidOperationException(
                $"Cannot compile read plan for '{tableModel.Table}': {reason}."
            )
        );

        foreach (var identityBinding in binding.IdentityBindings)
        {
            var identityColumn = ResolveHydrationProjectionColumn(
                tableModel,
                identityBinding.Column,
                columnOrdinalByName,
                $"reference-identity binding '{identityBinding.ReferenceJsonPath.Canonical}' for reference '{binding.ReferenceObjectPath.Canonical}' column"
            );

            ReadPlanProjectionContractValidator.ValidateReferenceIdentityBindingPathOrThrow(
                tableModel.Table,
                identityColumn,
                binding,
                identityBinding,
                reason => new InvalidOperationException(
                    $"Cannot compile read plan for '{tableModel.Table}': {reason}."
                )
            );
        }
    }

    /// <summary>
    /// Validates that future descriptor projection dependencies resolve to hydration select-list ordinals.
    /// </summary>
    private static void ValidateDescriptorEdgeSource(
        DescriptorEdgeSource edgeSource,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalByTable
    )
    {
        var tableModel = ResolveHydrationTableModel(edgeSource.Table, tablesByName);
        var columnOrdinalByName = ResolveHydrationColumnOrdinals(edgeSource.Table, columnOrdinalByTable);
        var fkColumn = ResolveHydrationProjectionColumn(
            tableModel,
            edgeSource.FkColumn,
            columnOrdinalByName,
            $"descriptor edge source '{edgeSource.DescriptorValuePath.Canonical}' FK column"
        );

        ReadPlanProjectionContractValidator.ValidateDescriptorEdgeSourcePathOrThrow(
            tableModel.Table,
            fkColumn,
            edgeSource,
            reason => new InvalidOperationException(
                $"Cannot compile read plan for '{tableModel.Table}': {reason}."
            )
        );
    }

    /// <summary>
    /// Resolves the table model for a projection binding owner.
    /// </summary>
    private static DbTableModel ResolveHydrationTableModel(
        DbTableName table,
        IReadOnlyDictionary<DbTableName, DbTableModel> tablesByName
    )
    {
        if (tablesByName.TryGetValue(table, out var tableModel))
        {
            return tableModel;
        }

        throw new InvalidOperationException(
            $"Cannot compile read plan for '{table}': owning table is not present in TablesInDependencyOrder."
        );
    }

    /// <summary>
    /// Resolves the table-local hydration select-list ordinal map for a binding owner.
    /// </summary>
    private static IReadOnlyDictionary<DbColumnName, int> ResolveHydrationColumnOrdinals(
        DbTableName table,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalByTable
    )
    {
        if (!columnOrdinalByTable.TryGetValue(table, out var columnOrdinalByName))
        {
            throw new InvalidOperationException(
                $"Cannot compile read plan for '{table}': owning table is not present in TablesInDependencyOrder."
            );
        }

        return columnOrdinalByName;
    }

    /// <summary>
    /// Ensures a binding column resolves to a deterministic hydration select-list ordinal.
    /// </summary>
    private static DbColumnModel ResolveHydrationProjectionColumn(
        DbTableModel tableModel,
        DbColumnName column,
        IReadOnlyDictionary<DbColumnName, int> columnOrdinalByName,
        string dependencyDescription
    )
    {
        if (!columnOrdinalByName.TryGetValue(column, out var columnOrdinal))
        {
            throw new InvalidOperationException(
                $"Cannot compile read plan for '{tableModel.Table}': {dependencyDescription} '{column.Value}' does not exist in hydration select-list columns."
            );
        }

        return tableModel.Columns[columnOrdinal];
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
        var rootDocumentIdKeyColumn = tableModel.Key.Columns[0].ColumnName;
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

        using (writer.Indent())
        {
            for (var index = 0; index < tableModel.Key.Columns.Count; index++)
            {
                AppendQualifiedColumn(writer, tableAlias, tableModel.Key.Columns[index].ColumnName);
                writer.Append(" ASC");

                if (index + 1 < tableModel.Key.Columns.Count)
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
    /// Appends a qualified column reference (<c>{alias}."Column"</c>) using dialect quoting rules.
    /// </summary>
    private static void AppendQualifiedColumn(SqlWriter writer, string tableAlias, DbColumnName columnName)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(columnName.Value);
    }

    private sealed record HydrationPlanMetadata(
        IReadOnlyDictionary<DbTableName, DbTableModel> TablesByName,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> ColumnOrdinalsByTable
    );
}
