// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.External.Profile;

/// <summary>
/// Shared ordering/deduplication helper for caller-supplied inlined scope
/// lists consumed by <see cref="CompiledScopeAdapterFactory"/> and the
/// backend's <c>ScopeTopologyIndex</c>. Sorts scopes ancestor-first using
/// dot-separator depth as the ordering key and removes duplicates against
/// a caller-supplied set of already-known canonical scopes.
/// </summary>
internal static class InlinedScopeNormalization
{
    public static IReadOnlyList<(string JsonScope, ScopeKind Kind)> Normalize(
        IReadOnlyList<(string JsonScope, ScopeKind Kind)>? additionalScopes,
        IReadOnlySet<string> knownScopes
    )
    {
        if (additionalScopes is not { Count: > 0 })
        {
            return [];
        }

        List<(string JsonScope, ScopeKind Kind, int Depth, int Index)> normalized = [];
        HashSet<string> seenScopes = new(knownScopes, StringComparer.Ordinal);

        for (var index = 0; index < additionalScopes.Count; index++)
        {
            var (jsonScope, kind) = additionalScopes[index];

            if (!seenScopes.Add(jsonScope))
            {
                continue;
            }

            normalized.Add((jsonScope, kind, CountScopeDepth(jsonScope), index));
        }

        normalized.Sort(
            static (left, right) =>
            {
                var depthComparison = left.Depth.CompareTo(right.Depth);
                return depthComparison != 0 ? depthComparison : left.Index.CompareTo(right.Index);
            }
        );

        return [.. normalized.Select(scope => (scope.JsonScope, scope.Kind))];
    }

    public static int CountScopeDepth(string jsonScope) => jsonScope.Count(c => c == '.') + 1;
}
