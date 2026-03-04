// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles deterministic relational write plans.
/// <para>
/// <see cref="TryCompile(RelationalResourceModel, out ResourceWritePlan?)" /> remains thin-slice gated for the
/// current mapping-set loop. <see cref="Compile(RelationalResourceModel)" /> compiles all tables in dependency order
/// for relational-table resources that do not yet require key-unification plan inventory.
/// </para>
/// </summary>
public sealed class RootOnlyWritePlanCompiler(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;
    private readonly SimpleInsertSqlEmitter _insertSqlEmitter = new(dialect);
    private readonly SimpleUpdateSqlEmitter _updateSqlEmitter = new(dialect);

    /// <summary>
    /// Returns <see langword="true"/> when a resource is supported by thin-slice root-only write compilation.
    /// </summary>
    public static bool IsSupported(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        return ThinSliceWritePlanSupportEvaluator.Evaluate(resourceModel).IsSupported;
    }

    /// <summary>
    /// Attempts to compile a root-only write plan, returning <see langword="false"/> when unsupported.
    /// </summary>
    public bool TryCompile(
        RelationalResourceModel resourceModel,
        [NotNullWhen(true)] out ResourceWritePlan? writePlan
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        var supportResult = ThinSliceWritePlanSupportEvaluator.Evaluate(resourceModel);

        if (!supportResult.IsSupported)
        {
            writePlan = null;
            return false;
        }

        writePlan = Compile(resourceModel);
        return true;
    }

    /// <summary>
    /// Compiles a relational-table write plan across all tables in dependency order.
    /// </summary>
    /// <param name="resourceModel">The resource model to compile.</param>
    public ResourceWritePlan Compile(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ValidateCompileEligibility(resourceModel);

        var tablePlans = resourceModel
            .TablesInDependencyOrder.Select(tableModel => CompileTablePlan(resourceModel, tableModel))
            .ToArray();

        return new ResourceWritePlan(resourceModel, tablePlans);
    }

    /// <summary>
    /// Validates compile-time support constraints for relational write-plan compilation.
    /// </summary>
    private static void ValidateCompileEligibility(RelationalResourceModel resourceModel)
    {
        if (resourceModel.StorageKind is not ResourceStorageKind.RelationalTables)
        {
            throw new NotSupportedException(
                "Only relational-table resources are supported for write-plan compilation. "
                    + $"Resource: {resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}, "
                    + $"StorageKind: {resourceModel.StorageKind}."
            );
        }

        if (resourceModel.TablesInDependencyOrder.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': no tables were found in dependency order."
            );
        }

        var tablesWithKeyUnification = resourceModel
            .TablesInDependencyOrder.Where(static table => table.KeyUnificationClasses.Count > 0)
            .Select(static table =>
                $"{table.Table} (KeyUnificationClassCount: {table.KeyUnificationClasses.Count})"
            )
            .ToArray();

        if (tablesWithKeyUnification.Length > 0)
        {
            throw new NotSupportedException(
                "Write-plan compilation for key-unification tables is not implemented yet. "
                    + $"Resource: {resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}, "
                    + $"Tables: {string.Join(", ", tablesWithKeyUnification)}."
            );
        }
    }

    /// <summary>
    /// Compiles one table write plan using deterministic column bindings and canonical SQL emission.
    /// </summary>
    private TableWritePlan CompileTablePlan(RelationalResourceModel resourceModel, DbTableModel tableModel)
    {
        ValidateWritableKeyColumns(tableModel);
        var columnBindings = CompileStoredColumnBindings(resourceModel, tableModel);

        var insertSql = _insertSqlEmitter.Emit(
            tableModel.Table,
            columnBindings.Select(static binding => binding.Column.ColumnName).ToArray(),
            columnBindings.Select(static binding => binding.ParameterName).ToArray()
        );
        var updateSql = TryEmitUpdateSql(tableModel, columnBindings);
        var bulkInsertBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            _dialect,
            columnBindings
        );

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: insertSql,
            UpdateSql: updateSql,
            DeleteByParentSql: null,
            BulkInsertBatching: bulkInsertBatching,
            ColumnBindings: columnBindings,
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Compiles deterministic stored-column bindings for one table.
    /// </summary>
    private static WriteColumnBinding[] CompileStoredColumnBindings(
        RelationalResourceModel resourceModel,
        DbTableModel tableModel
    )
    {
        var storedColumnsInOrder = tableModel
            .Columns.Where(static column => column.Storage is ColumnStorage.Stored)
            .ToArray();

        if (storedColumnsInOrder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for '{tableModel.Table}': no stored columns were found."
            );
        }

        var orderedColumnNames = storedColumnsInOrder.Select(static column => column.ColumnName).ToArray();
        var orderedParameterNames = PlanNamingConventions.DeriveWriteParameterNamesInOrder(
            orderedColumnNames
        );

        var columnBindings = new WriteColumnBinding[storedColumnsInOrder.Length];

        for (var index = 0; index < storedColumnsInOrder.Length; index++)
        {
            var column = storedColumnsInOrder[index];

            columnBindings[index] = new WriteColumnBinding(
                Column: column,
                Source: DeriveWriteValueSource(resourceModel, tableModel, column),
                ParameterName: orderedParameterNames[index]
            );
        }

        return columnBindings;
    }

    /// <summary>
    /// Validates that every key column maps to a writable stored column. Unified aliases are generated and non-writable.
    /// </summary>
    private static void ValidateWritableKeyColumns(DbTableModel tableModel)
    {
        foreach (var keyColumn in tableModel.Key.Columns)
        {
            var matchingColumn = tableModel.Columns.FirstOrDefault(column =>
                column.ColumnName.Equals(keyColumn.ColumnName)
            );

            if (matchingColumn is null)
            {
                throw new InvalidOperationException(
                    $"Cannot compile write plan for '{tableModel.Table}': key column '{keyColumn.ColumnName.Value}' does not exist in table columns."
                );
            }

            if (matchingColumn.Storage is ColumnStorage.UnifiedAlias unifiedAlias)
            {
                var presenceColumnDescription = unifiedAlias.PresenceColumn switch
                {
                    null => "<none>",
                    { } presenceColumn => presenceColumn.Value,
                };

                throw new InvalidOperationException(
                    $"Cannot compile write plan for '{tableModel.Table}': key column '{keyColumn.ColumnName.Value}' is UnifiedAlias "
                        + $"(canonical '{unifiedAlias.CanonicalColumn.Value}', presence '{presenceColumnDescription}') and is not writable."
                );
            }
        }
    }

    /// <summary>
    /// Emits table <c>UPDATE</c> SQL for 1:1 tables (no ordinal key column) when at least one stored non-key column is writable.
    /// </summary>
    private string? TryEmitUpdateSql(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        if (tableModel.Key.Columns.Any(static keyColumn => keyColumn.Kind is ColumnKind.Ordinal))
        {
            return null;
        }

        var keyColumnsInKeyOrder = tableModel
            .Key.Columns.Select(static keyColumn => keyColumn.ColumnName)
            .ToArray();
        var keyColumns = keyColumnsInKeyOrder.ToHashSet();

        var writableNonKeyBindingsInOrder = bindingsInColumnOrder
            .Where(binding =>
                binding.Column.Storage is ColumnStorage.Stored
                && !keyColumns.Contains(binding.Column.ColumnName)
            )
            .ToArray();

        if (writableNonKeyBindingsInOrder.Length == 0)
        {
            return null;
        }

        var parameterNameByColumn = new Dictionary<DbColumnName, string>(bindingsInColumnOrder.Count);

        foreach (var binding in bindingsInColumnOrder)
        {
            parameterNameByColumn[binding.Column.ColumnName] = binding.ParameterName;
        }

        var keyParameterNamesInKeyOrder = new string[keyColumnsInKeyOrder.Length];

        for (var index = 0; index < keyColumnsInKeyOrder.Length; index++)
        {
            var keyColumn = keyColumnsInKeyOrder[index];

            if (!parameterNameByColumn.TryGetValue(keyColumn, out var keyParameterName))
            {
                throw new InvalidOperationException(
                    $"Cannot emit update SQL for '{tableModel.Table}': key column '{keyColumn.Value}' does not have a write binding parameter."
                );
            }

            keyParameterNamesInKeyOrder[index] = keyParameterName;
        }

        return _updateSqlEmitter.Emit(
            tableModel.Table,
            writableNonKeyBindingsInOrder.Select(static binding => binding.Column.ColumnName).ToArray(),
            writableNonKeyBindingsInOrder.Select(static binding => binding.ParameterName).ToArray(),
            keyColumnsInKeyOrder,
            keyParameterNamesInKeyOrder
        );
    }

    /// <summary>
    /// Derives a deterministic write-time value source contract for a stored column binding.
    /// </summary>
    private static WriteValueSource DeriveWriteValueSource(
        RelationalResourceModel resourceModel,
        DbTableModel tableModel,
        DbColumnModel column
    )
    {
        if (IsDocumentIdKeyColumn(tableModel, column))
        {
            return new WriteValueSource.DocumentId();
        }

        return column.Kind switch
        {
            ColumnKind.ParentKeyPart => new WriteValueSource.ParentKeyPart(
                GetParentKeyPartIndex(tableModel, column)
            ),
            ColumnKind.Ordinal => new WriteValueSource.Ordinal(),
            ColumnKind.DocumentFk when column.SourceJsonPath is not null =>
                new WriteValueSource.DocumentReference(
                    FindDocumentReferenceBindingIndex(resourceModel, tableModel.Table, column.ColumnName)
                ),
            ColumnKind.DescriptorFk when column.SourceJsonPath is JsonPathExpression sourcePath =>
                CreateDescriptorReferenceSource(
                    resourceModel,
                    tableModel.Table,
                    column.ColumnName,
                    WritePlanJsonPathConventions.DeriveScopeRelativePath(tableModel.JsonScope, sourcePath)
                ),
            _ => CreateScalarOrPrecomputedSource(tableModel, column),
        };
    }

    /// <summary>
    /// Returns <see langword="true" /> when the column is the table key's <c>DocumentId</c> component.
    /// </summary>
    private static bool IsDocumentIdKeyColumn(DbTableModel tableModel, DbColumnModel column)
    {
        return column.Kind == ColumnKind.ParentKeyPart
            && tableModel.Key.Columns.Any(keyColumn =>
                keyColumn.Kind == ColumnKind.ParentKeyPart
                && keyColumn.ColumnName.Equals(column.ColumnName)
                && RelationalNameConventions.IsDocumentIdColumn(keyColumn.ColumnName)
            );
    }

    /// <summary>
    /// Gets the 0-based parent-key part index for the column in key order.
    /// </summary>
    private static int GetParentKeyPartIndex(DbTableModel tableModel, DbColumnModel column)
    {
        for (var index = 0; index < tableModel.Key.Columns.Count; index++)
        {
            if (!tableModel.Key.Columns[index].ColumnName.Equals(column.ColumnName))
            {
                continue;
            }

            return index;
        }

        throw new InvalidOperationException(
            $"Column '{column.ColumnName.Value}' on table '{tableModel.Table}' is not in table key order."
        );
    }

    /// <summary>
    /// Finds the document-reference binding inventory index for a specific FK column on a table.
    /// </summary>
    private static int FindDocumentReferenceBindingIndex(
        RelationalResourceModel resourceModel,
        DbTableName table,
        DbColumnName fkColumn
    )
    {
        var matchingIndex = -1;

        for (var index = 0; index < resourceModel.DocumentReferenceBindings.Count; index++)
        {
            var binding = resourceModel.DocumentReferenceBindings[index];

            if (!binding.Table.Equals(table) || !binding.FkColumn.Equals(fkColumn))
            {
                continue;
            }

            if (matchingIndex >= 0)
            {
                throw new InvalidOperationException(
                    $"Multiple document-reference bindings match '{table}.{fkColumn.Value}'."
                );
            }

            matchingIndex = index;
        }

        if (matchingIndex >= 0)
        {
            return matchingIndex;
        }

        throw new InvalidOperationException(
            $"No document-reference binding matches '{table}.{fkColumn.Value}'."
        );
    }

    /// <summary>
    /// Creates a descriptor-reference write value source by matching the descriptor edge source metadata.
    /// </summary>
    private static WriteValueSource CreateDescriptorReferenceSource(
        RelationalResourceModel resourceModel,
        DbTableName table,
        DbColumnName fkColumn,
        JsonPathExpression relativePath
    )
    {
        DescriptorEdgeSource? matchingEdgeSource = null;

        foreach (var edgeSource in resourceModel.DescriptorEdgeSources)
        {
            if (!edgeSource.Table.Equals(table) || !edgeSource.FkColumn.Equals(fkColumn))
            {
                continue;
            }

            if (matchingEdgeSource is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple descriptor edge sources match '{table}.{fkColumn.Value}'."
                );
            }

            matchingEdgeSource = edgeSource;
        }

        if (matchingEdgeSource is null)
        {
            throw new InvalidOperationException(
                $"No descriptor edge source matches '{table}.{fkColumn.Value}'."
            );
        }

        return new WriteValueSource.DescriptorReference(
            DescriptorResource: matchingEdgeSource.DescriptorResource,
            RelativePath: relativePath,
            DescriptorValuePath: matchingEdgeSource.DescriptorValuePath
        );
    }

    /// <summary>
    /// Creates a scalar write value source when JSON-bound; otherwise creates a precomputed value source placeholder.
    /// </summary>
    private static WriteValueSource CreateScalarOrPrecomputedSource(
        DbTableModel tableModel,
        DbColumnModel column
    )
    {
        if (column.SourceJsonPath is null)
        {
            return new WriteValueSource.Precomputed();
        }

        if (column.ScalarType is null)
        {
            throw new InvalidOperationException(
                $"Column '{column.ColumnName.Value}' has a source path but no scalar type."
            );
        }

        return new WriteValueSource.Scalar(
            WritePlanJsonPathConventions.DeriveScopeRelativePath(
                tableModel.JsonScope,
                column.SourceJsonPath.Value
            ),
            column.ScalarType
        );
    }
}
