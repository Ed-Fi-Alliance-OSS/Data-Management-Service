// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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

    private readonly record struct WriteSourceLookupEntry<TValue>(TValue Value, bool HasDuplicateMatch);

    private sealed record WriteSourceLookup(
        IReadOnlyDictionary<
            WriteSourceLookupKey,
            WriteSourceLookupEntry<int>
        > DocumentReferenceBindingIndexByKey,
        IReadOnlyDictionary<
            WriteSourceLookupKey,
            WriteSourceLookupEntry<DescriptorEdgeSource>
        > DescriptorEdgeSourceByKey
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
        var documentReferenceBindingIndexByKey = new Dictionary<
            WriteSourceLookupKey,
            WriteSourceLookupEntry<int>
        >(resourceModel.DocumentReferenceBindings.Count);

        for (var index = 0; index < resourceModel.DocumentReferenceBindings.Count; index++)
        {
            var binding = resourceModel.DocumentReferenceBindings[index];
            var lookupKey = new WriteSourceLookupKey(binding.Table, binding.FkColumn);

            AddWriteSourceLookupEntry(documentReferenceBindingIndexByKey, lookupKey, index);
        }

        var descriptorEdgeSourceByKey = new Dictionary<
            WriteSourceLookupKey,
            WriteSourceLookupEntry<DescriptorEdgeSource>
        >(resourceModel.DescriptorEdgeSources.Count);

        foreach (var edgeSource in resourceModel.DescriptorEdgeSources)
        {
            var lookupKey = new WriteSourceLookupKey(edgeSource.Table, edgeSource.FkColumn);

            AddWriteSourceLookupEntry(descriptorEdgeSourceByKey, lookupKey, edgeSource);
        }

        return new WriteSourceLookup(
            DocumentReferenceBindingIndexByKey: documentReferenceBindingIndexByKey,
            DescriptorEdgeSourceByKey: descriptorEdgeSourceByKey
        );
    }

    private static void AddWriteSourceLookupEntry<TValue>(
        IDictionary<WriteSourceLookupKey, WriteSourceLookupEntry<TValue>> lookupByKey,
        WriteSourceLookupKey lookupKey,
        TValue value
    )
    {
        if (!lookupByKey.TryGetValue(lookupKey, out var existingEntry))
        {
            lookupByKey.Add(
                lookupKey,
                new WriteSourceLookupEntry<TValue>(Value: value, HasDuplicateMatch: false)
            );
            return;
        }

        if (!existingEntry.HasDuplicateMatch)
        {
            lookupByKey[lookupKey] = existingEntry with { HasDuplicateMatch = true };
        }
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
            RequiredKeyUnificationPrecomputedColumns: requiredKeyUnificationPrecomputedColumns,
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
                    || presenceColumnModel.SourceJsonPath is not null
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

    private static Dictionary<DbColumnName, string> BuildParameterNameByColumnMapOrThrow(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        var parameterNameByColumn = new Dictionary<DbColumnName, string>(bindingsInColumnOrder.Count);

        foreach (var binding in bindingsInColumnOrder)
        {
            if (!parameterNameByColumn.TryAdd(binding.Column.ColumnName, binding.ParameterName))
            {
                throw CreateDuplicateColumnNameException(
                    tableModel,
                    binding.Column.ColumnName,
                    "parameterNameByColumn"
                );
            }
        }

        return parameterNameByColumn;
    }

    private static Dictionary<DbColumnName, DbColumnModel> BuildColumnByNameMapOrThrow(
        DbTableModel tableModel
    )
    {
        var columnByName = new Dictionary<DbColumnName, DbColumnModel>(tableModel.Columns.Count);

        foreach (var column in tableModel.Columns)
        {
            if (!columnByName.TryAdd(column.ColumnName, column))
            {
                throw CreateDuplicateColumnNameException(tableModel, column.ColumnName, "columnByName");
            }
        }

        return columnByName;
    }

    private static Dictionary<DbColumnName, int> BuildBindingIndexByColumnMapOrThrow(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        var bindingIndexByColumn = new Dictionary<DbColumnName, int>(bindingsInColumnOrder.Count);

        for (var bindingIndex = 0; bindingIndex < bindingsInColumnOrder.Count; bindingIndex++)
        {
            var binding = bindingsInColumnOrder[bindingIndex];

            if (!bindingIndexByColumn.TryAdd(binding.Column.ColumnName, bindingIndex))
            {
                throw CreateDuplicateColumnNameException(
                    tableModel,
                    binding.Column.ColumnName,
                    "bindingIndexByColumn"
                );
            }
        }

        return bindingIndexByColumn;
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
            ColumnKind.DocumentFk => new WriteValueSource.DocumentReference(
                FindDocumentReferenceBindingIndex(tableModel.Table, column.ColumnName, writeSourceLookup)
            ),
            ColumnKind.DescriptorFk when column.SourceJsonPath is JsonPathExpression sourcePath =>
                CreateDescriptorReferenceSource(
                    tableModel.Table,
                    column.ColumnName,
                    WritePlanJsonPathConventions.DeriveScopeRelativePath(tableModel.JsonScope, sourcePath),
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
        DbTableName table,
        DbColumnName fkColumn,
        WriteSourceLookup writeSourceLookup
    )
    {
        return GetUniqueLookupMatchOrThrow(
            writeSourceLookup.DocumentReferenceBindingIndexByKey,
            new WriteSourceLookupKey(table, fkColumn),
            onDuplicate: static key => new InvalidOperationException(
                $"Multiple document-reference bindings match '{key.Table}.{key.Column.Value}'."
            ),
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
        DbColumnName fkColumn,
        JsonPathExpression relativePath,
        WriteSourceLookup writeSourceLookup
    )
    {
        var matchingEdgeSource = GetUniqueLookupMatchOrThrow(
            writeSourceLookup.DescriptorEdgeSourceByKey,
            new WriteSourceLookupKey(table, fkColumn),
            onDuplicate: static key => new InvalidOperationException(
                $"Multiple descriptor edge sources match '{key.Table}.{key.Column.Value}'."
            ),
            onMissing: static key => new InvalidOperationException(
                $"No descriptor edge source matches '{key.Table}.{key.Column.Value}'."
            )
        );

        return new WriteValueSource.DescriptorReference(
            DescriptorResource: matchingEdgeSource.DescriptorResource,
            RelativePath: relativePath,
            DescriptorValuePath: matchingEdgeSource.DescriptorValuePath
        );
    }

    private static TValue GetUniqueLookupMatchOrThrow<TValue>(
        IReadOnlyDictionary<WriteSourceLookupKey, WriteSourceLookupEntry<TValue>> lookupByKey,
        WriteSourceLookupKey lookupKey,
        Func<WriteSourceLookupKey, InvalidOperationException> onDuplicate,
        Func<WriteSourceLookupKey, InvalidOperationException> onMissing
    )
    {
        if (!lookupByKey.TryGetValue(lookupKey, out var entry))
        {
            throw onMissing(lookupKey);
        }

        if (entry.HasDuplicateMatch)
        {
            throw onDuplicate(lookupKey);
        }

        return entry.Value;
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
                    $"Cannot compile write plan for '{tableModel.Table}': column '{column.ColumnName.Value}' has null SourceJsonPath but is not an explicitly supported precomputed target. "
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
