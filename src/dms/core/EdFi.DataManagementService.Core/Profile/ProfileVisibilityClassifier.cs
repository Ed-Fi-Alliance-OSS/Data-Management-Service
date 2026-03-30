// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Captures the member-level filter for a single scope: which properties are
/// explicitly named and whether the filter is include-only or exclude-only
/// (or include-all with nothing explicit).
/// </summary>
/// <param name="Mode">The member selection mode for the scope.</param>
/// <param name="ExplicitNames">
/// For IncludeOnly: the set of included property names.
/// For ExcludeOnly: the set of excluded property names.
/// For IncludeAll: empty.
/// </param>
public readonly record struct ScopeMemberFilter(MemberSelection Mode, IReadOnlySet<string> ExplicitNames);

/// <summary>
/// Pre-computes and caches profile visibility for every compiled scope in a resource's
/// scope catalog relative to a writable profile's <see cref="ContentTypeDefinition"/>.
/// Shared primitive consumed by C3 (request validation), C5 (pipeline orchestration),
/// and C6 (stored-state projection).
/// </summary>
public sealed class ProfileVisibilityClassifier
{
    // -----------------------------------------------------------------------
    //  Private cache types
    // -----------------------------------------------------------------------

    /// <summary>
    /// Cached result for a single compiled scope.
    /// </summary>
    /// <param name="IsHidden">True when the profile's member selection hides this scope.</param>
    /// <param name="Node">The navigator result; null when hidden.</param>
    /// <param name="ItemFilter">The collection item filter, or null if none applies.</param>
    private sealed record CachedScopeEntry(
        bool IsHidden,
        ProfileTreeNode? Node,
        CollectionItemFilter? ItemFilter
    );

    // -----------------------------------------------------------------------
    //  Fields
    // -----------------------------------------------------------------------

    private readonly Dictionary<string, CachedScopeEntry> _cache;
    private readonly Dictionary<string, ScopeKind> _scopeKinds;

    // -----------------------------------------------------------------------
    //  Constructor
    // -----------------------------------------------------------------------

    /// <summary>
    /// Initializes the classifier by pre-computing visibility for every scope in
    /// <paramref name="scopeCatalog"/> against the supplied writable profile definition.
    /// </summary>
    /// <param name="writeContentType">
    /// The writable profile's content-type definition that controls member visibility.
    /// </param>
    /// <param name="scopeCatalog">
    /// The compiled scope descriptors for the resource; determines which scopes exist
    /// and their structural relationships.
    /// </param>
    public ProfileVisibilityClassifier(
        ContentTypeDefinition writeContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        ArgumentNullException.ThrowIfNull(writeContentType);
        ArgumentNullException.ThrowIfNull(scopeCatalog);

        var navigator = new ProfileTreeNavigator(writeContentType);

        _cache = new Dictionary<string, CachedScopeEntry>(scopeCatalog.Count);
        _scopeKinds = new Dictionary<string, ScopeKind>(scopeCatalog.Count);

        foreach (CompiledScopeDescriptor scope in scopeCatalog)
        {
            _scopeKinds[scope.JsonScope] = scope.ScopeKind;

            ProfileTreeNode? node = navigator.Navigate(scope.JsonScope);
            bool isHidden = node == null;

            CollectionItemFilter? itemFilter = null;
            if (!isHidden && scope.ScopeKind == ScopeKind.Collection)
            {
                itemFilter = ResolveItemFilter(navigator, scope.JsonScope);
            }

            _cache[scope.JsonScope] = new CachedScopeEntry(isHidden, node, itemFilter);
        }
    }

    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Classifies the visibility of a compiled scope given optionally present scope data.
    /// </summary>
    /// <param name="jsonScope">The compiled scope identifier (e.g. "$", "$.classPeriods[*]").</param>
    /// <param name="scopeData">
    /// The JSON node representing the scope's data in the current document, or null if absent.
    /// </param>
    /// <returns>
    /// <see cref="ProfileVisibilityKind.Hidden"/> when the profile excludes the scope;
    /// <see cref="ProfileVisibilityKind.VisiblePresent"/> when visible and data is present;
    /// <see cref="ProfileVisibilityKind.VisibleAbsent"/> when visible but data is absent.
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="jsonScope"/> is not in the scope catalog this classifier
    /// was constructed with.
    /// </exception>
    public ProfileVisibilityKind ClassifyScope(string jsonScope, JsonNode? scopeData)
    {
        CachedScopeEntry entry = _cache[jsonScope];

        if (entry.IsHidden)
        {
            return ProfileVisibilityKind.Hidden;
        }

        return scopeData != null ? ProfileVisibilityKind.VisiblePresent : ProfileVisibilityKind.VisibleAbsent;
    }

