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
        /// <summary>Exact-path match only (Scalar, DescriptorReference).</summary>
        Exact,

        /// <summary>Ancestor-or-exact match (DocumentReference, ReferenceDerived).</summary>
        AncestorOrExact,
    }

    internal static bool IsHiddenGoverned(
        string memberPath,
        ImmutableArray<string> hiddenMemberPaths,
        HiddenPathMatchKind matchKind
    )
    {
        ArgumentNullException.ThrowIfNull(memberPath);
        return hiddenMemberPaths.Any(hidden => IsMatch(memberPath, hidden, matchKind));
    }

    internal static HiddenPathMatchKind MatchKindFor(WriteValueSource source) =>
        source switch
        {
            WriteValueSource.Scalar => HiddenPathMatchKind.Exact,
            WriteValueSource.DescriptorReference => HiddenPathMatchKind.Exact,
            WriteValueSource.DocumentReference => HiddenPathMatchKind.AncestorOrExact,
            WriteValueSource.ReferenceDerived => HiddenPathMatchKind.AncestorOrExact,
            _ => throw new ArgumentOutOfRangeException(
                nameof(source),
                source.GetType().Name,
                $"ProfileMemberGovernanceRules.MatchKindFor does not handle '{source.GetType().Name}'. "
                    + "The classifier filters Precomputed/DocumentId/ParentKeyPart/Ordinal before calling."
            ),
        };

    internal static RequestScopeState? LookupRequestScope(
        ProfileAppliedWriteRequest profileRequest,
        string scopeCanonical
    ) => profileRequest.RequestScopeStates.FirstOrDefault(state => state.Address.JsonScope == scopeCanonical);

    internal static StoredScopeState? LookupStoredScope(
        ProfileAppliedWriteContext context,
        string scopeCanonical
    ) => context.StoredScopeStates.FirstOrDefault(state => state.Address.JsonScope == scopeCanonical);

    private static bool IsMatch(string memberPath, string hiddenPath, HiddenPathMatchKind matchKind)
    {
        if (string.Equals(memberPath, hiddenPath, StringComparison.Ordinal))
        {
            return true;
        }
        if (
            matchKind == HiddenPathMatchKind.AncestorOrExact
            && memberPath.StartsWith(hiddenPath, StringComparison.Ordinal)
            && memberPath.Length > hiddenPath.Length
            && memberPath[hiddenPath.Length] == '.'
        )
        {
            return true;
        }
        return false;
    }
}
