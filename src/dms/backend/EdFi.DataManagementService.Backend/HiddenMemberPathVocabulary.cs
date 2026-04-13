// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Converts Core-produced <c>HiddenMemberPaths</c> (bare scope-relative member
/// names, e.g. <c>entryDate</c>, <c>officialAttendancePeriod</c>) into the
/// <c>$.</c>-prefixed scope-relative JSONPath vocabulary used by
/// <see cref="RelationalWriteBindingClassifier"/> and
/// key-unification overlay comparisons.
/// </summary>
/// <remarks>
/// <para>
/// Core's <c>StoredScopeState.HiddenMemberPaths</c> and
/// <c>VisibleStoredCollectionRow.HiddenMemberPaths</c> are produced against
/// <c>CompiledScopeDescriptor.CanonicalScopeRelativeMemberPaths</c>, which is
/// the scope-relative bare-name vocabulary
/// (<see cref="Backend.External.Profile.CompiledScopeAdapterFactory"/>).
/// </para>
/// <para>
/// Backend write plans use <c>$.</c>-prefixed scope-relative JSONPath in
/// <c>WriteValueSource.Scalar.RelativePath.Canonical</c>,
/// <c>WriteValueSource.DescriptorReference.RelativePath.Canonical</c>,
/// <c>WriteValueSource.ReferenceDerived.ReferenceSource.ReferenceJsonPath.Canonical</c>,
/// and <c>DocumentReferenceBinding.IdentityBindings[*].ReferenceJsonPath.Canonical</c>.
/// </para>
/// <para>
/// Always convert at the point where Core-shaped hidden paths are ingested,
/// never inside the classifier — the classifier is a pure comparator keyed
/// on the backend vocabulary.
/// </para>
/// </remarks>
internal static class HiddenMemberPathVocabulary
{
    /// <summary>
    /// Prefixes every bare scope-relative member name with <c>$.</c> so it
    /// can be compared to <c>RelativePath.Canonical</c> / <c>ReferenceJsonPath.Canonical</c>
    /// values in backend write plans. Empty input returns empty output.
    /// </summary>
    public static ImmutableArray<string> ToJsonPathRelative(ImmutableArray<string> coreHiddenMemberPaths)
    {
        if (coreHiddenMemberPaths.IsDefaultOrEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>(coreHiddenMemberPaths.Length);
        foreach (var bare in coreHiddenMemberPaths)
        {
            // Defensive: a caller that already did the conversion (or a future
            // Core contract change that emits "$."-prefixed paths) should pass
            // through unchanged rather than double-prefix.
            builder.Add(bare.StartsWith("$.", System.StringComparison.Ordinal) ? bare : "$." + bare);
        }

        return builder.MoveToImmutable();
    }
}
