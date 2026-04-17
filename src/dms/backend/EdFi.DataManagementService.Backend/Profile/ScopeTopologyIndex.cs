// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Backend-local lookup keyed by <c>JsonScope</c> canonical string, built from a
/// <see cref="ResourceWritePlan"/>. Maps each scope to a <see cref="ScopeTopologyKind"/>
/// describing its physical storage role.
/// </summary>
/// <remarks>
/// Scopes not in the write plan (inlined non-collection scopes on the root table) return
/// <see cref="ScopeTopologyKind.RootInlined"/> as the default.
/// </remarks>
internal sealed class ScopeTopologyIndex
{
    private readonly IReadOnlyDictionary<string, ScopeTopologyKind> _topologyByScope;

    private ScopeTopologyIndex(IReadOnlyDictionary<string, ScopeTopologyKind> topologyByScope)
    {
        _topologyByScope = topologyByScope;
    }

    /// <summary>
    /// Returns the <see cref="ScopeTopologyKind"/> for the given JSON scope canonical string.
    /// Returns <see cref="ScopeTopologyKind.RootInlined"/> for scopes not present in the write plan
    /// (inlined scopes have no backing table and are physically part of the root row).
    /// </summary>
    public ScopeTopologyKind GetTopology(string jsonScopeCanonical) =>
        _topologyByScope.TryGetValue(jsonScopeCanonical, out var kind) ? kind : ScopeTopologyKind.RootInlined;

    /// <summary>
    /// Builds a <see cref="ScopeTopologyIndex"/> from the given <see cref="ResourceWritePlan"/>.
    /// </summary>
    public static ScopeTopologyIndex BuildFromWritePlan(ResourceWritePlan plan)
    {
        // First pass: collect all collection-kinded scopes so nested detection can reference them.
        var collectionScopes = plan
            .TablePlansInDependencyOrder.Where(tp =>
                tp.TableModel.IdentityMetadata.TableKind
                    is DbTableKind.Collection
                        or DbTableKind.ExtensionCollection
            )
            .Select(tp => tp.TableModel.JsonScope.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        var topologyByScope = plan
            .TablePlansInDependencyOrder.Select(tp => tp.TableModel)
            .ToDictionary(
                tm => tm.JsonScope.Canonical,
                tm => ToTopologyKind(tm.JsonScope.Canonical, tm.IdentityMetadata.TableKind, collectionScopes),
                StringComparer.Ordinal
            );

        return new ScopeTopologyIndex(topologyByScope);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="DbTableKind"/> to its <see cref="ScopeTopologyKind"/>.
    /// For <see cref="DbTableKind.Collection"/>, walks ancestor segments to distinguish
    /// top-level from nested collections.
    /// </summary>
    private static ScopeTopologyKind ToTopologyKind(
        string jsonScope,
        DbTableKind tableKind,
        IReadOnlySet<string> collectionScopes
    ) =>
        tableKind switch
        {
            DbTableKind.Root => ScopeTopologyKind.RootInlined,
            DbTableKind.RootExtension => ScopeTopologyKind.SeparateTableNonCollection,
            DbTableKind.CollectionExtensionScope => ScopeTopologyKind.SeparateTableNonCollection,
            DbTableKind.ExtensionCollection => ScopeTopologyKind.NestedOrExtensionCollection,
            DbTableKind.Collection => HasCollectionAncestor(jsonScope, collectionScopes)
                ? ScopeTopologyKind.NestedOrExtensionCollection
                : ScopeTopologyKind.TopLevelBaseCollection,
            _ => ScopeTopologyKind.RootInlined,
        };

    /// <summary>
    /// Returns true if any ancestor segment of <paramref name="jsonScope"/> is itself
    /// a collection scope (i.e., exists in <paramref name="collectionScopes"/>).
    /// </summary>
    /// <remarks>
    /// Splits on <c>.</c> and walks progressively shorter prefixes, stopping before the
    /// scope itself. A scope like <c>$.addresses[*].periods[*]</c> will find
    /// <c>$.addresses[*]</c> in the collection set, making it nested.
    /// </remarks>
    private static bool HasCollectionAncestor(string jsonScope, IReadOnlySet<string> collectionScopes)
    {
        var segments = jsonScope.Split('.');

        // Walk all ancestor prefixes (excluding the scope itself: stop before segments.Length).
        for (var len = segments.Length - 1; len >= 1; len--)
        {
            var ancestor = string.Join(".", segments[..len]);
            if (collectionScopes.Contains(ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
