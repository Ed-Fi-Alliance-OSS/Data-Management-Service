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
/// On the request side (create-new), <see cref="ProfileVisibilityKind.Hidden"/> scope-state
/// metadata is preserve-only and does not escalate; only
/// <see cref="ProfileVisibilityKind.VisiblePresent"/> and
/// <see cref="ProfileVisibilityKind.VisibleAbsent"/> escalate by topology.
/// On the stored side, scope states always participate in family selection regardless of
/// visibility — even hidden stored scopes require the owning slice family because the
/// existing-document flow still has to preserve them correctly.
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
            if (scopeState.Visibility == ProfileVisibilityKind.Hidden)
            {
                continue;
            }
            var family = ToFamily(topologyIndex.GetTopology(scopeState.Address.JsonScope));
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
