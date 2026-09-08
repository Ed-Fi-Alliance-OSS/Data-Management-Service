// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Inlined non-collection descendant <see cref="RequestScopeState"/> / <see cref="StoredScopeState"/>
/// instances whose owner table equals a synthesized separate scope's table and whose
/// ancestor collection chain matches the synthesized scope's instance address. These are
/// the scope-states that the instance-aware classifier and resolver must consult so a
/// descendant scope's <see cref="StoredScopeState.HiddenMemberPaths"/>,
/// <see cref="StoredScopeState.Visibility"/>, and <see cref="RequestScopeState.Visibility"/>
/// govern bindings on this table under the descendant's own scope rather than falling
/// through to the parent scope's governance.
/// </summary>
internal readonly record struct ProfileSeparateScopeDescendantStates(
    ImmutableArray<RequestScopeState> RequestScopes,
    ImmutableArray<StoredScopeState> StoredScopes
)
{
    /// <summary>
    /// Collects request and stored scope states for descendant inlined non-collection scopes
    /// of <paramref name="directScopeAddress"/> whose owner table equals
    /// <paramref name="directTablePlan"/>. Filters by ancestor-instance chain equality so
    /// sibling collection-row instances do not leak state into one another.
    /// </summary>
    public static ProfileSeparateScopeDescendantStates Collect(
        ResourceWritePlan writePlan,
        TableWritePlan directTablePlan,
        ScopeInstanceAddress directScopeAddress,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(directTablePlan);
        ArgumentNullException.ThrowIfNull(directScopeAddress);
        ArgumentNullException.ThrowIfNull(profileRequest);

        var directTableJsonScope = directTablePlan.TableModel.JsonScope.Canonical;

        var requestScopes = profileRequest
            .RequestScopeStates.Where(state =>
                IsInlinedDescendantOnSameInstance(
                    state.Address,
                    directScopeAddress,
                    directTableJsonScope,
                    writePlan
                )
            )
            .ToImmutableArray();

        var storedScopes = profileAppliedContext is null
            ? ImmutableArray<StoredScopeState>.Empty
            : profileAppliedContext
                .StoredScopeStates.Where(state =>
                    IsInlinedDescendantOnSameInstance(
                        state.Address,
                        directScopeAddress,
                        directTableJsonScope,
                        writePlan
                    )
                )
                .ToImmutableArray();

        return new ProfileSeparateScopeDescendantStates(requestScopes, storedScopes);
    }

    private static bool IsInlinedDescendantOnSameInstance(
        ScopeInstanceAddress candidateAddress,
        ScopeInstanceAddress directScopeAddress,
        string directTableJsonScope,
        ResourceWritePlan writePlan
    )
    {
        var directScopeJsonScope = directScopeAddress.JsonScope;
        var candidateJsonScope = candidateAddress.JsonScope;

        // (1) Strict descendant: must be longer and the next char after directScopeJsonScope
        //     must be a '.' segment boundary.
        if (
            candidateJsonScope.Length <= directScopeJsonScope.Length
            || !candidateJsonScope.StartsWith(directScopeJsonScope, StringComparison.Ordinal)
            || candidateJsonScope[directScopeJsonScope.Length] != '.'
        )
        {
            return false;
        }

        // (2) Same physical table (inlined onto the direct scope's table).
        var ownerTable = ProfileBindingClassificationCore.ResolveOwnerTablePlan(
            candidateJsonScope,
            writePlan
        );
        if (
            ownerTable is null
            || !string.Equals(
                ownerTable.TableModel.JsonScope.Canonical,
                directTableJsonScope,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        // (3) Same ancestor collection-instance chain. For RootExtension this is `[]==[]`.
        //     For CollectionExtensionScope this binds to the parent collection row currently
        //     being synthesized, preventing cross-instance leakage. Reuse the existing
        //     ScopeInstanceAddressEquals via a synthetic address whose JsonScope matches the
        //     direct scope's so equality reduces to ancestor-chain comparison plus the
        //     json-scope match (which is trivially satisfied).
        var candidateDirectAddress = new ScopeInstanceAddress(
            directScopeJsonScope,
            candidateAddress.AncestorCollectionInstances
        );
        return ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(
            directScopeAddress,
            candidateDirectAddress
        );
    }
}
