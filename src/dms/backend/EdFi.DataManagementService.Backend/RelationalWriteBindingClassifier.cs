// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal enum BindingClassification
{
    VisibleWritable,
    HiddenPreserved,
    StorageManaged,
    ClearOnVisibleAbsent,
}

internal static class RelationalWriteBindingClassifier
{
    public static BindingClassification[] Classify(
        TableWritePlan tableWritePlan,
        ImmutableArray<string> hiddenMemberPaths,
        IReadOnlyList<DocumentReferenceBinding>? documentReferenceBindings = null
    ) => Classify(tableWritePlan, hiddenMemberPaths, [], documentReferenceBindings);

    public static BindingClassification[] Classify(
        TableWritePlan tableWritePlan,
        ImmutableArray<string> hiddenMemberPaths,
        ImmutableArray<string> clearableMemberPaths,
        IReadOnlyList<DocumentReferenceBinding>? documentReferenceBindings = null
    )
    {
        var hiddenSet = new HashSet<string>(hiddenMemberPaths, StringComparer.Ordinal);
        var clearableSet = new HashSet<string>(clearableMemberPaths, StringComparer.Ordinal);
        var bindings = tableWritePlan.ColumnBindings;
        var result = new BindingClassification[bindings.Length];

        for (var i = 0; i < bindings.Length; i++)
        {
            result[i] = bindings[i].Source switch
            {
                WriteValueSource.DocumentId => BindingClassification.StorageManaged,
                WriteValueSource.ParentKeyPart => BindingClassification.StorageManaged,
                WriteValueSource.Ordinal => BindingClassification.StorageManaged,
                WriteValueSource.Precomputed => BindingClassification.StorageManaged,
                WriteValueSource.ReferenceDerived referenceDerived => ClassifyMemberBinding(
                    referenceDerived.ReferenceSource.ReferenceJsonPath.Canonical,
                    hiddenSet,
                    clearableSet
                ),
                WriteValueSource.Scalar scalar => ClassifyMemberBinding(
                    scalar.RelativePath.Canonical,
                    hiddenSet,
                    clearableSet
                ),
                WriteValueSource.DescriptorReference descriptor => ClassifyMemberBinding(
                    descriptor.RelativePath.Canonical,
                    hiddenSet,
                    clearableSet
                ),
                WriteValueSource.DocumentReference docRef => ClassifyDocumentReferenceBinding(
                    docRef.BindingIndex,
                    documentReferenceBindings,
                    hiddenSet,
                    clearableSet
                ),
                _ => throw new InvalidOperationException(
                    $"Unrecognized WriteValueSource type '{bindings[i].Source.GetType().Name}' at binding index {i}."
                ),
            };
        }

        return result;
    }

    private static BindingClassification ClassifyMemberBinding(
        string canonicalPath,
        HashSet<string> hiddenSet,
        HashSet<string> clearableSet
    )
    {
        if (hiddenSet.Contains(canonicalPath))
        {
            return BindingClassification.HiddenPreserved;
        }

        if (clearableSet.Contains(canonicalPath))
        {
            return BindingClassification.ClearOnVisibleAbsent;
        }

        return BindingClassification.VisibleWritable;
    }

    private static BindingClassification ClassifyDocumentReferenceBinding(
        int bindingIndex,
        IReadOnlyList<DocumentReferenceBinding>? documentReferenceBindings,
        HashSet<string> hiddenSet,
        HashSet<string> clearableSet
    )
    {
        if (HasAnyHiddenReferenceMember(bindingIndex, documentReferenceBindings, hiddenSet))
        {
            return BindingClassification.HiddenPreserved;
        }

        if (IsDocumentReferenceFullyClearable(bindingIndex, documentReferenceBindings, clearableSet))
        {
            return BindingClassification.ClearOnVisibleAbsent;
        }

        return BindingClassification.VisibleWritable;
    }

    /// <summary>
    /// A document-reference FK column is hidden when any of its constituent identity member
    /// paths are hidden. A single hidden member forces the entire FK to be preserved, because
    /// the FK column represents the composite reference as a whole. When no reference binding
    /// metadata is available (no-profile path or legacy callers), the FK is treated as visible.
    /// </summary>
    private static bool HasAnyHiddenReferenceMember(
        int bindingIndex,
        IReadOnlyList<DocumentReferenceBinding>? documentReferenceBindings,
        HashSet<string> hiddenSet
    )
    {
        if (documentReferenceBindings is null || bindingIndex >= documentReferenceBindings.Count)
        {
            return false;
        }

        var binding = documentReferenceBindings[bindingIndex];

        return binding.IdentityBindings.Any(ib => hiddenSet.Contains(ib.ReferenceJsonPath.Canonical));
    }

    /// <summary>
    /// A document-reference FK column is clearable when all of its constituent identity member
    /// paths are in the clearable set and none are hidden. The hidden check is performed by the
    /// caller (<see cref="HasAnyHiddenReferenceMember"/>) before this method is reached.
    /// </summary>
    private static bool IsDocumentReferenceFullyClearable(
        int bindingIndex,
        IReadOnlyList<DocumentReferenceBinding>? documentReferenceBindings,
        HashSet<string> clearableSet
    )
    {
        if (documentReferenceBindings is null || bindingIndex >= documentReferenceBindings.Count)
        {
            return false;
        }

        var binding = documentReferenceBindings[bindingIndex];

        if (binding.IdentityBindings.Count == 0)
        {
            return false;
        }

        foreach (var identityBinding in binding.IdentityBindings)
        {
            if (!clearableSet.Contains(identityBinding.ReferenceJsonPath.Canonical))
            {
                return false;
            }
        }

        return true;
    }

    public static void ValidateCollectionKeyBinding(
        TableWritePlan tableWritePlan,
        BindingClassification[] classifications
    )
    {
        var mergePlan = tableWritePlan.CollectionMergePlan;

        if (mergePlan is null)
        {
            return;
        }

        var stableIndex = mergePlan.StableRowIdentityBindingIndex;
        var classification = classifications[stableIndex];

        if (classification != BindingClassification.StorageManaged)
        {
            throw new InvalidOperationException(
                $"Collection merge stable-row-identity binding at index {stableIndex} must be classified as {nameof(BindingClassification.StorageManaged)}, but was {classification}."
            );
        }
    }
}
