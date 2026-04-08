// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class KeyUnificationWritePlanCompiler
{
    /// <summary>
    /// Compiles per-table key-unification plan inventory in deterministic class/member order.
    /// </summary>
    public static KeyUnificationWritePlan[] Compile(
        WritePlanTableCompilationContext tableCompilationContext,
        CollectionKeyPreallocationPlan? collectionKeyPreallocationPlan
    )
    {
        var tableModel = tableCompilationContext.TableModel;
        var bindingsInColumnOrder = tableCompilationContext.ColumnBindings;
        var precomputedBindingIndices = GetPrecomputedBindingIndices(bindingsInColumnOrder);

        var keyUnificationPlans = new KeyUnificationWritePlan[tableModel.KeyUnificationClasses.Count];

        for (var classIndex = 0; classIndex < tableModel.KeyUnificationClasses.Count; classIndex++)
        {
            var keyClass = tableModel.KeyUnificationClasses[classIndex];
            var canonicalBindingIndex = GetRequiredBindingIndex(
                tableModel,
                keyClass.CanonicalColumn,
                "canonical key-unification column",
                tableCompilationContext.BindingIndexByColumn
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

                if (
                    !tableCompilationContext.ColumnByName.TryGetValue(
                        memberPathColumnName,
                        out var memberPathColumn
                    )
                )
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
                    tableCompilationContext,
                    memberPathColumn
                );
                tableCompilationContext.ReferenceDerivedSourceByColumn.TryGetValue(
                    memberPathColumnName,
                    out var referenceDerivedSource
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
                    referenceDerivedSource,
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
            collectionKeyPreallocationPlan,
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
        CollectionKeyPreallocationPlan? collectionKeyPreallocationPlan,
        IReadOnlyList<int> precomputedBindingIndices
    )
    {
        if (precomputedBindingIndices.Count == 0)
        {
            return;
        }

        if (keyUnificationPlans.Count == 0 && collectionKeyPreallocationPlan is null)
        {
            var precomputedColumns = string.Join(
                ", ",
                precomputedBindingIndices.Select(index =>
                    $"'{bindingsInColumnOrder[index].Column.ColumnName.Value}'"
                )
            );

            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': precomputed bindings {precomputedColumns} require producer inventory."
            );
        }

        var producerCountByBindingIndex = precomputedBindingIndices.ToDictionary(
            static bindingIndex => bindingIndex,
            static _ => 0
        );

        if (collectionKeyPreallocationPlan is not null)
        {
            IncrementCollectionKeyProducerCount(
                tableModel,
                bindingsInColumnOrder,
                producerCountByBindingIndex,
                collectionKeyPreallocationPlan
            );
        }

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
                $"Cannot compile key-unification plan for '{tableModel.Table}': precomputed bindings not produced by collection-key preallocation or key-unification inventory: {string.Join(", ", orphanedPrecomputedColumns)}."
            );
        }

        var duplicateProducerColumns = producerCountByBindingIndex
            .Where(static entry => entry.Value > 1)
            .Select(entry => $"'{bindingsInColumnOrder[entry.Key].Column.ColumnName.Value}'")
            .ToArray();

        if (duplicateProducerColumns.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': precomputed bindings produced multiple times by collection-key preallocation or key-unification inventory: {string.Join(", ", duplicateProducerColumns)}."
            );
        }
    }

    private static void IncrementCollectionKeyProducerCount(
        DbTableModel tableModel,
        IReadOnlyList<WriteColumnBinding> bindingsInColumnOrder,
        IDictionary<int, int> producerCountByBindingIndex,
        CollectionKeyPreallocationPlan collectionKeyPreallocationPlan
    )
    {
        var bindingIndex = collectionKeyPreallocationPlan.BindingIndex;

        IncrementPrecomputedProducerCount(
            tableModel,
            bindingsInColumnOrder,
            producerCountByBindingIndex,
            bindingIndex
        );

        var binding = bindingsInColumnOrder[bindingIndex];

        if (!binding.Column.ColumnName.Equals(collectionKeyPreallocationPlan.ColumnName))
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': collection-key preallocation column '{collectionKeyPreallocationPlan.ColumnName.Value}' does not match binding column '{binding.Column.ColumnName.Value}'."
            );
        }

        if (binding.Column.Kind is not ColumnKind.CollectionKey)
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': collection-key preallocation binding '{binding.Column.ColumnName.Value}' must target kind '{ColumnKind.CollectionKey}'."
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
        WritePlanTableCompilationContext tableCompilationContext,
        DbColumnModel memberPathColumn
    )
    {
        var tableModel = tableCompilationContext.TableModel;

        if (memberPathColumn.Storage is not ColumnStorage.UnifiedAlias { PresenceColumn: { } presenceColumn })
        {
            return (null, null, false);
        }

        if (!tableCompilationContext.ColumnByName.TryGetValue(presenceColumn, out var presenceColumnModel))
        {
            throw new InvalidOperationException(
                $"Cannot compile key-unification plan for '{tableModel.Table}': presence column '{presenceColumn.Value}' for member '{memberPathColumn.ColumnName.Value}' does not exist."
            );
        }

        var presenceBindingIndex = GetRequiredBindingIndex(
            tableModel,
            presenceColumn,
            $"presence column for member '{memberPathColumn.ColumnName.Value}'",
            tableCompilationContext.BindingIndexByColumn
        );
        var presenceIsSynthetic = KeyUnificationPresenceConventions.IsSyntheticPresenceColumn(
            presenceColumnModel
        );

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
        ReferenceDerivedValueSourceMetadata? referenceDerivedSource,
        DbColumnName? presenceColumn,
        int? presenceBindingIndex,
        bool presenceIsSynthetic
    )
    {
        if (referenceDerivedSource is not null)
        {
            ValidateReferenceDerivedSourcePath(tableModel, memberPathColumn, referenceDerivedSource);

            return new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
                MemberPathColumn: memberPathColumn.ColumnName,
                RelativePath: relativePath,
                ReferenceSource: referenceDerivedSource,
                PresenceColumn: presenceColumn,
                PresenceBindingIndex: presenceBindingIndex,
                PresenceIsSynthetic: presenceIsSynthetic
            );
        }

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

    private static void ValidateReferenceDerivedSourcePath(
        DbTableModel tableModel,
        DbColumnModel memberPathColumn,
        ReferenceDerivedValueSourceMetadata referenceDerivedSource
    )
    {
        var sourcePath = memberPathColumn.SourceJsonPath?.Canonical ?? "<null>";

        if (memberPathColumn.SourceJsonPath?.Canonical == referenceDerivedSource.ReferenceJsonPath.Canonical)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot compile key-unification plan for '{tableModel.Table}': reference-derived source mismatch for member path column '{memberPathColumn.ColumnName.Value}'. "
                + $"DbColumnModel.SourceJsonPath '{sourcePath}' does not match ReferenceIdentityBinding.ReferenceJsonPath '{referenceDerivedSource.ReferenceJsonPath.Canonical}'."
        );
    }
}
