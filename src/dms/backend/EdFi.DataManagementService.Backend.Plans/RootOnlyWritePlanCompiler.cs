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
/// Compiles thin-slice root-table write plans for root-only relational resources.
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

        writePlan = CompileCore(resourceModel, supportResult);
        return true;
    }

    /// <summary>
    /// Compiles a root-only write plan for a single supported resource.
    /// </summary>
    /// <param name="resourceModel">The resource model to compile.</param>
    public ResourceWritePlan Compile(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        var supportResult = ThinSliceWritePlanSupportEvaluator.Evaluate(resourceModel);

        return CompileCore(resourceModel, supportResult);
    }

    /// <summary>
    /// Compiles a root-table write plan using deterministic binding order, parameter naming, and canonical SQL emission.
    /// </summary>
    private ResourceWritePlan CompileCore(
        RelationalResourceModel resourceModel,
        ThinSliceWritePlanSupportResult supportResult
    )
    {
        if (!supportResult.IsSupported)
        {
            throw new NotSupportedException(
                "Only root-only relational-table resources are supported. "
                    + $"Resource: {resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}, "
                    + $"StorageKind: {supportResult.StorageKind}, "
                    + $"TableCount: {supportResult.TableCount}, "
                    + $"RootKeyUnificationClassCount: {supportResult.RootKeyUnificationClassCount}, "
                    + $"RootStoredNonKeyColumnsWithoutSourceJsonPath: {supportResult.RootStoredNonKeyColumnsWithoutSourceJsonPathCount}."
            );
        }

        var rootTable = resourceModel.TablesInDependencyOrder[0];
        var storedColumnsInOrder = rootTable
            .Columns.Where(static column => column.Storage is ColumnStorage.Stored)
            .ToArray();

        if (storedColumnsInOrder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for '{rootTable.Table}': no stored columns were found."
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
                Source: DeriveWriteValueSource(resourceModel, rootTable, column),
                ParameterName: orderedParameterNames[index]
            );
        }

        var insertSql = _insertSqlEmitter.Emit(rootTable.Table, orderedColumnNames, orderedParameterNames);
        var updateSql = TryEmitUpdateSql(rootTable, columnBindings);
        var bulkInsertBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            _dialect,
            columnBindings
        );

        var tablePlan = new TableWritePlan(
            TableModel: rootTable,
            InsertSql: insertSql,
            UpdateSql: updateSql,
            DeleteByParentSql: null,
            BulkInsertBatching: bulkInsertBatching,
            ColumnBindings: columnBindings,
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(resourceModel, [tablePlan]);
    }

    /// <summary>
    /// Emits root-table <c>UPDATE</c> SQL when at least one stored non-key column is writable; otherwise returns <see langword="null" />.
    /// </summary>
    private string? TryEmitUpdateSql(
        DbTableModel rootTable,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        var keyColumnsInKeyOrder = rootTable
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
                    $"Cannot emit update SQL for '{rootTable.Table}': key column '{keyColumn.Value}' does not have a write binding parameter."
                );
            }

            keyParameterNamesInKeyOrder[index] = keyParameterName;
        }

        return _updateSqlEmitter.Emit(
            rootTable.Table,
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
