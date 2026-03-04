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

    private sealed record WriteSourceLookup(
        IReadOnlyDictionary<WriteSourceLookupKey, int> DocumentReferenceBindingIndexByKey,
        IReadOnlySet<WriteSourceLookupKey> DuplicateDocumentReferenceBindingKeys,
        IReadOnlyDictionary<WriteSourceLookupKey, DescriptorEdgeSource> DescriptorEdgeSourceByKey,
        IReadOnlySet<WriteSourceLookupKey> DuplicateDescriptorEdgeSourceKeys
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
        var documentReferenceBindingIndexByKey = new Dictionary<WriteSourceLookupKey, int>(
            resourceModel.DocumentReferenceBindings.Count
        );
        var duplicateDocumentReferenceBindingKeys = new HashSet<WriteSourceLookupKey>();

        for (var index = 0; index < resourceModel.DocumentReferenceBindings.Count; index++)
        {
            var binding = resourceModel.DocumentReferenceBindings[index];
            var lookupKey = new WriteSourceLookupKey(binding.Table, binding.FkColumn);

            if (!documentReferenceBindingIndexByKey.TryAdd(lookupKey, index))
            {
                duplicateDocumentReferenceBindingKeys.Add(lookupKey);
            }
        }

        var descriptorEdgeSourceByKey = new Dictionary<WriteSourceLookupKey, DescriptorEdgeSource>(
            resourceModel.DescriptorEdgeSources.Count
        );
        var duplicateDescriptorEdgeSourceKeys = new HashSet<WriteSourceLookupKey>();

        foreach (var edgeSource in resourceModel.DescriptorEdgeSources)
        {
            var lookupKey = new WriteSourceLookupKey(edgeSource.Table, edgeSource.FkColumn);

            if (!descriptorEdgeSourceByKey.TryAdd(lookupKey, edgeSource))
            {
                duplicateDescriptorEdgeSourceKeys.Add(lookupKey);
            }
        }

        return new WriteSourceLookup(
            DocumentReferenceBindingIndexByKey: documentReferenceBindingIndexByKey,
            DuplicateDocumentReferenceBindingKeys: duplicateDocumentReferenceBindingKeys,
            DescriptorEdgeSourceByKey: descriptorEdgeSourceByKey,
            DuplicateDescriptorEdgeSourceKeys: duplicateDescriptorEdgeSourceKeys
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
        ValidateWritableKeyColumns(tableModel);
        var columnBindings = CompileStoredColumnBindings(tableModel, writeSourceLookup);
        var keyUnificationPlans = CompileKeyUnificationPlans(tableModel, columnBindings);

        var insertSql = _insertSqlEmitter.Emit(
            tableModel.Table,
            columnBindings.Select(static binding => binding.Column.ColumnName).ToArray(),
            columnBindings.Select(static binding => binding.ParameterName).ToArray()
        );
        var updateSql = TryEmitUpdateSql(tableModel, columnBindings);
        var deleteByParentSql = TryEmitDeleteByParentSql(rootScopeTableModel, tableModel, columnBindings);
        var bulkInsertBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            _dialect,
            columnBindings
        );

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: insertSql,
            UpdateSql: updateSql,
            DeleteByParentSql: deleteByParentSql,
            BulkInsertBatching: bulkInsertBatching,
            ColumnBindings: columnBindings,
            KeyUnificationPlans: keyUnificationPlans
        );
    }

    /// <summary>
    /// Compiles deterministic stored-column bindings for one table.
    /// Includes writable stored columns plus key columns and key-unification precomputed targets.
    /// </summary>
    private static WriteColumnBinding[] CompileStoredColumnBindings(
        DbTableModel tableModel,
        WriteSourceLookup writeSourceLookup
    )
    {
        var keyColumnNames = tableModel
            .Key.Columns.Select(static keyColumn => keyColumn.ColumnName)
            .ToHashSet();
        var requiredKeyUnificationPrecomputedColumns = DeriveRequiredKeyUnificationPrecomputedColumns(
            tableModel
        );
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
                Source: DeriveWriteValueSource(tableModel, column, writeSourceLookup),
                ParameterName: orderedParameterNames[index]
            );
        }

        return columnBindings;
    }

    private static ISet<DbColumnName> DeriveRequiredKeyUnificationPrecomputedColumns(DbTableModel tableModel)
    {
        if (tableModel.KeyUnificationClasses.Count == 0)
        {
            return new HashSet<DbColumnName>();
        }

        var columnByName = BuildColumnByNameMapOrThrow(tableModel);

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
    /// Compiles per-table key-unification plan inventory in deterministic class/member order.
    /// </summary>
    private static KeyUnificationWritePlan[] CompileKeyUnificationPlans(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        var precomputedBindingIndices = GetPrecomputedBindingIndices(bindingsInColumnOrder);
        var columnByName = BuildColumnByNameMapOrThrow(tableModel);
        var bindingIndexByColumn = BuildBindingIndexByColumnMapOrThrow(tableModel, bindingsInColumnOrder);

        var keyUnificationPlans = new KeyUnificationWritePlan[tableModel.KeyUnificationClasses.Count];

        for (var classIndex = 0; classIndex < tableModel.KeyUnificationClasses.Count; classIndex++)
        {
            var keyClass = tableModel.KeyUnificationClasses[classIndex];
            var canonicalBindingIndex = GetRequiredBindingIndex(
                tableModel,
                keyClass.CanonicalColumn,
                "canonical key-unification column",
                bindingIndexByColumn
            );
            var canonicalBinding = bindingsInColumnOrder[canonicalBindingIndex];

            if (canonicalBinding.Source is not WriteValueSource.Precomputed)
            {
                throw new InvalidOperationException(
                    $"Cannot compile key-unification plan for '{tableModel.Table}': canonical column '{keyClass.CanonicalColumn.Value}' must bind as {nameof(WriteValueSource.Precomputed)}."
                );
            }

            var membersInOrder = new KeyUnificationMemberWritePlan[keyClass.MemberPathColumns.Count];

            for (var memberIndex = 0; memberIndex < keyClass.MemberPathColumns.Count; memberIndex++)
            {
                var memberPathColumnName = keyClass.MemberPathColumns[memberIndex];

                if (!columnByName.TryGetValue(memberPathColumnName, out var memberPathColumn))
                {
                    throw new InvalidOperationException(
                        $"Cannot compile key-unification plan for '{tableModel.Table}': member path column '{memberPathColumnName.Value}' does not exist."
                    );
                }

                if (memberPathColumn.Storage is not ColumnStorage.UnifiedAlias)
                {
                    var storageType = memberPathColumn.Storage switch
                    {
                        ColumnStorage.Stored => nameof(ColumnStorage.Stored),
                        ColumnStorage.UnifiedAlias => nameof(ColumnStorage.UnifiedAlias),
                        _ => memberPathColumn.Storage.GetType().Name,
                    };

                    throw new InvalidOperationException(
                        $"Cannot compile key-unification plan for '{tableModel.Table}': member path column '{memberPathColumnName.Value}' must use {nameof(ColumnStorage.UnifiedAlias)} storage, but was {storageType}."
                    );
                }

                if (memberPathColumn.Kind is not ColumnKind.Scalar and not ColumnKind.DescriptorFk)
                {
                    throw new InvalidOperationException(
                        $"Cannot compile key-unification plan for '{tableModel.Table}': member path column '{memberPathColumnName.Value}' has unsupported kind '{memberPathColumn.Kind}'. Supported kinds are {nameof(ColumnKind.Scalar)} and {nameof(ColumnKind.DescriptorFk)}."
                    );
                }

                if (memberPathColumn.SourceJsonPath is not JsonPathExpression sourcePath)
                {
                    throw new InvalidOperationException(
                        $"Cannot compile key-unification plan for '{tableModel.Table}': member path column '{memberPathColumnName.Value}' must define a source JSON path."
                    );
                }

                var relativePath = WritePlanJsonPathConventions.DeriveScopeRelativePath(
                    tableModel.JsonScope,
                    sourcePath
                );
                var (presenceColumn, presenceBindingIndex, presenceIsSynthetic) = DerivePresenceBindingInfo(
                    tableModel,
                    memberPathColumn,
                    columnByName,
                    bindingIndexByColumn
                );

                if (
                    presenceIsSynthetic
                    && presenceBindingIndex is int syntheticPresenceBindingIndex
                    && bindingsInColumnOrder[syntheticPresenceBindingIndex].Source
                        is not WriteValueSource.Precomputed
                )
                {
                    throw new InvalidOperationException(
                        $"Cannot compile key-unification plan for '{tableModel.Table}': synthetic presence column '{presenceColumn!.Value}' for member '{memberPathColumnName.Value}' must bind as {nameof(WriteValueSource.Precomputed)}."
                    );
                }

                membersInOrder[memberIndex] = CreateKeyUnificationMemberWritePlan(
                    tableModel,
                    keyClass.CanonicalColumn,
                    memberPathColumn,
                    relativePath,
                    presenceColumn,
                    presenceBindingIndex,
                    presenceIsSynthetic
                );
            }

            keyUnificationPlans[classIndex] = new KeyUnificationWritePlan(
                CanonicalColumn: keyClass.CanonicalColumn,
                CanonicalBindingIndex: canonicalBindingIndex,
                MembersInOrder: membersInOrder
            );
        }

        ValidatePrecomputedBindingProducerAccounting(
            tableModel,
            bindingsInColumnOrder,
            keyUnificationPlans,
            precomputedBindingIndices
        );

        return keyUnificationPlans;
    }

    private static int[] GetPrecomputedBindingIndices(IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder)
    {
        return bindingsInColumnOrder
            .Select((binding, index) => (binding, index))
            .Where(static tuple => tuple.binding.Source is WriteValueSource.Precomputed)
            .Select(static tuple => tuple.index)
            .ToArray();
    }

    private static void ValidatePrecomputedBindingProducerAccounting(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder,
        IReadOnlyList<KeyUnificationWritePlan> keyUnificationPlans,
        IReadOnlyList<int> precomputedBindingIndices
    )
    {
        if (precomputedBindingIndices.Count == 0)
        {
            return;
        }

        if (keyUnificationPlans.Count == 0)
        {
            var precomputedColumns = string.Join(
                ", ",
                precomputedBindingIndices.Select(index =>
                    $"'{bindingsInColumnOrder[index].Column.ColumnName.Value}'"
                )
            );

            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': precomputed bindings {precomputedColumns} require key-unification inventory."
            );
        }

        var producerCountByBindingIndex = precomputedBindingIndices.ToDictionary(
            static bindingIndex => bindingIndex,
            static _ => 0
        );

        foreach (var keyUnificationPlan in keyUnificationPlans)
        {
            IncrementPrecomputedProducerCount(
                tableModel,
                bindingsInColumnOrder,
                producerCountByBindingIndex,
                keyUnificationPlan.CanonicalBindingIndex
            );

            foreach (var member in keyUnificationPlan.MembersInOrder)
            {
                if (!member.PresenceIsSynthetic || member.PresenceBindingIndex is not int bindingIndex)
                {
                    continue;
                }

                IncrementPrecomputedProducerCount(
                    tableModel,
                    bindingsInColumnOrder,
                    producerCountByBindingIndex,
                    bindingIndex
                );
            }
        }

        var orphanedPrecomputedColumns = producerCountByBindingIndex
            .Where(static entry => entry.Value == 0)
            .Select(entry => $"'{bindingsInColumnOrder[entry.Key].Column.ColumnName.Value}'")
            .ToArray();

        if (orphanedPrecomputedColumns.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': precomputed bindings not produced by key-unification inventory: {string.Join(", ", orphanedPrecomputedColumns)}."
            );
        }

        var duplicateProducerColumns = producerCountByBindingIndex
            .Where(static entry => entry.Value > 1)
            .Select(entry => $"'{bindingsInColumnOrder[entry.Key].Column.ColumnName.Value}'")
            .ToArray();

        if (duplicateProducerColumns.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': precomputed bindings produced multiple times by key-unification inventory: {string.Join(", ", duplicateProducerColumns)}."
            );
        }
    }

    private static void IncrementPrecomputedProducerCount(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder,
        IDictionary<int, int> producerCountByBindingIndex,
        int bindingIndex
    )
    {
        if (
            bindingIndex < 0
            || bindingIndex >= bindingsInColumnOrder.Count
            || !producerCountByBindingIndex.ContainsKey(bindingIndex)
        )
        {
            var columnName =
                bindingIndex >= 0 && bindingIndex < bindingsInColumnOrder.Count
                    ? bindingsInColumnOrder[bindingIndex].Column.ColumnName.Value
                    : $"<binding-index-{bindingIndex}>";

            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': binding '{columnName}' must bind as {nameof(WriteValueSource.Precomputed)}."
            );
        }

        producerCountByBindingIndex[bindingIndex]++;
    }

    /// <summary>
    /// Gets the authoritative column-binding index for a key-unification participant column.
    /// </summary>
    private static int GetRequiredBindingIndex(
        DbTableModel tableModel,
        DbColumnName columnName,
        string role,
        IReadOnlyDictionary<DbColumnName, int> bindingIndexByColumn
    )
    {
        if (bindingIndexByColumn.TryGetValue(columnName, out var bindingIndex))
        {
            return bindingIndex;
        }

        throw new InvalidOperationException(
            $"Cannot compile key-unification plan for '{tableModel.Table}': {role} '{columnName.Value}' does not have a stored write binding."
        );
    }

    /// <summary>
    /// Derives optional presence metadata for a key-unification member.
    /// </summary>
    private static (
        DbColumnName? PresenceColumn,
        int? PresenceBindingIndex,
        bool PresenceIsSynthetic
    ) DerivePresenceBindingInfo(
        DbTableModel tableModel,
        DbColumnModel memberPathColumn,
        IReadOnlyDictionary<DbColumnName, DbColumnModel> columnByName,
        IReadOnlyDictionary<DbColumnName, int> bindingIndexByColumn
    )
    {
        if (memberPathColumn.Storage is not ColumnStorage.UnifiedAlias { PresenceColumn: { } presenceColumn })
        {
            return (null, null, false);
        }

        if (!columnByName.TryGetValue(presenceColumn, out var presenceColumnModel))
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': presence column '{presenceColumn.Value}' for member '{memberPathColumn.ColumnName.Value}' does not exist."
            );
        }

        var presenceBindingIndex = GetRequiredBindingIndex(
            tableModel,
            presenceColumn,
            $"presence column for member '{memberPathColumn.ColumnName.Value}'",
            bindingIndexByColumn
        );
        var presenceIsSynthetic = presenceColumnModel.SourceJsonPath is null;

        if (presenceIsSynthetic)
        {
            ValidateSyntheticPresenceColumn(tableModel, memberPathColumn.ColumnName, presenceColumnModel);
        }

        return (presenceColumn, presenceBindingIndex, presenceIsSynthetic);
    }

    private static void ValidateSyntheticPresenceColumn(
        DbTableModel tableModel,
        DbColumnName memberPathColumn,
        DbColumnModel presenceColumnModel
    )
    {
        var isNullableBoolean =
            presenceColumnModel.IsNullable
            && presenceColumnModel.ScalarType is RelationalScalarType { Kind: ScalarKind.Boolean };

        if (!isNullableBoolean)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': synthetic presence column '{presenceColumnModel.ColumnName.Value}' for member '{memberPathColumn.Value}' must be nullable boolean."
            );
        }

        var hasNullOrTrueConstraint = tableModel.Constraints.Any(constraint =>
            constraint is TableConstraint.NullOrTrue nullOrTrue
            && nullOrTrue.Column.Equals(presenceColumnModel.ColumnName)
        );

        if (!hasNullOrTrueConstraint)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': synthetic presence column '{presenceColumnModel.ColumnName.Value}' for member '{memberPathColumn.Value}' must define a matching {nameof(TableConstraint.NullOrTrue)} constraint."
            );
        }
    }

    /// <summary>
    /// Compiles one key-unification member metadata record.
    /// </summary>
    private static KeyUnificationMemberWritePlan CreateKeyUnificationMemberWritePlan(
        DbTableModel tableModel,
        DbColumnName canonicalColumn,
        DbColumnModel memberPathColumn,
        JsonPathExpression relativePath,
        DbColumnName? presenceColumn,
        int? presenceBindingIndex,
        bool presenceIsSynthetic
    )
    {
        if (memberPathColumn.Kind is ColumnKind.DescriptorFk)
        {
            if (memberPathColumn.TargetResource is not QualifiedResourceName descriptorResource)
            {
                throw new InvalidOperationException(
                    $"Cannot compile key-unification plan for '{tableModel.Table}': descriptor member '{memberPathColumn.ColumnName.Value}' in canonical class '{canonicalColumn.Value}' does not define a descriptor resource."
                );
            }

            return new KeyUnificationMemberWritePlan.DescriptorMember(
                MemberPathColumn: memberPathColumn.ColumnName,
                RelativePath: relativePath,
                DescriptorResource: descriptorResource,
                PresenceColumn: presenceColumn,
                PresenceBindingIndex: presenceBindingIndex,
                PresenceIsSynthetic: presenceIsSynthetic
            );
        }

        if (memberPathColumn.ScalarType is not RelationalScalarType scalarType)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': scalar member '{memberPathColumn.ColumnName.Value}' in canonical class '{canonicalColumn.Value}' does not define a scalar type."
            );
        }

        return new KeyUnificationMemberWritePlan.ScalarMember(
            MemberPathColumn: memberPathColumn.ColumnName,
            RelativePath: relativePath,
            ScalarType: scalarType,
            PresenceColumn: presenceColumn,
            PresenceBindingIndex: presenceBindingIndex,
            PresenceIsSynthetic: presenceIsSynthetic
        );
    }

    /// <summary>
    /// Validates that every key column maps to a writable stored column. Unified aliases are generated and non-writable.
    /// </summary>
    private static void ValidateWritableKeyColumns(DbTableModel tableModel)
    {
        var columnByName = BuildColumnByNameMapOrThrow(tableModel);

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
            .Where(binding => !keyColumns.Contains(binding.Column.ColumnName))
            .ToArray();

        if (writableNonKeyBindingsInOrder.Length == 0)
        {
            return null;
        }

        var parameterNameByColumn = BuildParameterNameByColumn(tableModel, bindingsInColumnOrder);
        var keyParameterNamesInKeyOrder = DeriveRequiredKeyParameterNamesInOrder(
            tableModel,
            keyColumnsInKeyOrder,
            parameterNameByColumn,
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
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
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

        var parameterNameByColumn = BuildParameterNameByColumn(tableModel, bindingsInColumnOrder);
        var keyParameterNamesInOrder = DeriveRequiredKeyParameterNamesInOrder(
            tableModel,
            keyColumnsInOrder,
            parameterNameByColumn,
            sqlOperation: "delete-by-parent"
        );

        return _deleteSqlEmitter.Emit(tableModel.Table, keyColumnsInOrder, keyParameterNamesInOrder);
    }

    private static Dictionary<DbColumnName, string> BuildParameterNameByColumn(
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
        WriteSourceLookup writeSourceLookup
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
                    FindDocumentReferenceBindingIndex(tableModel.Table, column.ColumnName, writeSourceLookup)
                ),
            ColumnKind.DescriptorFk when column.SourceJsonPath is JsonPathExpression sourcePath =>
                CreateDescriptorReferenceSource(
                    tableModel.Table,
                    column.ColumnName,
                    WritePlanJsonPathConventions.DeriveScopeRelativePath(tableModel.JsonScope, sourcePath),
                    writeSourceLookup
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
        DbTableName table,
        DbColumnName fkColumn,
        WriteSourceLookup writeSourceLookup
    )
    {
        var lookupKey = new WriteSourceLookupKey(table, fkColumn);

        if (writeSourceLookup.DuplicateDocumentReferenceBindingKeys.Contains(lookupKey))
        {
            throw new InvalidOperationException(
                $"Multiple document-reference bindings match '{table}.{fkColumn.Value}'."
            );
        }

        if (
            writeSourceLookup.DocumentReferenceBindingIndexByKey.TryGetValue(lookupKey, out var matchingIndex)
        )
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
        DbTableName table,
        DbColumnName fkColumn,
        JsonPathExpression relativePath,
        WriteSourceLookup writeSourceLookup
    )
    {
        var lookupKey = new WriteSourceLookupKey(table, fkColumn);

        if (writeSourceLookup.DuplicateDescriptorEdgeSourceKeys.Contains(lookupKey))
        {
            throw new InvalidOperationException(
                $"Multiple descriptor edge sources match '{table}.{fkColumn.Value}'."
            );
        }

        if (!writeSourceLookup.DescriptorEdgeSourceByKey.TryGetValue(lookupKey, out var matchingEdgeSource))
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
