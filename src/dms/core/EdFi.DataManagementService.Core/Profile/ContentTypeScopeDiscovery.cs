// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Discovers all JSON scope paths from a <see cref="ContentTypeDefinition"/> tree that
/// are not already present in a given set of known scopes. Used to find inlined (non-table-backed)
/// scopes that the profile tree defines but the relational plan does not assign a table to.
/// </summary>
internal static class ContentTypeScopeDiscovery
{
    /// <summary>
    /// Walks the content type tree and returns scope paths (with their kinds) that are
    /// not already in <paramref name="knownScopes"/>.
    /// </summary>
    public static IReadOnlyList<(string JsonScope, ScopeKind Kind)> DiscoverInlinedScopes(
        ContentTypeDefinition contentType,
        IReadOnlySet<string> knownScopes
    )
    {
        List<(string JsonScope, ScopeKind Kind)> discovered = [];
        WalkObjects("$", contentType.Objects, knownScopes, discovered);
        WalkCollections("$", contentType.Collections, knownScopes, discovered);
        WalkExtensions("$", contentType.Extensions, knownScopes, discovered);
        return discovered;
    }

    private static void WalkObjects(
        string parentScope,
        IReadOnlyList<ObjectRule>? objects,
        IReadOnlySet<string> knownScopes,
        List<(string JsonScope, ScopeKind Kind)> discovered
    )
    {
        if (objects is null)
        {
            return;
        }

        foreach (var obj in objects)
        {
            var scope = $"{parentScope}.{obj.Name}";
            if (!knownScopes.Contains(scope))
            {
                discovered.Add((scope, ScopeKind.NonCollection));
            }

            WalkObjects(scope, obj.NestedObjects, knownScopes, discovered);
            WalkCollections(scope, obj.Collections, knownScopes, discovered);
            WalkExtensions(scope, obj.Extensions, knownScopes, discovered);
        }
    }

    private static void WalkCollections(
        string parentScope,
        IReadOnlyList<CollectionRule>? collections,
        IReadOnlySet<string> knownScopes,
        List<(string JsonScope, ScopeKind Kind)> discovered
    )
    {
        if (collections is null)
        {
            return;
        }

        foreach (var coll in collections)
        {
            var scope = $"{parentScope}.{coll.Name}[*]";
            if (!knownScopes.Contains(scope))
            {
                discovered.Add((scope, ScopeKind.Collection));
            }

            WalkObjects(scope, coll.NestedObjects, knownScopes, discovered);
            WalkCollections(scope, coll.NestedCollections, knownScopes, discovered);
            WalkExtensions(scope, coll.Extensions, knownScopes, discovered);
        }
    }

    private static void WalkExtensions(
        string parentScope,
        IReadOnlyList<ExtensionRule>? extensions,
        IReadOnlySet<string> knownScopes,
        List<(string JsonScope, ScopeKind Kind)> discovered
    )
    {
        if (extensions is null)
        {
            return;
        }

        foreach (var ext in extensions)
        {
            var scope = $"{parentScope}._ext.{ext.Name}";
            if (!knownScopes.Contains(scope))
            {
                discovered.Add((scope, ScopeKind.NonCollection));
            }

            WalkObjects(scope, ext.Objects, knownScopes, discovered);
            WalkCollections(scope, ext.Collections, knownScopes, discovered);
        }
    }
}