    /// <summary>
    /// Determines whether a collection item passes the profile's item-level filter for
    /// the specified collection scope.
    /// </summary>
    /// <param name="jsonScope">
    /// The compiled scope identifier for the collection (e.g. "$.addresses[*]").
    /// </param>
    /// <param name="collectionItem">The JSON node representing a single collection item.</param>
    /// <returns>
    /// True when the item should be included (no filter, or filter is satisfied);
    /// false when the item should be excluded.
    /// </returns>
    public bool PassesCollectionItemFilter(string jsonScope, JsonNode collectionItem)
    {
        CachedScopeEntry entry = _cache[jsonScope];

        if (entry.ItemFilter == null)
        {
            return true;
        }

        CollectionItemFilter filter = entry.ItemFilter;
        string? itemValue = collectionItem[filter.PropertyName]?.GetValue<string>();

        if (itemValue == null)
        {
            // No value for the filter property
            return filter.FilterMode == FilterMode.ExcludeOnly;
        }

        bool matchesFilter = filter.Values.Contains(itemValue);

        return filter.FilterMode switch
        {
            FilterMode.IncludeOnly => matchesFilter,
            FilterMode.ExcludeOnly => !matchesFilter,
            _ => true,
        };
    }

    /// <summary>
    /// Returns the member filter for the specified scope, describing which properties
    /// are explicitly named and whether the mode is include-only, exclude-only, or
    /// include-all.
    /// </summary>
    /// <param name="jsonScope">The compiled scope identifier.</param>
    /// <returns>
    /// A <see cref="ScopeMemberFilter"/> with <see cref="MemberSelection.IncludeAll"/> and an
    /// empty name set when the scope is hidden or the node is null (safe default for hidden scopes).
    /// </returns>
    public ScopeMemberFilter GetMemberFilter(string jsonScope)
    {
        CachedScopeEntry entry = _cache[jsonScope];

        if (entry.IsHidden || entry.Node == null)
        {
            return new ScopeMemberFilter(MemberSelection.IncludeAll, new HashSet<string>());
        }

        return new ScopeMemberFilter(
            entry.Node.Value.MemberSelection,
            entry.Node.Value.ExplicitPropertyNames
        );
    }

    /// <summary>
    /// All compiled scope JSON paths known to this classifier (the full scope catalog).
    /// </summary>
    public IEnumerable<string> AllScopeJsonScopes => _cache.Keys;

    /// <summary>
    /// Returns whether the specified scope exists in the scope catalog this classifier
    /// was constructed with.
    /// </summary>
    /// <param name="jsonScope">The compiled scope identifier to look up.</param>
    /// <returns>True when the scope is known; false otherwise.</returns>
    public bool ContainsScope(string jsonScope) => _cache.ContainsKey(jsonScope);

    /// <summary>
    /// Returns the <see cref="ScopeKind"/> for the specified compiled scope.
    /// </summary>
    /// <param name="jsonScope">The compiled scope identifier.</param>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="jsonScope"/> is not in the scope catalog.
    /// </exception>
    public ScopeKind GetScopeKind(string jsonScope) => _scopeKinds[jsonScope];

    // -----------------------------------------------------------------------
    //  ItemFilter resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the <see cref="CollectionItemFilter"/> for a collection scope by navigating
    /// to the parent scope and looking up the collection rule by name.
    /// </summary>
    /// <remarks>
    /// The <see cref="CollectionItemFilter"/> lives on the <see cref="CollectionRule"/> at the
    /// parent level, not on the <see cref="ProfileTreeNode"/> returned by navigation.
    /// This method navigates the parent, extracts the collection name from the scope path,
    /// and looks up the corresponding <see cref="CollectionRule"/>.
    /// </remarks>
    private static CollectionItemFilter? ResolveItemFilter(ProfileTreeNavigator navigator, string jsonScope)
    {
        // Extract collection name: last segment, strip "[*]" suffix
        // e.g. "$.addresses[*]" → "addresses"
        //      "$.addresses[*].periods[*]" → "periods"
        int lastDot = jsonScope.LastIndexOf('.');
        if (lastDot < 0)
        {
            return null;
        }

        string lastSegment = jsonScope[(lastDot + 1)..];
        if (!lastSegment.EndsWith("[*]", StringComparison.Ordinal))
        {
            return null;
        }

        string collectionName = lastSegment[..^3];

        // Build the parent path: everything before the last dot segment
        string parentPath = jsonScope[..lastDot];

        // Navigate to the parent node
        ProfileTreeNode? parentNode = navigator.Navigate(parentPath);
        if (parentNode == null)
        {
            return null;
        }

        // Look up the collection rule in the parent node
        if (!parentNode.Value.CollectionsByName.TryGetValue(collectionName, out CollectionRule? rule))
        {
            return null;
        }

        return rule.ItemFilter;
    }
}
