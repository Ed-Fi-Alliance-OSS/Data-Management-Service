// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Shared governance helper: single source of truth for visibility lookup and
/// hidden-path matching. The root-table binding classifier and the post-overlay
/// key-unification resolver both consult this so precedence rules can't drift.
/// </summary>
internal static class ProfileMemberGovernanceRules
{
    internal enum HiddenPathMatchKind
    {
        /// <summary>
        /// Hidden path must equal the binding's governing path exactly. Used for scalar
        /// and descriptor bindings, where governance is keyed on the binding's own member path.
        /// </summary>
        Exact,

        /// <summary>
        /// Hidden path must equal the binding's governing reference path, or be a descendant
        /// of it. Used for document-reference FK bindings and reference-derived bindings: the
        /// governing path is the owning reference root (e.g. <c>schoolReference</c>), and any
        /// hidden member at or below that root (e.g. <c>schoolReference.schoolId</c>) preserves
        /// the entire reference-derived storage family.
        /// See <c>profiles.md:782</c> and <c>02-root-table-only-profile-merge.md:75</c>.
        /// </summary>
        ReferenceRooted,
    }

    /// <summary>
    /// Returns true iff any hidden member path in <paramref name="hiddenMemberPaths"/> governs
    /// a binding with the given <paramref name="governingPath"/> and <paramref name="matchKind"/>.
    /// </summary>
    /// <param name="governingPath">
    /// Scope-relative path used for hidden-path matching. For <see cref="HiddenPathMatchKind.Exact"/>
    /// this is the binding's own member path. For <see cref="HiddenPathMatchKind.ReferenceRooted"/>
    /// this is the owning document-reference root path.
    /// </param>
    internal static bool IsHiddenGoverned(
        string governingPath,
        ImmutableArray<string> hiddenMemberPaths,
        HiddenPathMatchKind matchKind
    )
    {
        ArgumentNullException.ThrowIfNull(governingPath);
        return hiddenMemberPaths.Any(hidden => IsMatch(governingPath, hidden, matchKind));
    }

    internal static HiddenPathMatchKind MatchKindFor(WriteValueSource source) =>
        source switch
        {
            WriteValueSource.Scalar => HiddenPathMatchKind.Exact,
            WriteValueSource.DescriptorReference => HiddenPathMatchKind.Exact,
            WriteValueSource.DocumentReference => HiddenPathMatchKind.ReferenceRooted,
            WriteValueSource.ReferenceDerived => HiddenPathMatchKind.ReferenceRooted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(source),
                source.GetType().Name,
                $"ProfileMemberGovernanceRules.MatchKindFor does not handle '{source.GetType().Name}'. "
                    + "The classifier filters Precomputed/DocumentId/ParentKeyPart/Ordinal before calling."
            ),
        };

    internal static HiddenPathMatchKind MatchKindFor(KeyUnificationMemberWritePlan member) =>
        member switch
        {
            KeyUnificationMemberWritePlan.ScalarMember => HiddenPathMatchKind.Exact,
            KeyUnificationMemberWritePlan.DescriptorMember => HiddenPathMatchKind.Exact,
            KeyUnificationMemberWritePlan.ReferenceDerivedMember => HiddenPathMatchKind.ReferenceRooted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(member),
                member.GetType().Name,
                $"ProfileMemberGovernanceRules.MatchKindFor does not handle KeyUnificationMemberWritePlan subtype '{member.GetType().Name}'."
            ),
        };

    internal static RequestScopeState? LookupRequestScope(
        ProfileAppliedWriteRequest profileRequest,
        string scopeCanonical
    ) => profileRequest.RequestScopeStates.FirstOrDefault(state => state.Address.JsonScope == scopeCanonical);

    internal static RequestScopeState? LookupRequestScope(
        ProfileAppliedWriteRequest profileRequest,
        ScopeInstanceAddress scopeAddress
    ) =>
        profileRequest.RequestScopeStates.FirstOrDefault(state =>
            ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(state.Address, scopeAddress)
        );

    internal static StoredScopeState? LookupStoredScope(
        ProfileAppliedWriteContext context,
        string scopeCanonical
    ) => context.StoredScopeStates.FirstOrDefault(state => state.Address.JsonScope == scopeCanonical);

    internal static StoredScopeState? LookupStoredScope(
        ProfileAppliedWriteContext context,
        ScopeInstanceAddress scopeAddress
    ) =>
        context.StoredScopeStates.FirstOrDefault(state =>
            ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(state.Address, scopeAddress)
        );

    private static bool IsMatch(string governingPath, string hiddenPath, HiddenPathMatchKind matchKind)
    {
        if (string.Equals(governingPath, hiddenPath, StringComparison.Ordinal))
        {
            return true;
        }
        if (matchKind != HiddenPathMatchKind.ReferenceRooted)
        {
            return false;
        }
        // Empty governingPath means the containing scope IS the reference root, so every
        // hidden member path under that scope is governed by this reference family.
        if (governingPath.Length == 0)
        {
            return true;
        }
        return hiddenPath.StartsWith(governingPath, StringComparison.Ordinal)
            && hiddenPath.Length > governingPath.Length
            && hiddenPath[governingPath.Length] == '.';
    }
}
