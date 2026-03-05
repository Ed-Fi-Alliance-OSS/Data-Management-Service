// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles deterministic relational write plans across all dependency-ordered tables for relational-table resources.
/// </summary>
public sealed class WritePlanCompiler(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;
    private readonly SimpleInsertSqlEmitter _insertSqlEmitter = new(dialect);
    private readonly SimpleUpdateSqlEmitter _updateSqlEmitter = new(dialect);
    private readonly SimpleDeleteSqlEmitter _deleteSqlEmitter = new(dialect);

    private readonly record struct WriteSourceLookupKey(DbTableName Table, DbColumnName Column);

    private sealed record WriteSourceLookup(
        IReadOnlyDictionary<WriteSourceLookupKey, int> DocumentReferenceBindingIndexByKey,
        IReadOnlyDictionary<WriteSourceLookupKey, DescriptorEdgeSource> DescriptorEdgeSourceByKey
    );

    /// <summary>
    /// Compiles a relational-table write plan across all tables in dependency order.
    /// </summary>
    /// <param name="resourceModel">The resource model to compile.</param>
    public ResourceWritePlan Compile(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ValidateCompileEligibility(resourceModel);
        var rootScopeTableModel = ResolveRootScopeTableModelOrThrow(resourceModel);
        var writeSourceLookup = BuildWriteSourceLookup(resourceModel);

        var tablePlans = resourceModel
            .TablesInDependencyOrder.Select(tableModel =>
                CompileTablePlan(rootScopeTableModel, tableModel, writeSourceLookup)
            )
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
    }

    private static DbTableModel ResolveRootScopeTableModelOrThrow(RelationalResourceModel resourceModel)
    {
        if (!IsRootJsonScope(resourceModel.Root.JsonScope))
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': resourceModel.Root must have JsonScope '$', but was '{resourceModel.Root.JsonScope.Canonical}'."
            );
        }

        var rootScopeTables = resourceModel
            .TablesInDependencyOrder.Where(static tableModel => IsRootJsonScope(tableModel.JsonScope))
            .ToArray();

        if (rootScopeTables.Length != 1)
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found {rootScopeTables.Length}."
            );
        }

        var rootScopeTable = rootScopeTables[0];

        if (!rootScopeTable.Table.Equals(resourceModel.Root.Table))
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': root-scope table '{rootScopeTable.Table}' does not match resourceModel.Root table '{resourceModel.Root.Table}'."
            );
        }

        return rootScopeTable;
    }

    private static bool IsRootJsonScope(JsonPathExpression jsonScope)
    {
        return jsonScope.Canonical == "$" && jsonScope.Segments.Count == 0;
    }

    private static WriteSourceLookup BuildWriteSourceLookup(RelationalResourceModel resourceModel)
    {
        ValidateUniqueWriteSourceInventoryKeysOrThrow(resourceModel);

        return new WriteSourceLookup(
            DocumentReferenceBindingIndexByKey: BuildWriteSourceLookupMap(
                resourceModel.DocumentReferenceBindings,
                static binding => new WriteSourceLookupKey(binding.Table, binding.FkColumn),
                static (_, index) => index
            ),
            DescriptorEdgeSourceByKey: BuildWriteSourceLookupMap(
                resourceModel.DescriptorEdgeSources,
                static edgeSource => new WriteSourceLookupKey(edgeSource.Table, edgeSource.FkColumn),
                static (edgeSource, _) => edgeSource
            )
        );
    }

    private static FrozenDictionary<WriteSourceLookupKey, TValue> BuildWriteSourceLookupMap<TSource, TValue>(
        IReadOnlyList<TSource> sourceInventory,
        Func<TSource, WriteSourceLookupKey> keySelector,
        Func<TSource, int, TValue> valueSelector
    )
    {
        var lookupByKey = new Dictionary<WriteSourceLookupKey, TValue>(sourceInventory.Count);

        for (var index = 0; index < sourceInventory.Count; index++)
        {
            var source = sourceInventory[index];
            var lookupKey = keySelector(source);
            var value = valueSelector(source, index);

            if (!lookupByKey.TryAdd(lookupKey, value))
            {
                throw new InvalidOperationException(
                    $"Duplicate write-source lookup key '{lookupKey.Table}.{lookupKey.Column.Value}' was encountered while building compiler lookup maps."
                );
            }
        }

        return lookupByKey.ToFrozenDictionary();
    }

    private static void ValidateUniqueWriteSourceInventoryKeysOrThrow(RelationalResourceModel resourceModel)
    {
        ValidateUniqueWriteSourceInventoryKeysOrThrow(
            resourceModel,
            resourceModel.DocumentReferenceBindings,
            static binding => new WriteSourceLookupKey(binding.Table, binding.FkColumn),
            "document-reference binding"
        );
        ValidateUniqueWriteSourceInventoryKeysOrThrow(
            resourceModel,
            resourceModel.DescriptorEdgeSources,
            static edgeSource => new WriteSourceLookupKey(edgeSource.Table, edgeSource.FkColumn),
            "descriptor edge source"
        );
    }

    private static void ValidateUniqueWriteSourceInventoryKeysOrThrow<TSource>(
        RelationalResourceModel resourceModel,
        IReadOnlyList<TSource> sourceInventory,
        Func<TSource, WriteSourceLookupKey> keySelector,
        string sourceInventoryName
    )
    {
        if (sourceInventory.Count <= 1)
        {
            return;
        }

        var keyCountByLookupKey = new Dictionary<WriteSourceLookupKey, int>(sourceInventory.Count);

        foreach (var source in sourceInventory)
        {
            var lookupKey = keySelector(source);
            var existingCount = keyCountByLookupKey.GetValueOrDefault(lookupKey);

            keyCountByLookupKey[lookupKey] = existingCount + 1;
        }

        var duplicateKeySummaries = keyCountByLookupKey
            .Where(static keyCountPair => keyCountPair.Value > 1)
            .OrderBy(static keyCountPair => keyCountPair.Key.Table.ToString(), StringComparer.Ordinal)
            .ThenBy(static keyCountPair => keyCountPair.Key.Column.Value, StringComparer.Ordinal)
            .Select(static keyCountPair =>
                $"{keyCountPair.Key.Table}.{keyCountPair.Key.Column.Value} (count: {keyCountPair.Value})"
            )
            .ToArray();

        if (duplicateKeySummaries.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot compile write plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}': duplicate {sourceInventoryName} key(s) were found: {string.Join(", ", duplicateKeySummaries)}."
        );
    }

    /// <summary>
    /// Compiles one table write plan using deterministic column bindings and canonical SQL emission.
    /// </summary>
    private TableWritePlan CompileTablePlan(
        DbTableModel rootScopeTableModel,
        DbTableModel tableModel,
        WriteSourceLookup writeSourceLookup
    )
    {
        var tableCompilationContext = CreateTableCompilationContext(tableModel, writeSourceLookup);
        var keyUnificationPlans = KeyUnificationWritePlanCompiler.Compile(tableCompilationContext);

        var insertSql = _insertSqlEmitter.Emit(
            tableModel.Table,
            tableCompilationContext
                .ColumnBindings.Select(static binding => binding.Column.ColumnName)
                .ToArray(),
            tableCompilationContext.ColumnBindings.Select(static binding => binding.ParameterName).ToArray()
        );
        var updateSql = TryEmitUpdateSql(tableCompilationContext);
        var deleteByParentSql = TryEmitDeleteByParentSql(rootScopeTableModel, tableCompilationContext);
        var bulkInsertBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            _dialect,
            tableCompilationContext.ColumnBindings
        );

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: insertSql,
            UpdateSql: updateSql,
            DeleteByParentSql: deleteByParentSql,
            BulkInsertBatching: bulkInsertBatching,
            ColumnBindings: tableCompilationContext.ColumnBindings,
            KeyUnificationPlans: keyUnificationPlans
        );
    }

    private static WritePlanTableCompilationContext CreateTableCompilationContext(
        DbTableModel tableModel,
        WriteSourceLookup writeSourceLookup
    )
    {
        var columnByName = BuildColumnByNameMapOrThrow(tableModel);
        ValidateWritableKeyColumns(tableModel, columnByName);

        var keyColumnNames = tableModel
            .Key.Columns.Select(static keyColumn => keyColumn.ColumnName)
            .ToHashSet();
        var requiredKeyUnificationPrecomputedColumns = DeriveRequiredKeyUnificationPrecomputedColumns(
            tableModel,
            columnByName
        );

        var columnBindings = CompileStoredColumnBindings(
            tableModel,
            writeSourceLookup,
            keyColumnNames,
            requiredKeyUnificationPrecomputedColumns
        );

        var bindingIndexByColumn = BuildBindingIndexByColumnMapOrThrow(tableModel, columnBindings);
        var parameterNameByColumn = BuildParameterNameByColumnMapOrThrow(tableModel, columnBindings);

        return new WritePlanTableCompilationContext(
            TableModel: tableModel,
            ColumnByName: columnByName,
            BindingIndexByColumn: bindingIndexByColumn,
            ParameterNameByColumn: parameterNameByColumn,
            KeyColumnNames: keyColumnNames,
            ColumnBindings: columnBindings
        );
    }

    /// <summary>
    /// Compiles deterministic stored-column bindings for one table.
    /// Includes writable stored columns plus key columns and key-unification precomputed targets.
    /// </summary>
    private static WriteColumnBinding[] CompileStoredColumnBindings(
        DbTableModel tableModel,
        WriteSourceLookup writeSourceLookup,
        IReadOnlySet<DbColumnName> keyColumnNames,
        IReadOnlySet<DbColumnName> requiredKeyUnificationPrecomputedColumns
    )
    {
        var storedColumnsInOrder = tableModel
            .Columns.Where(column =>
                column.Storage is ColumnStorage.Stored
                && (
                    column.IsWritable
                    || keyColumnNames.Contains(column.ColumnName)
                    || requiredKeyUnificationPrecomputedColumns.Contains(column.ColumnName)
                )
            )
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
                Source: DeriveWriteValueSource(
                    tableModel,
                    column,
                    writeSourceLookup,
                    requiredKeyUnificationPrecomputedColumns
                ),
                ParameterName: orderedParameterNames[index]
            );
        }

        return columnBindings;
    }

    private static IReadOnlySet<DbColumnName> DeriveRequiredKeyUnificationPrecomputedColumns(
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnByName
    )
    {
        if (tableModel.KeyUnificationClasses.Count == 0)
        {
            return new HashSet<DbColumnName>();
        }

        var requiredColumns = new HashSet<DbColumnName>();

        foreach (var keyClass in tableModel.KeyUnificationClasses)
        {
            requiredColumns.Add(keyClass.CanonicalColumn);

            foreach (var memberPathColumnName in keyClass.MemberPathColumns)
            {
                if (
                    !columnByName.TryGetValue(memberPathColumnName, out var memberPathColumn)
                    || memberPathColumn.Storage
                        is not ColumnStorage.UnifiedAlias { PresenceColumn: { } presenceColumn }
                    || !columnByName.TryGetValue(presenceColumn, out var presenceColumnModel)
                    || !KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(presenceColumnModel)
                )
                {
                    continue;
                }

                requiredColumns.Add(presenceColumn);
            }
        }

        return requiredColumns;
    }

    /// <summary>
    /// Validates that every key column maps to a writable stored column. Unified aliases are generated and non-writable.
    /// </summary>
    private static void ValidateWritableKeyColumns(
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnByName
    )
    {
        if (tableModel.Key.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for '{tableModel.Table}': table key contains no columns."
            );
        }

        var documentIdParentKeyPartCount = 0;
        var ordinalKeyColumnCount = 0;

        foreach (var keyColumn in tableModel.Key.Columns)
        {
            if (keyColumn.Kind is not ColumnKind.ParentKeyPart and not ColumnKind.Ordinal)
            {
                throw new InvalidOperationException(
                    $"Cannot compile write plan for '{tableModel.Table}': key column '{keyColumn.ColumnName.Value}' has unsupported kind '{keyColumn.Kind}'. "
                        + $"Supported key kinds are {nameof(ColumnKind.ParentKeyPart)} and {nameof(ColumnKind.Ordinal)}."
                );
            }

            if (!columnByName.TryGetValue(keyColumn.ColumnName, out var matchingColumn))
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

            if (
                keyColumn.Kind is ColumnKind.ParentKeyPart
                && RelationalNameConventions.IsDocumentIdColumn(keyColumn.ColumnName)
            )
            {
                documentIdParentKeyPartCount++;
            }

            if (keyColumn.Kind is ColumnKind.Ordinal)
            {
                ordinalKeyColumnCount++;
            }
        }

        if (documentIdParentKeyPartCount != 1)
        {
            var keyColumnSummary = string.Join(
                ", ",
                tableModel.Key.Columns.Select(static keyColumn =>
                    $"{keyColumn.ColumnName.Value}:{keyColumn.Kind}"
                )
            );

            throw new InvalidOperationException(
                $"Cannot compile write plan for '{tableModel.Table}': expected exactly one ParentKeyPart document-id key column "
                    + $"('{RelationalNameConventions.DocumentIdColumnName.Value}' or '*_{RelationalNameConventions.DocumentIdColumnName.Value}'), "
                    + $"but found {documentIdParentKeyPartCount}. Key columns: [{keyColumnSummary}]."
            );
        }

        var firstKeyColumn = tableModel.Key.Columns[0];

        if (
            firstKeyColumn.Kind is not ColumnKind.ParentKeyPart
            || !RelationalNameConventions.IsDocumentIdColumn(firstKeyColumn.ColumnName)
        )
        {
            var keyColumnSummary = string.Join(
                ", ",
                tableModel.Key.Columns.Select(static keyColumn =>
                    $"{keyColumn.ColumnName.Value}:{keyColumn.Kind}"
                )
            );

            throw new InvalidOperationException(
                $"Cannot compile write plan for '{tableModel.Table}': expected document-id ParentKeyPart key column ('{RelationalNameConventions.DocumentIdColumnName.Value}' or '*_{RelationalNameConventions.DocumentIdColumnName.Value}') to be first in key order, but found '{firstKeyColumn.ColumnName.Value}:{firstKeyColumn.Kind}'. Key columns: [{keyColumnSummary}]."
            );
        }

        if (ordinalKeyColumnCount > 1)
        {
            var keyColumnSummary = string.Join(
                ", ",
                tableModel.Key.Columns.Select(static keyColumn =>
                    $"{keyColumn.ColumnName.Value}:{keyColumn.Kind}"
                )
            );

            throw new InvalidOperationException(
                $"Cannot compile write plan for '{tableModel.Table}': expected at most one Ordinal key column, but found {ordinalKeyColumnCount}. Key columns: [{keyColumnSummary}]."
            );
        }

        if (ordinalKeyColumnCount == 1 && tableModel.Key.Columns[^1].Kind is not ColumnKind.Ordinal)
        {
            var keyColumnSummary = string.Join(
                ", ",
                tableModel.Key.Columns.Select(static keyColumn =>
                    $"{keyColumn.ColumnName.Value}:{keyColumn.Kind}"
                )
            );

            throw new InvalidOperationException(
                $"Cannot compile write plan for '{tableModel.Table}': expected Ordinal key column to be last in key order. Key columns: [{keyColumnSummary}]."
            );
        }
    }

    /// <summary>
    /// Emits table <c>UPDATE</c> SQL for 1:1 tables (no ordinal key column) when at least one stored non-key column is writable.
    /// </summary>
    private string? TryEmitUpdateSql(WritePlanTableCompilationContext tableCompilationContext)
    {
        var tableModel = tableCompilationContext.TableModel;

        if (tableModel.Key.Columns.Any(static keyColumn => keyColumn.Kind is ColumnKind.Ordinal))
        {
            return null;
        }

        var keyColumnsInKeyOrder = tableModel
            .Key.Columns.Select(static keyColumn => keyColumn.ColumnName)
            .ToArray();

        var writableNonKeyBindingsInOrder = tableCompilationContext
            .ColumnBindings.Where(binding =>
                !tableCompilationContext.KeyColumnNames.Contains(binding.Column.ColumnName)
            )
            .ToArray();

        if (writableNonKeyBindingsInOrder.Length == 0)
        {
            return null;
        }

        var keyParameterNamesInKeyOrder = DeriveRequiredKeyParameterNamesInOrder(
            tableModel,
            keyColumnsInKeyOrder,
            tableCompilationContext.ParameterNameByColumn,
            sqlOperation: "update"
        );

        return _updateSqlEmitter.Emit(
            tableModel.Table,
            writableNonKeyBindingsInOrder.Select(static binding => binding.Column.ColumnName).ToArray(),
            writableNonKeyBindingsInOrder.Select(static binding => binding.ParameterName).ToArray(),
            keyColumnsInKeyOrder,
            keyParameterNamesInKeyOrder
        );
    }

    /// <summary>
    /// Emits table <c>DELETE</c> SQL for replace semantics by parent key for all non-root tables.
    /// </summary>
    private string? TryEmitDeleteByParentSql(
        DbTableModel rootScopeTableModel,
        WritePlanTableCompilationContext tableCompilationContext
    )
    {
        var tableModel = tableCompilationContext.TableModel;

        if (tableModel.Table.Equals(rootScopeTableModel.Table) && IsRootJsonScope(tableModel.JsonScope))
        {
            return null;
        }

        var keyColumnsInOrder = tableModel
            .Key.Columns.Where(static keyColumn => keyColumn.Kind is ColumnKind.ParentKeyPart)
            .Select(static keyColumn => keyColumn.ColumnName)
            .ToArray();

        if (keyColumnsInOrder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot emit delete-by-parent SQL for '{tableModel.Table}': no parent key columns were found."
            );
        }

        var keyParameterNamesInOrder = DeriveRequiredKeyParameterNamesInOrder(
            tableModel,
            keyColumnsInOrder,
            tableCompilationContext.ParameterNameByColumn,
            sqlOperation: "delete-by-parent"
        );

        return _deleteSqlEmitter.Emit(tableModel.Table, keyColumnsInOrder, keyParameterNamesInOrder);
    }

    private static FrozenDictionary<DbColumnName, string> BuildParameterNameByColumnMapOrThrow(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        return BuildColumnKeyedMapOrThrow(
            tableModel,
            bindingsInColumnOrder,
            static binding => binding.Column.ColumnName,
            static (binding, _) => binding.ParameterName,
            "parameterNameByColumn"
        );
    }

    private static FrozenDictionary<DbColumnName, DbColumnModel> BuildColumnByNameMapOrThrow(
        DbTableModel tableModel
    )
    {
        return BuildColumnKeyedMapOrThrow(
            tableModel,
            tableModel.Columns,
            static column => column.ColumnName,
            static (column, _) => column,
            "columnByName"
        );
    }

    private static FrozenDictionary<DbColumnName, int> BuildBindingIndexByColumnMapOrThrow(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        return BuildColumnKeyedMapOrThrow(
            tableModel,
            bindingsInColumnOrder,
            static binding => binding.Column.ColumnName,
            static (_, index) => index,
            "bindingIndexByColumn"
        );
    }

    private static FrozenDictionary<DbColumnName, TValue> BuildColumnKeyedMapOrThrow<TSource, TValue>(
        DbTableModel tableModel,
        IReadOnlyList<TSource> sourceItems,
        Func<TSource, DbColumnName> columnNameSelector,
        Func<TSource, int, TValue> valueSelector,
        string mapName
    )
    {
        var mapByColumn = new Dictionary<DbColumnName, TValue>(sourceItems.Count);

        for (var index = 0; index < sourceItems.Count; index++)
        {
            var sourceItem = sourceItems[index];
            var columnName = columnNameSelector(sourceItem);

            if (!mapByColumn.TryAdd(columnName, valueSelector(sourceItem, index)))
            {
                throw CreateDuplicateColumnNameException(tableModel, columnName, mapName);
            }
        }

        return mapByColumn.ToFrozenDictionary();
    }

    private static InvalidOperationException CreateDuplicateColumnNameException(
        DbTableModel tableModel,
        DbColumnName duplicateColumnName,
        string mapName
    )
    {
        return new InvalidOperationException(
            $"Cannot compile write plan for '{tableModel.Table}': duplicate column name '{duplicateColumnName.Value}' encountered while building '{mapName}' map."
        );
    }

    private static string[] DeriveRequiredKeyParameterNamesInOrder(
        DbTableModel tableModel,
        IReadOnlyList<DbColumnName> keyColumnsInOrder,
        IReadOnlyDictionary<DbColumnName, string> parameterNameByColumn,
        string sqlOperation
    )
    {
        var keyParameterNamesInOrder = new string[keyColumnsInOrder.Count];

        for (var index = 0; index < keyColumnsInOrder.Count; index++)
        {
            var keyColumn = keyColumnsInOrder[index];

            if (!parameterNameByColumn.TryGetValue(keyColumn, out var keyParameterName))
            {
                throw new InvalidOperationException(
                    $"Cannot emit {sqlOperation} SQL for '{tableModel.Table}': key column '{keyColumn.Value}' does not have a write binding parameter."
                );
            }

            keyParameterNamesInOrder[index] = keyParameterName;
        }

        return keyParameterNamesInOrder;
    }

    /// <summary>
    /// Derives a deterministic write-time value source contract for a stored column binding.
    /// </summary>
    private static WriteValueSource DeriveWriteValueSource(
        DbTableModel tableModel,
        DbColumnModel column,
        WriteSourceLookup writeSourceLookup,
        IReadOnlySet<DbColumnName> requiredKeyUnificationPrecomputedColumns
    )
    {
        if (IsCanonicalDocumentIdKeyColumn(tableModel, column))
        {
            return new WriteValueSource.DocumentId();
        }

        return column.Kind switch
        {
            ColumnKind.ParentKeyPart => new WriteValueSource.ParentKeyPart(
                GetParentKeyPartIndex(tableModel, column)
            ),
            ColumnKind.Ordinal => new WriteValueSource.Ordinal(),
            ColumnKind.DocumentFk => new WriteValueSource.DocumentReference(
                FindDocumentReferenceBindingIndex(tableModel.Table, column.ColumnName, writeSourceLookup)
            ),
            ColumnKind.DescriptorFk when column.SourceJsonPath is JsonPathExpression sourcePath =>
                CreateDescriptorReferenceSource(
                    tableModel.Table,
                    tableModel.JsonScope,
                    column.ColumnName,
                    sourcePath,
                    writeSourceLookup
                ),
            _ => CreateScalarOrPrecomputedSource(
                tableModel,
                column,
                requiredKeyUnificationPrecomputedColumns
            ),
        };
    }

    /// <summary>
    /// Returns <see langword="true" /> when the column is the canonical key <c>DocumentId</c> component.
    /// </summary>
    private static bool IsCanonicalDocumentIdKeyColumn(DbTableModel tableModel, DbColumnModel column)
    {
        return column.Kind == ColumnKind.ParentKeyPart
            && column.ColumnName.Equals(RelationalNameConventions.DocumentIdColumnName)
            && tableModel.Key.Columns.Any(keyColumn =>
                keyColumn.Kind == ColumnKind.ParentKeyPart && keyColumn.ColumnName.Equals(column.ColumnName)
            );
    }

    /// <summary>
    /// Gets the 0-based parent-key part index for the column in parent-key part order (DocumentId + ancestor ordinals).
    /// </summary>
    private static int GetParentKeyPartIndex(DbTableModel tableModel, DbColumnModel column)
    {
        var parentKeyPartIndex = 0;

        foreach (var keyColumn in tableModel.Key.Columns)
        {
            if (keyColumn.Kind is not ColumnKind.ParentKeyPart)
            {
                continue;
            }

            if (keyColumn.ColumnName.Equals(column.ColumnName))
            {
                return parentKeyPartIndex;
            }

            parentKeyPartIndex++;
        }

        throw new InvalidOperationException(
            $"Column '{column.ColumnName.Value}' on table '{tableModel.Table}' is not in parent key order."
        );
    }

    /// <summary>
    /// Finds the document-reference binding inventory index for a specific FK column on a table.
    /// </summary>
    private static int FindDocumentReferenceBindingIndex(
        DbTableName table,
        DbColumnName fkColumn,
        WriteSourceLookup writeSourceLookup
    )
    {
        return GetLookupMatchOrThrow(
            writeSourceLookup.DocumentReferenceBindingIndexByKey,
            new WriteSourceLookupKey(table, fkColumn),
            onMissing: static key => new InvalidOperationException(
                $"No document-reference binding matches '{key.Table}.{key.Column.Value}'."
            )
        );
    }

    /// <summary>
    /// Creates a descriptor-reference write value source by matching the descriptor edge source metadata.
    /// </summary>
    private static WriteValueSource CreateDescriptorReferenceSource(
        DbTableName table,
        JsonPathExpression tableJsonScope,
        DbColumnName fkColumn,
        JsonPathExpression sourcePath,
        WriteSourceLookup writeSourceLookup
    )
    {
        var matchingEdgeSource = GetLookupMatchOrThrow(
            writeSourceLookup.DescriptorEdgeSourceByKey,
            new WriteSourceLookupKey(table, fkColumn),
            onMissing: static key => new InvalidOperationException(
                $"No descriptor edge source matches '{key.Table}.{key.Column.Value}'."
            )
        );

        if (sourcePath.Canonical != matchingEdgeSource.DescriptorValuePath.Canonical)
        {
            throw new InvalidOperationException(
                $"Cannot compile write plan for '{table}': descriptor source mismatch for column '{fkColumn.Value}'. DbColumnModel.SourceJsonPath '{sourcePath.Canonical}' does not match DescriptorEdgeSource.DescriptorValuePath '{matchingEdgeSource.DescriptorValuePath.Canonical}'."
            );
        }

        return new WriteValueSource.DescriptorReference(
            DescriptorResource: matchingEdgeSource.DescriptorResource,
            RelativePath: WritePlanJsonPathConventions.DeriveScopeRelativePath(
                tableJsonScope,
                matchingEdgeSource.DescriptorValuePath
            ),
            DescriptorValuePath: matchingEdgeSource.DescriptorValuePath
        );
    }

    private static TValue GetLookupMatchOrThrow<TValue>(
        IReadOnlyDictionary<WriteSourceLookupKey, TValue> lookupByKey,
        WriteSourceLookupKey lookupKey,
        Func<WriteSourceLookupKey, InvalidOperationException> onMissing
    )
    {
        if (!lookupByKey.TryGetValue(lookupKey, out var value))
        {
            throw onMissing(lookupKey);
        }

        return value;
    }

    /// <summary>
    /// Creates a scalar write value source when JSON-bound; otherwise creates a precomputed value source for explicitly allowed targets.
    /// </summary>
    private static WriteValueSource CreateScalarOrPrecomputedSource(
        DbTableModel tableModel,
        DbColumnModel column,
        IReadOnlySet<DbColumnName> requiredKeyUnificationPrecomputedColumns
    )
    {
        if (column.SourceJsonPath is null)
        {
            if (!requiredKeyUnificationPrecomputedColumns.Contains(column.ColumnName))
            {
                throw new InvalidOperationException(
                    $"Cannot compile write plan for '{tableModel.Table}': column '{column.ColumnName.Value}' (kind '{column.Kind}') has null SourceJsonPath but is not an explicitly supported precomputed target. "
                        + "Mark the column IsWritable=false or add a producer plan (for example, key-unification canonical/synthetic presence)."
                );
            }

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
