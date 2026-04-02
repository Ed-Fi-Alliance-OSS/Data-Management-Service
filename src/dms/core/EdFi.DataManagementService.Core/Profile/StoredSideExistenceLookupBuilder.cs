// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Walks a stored JSON document using <see cref="AddressDerivationEngine"/> and
/// <see cref="ProfileVisibilityClassifier"/> to build an address-keyed existence
/// lookup. The lookup answers "does a visible stored scope/item exist at this
/// address?" and is consumed by <see cref="CreatabilityAnalyzer"/> for
/// create-vs-update decisions.
/// </summary>
public static class StoredSideExistenceLookupBuilder
{
    /// <summary>
    /// Builds a <see cref="StoredSideExistenceLookupResult"/> by walking the stored
    /// document, classifying scope visibility, and deriving addresses for visible
    /// scopes and collection items.
    /// </summary>
    /// <param name="storedDocument">
    /// The stored JSON document, or null for create flows (no stored state).
    /// </param>
    /// <param name="scopeCatalog">
    /// The compiled scope descriptors for the resource.
    /// </param>
    /// <param name="classifier">
    /// The shared profile visibility classifier (from C3) that controls member visibility.
    /// </param>
    /// <param name="addressEngine">
    /// The shared address derivation engine (from C3) for computing scope/collection addresses.
    /// </param>
    /// <returns>
    /// A result containing the existence lookup, classified stored scopes, and
    /// classified stored collection rows.
    /// </returns>
    public static StoredSideExistenceLookupResult Build(
        JsonNode? storedDocument,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine
    )
    {
        if (storedDocument == null)
        {
            return new StoredSideExistenceLookupResult(new EmptyExistenceLookup(), [], []);
        }

        var scopesByJsonScope = scopeCatalog.ToDictionary(s => s.JsonScope);

        List<StoredScopeState> classifiedScopes = [];
        List<VisibleStoredCollectionRow> classifiedRows = [];
        HashSet<ScopeInstanceAddress> visibleScopeAddresses = new(ScopeInstanceAddressComparer.Instance);
        HashSet<string> walkedScopes = [];
        HashSet<CollectionRowAddress> visibleRowAddresses = new(CollectionRowAddressComparer.Instance);

        // Walk the stored document depth-first starting from root
        WalkScope(
            "$",
            storedDocument.AsObject(),
            [],
            classifier,
            addressEngine,
            scopesByJsonScope,
            classifiedScopes,
            classifiedRows,
            visibleScopeAddresses,
            walkedScopes,
            visibleRowAddresses
        );

        // Emit missing non-collection scope states for scopes not encountered during the walk
        EmitMissingScopeStates(classifier, addressEngine, scopesByJsonScope, classifiedScopes, walkedScopes);

        var lookup = new HashSetExistenceLookup(visibleScopeAddresses, visibleRowAddresses);

        return new StoredSideExistenceLookupResult(lookup, [.. classifiedScopes], [.. classifiedRows]);
    }

    // -----------------------------------------------------------------------
    //  Recursive walk
    // -----------------------------------------------------------------------

