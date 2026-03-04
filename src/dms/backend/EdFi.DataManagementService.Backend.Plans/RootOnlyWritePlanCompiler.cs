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
public sealed class RootOnlyWritePlanCompiler(SqlDialect dialect)
{
    private readonly SqlDialect _dialect = dialect;
    private readonly SimpleInsertSqlEmitter _insertSqlEmitter = new(dialect);
    private readonly SimpleUpdateSqlEmitter _updateSqlEmitter = new(dialect);
    private readonly SimpleDeleteSqlEmitter _deleteSqlEmitter = new(dialect);

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
    }

    /// <summary>
    /// Compiles one table write plan using deterministic column bindings and canonical SQL emission.
    /// </summary>
    private TableWritePlan CompileTablePlan(RelationalResourceModel resourceModel, DbTableModel tableModel)
    {
        ValidateWritableKeyColumns(tableModel);
        var columnBindings = CompileStoredColumnBindings(resourceModel, tableModel);
        var keyUnificationPlans = CompileKeyUnificationPlans(tableModel, columnBindings);

        var insertSql = _insertSqlEmitter.Emit(
            tableModel.Table,
            columnBindings.Select(static binding => binding.Column.ColumnName).ToArray(),
            columnBindings.Select(static binding => binding.ParameterName).ToArray()
        );
        var updateSql = TryEmitUpdateSql(tableModel, columnBindings);
        var deleteByParentSql = TryEmitDeleteByParentSql(resourceModel, tableModel, columnBindings);
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
    /// Compiles per-table key-unification plan inventory in deterministic class/member order.
    /// </summary>
    private static KeyUnificationWritePlan[] CompileKeyUnificationPlans(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        var precomputedBindingIndices = GetPrecomputedBindingIndices(bindingsInColumnOrder);

        var columnByName = new Dictionary<DbColumnName, DbColumnModel>(tableModel.Columns.Count);

        foreach (var column in tableModel.Columns)
        {
            columnByName[column.ColumnName] = column;
        }

        var bindingIndexByColumn = new Dictionary<DbColumnName, int>(bindingsInColumnOrder.Count);

        for (var bindingIndex = 0; bindingIndex < bindingsInColumnOrder.Count; bindingIndex++)
        {
            var binding = bindingsInColumnOrder[bindingIndex];
            bindingIndexByColumn[binding.Column.ColumnName] = bindingIndex;
        }

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
    /// Emits table <c>DELETE</c> SQL for replace semantics by parent key for all non-root tables.
    /// </summary>
    private string? TryEmitDeleteByParentSql(
        RelationalResourceModel resourceModel,
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder
    )
    {
        if (tableModel.Equals(resourceModel.Root))
        {
            return null;
        }

        var keyColumnsInOrder = tableModel
            .Key.Columns.Where(static keyColumn => keyColumn.Kind is not ColumnKind.Ordinal)
            .Select(static keyColumn => keyColumn.ColumnName)
            .ToArray();

        if (keyColumnsInOrder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Cannot emit delete-by-parent SQL for '{tableModel.Table}': no key columns remain after excluding ordinal key columns."
            );
        }

        var parameterNameByColumn = new Dictionary<DbColumnName, string>(bindingsInColumnOrder.Count);

        foreach (var binding in bindingsInColumnOrder)
        {
            parameterNameByColumn[binding.Column.ColumnName] = binding.ParameterName;
        }

        var keyParameterNamesInOrder = new string[keyColumnsInOrder.Length];

        for (var index = 0; index < keyColumnsInOrder.Length; index++)
        {
            var keyColumn = keyColumnsInOrder[index];

            if (!parameterNameByColumn.TryGetValue(keyColumn, out var keyParameterName))
            {
                throw new InvalidOperationException(
                    $"Cannot emit delete-by-parent SQL for '{tableModel.Table}': key column '{keyColumn.Value}' does not have a write binding parameter."
                );
            }

            keyParameterNamesInOrder[index] = keyParameterName;
        }

        return _deleteSqlEmitter.Emit(tableModel.Table, keyColumnsInOrder, keyParameterNamesInOrder);
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
