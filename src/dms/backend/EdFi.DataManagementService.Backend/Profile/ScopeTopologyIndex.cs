// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Backend-local lookup keyed by <c>JsonScope</c> canonical string, built from a
/// <see cref="ResourceWritePlan"/>. Maps each scope to a <see cref="ScopeTopologyKind"/>
/// describing its physical storage role.
/// </summary>
/// <remarks>
/// Inlined scopes (scopes defined by the profile content type tree but not given their own backing
/// table — e.g. a common-type member under a collection row, or an object member under an extension row)
/// are classified based on the <see cref="ScopeKind"/> supplied by the caller:
/// <list type="bullet">
/// <item><description><see cref="ScopeKind.Collection"/> entries are classified against the same
/// collection rules as table-backed collections: an inlined collection under an <c>_ext</c> path or
/// nested under another collection is <see cref="ScopeTopologyKind.NestedOrExtensionCollection"/>;
/// otherwise it is <see cref="ScopeTopologyKind.TopLevelBaseCollection"/>.</description></item>
/// <item><description>All other inlined scopes inherit the topology of their closest table-backed
/// ancestor, so the fence family matches the physical row that actually stores them.</description></item>
/// </list>
/// Scopes with no ancestor entry at all fall back to <see cref="ScopeTopologyKind.RootInlined"/>.
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
    /// Returns <see cref="ScopeTopologyKind.RootInlined"/> for scopes that are neither registered
    /// as table-backed nor passed in as inlined via <c>additionalScopes</c>.
    /// </summary>
    public ScopeTopologyKind GetTopology(string jsonScopeCanonical) =>
        _topologyByScope.TryGetValue(jsonScopeCanonical, out var kind) ? kind : ScopeTopologyKind.RootInlined;

    /// <summary>
    /// Builds a <see cref="ScopeTopologyIndex"/> from the given <see cref="ResourceWritePlan"/>.
    /// When <paramref name="additionalScopes"/> is supplied, each inlined scope is classified
    /// according to its <see cref="ScopeKind"/>: <see cref="ScopeKind.Collection"/> entries are
    /// classified against the collection rules (top-level vs nested/extension), while all other
    /// inlined scopes inherit the topology of their closest table-backed ancestor so the fence
    /// family reflects the physical row that stores them.
    /// </summary>
    public static ScopeTopologyIndex BuildFromWritePlan(
        ResourceWritePlan plan,
        IReadOnlyList<(string JsonScope, ScopeKind Kind)>? additionalScopes = null
    )
    {
        var tableScopes = plan
            .TablePlansInDependencyOrder.Select(tp => tp.TableModel.JsonScope.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        var normalizedAdditionalScopes = InlinedScopeNormalization.Normalize(additionalScopes, tableScopes);

        // First pass: collect all collection-kinded scopes (table-backed plus any inlined
        // collections the caller passes in) so nested detection sees them all.
        var collectionScopes = plan
            .TablePlansInDependencyOrder.Where(tp =>
                tp.TableModel.IdentityMetadata.TableKind
                    is DbTableKind.Collection
                        or DbTableKind.ExtensionCollection
            )
            .Select(tp => tp.TableModel.JsonScope.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (jsonScope, kind) in normalizedAdditionalScopes)
        {
            if (kind == ScopeKind.Collection)
            {
                collectionScopes.Add(jsonScope);
            }
        }

        var topologyByScope = new Dictionary<string, ScopeTopologyKind>(StringComparer.Ordinal);
        foreach (var tableModel in plan.TablePlansInDependencyOrder.Select(tp => tp.TableModel))
        {
            topologyByScope[tableModel.JsonScope.Canonical] = ToTopologyKind(
                tableModel.JsonScope.Canonical,
                tableModel.IdentityMetadata.TableKind,
                collectionScopes
            );
        }

        foreach (var (jsonScope, kind) in normalizedAdditionalScopes)
        {
            // Inlined scopes must not clobber a table-backed topology. TryAdd skips if the scope is already registered.
            var topology =
                kind == ScopeKind.Collection
                    ? ClassifyInlinedCollection(jsonScope, collectionScopes)
                    : InheritAncestorTopology(jsonScope, topologyByScope);
            topologyByScope.TryAdd(jsonScope, topology);
        }

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

    /// <summary>
    /// Walks the dot-separated ancestor prefixes of <paramref name="jsonScope"/> (excluding the
    /// scope itself) and returns the first <see cref="ScopeTopologyKind"/> found in
    /// <paramref name="topologyByScope"/>. Falls back to <see cref="ScopeTopologyKind.RootInlined"/>
    /// only if no ancestor is registered.
    /// </summary>
    private static ScopeTopologyKind InheritAncestorTopology(
        string jsonScope,
        IReadOnlyDictionary<string, ScopeTopologyKind> topologyByScope
    )
    {
        var segments = jsonScope.Split('.');
        for (var len = segments.Length - 1; len >= 1; len--)
        {
            var ancestor = string.Join(".", segments[..len]);
            if (topologyByScope.TryGetValue(ancestor, out var topology))
            {
                return topology;
            }
        }

        return ScopeTopologyKind.RootInlined;
    }

    /// <summary>
    /// Classifies an inlined scope whose caller-declared <see cref="ScopeKind"/> is
    /// <see cref="ScopeKind.Collection"/>. Mirrors the table-backed collection rules:
    /// any collection under an <c>_ext</c> path or with a collection ancestor is
    /// <see cref="ScopeTopologyKind.NestedOrExtensionCollection"/>; otherwise it is
    /// <see cref="ScopeTopologyKind.TopLevelBaseCollection"/>.
    /// </summary>
    private static ScopeTopologyKind ClassifyInlinedCollection(
        string jsonScope,
        IReadOnlySet<string> collectionScopes
    ) =>
        jsonScope.Contains("._ext.", StringComparison.Ordinal)
        || HasCollectionAncestor(jsonScope, collectionScopes)
            ? ScopeTopologyKind.NestedOrExtensionCollection
            : ScopeTopologyKind.TopLevelBaseCollection;
}
