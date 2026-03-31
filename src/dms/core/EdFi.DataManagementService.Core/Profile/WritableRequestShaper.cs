// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// The result of shaping a writable request body through a profile.
/// Contains the filtered JSON, scope state emissions, visible collection
/// items, and any validation failures for items that failed value filters.
/// </summary>
/// <param name="WritableRequestBody">
/// The shaped request body with hidden members stripped.
/// </param>
/// <param name="RequestScopeStates">
/// Scope state for each non-collection scope, with Creatable initially false.
/// </param>
/// <param name="VisibleRequestCollectionItems">
/// Visible collection items that passed value filters, with Creatable initially false.
/// </param>
/// <param name="ValidationFailures">
/// Category-3 failures for collection items that failed value filters.
/// </param>
public sealed record WritableRequestShapingResult(
    JsonNode WritableRequestBody,
    ImmutableArray<RequestScopeState> RequestScopeStates,
    ImmutableArray<VisibleRequestCollectionItem> VisibleRequestCollectionItems,
    ImmutableArray<WritableProfileValidationFailure> ValidationFailures
);

/// <summary>
/// Walks a request body once, building a shaped JSON output while emitting
/// <see cref="RequestScopeState"/> and <see cref="VisibleRequestCollectionItem"/>
/// entries, and collecting validation failures for collection items that fail
/// value filters. Consumes <see cref="ProfileVisibilityClassifier"/> and
/// <see cref="AddressDerivationEngine"/>.
/// </summary>
public sealed class WritableRequestShaper(
    ProfileVisibilityClassifier classifier,
    AddressDerivationEngine addressEngine,
    string profileName,
    string resourceName,
    string method,
    string operation
)
{
    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shapes the given request body according to the writable profile,
    /// performing a single-pass walk of the JSON document.
    /// </summary>
    public WritableRequestShapingResult Shape(JsonNode requestBody)
    {
        List<RequestScopeState> scopeStates = [];
        List<VisibleRequestCollectionItem> collectionItems = [];
        List<WritableProfileValidationFailure> validationFailures = [];
        HashSet<string> emittedScopes = [];

        JsonObject shapedRoot = ShapeScope(
            "$",
            requestBody.AsObject(),
            [],
            scopeStates,
            collectionItems,
            validationFailures,
            emittedScopes
        );

        // Emit missing non-collection scope states for scopes not encountered during the walk
        EmitMissingScopeStates(scopeStates, emittedScopes);

        return new WritableRequestShapingResult(
            shapedRoot,
            [.. scopeStates],
            [.. collectionItems],
            [.. validationFailures]
        );
    }

    // -----------------------------------------------------------------------
    //  Core recursive shaping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shapes a single scope (root or non-collection child) by classifying
    /// visibility, emitting its RequestScopeState, and delegating member
    /// walking to <see cref="ShapeScopeMembers"/>.
    /// </summary>
    private JsonObject ShapeScope(
        string jsonScope,
        JsonObject source,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> validationFailures,
        HashSet<string> emittedScopes
    )
    {
        // Classify and emit scope state
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, source);
        ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(jsonScope, ancestorItems);
        scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
        emittedScopes.Add(jsonScope);

        return ShapeScopeMembers(
            jsonScope,
            source,
            ancestorItems,
            scopeStates,
            collectionItems,
            validationFailures,
            emittedScopes
        );
    }

    /// <summary>
    /// Walks the members of a scope's JSON object, recursively handling
    /// non-collection child scopes, collection child scopes, extensions,
    /// and scalar members. After the walk, emits absent-child scope states
    /// for any known child non-collection scopes not encountered.
    /// </summary>
    private JsonObject ShapeScopeMembers(
        string jsonScope,
        JsonObject source,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> validationFailures,
        HashSet<string> emittedScopes
    )
    {
        ScopeMemberFilter memberFilter = classifier.GetMemberFilter(jsonScope);
        var output = new JsonObject();
        HashSet<string> handledChildScopes = [];

        foreach (var kvp in source)
        {
            string memberName = kvp.Key;
            JsonNode? memberValue = kvp.Value;

            // Handle _ext (extensions)
            if (memberName == "_ext")
            {
                JsonObject? shapedExt = ShapeExtensions(
                    jsonScope,
                    memberValue,
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    validationFailures,
                    emittedScopes,
                    handledChildScopes
                );
                if (shapedExt != null && shapedExt.Count > 0)
                {
                    output["_ext"] = shapedExt;
                }
                continue;
            }

            // Check if member is a non-collection scope
            string childNonCollectionScope = $"{jsonScope}.{memberName}";
            if (
                IsScopeKnown(childNonCollectionScope, out ScopeKind childKind)
                && childKind == ScopeKind.NonCollection
            )
            {
                handledChildScopes.Add(childNonCollectionScope);
                ShapeNonCollectionChild(
                    childNonCollectionScope,
                    memberValue,
                    ancestorItems,
                    output,
                    memberName,
                    scopeStates,
                    collectionItems,
                    validationFailures,
                    emittedScopes
                );
                continue;
            }

            // Check if member is a collection scope
            string childCollectionScope = $"{jsonScope}.{memberName}[*]";
            if (
                IsScopeKnown(childCollectionScope, out ScopeKind collKind)
                && collKind == ScopeKind.Collection
            )
            {
                ShapeCollection(
                    childCollectionScope,
                    memberValue,
                    ancestorItems,
                    output,
                    memberName,
                    scopeStates,
                    collectionItems,
                    validationFailures,
                    emittedScopes
                );
                continue;
            }

            // Scalar member: apply member filter
            if (IsMemberVisible(memberFilter, memberName))
            {
                output[memberName] = memberValue?.DeepClone();
            }
        }

        // Emit states for child non-collection scopes not encountered during the walk
        EmitAbsentChildScopeStates(jsonScope, ancestorItems, scopeStates, emittedScopes, handledChildScopes);

        return output;
    }

    /// <summary>
    /// Shapes a non-collection child scope. Classifies visibility, derives
    /// address, emits RequestScopeState. If VisiblePresent, delegates to
    /// <see cref="ShapeScopeMembers"/> for recursive member handling. If
    /// absent or hidden, recursively emits states for descendant scopes.
    /// </summary>
    private void ShapeNonCollectionChild(
        string jsonScope,
        JsonNode? scopeData,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        JsonObject output,
        string memberName,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> validationFailures,
        HashSet<string> emittedScopes
    )
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, scopeData);
        ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(jsonScope, ancestorItems);
        scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
        emittedScopes.Add(jsonScope);

        if (visibility == ProfileVisibilityKind.VisiblePresent && scopeData != null)
        {
            output[memberName] = ShapeScopeMembers(
                jsonScope,
                scopeData.AsObject(),
                ancestorItems,
                scopeStates,
                collectionItems,
                validationFailures,
                emittedScopes
            );
        }
        else
        {
            // Scope is absent or hidden — recursively emit states for descendant
            // non-collection scopes that cannot be encountered during the walk.
            EmitAbsentChildScopeStates(jsonScope, ancestorItems, scopeStates, emittedScopes, []);
        }
    }

    /// <summary>
    /// Shapes a collection scope. Classifies visibility. If VisiblePresent,
    /// iterates items, checking value filters, emitting
    /// VisibleRequestCollectionItem for passing items and failures for failing
    /// items. After each item's member walk, emits states for absent child
    /// non-collection scopes.
    /// </summary>
    private void ShapeCollection(
        string jsonScope,
        JsonNode? scopeData,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        JsonObject output,
        string memberName,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> validationFailures,
        HashSet<string> emittedScopes
    )
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, scopeData);

        // For collections, emit as a scope state for missing-scope detection
        // (collections are tracked via emittedScopes but not emitted as RequestScopeState)
        emittedScopes.Add(jsonScope);

        if (visibility != ProfileVisibilityKind.VisiblePresent || scopeData == null)
        {
            // If VisibleAbsent, emit the array key with empty array to match expectations
            if (visibility == ProfileVisibilityKind.VisibleAbsent)
            {
                output[memberName] = new JsonArray();
            }
            return;
        }

        ScopeMemberFilter itemMemberFilter = classifier.GetMemberFilter(jsonScope);
        var outputArray = new JsonArray();

        JsonArray sourceArray = scopeData.AsArray();
        for (int i = 0; i < sourceArray.Count; i++)
        {
            JsonNode item = sourceArray[i]!;

            if (!classifier.PassesCollectionItemFilter(jsonScope, item))
            {
                // Item fails value filter — create ForbiddenSubmittedData failure
                validationFailures.Add(
                    ProfileFailures.ForbiddenSubmittedData(
                        profileName: profileName,
                        resourceName: resourceName,
                        method: method,
                        operation: operation,
                        jsonScope: jsonScope,
                        scopeKind: ScopeKind.Collection,
                        requestJsonPaths: [$"{memberName}[{i}]"],
                        forbiddenCanonicalMemberPaths: []
                    )
                );
                continue;
            }

            // Item passes filter — derive address and emit
            CollectionRowAddress rowAddress = addressEngine.DeriveCollectionRowAddress(
                jsonScope,
                item,
                ancestorItems
            );
            collectionItems.Add(new VisibleRequestCollectionItem(rowAddress, Creatable: false));

            // Build filtered item
            var filteredItem = new JsonObject();
            JsonObject itemObj = item.AsObject();

            // Build ancestor context for potential nested scopes
            var newAncestors = new List<AncestorItemContext>(ancestorItems) { new(jsonScope, item) };

            // Track which child non-collection scopes are handled for this item
            HashSet<string> itemHandledChildScopes = [];

            foreach (var kvp in itemObj)
            {
                string itemMemberName = kvp.Key;
                JsonNode? itemMemberValue = kvp.Value;

                // Handle _ext (extensions within collection items)
                if (itemMemberName == "_ext")
                {
                    JsonObject? shapedExt = ShapeExtensions(
                        jsonScope,
                        itemMemberValue,
                        newAncestors,
                        scopeStates,
                        collectionItems,
                        validationFailures,
                        emittedScopes,
                        itemHandledChildScopes
                    );
                    if (shapedExt != null && shapedExt.Count > 0)
                    {
                        filteredItem["_ext"] = shapedExt;
                    }
                    continue;
                }

                // Check for nested non-collection scope
                string nestedNonCollScope = $"{jsonScope}.{itemMemberName}";
                if (
                    IsScopeKnown(nestedNonCollScope, out ScopeKind nestedKind)
                    && nestedKind == ScopeKind.NonCollection
                )
                {
                    itemHandledChildScopes.Add(nestedNonCollScope);
                    ShapeNonCollectionChild(
                        nestedNonCollScope,
                        itemMemberValue,
                        newAncestors,
                        filteredItem,
                        itemMemberName,
                        scopeStates,
                        collectionItems,
                        validationFailures,
                        emittedScopes
                    );
                    continue;
                }

                // Check for nested collection scope
                string nestedCollScope = $"{jsonScope}.{itemMemberName}[*]";
                if (
                    IsScopeKnown(nestedCollScope, out ScopeKind nestedCollKind)
                    && nestedCollKind == ScopeKind.Collection
                )
                {
                    ShapeCollection(
                        nestedCollScope,
                        itemMemberValue,
                        newAncestors,
                        filteredItem,
                        itemMemberName,
                        scopeStates,
                        collectionItems,
                        validationFailures,
                        emittedScopes
                    );
                    continue;
                }

                // Scalar: apply member filter
                if (IsMemberVisible(itemMemberFilter, itemMemberName))
                {
                    filteredItem[itemMemberName] = itemMemberValue?.DeepClone();
                }
            }

            // Emit states for child non-collection scopes absent from this item
            EmitAbsentChildScopeStates(
                jsonScope,
                newAncestors,
                scopeStates,
                emittedScopes,
                itemHandledChildScopes
            );

            outputArray.Add(filteredItem);
        }

        output[memberName] = outputArray;
    }

    /// <summary>
    /// Shapes extensions under the _ext member of a scope. Iterates extension
    /// scope members, classifying and emitting scope state for each. When
    /// VisiblePresent, delegates to <see cref="ShapeScopeMembers"/> for
    /// recursive inner member handling. When absent or hidden, recursively
    /// emits descendant states.
    /// </summary>
    private JsonObject? ShapeExtensions(
        string parentScope,
        JsonNode? extNode,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> validationFailures,
        HashSet<string> emittedScopes,
        HashSet<string> handledChildScopes
    )
    {
        if (extNode == null)
        {
            return null;
        }

        var extOutput = new JsonObject();

        foreach (var kvp in extNode.AsObject())
        {
            string extName = kvp.Key;
            JsonNode? extData = kvp.Value;

            string extScope = $"{parentScope}._ext.{extName}";

            if (!IsScopeKnown(extScope, out _))
            {
                // Not a known scope — skip
                continue;
            }

            handledChildScopes.Add(extScope);

            ProfileVisibilityKind visibility = classifier.ClassifyScope(extScope, extData);
            ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(extScope, ancestorItems);
            scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
            emittedScopes.Add(extScope);

            if (visibility == ProfileVisibilityKind.VisiblePresent && extData != null)
            {
                extOutput[extName] = ShapeScopeMembers(
                    extScope,
                    extData.AsObject(),
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    validationFailures,
                    emittedScopes
                );
            }
            else
            {
                // Extension scope is absent or hidden — emit descendant states
                EmitAbsentChildScopeStates(extScope, ancestorItems, scopeStates, emittedScopes, []);
            }
        }

        return extOutput;
    }

    // -----------------------------------------------------------------------
    //  Missing scope state emission
    // -----------------------------------------------------------------------

    /// <summary>
    /// Emits RequestScopeState for child non-collection scopes of the given
    /// parent scope that were not encountered during the JSON walk. Recurses
    /// into each absent child to emit descendant states.
    /// </summary>
    private void EmitAbsentChildScopeStates(
        string parentScope,
        IReadOnlyList<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        HashSet<string> emittedScopes,
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
            scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
            emittedScopes.Add(childScope);

            // Recursively emit for this absent scope's own children
            EmitAbsentChildScopeStates(childScope, ancestorItems, scopeStates, emittedScopes, []);
        }
    }

    /// <summary>
    /// After the walk, emits RequestScopeState for any non-collection scope
    /// in the classifier's catalog that was not encountered during the walk.
    /// These are VisibleAbsent or Hidden depending on the classifier.
    /// </summary>
    /// <remarks>
    /// Scopes nested inside collections (those with collection ancestors) are skipped
    /// because their addresses require concrete ancestor item context that is only
    /// available during the collection item walk. If they were not emitted during the
    /// walk, it means the parent collection was absent or empty — there are no instances
    /// to address.
    /// </remarks>
    private void EmitMissingScopeStates(List<RequestScopeState> scopeStates, HashSet<string> emittedScopes)
    {
        foreach (string jsonScope in classifier.AllScopeJsonScopes)
        {
            if (emittedScopes.Contains(jsonScope))
            {
                continue;
            }

            ScopeKind kind = classifier.GetScopeKind(jsonScope);
            if (kind == ScopeKind.Collection)
            {
                continue;
            }

            // Scopes nested inside collections require ancestor item context for address
            // derivation. They should have been emitted during the collection item walk.
            if (addressEngine.HasCollectionAncestors(jsonScope))
            {
                continue;
            }

            // Classify with null data (absent from request)
            ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, null);
            ScopeInstanceAddress address = addressEngine.DeriveScopeInstanceAddress(jsonScope, []);
            scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
            emittedScopes.Add(jsonScope);
        }
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Checks whether the given jsonScope exists in the classifier's scope catalog.
    /// </summary>
    private bool IsScopeKnown(string jsonScope, out ScopeKind scopeKind)
    {
        if (!classifier.ContainsScope(jsonScope))
        {
            scopeKind = default;
            return false;
        }

        scopeKind = classifier.GetScopeKind(jsonScope);
        return true;
    }

    /// <summary>
    /// Determines whether a member is visible given the scope's member filter.
    /// </summary>
    private static bool IsMemberVisible(ScopeMemberFilter filter, string name)
    {
        return filter.Mode switch
        {
            MemberSelection.IncludeOnly => filter.ExplicitNames.Contains(name),
            MemberSelection.ExcludeOnly => !filter.ExplicitNames.Contains(name),
            MemberSelection.IncludeAll => true,
            _ => true,
        };
    }
}
