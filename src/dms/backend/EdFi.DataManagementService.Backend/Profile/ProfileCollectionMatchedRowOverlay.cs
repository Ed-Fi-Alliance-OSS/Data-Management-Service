// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Composes the row-level binding classifier, key-unification seam, and shared row
/// helpers into a single matched-row emission call for profile-aware collection merge.
/// Called once per <see cref="ProfileCollectionPlanEntry.MatchedUpdateEntry"/> from
/// <see cref="ProfileCollectionWalker.WalkChildren"/>.
/// </summary>
/// <remarks>
/// Composition order (spec Section 4.5):
/// <list type="number">
///   <item>Classify bindings via <see cref="ProfileBindingClassificationCore.ClassifyBindingsWithExplicitHiddenPaths"/>.</item>
///   <item>Apply per-disposition overlay seeded from the stored row values.</item>
///   <item>Resolve key-unification via <see cref="ProfileKeyUnificationCore.ResolveForCollectionRow"/>.</item>
///   <item>Rewrite parent key parts via <see cref="RelationalWriteRowHelpers.RewriteParentKeyPartValues"/>.</item>
///   <item>Rewrite stable row identity for continuity via <see cref="RelationalWriteRowHelpers.RewriteCollectionStableRowIdentity"/>.</item>
///   <item>Overwrite ordinal column with <paramref name="finalOrdinal"/>.</item>
///   <item>Build the merged row via <see cref="RelationalWriteRowHelpers.CreateMergedTableRow"/>.</item>
/// </list>
/// </remarks>
internal static class ProfileCollectionMatchedRowOverlay
{
    /// <summary>
    /// Builds a single merged row for a matched collection row update, applying
    /// profile-aware hidden governance, key-unification preservation, stable-row-identity
    /// continuity, parent-key rewriting, and ordinal stamping in the required composition
    /// order.
    /// </summary>
    /// <param name="resourceWritePlan">The outermost resource write plan; passed through to the classifier.</param>
    /// <param name="tableWritePlan">The collection table's write plan.</param>
    /// <param name="profileRequest">The profile-applied write request; used for binding classification.</param>
    /// <param name="storedRow">The matched current collection row snapshot from the database.</param>
    /// <param name="requestCandidate">The matched flattened request candidate, binding-index-aligned.</param>
    /// <param name="hiddenMemberPaths">
    /// Pre-computed hidden member paths from the matched
    /// <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>. Supplied explicitly so
    /// the row-level classifier bypasses stored scope-state derivation.
    /// </param>
    /// <param name="finalOrdinal">The position-derived ordinal (1-based) to stamp onto the row.</param>
    /// <param name="parentPhysicalRowIdentityValues">
    /// The parent collection-row's physical identity values used to rewrite parent-key-part
    /// bindings. For top-level collections the parent is the root row; for nested
    /// collections the parent is the enclosing collection row.
    /// </param>
    /// <param name="concreteRequestItemNode">
    /// The concrete request item JSON node resolved from the writable request body by
    /// evaluating the matched item's <see cref="VisibleRequestCollectionItem.RequestJsonPath"/>
    /// (e.g. <c>$.classPeriods[0]</c>). Used by the key-unification seam for visible-member
    /// evaluation.
    /// </param>
    /// <param name="resolvedReferenceLookups">
    /// Resolved reference lookups compiled once per synthesis pass, reused across all
    /// collection rows.
    /// </param>
    internal static RelationalWriteMergedTableRow BuildMatchedRowEmission(
        ResourceWritePlan resourceWritePlan,
        TableWritePlan tableWritePlan,
        ProfileAppliedWriteRequest profileRequest,
        CurrentCollectionRowSnapshot storedRow,
        CollectionWriteCandidate requestCandidate,
        ImmutableArray<string> hiddenMemberPaths,
        int finalOrdinal,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        JsonNode concreteRequestItemNode,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        ArgumentNullException.ThrowIfNull(resourceWritePlan);
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(profileRequest);
        ArgumentNullException.ThrowIfNull(storedRow);
        ArgumentNullException.ThrowIfNull(requestCandidate);
        ArgumentNullException.ThrowIfNull(parentPhysicalRowIdentityValues);
        ArgumentNullException.ThrowIfNull(concreteRequestItemNode);
        ArgumentNullException.ThrowIfNull(resolvedReferenceLookups);

        // Step 1: Classify bindings using pre-computed hidden paths (bypasses stored scope derivation).
        var resolverOwnedBindingIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(
            tableWritePlan
        );
        var dispositions = ProfileBindingClassificationCore.ClassifyBindingsWithExplicitHiddenPaths(
            resourceWritePlan,
            tableWritePlan,
            profileRequest,
            resolverOwnedBindingIndices,
            hiddenMemberPaths
        );

        // Step 2: Apply per-disposition overlay onto a mutable buffer seeded from the stored row.
        var values = ApplyDispositionOverlay(
            tableWritePlan,
            storedRow.ProjectedCurrentRow.Values,
            requestCandidate.Values,
            dispositions,
            resolverOwnedBindingIndices
        );

        // Step 3: Resolve key-unification — writes canonical and synthetic-presence bindings
        // in place. Only needed when the table has key-unification plans.
        if (tableWritePlan.KeyUnificationPlans.Length > 0)
        {
            // Use the snapshot's column-name-keyed projection (built once at projection time
            // from the raw hydrated row) so hidden member preservation reads UnifiedAlias
            // MemberPathColumn / PresenceColumn values that are absent from ColumnBindings.
            var currentRowByColumnName = storedRow.CurrentRowByColumnName;
            var kuContext = new ProfileCollectionRowKeyUnificationContext(
                RequestItemNode: concreteRequestItemNode,
                CurrentRowByColumnName: currentRowByColumnName,
                HiddenMemberPaths: hiddenMemberPaths,
                OrdinalPath: requestCandidate.OrdinalPath,
                ResolvedReferenceLookups: resolvedReferenceLookups
            );
            var valueAssigned = new bool[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                valueAssigned[i] = true;
            }
            foreach (var kuPlan in tableWritePlan.KeyUnificationPlans)
            {
                ProfileKeyUnificationCore.ResolveForCollectionRow(
                    tableWritePlan,
                    kuPlan,
                    kuContext,
                    values,
                    valueAssigned,
                    resolverOwnedBindingIndices
                );
            }
        }

        // Step 4: Rewrite parent key parts with the parent row's physical identity.
        var valuesAfterParentKey = RelationalWriteRowHelpers.RewriteParentKeyPartValues(
            tableWritePlan,
            values,
            parentPhysicalRowIdentityValues
        );

        // Step 5: Rewrite stable row identity for update continuity (keep the stored stable id).
        var valuesAfterStableId = RelationalWriteRowHelpers.RewriteCollectionStableRowIdentity(
            tableWritePlan,
            valuesAfterParentKey,
            storedRow.ProjectedCurrentRow.Values
        );

        // Step 6: Overwrite ordinal with the planner-supplied final ordinal.
        var valuesWithOrdinal = StampOrdinal(tableWritePlan, valuesAfterStableId, finalOrdinal);

        // Step 7: Build and return the merged row.
        return RelationalWriteRowHelpers.CreateMergedTableRow(tableWritePlan, valuesWithOrdinal);
    }

