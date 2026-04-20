// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Classifies a profiled write request to determine the minimum
/// <see cref="RequiredSliceFamily"/> that the executor must land
/// before applying profile-constrained writes.
/// </summary>
/// <remarks>
/// Visibility rule for scope-state streams:
/// <see cref="ProfileVisibilityKind.Hidden"/> scope-state metadata is preserve-only
/// except for separate-table non-collection scopes, which remain fenced even when
/// hidden or request-absent; <see cref="ProfileVisibilityKind.VisiblePresent"/> and
/// <see cref="ProfileVisibilityKind.VisibleAbsent"/> both escalate by topology.
/// Row streams (<c>VisibleRequestCollectionItems</c>, <c>VisibleStoredCollectionRows</c>)
/// are visible-only by contract and continue to escalate unconditionally.
/// </remarks>
internal static class ProfileSliceFenceClassifier
{
    /// <summary>
    /// Classifies the required slice family for a create-new (insert) flow,
    /// examining only the request-side metadata.
    /// </summary>
    public static RequiredSliceFamily ClassifyForCreateNew(
        ProfileAppliedWriteRequest request,
        ScopeTopologyIndex topologyIndex
    )
    {
        var max = RequiredSliceFamily.RootTableOnly;

        foreach (var scopeState in request.RequestScopeStates)
        {
            var family = ToFamily(topologyIndex.GetTopology(scopeState.Address.JsonScope));

            if (
                scopeState.Visibility == ProfileVisibilityKind.Hidden
                && family != RequiredSliceFamily.SeparateTableNonCollection
            )
            {
                continue;
            }
            if (family > max)
            {
                max = family;
            }
        }

        foreach (var collectionItem in request.VisibleRequestCollectionItems)
        {
            var family = ToFamily(topologyIndex.GetTopology(collectionItem.Address.JsonScope));
            if (family > max)
            {
                max = family;
            }
        }

        return max;
    }

    /// <summary>
    /// Classifies the required slice family for an existing-document (update/upsert) flow,
    /// examining both request-side and stored-side metadata. Returns the maximum across both.
    /// </summary>
    public static RequiredSliceFamily ClassifyForExistingDocument(
        ProfileAppliedWriteContext context,
        ScopeTopologyIndex topologyIndex
    )
    {
        var max = ClassifyForCreateNew(context.Request, topologyIndex);

        foreach (var storedScope in context.StoredScopeStates)
        {
            // Stored-side scope states always participate in family selection:
            // even hidden stored scopes require the owning slice to preserve them correctly.
            var family = ToFamily(topologyIndex.GetTopology(storedScope.Address.JsonScope));
            if (family > max)
            {
                max = family;
            }
        }

        foreach (var storedRow in context.VisibleStoredCollectionRows)
        {
            var family = ToFamily(topologyIndex.GetTopology(storedRow.Address.JsonScope));
            if (family > max)
            {
                max = family;
            }
        }

        return max;
    }

    /// <summary>
    /// Conservatively escalates the required slice family based on the compiled
    /// scope catalog. Slice 2's contract has no completeness marker for
    /// collection scopes, so if any catalog entry is a collection scope we
    /// cannot prove the merge is safe — return the matching collection family.
    /// The caller pairs this with <see cref="ClassifyForCreateNew"/> or
    /// <see cref="ClassifyForExistingDocument"/> and takes the maximum.
    /// </summary>
    public static RequiredSliceFamily ClassifyFromCatalog(
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        ScopeTopologyIndex topologyIndex
    )
    {
        var max = RequiredSliceFamily.RootTableOnly;
        foreach (var descriptor in scopeCatalog)
        {
            if (descriptor.ScopeKind != ScopeKind.Collection)
            {
                continue;
            }

            var family = ToFamily(topologyIndex.GetTopology(descriptor.JsonScope));
            if (family > max)
            {
                max = family;
            }
        }
        return max;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="ScopeTopologyKind"/> to the corresponding
    /// <see cref="RequiredSliceFamily"/>.
    /// </summary>
    private static RequiredSliceFamily ToFamily(ScopeTopologyKind topology) =>
        topology switch
        {
            ScopeTopologyKind.RootInlined => RequiredSliceFamily.RootTableOnly,
            ScopeTopologyKind.SeparateTableNonCollection => RequiredSliceFamily.SeparateTableNonCollection,
            ScopeTopologyKind.TopLevelBaseCollection => RequiredSliceFamily.TopLevelCollection,
            ScopeTopologyKind.NestedOrExtensionCollection =>
                RequiredSliceFamily.NestedAndExtensionCollections,
            _ => RequiredSliceFamily.RootTableOnly,
        };
}
