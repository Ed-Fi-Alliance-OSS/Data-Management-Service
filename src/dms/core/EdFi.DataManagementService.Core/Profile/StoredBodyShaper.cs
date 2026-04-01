// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Recursive JSON walker that filters a stored document through writable-profile
/// visibility rules. Parallel in structure to <see cref="WritableRequestShaper"/>
/// but simpler: no validation failures, no address derivation, no scope-state
/// emission. Stored data is trusted.
/// </summary>
internal sealed class StoredBodyShaper(ProfileVisibilityClassifier classifier)
{
    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shapes the given stored document according to the writable profile,
    /// producing a filtered JSON output with hidden members stripped and
    /// collection items that fail value filters silently excluded.
    /// </summary>
    public JsonNode Shape(JsonNode storedDocument)
    {
        return ShapeScope("$", storedDocument.AsObject());
    }

    // -----------------------------------------------------------------------
    //  Core recursive shaping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shapes a single scope (root or non-collection child) by classifying
    /// visibility and delegating member walking to <see cref="ShapeScopeMembers"/>.
    /// </summary>
    private JsonObject ShapeScope(string jsonScope, JsonObject source)
    {
        // Classify scope — stored data is trusted, so no state emission needed
        classifier.ClassifyScope(jsonScope, source);

        return ShapeScopeMembers(jsonScope, source);
    }

    /// <summary>
    /// Walks the members of a scope's JSON object, recursively handling
    /// non-collection child scopes, collection child scopes, extensions,
    /// and scalar members.
    /// </summary>
    private JsonObject ShapeScopeMembers(string jsonScope, JsonObject source)
    {
        ScopeMemberFilter memberFilter = classifier.GetMemberFilter(jsonScope);
        var output = new JsonObject();

        foreach (var kvp in source)
        {
            string memberName = kvp.Key;
            JsonNode? memberValue = kvp.Value;

            // Handle _ext (extensions)
            if (memberName == "_ext")
            {
                JsonObject? shapedExt = ShapeExtensions(jsonScope, memberValue);
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
                ShapeNonCollectionChild(childNonCollectionScope, memberValue, output, memberName);
                continue;
            }

            // Check if member is a collection scope
            string childCollectionScope = $"{jsonScope}.{memberName}[*]";
            if (
                IsScopeKnown(childCollectionScope, out ScopeKind collKind)
                && collKind == ScopeKind.Collection
            )
            {
                ShapeCollection(childCollectionScope, memberValue, output, memberName);
                continue;
            }

            // Scalar member: apply member filter
            if (IsMemberVisible(memberFilter, memberName))
            {
                output[memberName] = memberValue?.DeepClone();
            }
            // Hidden scalars are silently dropped — no validation failures for stored data
        }

        return output;
    }

    /// <summary>
    /// Shapes a non-collection child scope. Classifies visibility; if
    /// <see cref="ProfileVisibilityKind.VisiblePresent"/> and data is present,
    /// recurses into members and includes in output. Otherwise omits entirely.
    /// </summary>
    private void ShapeNonCollectionChild(
        string jsonScope,
        JsonNode? scopeData,
        JsonObject output,
        string memberName
    )
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, scopeData);

        if (visibility == ProfileVisibilityKind.VisiblePresent && scopeData != null)
        {
            output[memberName] = ShapeScopeMembers(jsonScope, scopeData.AsObject());
        }
        // Hidden or absent scopes are silently omitted — no validation failures for stored data
    }

    /// <summary>
    /// Shapes a collection scope. If Hidden, omits entirely. If VisibleAbsent
    /// or null data, emits empty array. If VisiblePresent, iterates items,
    /// silently skips failing collection item filters, filters members and
    /// recurses into nested scopes for passing items.
    /// </summary>
    private void ShapeCollection(string jsonScope, JsonNode? scopeData, JsonObject output, string memberName)
    {
        ProfileVisibilityKind visibility = classifier.ClassifyScope(jsonScope, scopeData);

        if (visibility == ProfileVisibilityKind.Hidden)
        {
            // Hidden collection — omit entirely
            return;
        }

        if (visibility == ProfileVisibilityKind.VisibleAbsent || scopeData == null)
        {
            // Visible but absent — emit empty array
            output[memberName] = new JsonArray();
            return;
        }

        // VisiblePresent — iterate items
        ScopeMemberFilter itemMemberFilter = classifier.GetMemberFilter(jsonScope);
        var outputArray = new JsonArray();

        JsonArray sourceArray = scopeData.AsArray();
        for (int i = 0; i < sourceArray.Count; i++)
        {
            JsonNode item = sourceArray[i]!;

            // Silently exclude items that fail the value filter
            if (!classifier.PassesCollectionItemFilter(jsonScope, item))
            {
                continue;
            }

            // Build filtered item
            var filteredItem = new JsonObject();
            JsonObject itemObj = item.AsObject();

            foreach (var kvp in itemObj)
            {
                string itemMemberName = kvp.Key;
                JsonNode? itemMemberValue = kvp.Value;

                // Handle _ext (extensions within collection items)
                if (itemMemberName == "_ext")
                {
                    JsonObject? shapedExt = ShapeExtensions(jsonScope, itemMemberValue);
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
                    ShapeNonCollectionChild(
                        nestedNonCollScope,
                        itemMemberValue,
                        filteredItem,
                        itemMemberName
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
                    ShapeCollection(nestedCollScope, itemMemberValue, filteredItem, itemMemberName);
                    continue;
                }

                // Scalar: apply member filter
                if (IsMemberVisible(itemMemberFilter, itemMemberName))
                {
                    filteredItem[itemMemberName] = itemMemberValue?.DeepClone();
                }
                // Hidden scalars are silently dropped — no validation failures for stored data
            }

            outputArray.Add(filteredItem);
        }

        output[memberName] = outputArray;
    }

    /// <summary>
    /// Shapes extensions under the _ext member of a scope. Iterates extension
    /// scope members, classifying each; if <see cref="ProfileVisibilityKind.VisiblePresent"/>,
    /// recurses into members. Otherwise omits the extension.
    /// </summary>
    private JsonObject? ShapeExtensions(string parentScope, JsonNode? extNode)
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

            ProfileVisibilityKind visibility = classifier.ClassifyScope(extScope, extData);

            if (visibility == ProfileVisibilityKind.VisiblePresent && extData != null)
            {
                extOutput[extName] = ShapeScopeMembers(extScope, extData.AsObject());
            }
            // Hidden or absent extensions are silently omitted
        }

        return extOutput;
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