    /// <summary>
    /// Walks a non-collection scope: classifies visibility, derives address, emits
    /// StoredScopeState, then recurses into child scopes found in the JSON object.
    /// </summary>
    private static void WalkScope(
        string jsonScope,
        JsonObject source,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        List<VisibleStoredCollectionRow> classifiedRows,
        HashSet<ScopeInstanceAddress> visibleScopeAddresses,
        HashSet<string> walkedScopes,
        HashSet<CollectionRowAddress> visibleRowAddresses
    )
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, source);
        ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(jsonScope, ancestorItems);
        ImmutableArray<string> hiddenPaths = DeriveHiddenMemberPaths(
            jsonScope,
            visibility,
            classifier,
            scopesByJsonScope
        );

        classifiedScopes.Add(new StoredScopeState(address, visibility, hiddenPaths));
        walkedScopes.Add(jsonScope);

        if (visibility == ProfileVisibilityKind.VisiblePresent)
        {
            visibleScopeAddresses.Add(address);
        }

        // Walk child members
        WalkScopeMembers(
            jsonScope,
            source,
            ancestorItems,
            classifier,
            addressEngine,
            scopesByJsonScope,
            classifiedScopes,
            classifiedRows,
            visibleScopeAddresses,
            walkedScopes,
            visibleRowAddresses
        );
    }

    /// <summary>
    /// Walks the members of a scope's JSON object, handling non-collection child scopes,
    /// collection child scopes, and extensions.
    /// </summary>
    private static void WalkScopeMembers(
        string jsonScope,
        JsonObject source,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        List<VisibleStoredCollectionRow> classifiedRows,
        HashSet<ScopeInstanceAddress> visibleScopeAddresses,
        HashSet<string> walkedScopes,
        HashSet<CollectionRowAddress> visibleRowAddresses
    )
    {
        foreach (var kvp in source)
        {
            string memberName = kvp.Key;
            JsonNode? memberValue = kvp.Value;

            // Handle _ext (extensions)
            if (memberName == "_ext")
            {
                WalkExtensions(
                    jsonScope,
                    memberValue,
                    ancestorItems,
                    classifier,
                    addressEngine,
                    scopesByJsonScope,
                    classifiedScopes,
                    classifiedRows,
                    visibleScopeAddresses,
                    walkedScopes,
                    visibleRowAddresses
                );
                continue;
            }

            // Check if member is a non-collection scope
            string childNonCollectionScope = $"{jsonScope}.{memberName}";
            if (
                scopesByJsonScope.TryGetValue(childNonCollectionScope, out var childDesc)
                && childDesc.ScopeKind == ScopeKind.NonCollection
            )
            {
                WalkNonCollectionChild(
                    childNonCollectionScope,
                    memberValue,
                    ancestorItems,
                    classifier,
                    addressEngine,
                    scopesByJsonScope,
                    classifiedScopes,
                    classifiedRows,
                    visibleScopeAddresses,
                    walkedScopes,
                    visibleRowAddresses
                );
                continue;
            }

            // Check if member is a collection scope
            string childCollectionScope = $"{jsonScope}.{memberName}[*]";
            if (
                scopesByJsonScope.TryGetValue(childCollectionScope, out var collDesc)
                && collDesc.ScopeKind == ScopeKind.Collection
            )
            {
                WalkCollection(
                    childCollectionScope,
                    memberValue,
                    ancestorItems,
                    classifier,
                    addressEngine,
                    scopesByJsonScope,
                    classifiedScopes,
                    classifiedRows,
                    visibleScopeAddresses,
                    walkedScopes,
                    visibleRowAddresses
                );
            }

            // Scalar members are not addressed — skip
        }
    }

    /// <summary>
    /// Walks a non-collection child scope. Classifies visibility, derives address,
    /// and recurses into its members if VisiblePresent.
    /// </summary>
    private static void WalkNonCollectionChild(
        string jsonScope,
        JsonNode? scopeData,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        List<VisibleStoredCollectionRow> classifiedRows,
        HashSet<ScopeInstanceAddress> visibleScopeAddresses,
        HashSet<string> walkedScopes,
        HashSet<CollectionRowAddress> visibleRowAddresses
    )
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, scopeData);
        ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(jsonScope, ancestorItems);
        ImmutableArray<string> hiddenPaths = DeriveHiddenMemberPaths(
            jsonScope,
            visibility,
            classifier,
            scopesByJsonScope
        );

        classifiedScopes.Add(new StoredScopeState(address, visibility, hiddenPaths));
        walkedScopes.Add(jsonScope);

        if (visibility == ProfileVisibilityKind.VisiblePresent && scopeData != null)
        {
            visibleScopeAddresses.Add(address);

            WalkScopeMembers(
                jsonScope,
                scopeData.AsObject(),
                ancestorItems,
                classifier,
                addressEngine,
                scopesByJsonScope,
                classifiedScopes,
                classifiedRows,
                visibleScopeAddresses,
                walkedScopes,
                visibleRowAddresses
            );
        }
    }

    /// <summary>
    /// Walks a collection scope. If visible and present, iterates items, applies the
    /// item value filter, and derives CollectionRowAddress for passing items.
    /// </summary>
    private static void WalkCollection(
        string jsonScope,
        JsonNode? scopeData,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        List<VisibleStoredCollectionRow> classifiedRows,
        HashSet<ScopeInstanceAddress> visibleScopeAddresses,
        HashSet<string> walkedScopes,
        HashSet<CollectionRowAddress> visibleRowAddresses
    )
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, scopeData);

        if (visibility != ProfileVisibilityKind.VisiblePresent || scopeData == null)
        {
            return;
        }

        ImmutableArray<string> hiddenPaths = DeriveHiddenMemberPaths(
            jsonScope,
            visibility,
            classifier,
            scopesByJsonScope
        );

        JsonArray sourceArray = scopeData.AsArray();
        for (int i = 0; i < sourceArray.Count; i++)
        {
            JsonNode item = sourceArray[i]!;

            if (!classifier.PassesCollectionItemFilter(jsonScope, item))
            {
                // Item fails value filter — not visible, skip
                continue;
            }

            CollectionRowAddress rowAddress = addressEngine.DeriveCollectionRowAddress(
                jsonScope,
                item,
                ancestorItems
            );

            classifiedRows.Add(new VisibleStoredCollectionRow(rowAddress, hiddenPaths));
            visibleRowAddresses.Add(rowAddress);

            // Walk child non-collection scopes inside this collection item
            var newAncestors = new List<AncestorItemContext>(ancestorItems) { new(jsonScope, item) };
            HashSet<string> itemHandledChildScopes = [];

            foreach (var kvp in item.AsObject())
            {
                string itemMemberName = kvp.Key;
                JsonNode? itemMemberValue = kvp.Value;

                // Handle _ext inside collection items
                if (itemMemberName == "_ext")
                {
                    WalkExtensions(
                        jsonScope,
                        itemMemberValue,
                        newAncestors,
                        classifier,
                        addressEngine,
                        scopesByJsonScope,
                        classifiedScopes,
                        classifiedRows,
                        visibleScopeAddresses,
                        walkedScopes,
                        visibleRowAddresses,
                        itemHandledChildScopes
                    );
                    continue;
                }

                // Check for nested non-collection scope
                string nestedNonCollScope = $"{jsonScope}.{itemMemberName}";
                if (
                    scopesByJsonScope.TryGetValue(nestedNonCollScope, out var nestedDesc)
                    && nestedDesc.ScopeKind == ScopeKind.NonCollection
                )
                {
                    itemHandledChildScopes.Add(nestedNonCollScope);
                    WalkNonCollectionChild(
                        nestedNonCollScope,
                        itemMemberValue,
                        newAncestors,
                        classifier,
                        addressEngine,
                        scopesByJsonScope,
                        classifiedScopes,
                        classifiedRows,
                        visibleScopeAddresses,
                        walkedScopes,
                        visibleRowAddresses
                    );
                    continue;
                }

                // Check for nested collection scope
                string nestedCollScope = $"{jsonScope}.{itemMemberName}[*]";
                if (
                    scopesByJsonScope.TryGetValue(nestedCollScope, out var nestedCollDesc)
                    && nestedCollDesc.ScopeKind == ScopeKind.Collection
                )
                {
                    WalkCollection(
                        nestedCollScope,
                        itemMemberValue,
                        newAncestors,
                        classifier,
                        addressEngine,
                        scopesByJsonScope,
                        classifiedScopes,
                        classifiedRows,
                        visibleScopeAddresses,
                        walkedScopes,
                        visibleRowAddresses
                    );
                }
            }

            // Emit states for child non-collection scopes absent from this item
            EmitAbsentChildScopeStates(
                jsonScope,
                newAncestors,
                classifier,
                addressEngine,
                scopesByJsonScope,
                classifiedScopes,
                itemHandledChildScopes
            );
        }
    }

    /// <summary>
    /// Walks extension scopes under the _ext member of a parent scope.
    /// When <paramref name="handledChildScopes"/> is provided (collection item context),
    /// tracks which extension scopes were handled for absent-child emission.
    /// </summary>
    private static void WalkExtensions(
        string parentScope,
        JsonNode? extNode,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        List<VisibleStoredCollectionRow> classifiedRows,
        HashSet<ScopeInstanceAddress> visibleScopeAddresses,
        HashSet<string> walkedScopes,
        HashSet<CollectionRowAddress> visibleRowAddresses,
        HashSet<string>? handledChildScopes = null
    )
    {
        if (extNode == null)
        {
            return;
        }

        foreach (var kvp in extNode.AsObject())
        {
            string extName = kvp.Key;
            JsonNode? extData = kvp.Value;

            string extScope = $"{parentScope}._ext.{extName}";

            if (!scopesByJsonScope.ContainsKey(extScope))
            {
                continue;
            }

            handledChildScopes?.Add(extScope);

            WalkNonCollectionChild(
                extScope,
                extData,
                ancestorItems,
                classifier,
                addressEngine,
                scopesByJsonScope,
                classifiedScopes,
                classifiedRows,
                visibleScopeAddresses,
                walkedScopes,
                visibleRowAddresses
            );
        }
    }

    // -----------------------------------------------------------------------
    //  Missing / absent scope state emission
    // -----------------------------------------------------------------------

    /// <summary>
    /// Emits StoredScopeState for child non-collection scopes of the given
    /// parent scope that were not encountered during a collection item walk.
    /// Recurses into each absent child to emit descendant states.
    /// </summary>
    private static void EmitAbsentChildScopeStates(
        string parentScope,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        HashSet<string> handledChildScopes
    )
    {
        foreach (string childScope in classifier.GetChildNonCollectionScopes(parentScope))
        {
            if (handledChildScopes.Contains(childScope))
            {
                continue;
            }

            ProfileVisibilityKind visibility = classifier.ClassifyScope(childScope, null);
            ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(
                childScope,
                ancestorItems
            );
            ImmutableArray<string> hiddenPaths = DeriveHiddenMemberPaths(
                childScope,
                visibility,
                classifier,
                scopesByJsonScope
            );

            classifiedScopes.Add(new StoredScopeState(address, visibility, hiddenPaths));

            // Recursively emit for this absent scope's own children
            EmitAbsentChildScopeStates(
                childScope,
                ancestorItems,
                classifier,
                addressEngine,
                scopesByJsonScope,
                classifiedScopes,
                []
            );
        }
    }

    /// <summary>
    /// Emits StoredScopeState for non-collection scopes not encountered during the
    /// stored document walk. These represent scopes that are absent from the stored
    /// document. Skips scopes nested inside collections — those are handled per-item
    /// by <see cref="EmitAbsentChildScopeStates"/> during the collection walk.
    /// </summary>
    private static void EmitMissingScopeStates(
        ProfileVisibilityClassifier classifier,
        AddressDerivationEngine addressEngine,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope,
        List<StoredScopeState> classifiedScopes,
        HashSet<string> emittedScopeJsonScopes
    )
    {
        foreach (string jsonScope in classifier.AllScopeJsonScopes)
        {
            if (emittedScopeJsonScopes.Contains(jsonScope))
            {
                continue;
            }

            ScopeKind kind = classifier.GetScopeKind(jsonScope);
            if (kind == ScopeKind.Collection)
            {
                continue;
            }

            // Scopes nested inside collections require ancestor item context
            if (addressEngine.HasCollectionAncestors(jsonScope))
            {
                continue;
            }

            ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, null);
            ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(jsonScope, []);
            ImmutableArray<string> hiddenPaths = DeriveHiddenMemberPaths(
                jsonScope,
                visibility,
                classifier,
                scopesByJsonScope
            );

            classifiedScopes.Add(new StoredScopeState(address, visibility, hiddenPaths));
        }
    }

    // -----------------------------------------------------------------------
    //  HiddenMemberPaths derivation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Derives the hidden member paths for a scope by checking each canonical
    /// scope-relative member path against the profile's member filter.
    /// Hidden scopes emit ALL canonical member paths as hidden — the entire
    /// scope is preserved by backend.
    /// </summary>
    private static ImmutableArray<string> DeriveHiddenMemberPaths(
        string jsonScope,
        ProfileVisibilityKind visibility,
        ProfileVisibilityClassifier classifier,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope
    )
    {
        if (!scopesByJsonScope.TryGetValue(jsonScope, out var descriptor))
        {
            return [];
        }

        // Hidden scopes have ALL members hidden — backend preserves the entire scope.
        if (visibility == ProfileVisibilityKind.Hidden)
        {
            return [.. descriptor.CanonicalScopeRelativeMemberPaths];
        }

        ScopeMemberFilter filter = classifier.GetMemberFilter(jsonScope);

        // If IncludeAll and scope is visible, nothing is hidden
        if (filter.Mode == MemberSelection.IncludeAll)
        {
            return [];
        }

        ImmutableArray<string>.Builder? hidden = null;

        foreach (string memberPath in descriptor.CanonicalScopeRelativeMemberPaths)
        {
            bool isVisible = MemberPathVisibility.IsVisible(filter, memberPath);

            if (!isVisible)
            {
                hidden ??= ImmutableArray.CreateBuilder<string>();
                hidden.Add(memberPath);
            }
        }

        return hidden?.ToImmutable() ?? [];
    }

    // -----------------------------------------------------------------------
    //  Lookup implementations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Empty lookup for create flows where there is no stored document.
    /// </summary>
    private sealed class EmptyExistenceLookup : IStoredSideExistenceLookup
    {
        public bool VisibleScopeExistsAt(ScopeInstanceAddress address) => false;

        public bool VisibleCollectionRowExistsAt(CollectionRowAddress address) => false;
    }

    /// <summary>
    /// Hash-set-based lookup for update flows. Uses <see cref="ScopeInstanceAddressComparer"/>
    /// for structural comparison of non-collection scope addresses and
    /// <see cref="CollectionRowAddressComparer"/> for collection rows.
    /// </summary>
    private sealed class HashSetExistenceLookup(
        HashSet<ScopeInstanceAddress> visibleScopeAddresses,
        HashSet<CollectionRowAddress> visibleRowAddresses
    ) : IStoredSideExistenceLookup
    {
        public bool VisibleScopeExistsAt(ScopeInstanceAddress address) =>
            visibleScopeAddresses.Contains(address);

        public bool VisibleCollectionRowExistsAt(CollectionRowAddress address) =>
            visibleRowAddresses.Contains(address);
    }
}