    /// <summary>
    /// Applies per-binding dispositions to build the initial mutable values buffer.
    /// Seeded from the stored row; visible-writable bindings take the request candidate value.
    /// Resolver-owned bindings are skipped (the key-unification resolver writes them).
    /// </summary>
    private static FlattenedWriteValue[] ApplyDispositionOverlay(
        TableWritePlan tableWritePlan,
        ImmutableArray<FlattenedWriteValue> storedValues,
        ImmutableArray<FlattenedWriteValue> requestValues,
        ImmutableArray<RootBindingDisposition> dispositions,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        var values = new FlattenedWriteValue[tableWritePlan.ColumnBindings.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (resolverOwnedBindingIndices.Contains(i))
            {
                // Key-unification resolver will write this binding; seed with stored value so
                // the buffer is fully populated, but it will be overwritten.
                values[i] = storedValues[i];
                continue;
            }
            values[i] = dispositions[i] switch
            {
                RootBindingDisposition.VisibleWritable => requestValues[i],
                RootBindingDisposition.HiddenPreserved => storedValues[i],
                RootBindingDisposition.StorageManaged => storedValues[i],
                RootBindingDisposition.ClearOnVisibleAbsent => throw new InvalidOperationException(
                    $"ClearOnVisibleAbsent disposition is not expected for top-level collection rows; "
                        + "row-level omission is row-delete, not per-column clear. "
                        + $"Binding index {i} on table '{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}' "
                        + $"produced {nameof(RootBindingDisposition.ClearOnVisibleAbsent)}."
                ),
                _ => throw new InvalidOperationException(
                    $"Unexpected {nameof(RootBindingDisposition)} '{dispositions[i]}' at index {i} "
                        + $"on table '{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}'."
                ),
            };
        }
        return values;
    }

    /// <summary>
    /// Overwrites the ordinal binding with <paramref name="finalOrdinal"/>. The ordinal
    /// binding is located via <see cref="CollectionMergePlan.OrdinalBindingIndex"/>. Throws
    /// when the table lacks a <see cref="CollectionMergePlan"/> — ordinal stamping is only
    /// meaningful for collection tables.
    /// </summary>
    private static ImmutableArray<FlattenedWriteValue> StampOrdinal(
        TableWritePlan tableWritePlan,
        ImmutableArray<FlattenedWriteValue> values,
        int finalOrdinal
    )
    {
        var mergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{ProfileBindingClassificationCore.FormatTable(tableWritePlan)}' does not have a "
                    + "compiled collection merge plan; cannot stamp ordinal."
            );

        FlattenedWriteValue[] stamped = [.. values];
        stamped[mergePlan.OrdinalBindingIndex] = new FlattenedWriteValue.Literal(finalOrdinal);
        return stamped.ToImmutableArray();
    }
}
