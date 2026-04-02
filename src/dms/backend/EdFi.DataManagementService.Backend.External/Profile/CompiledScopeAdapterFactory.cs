// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.External.Profile;

/// <summary>
/// Builds a <see cref="CompiledScopeDescriptor"/> array from a <see cref="ResourceWritePlan"/>,
/// bridging backend relational plan types into Core's profile address derivation vocabulary.
/// </summary>
public static class CompiledScopeAdapterFactory
{
    /// <summary>
    /// Builds compiled scope descriptors from the given <see cref="ResourceWritePlan"/>.
    /// </summary>
    public static CompiledScopeDescriptor[] BuildFromWritePlan(ResourceWritePlan plan)
    {
        // Build a lookup of JsonScope canonical string -> ScopeKind for parent resolution
        var scopeKindByCanonical = plan
            .TablePlansInDependencyOrder.Select(tp => tp.TableModel)
            .ToDictionary(tm => tm.JsonScope.Canonical, tm => ToScopeKind(tm.IdentityMetadata.TableKind));

        return [.. plan.TablePlansInDependencyOrder.Select(tp => BuildDescriptor(tp, scopeKindByCanonical))];
    }

    private static CompiledScopeDescriptor BuildDescriptor(
        TableWritePlan tablePlan,
        IReadOnlyDictionary<string, ScopeKind> scopeKindByCanonical
    )
    {
        var tableModel = tablePlan.TableModel;
        var jsonScopeCanonical = tableModel.JsonScope.Canonical;
        var scopeKind = ToScopeKind(tableModel.IdentityMetadata.TableKind);

        var immediateParentJsonScope = ResolveImmediateParentJsonScope(
            jsonScopeCanonical,
            scopeKindByCanonical
        );

        var collectionAncestorsInOrder = BuildCollectionAncestors(
            immediateParentJsonScope,
            scopeKindByCanonical
        );

        var semanticIdentityPaths = BuildSemanticIdentityPaths(tablePlan.CollectionMergePlan);

        var canonicalMemberPaths = BuildCanonicalMemberPaths(tableModel);

        return new CompiledScopeDescriptor(
            JsonScope: jsonScopeCanonical,
            ScopeKind: scopeKind,
            ImmediateParentJsonScope: immediateParentJsonScope,
            CollectionAncestorsInOrder: collectionAncestorsInOrder,
            SemanticIdentityRelativePathsInOrder: semanticIdentityPaths,
            CanonicalScopeRelativeMemberPaths: canonicalMemberPaths
        );
    }

    /// <summary>
    /// Maps a <see cref="DbTableKind"/> to its corresponding <see cref="ScopeKind"/>.
    /// </summary>
    private static ScopeKind ToScopeKind(DbTableKind tableKind) =>
        tableKind switch
        {
            DbTableKind.Root => ScopeKind.Root,
            DbTableKind.Collection or DbTableKind.ExtensionCollection => ScopeKind.Collection,
            _ => ScopeKind.NonCollection,
        };

    /// <summary>
    /// Resolves the immediate parent JSON scope by walking back through the scope path segments
    /// and finding the closest ancestor that exists in the table set. Returns null for root.
    /// </summary>
    private static string? ResolveImmediateParentJsonScope(
        string jsonScopeCanonical,
        IReadOnlyDictionary<string, ScopeKind> scopeKindByCanonical
    )
    {
        if (jsonScopeCanonical == "$")
        {
            return null;
        }

        // Split on '.' and walk back segment by segment to find the closest ancestor
        // that is in the table set.
        // e.g. "$.addresses[*]._ext.sample" -> try "$._ext" (not in set), try "$.addresses[*]" (in set)
        // We need to reconstruct candidates by stripping the last segment each time.
        var segments = jsonScopeCanonical.Split('.');

        // Try progressively shorter paths by removing the last dot-segment
        for (var len = segments.Length - 1; len >= 1; len--)
        {
            var candidate = string.Join(".", segments[..len]);
            if (scopeKindByCanonical.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        // Fall back to root
        return "$";
    }

    /// <summary>
    /// Builds the ordered list of collection ancestor JSON scopes, from root-most to
    /// the immediate parent collection ancestor (exclusive of the current scope itself).
    /// </summary>
    private static ImmutableArray<string> BuildCollectionAncestors(
        string? immediateParentJsonScope,
        IReadOnlyDictionary<string, ScopeKind> scopeKindByCanonical
    )
    {
        if (immediateParentJsonScope is null)
        {
            return [];
        }

        // Walk up from immediate parent and collect all collection-kinded ancestors
        var collectionAncestors = new List<string>();
        var current = immediateParentJsonScope;

        while (current is not null)
        {
            if (scopeKindByCanonical.TryGetValue(current, out var kind) && kind == ScopeKind.Collection)
            {
                collectionAncestors.Add(current);
            }

            current = ResolveImmediateParentJsonScope(current, scopeKindByCanonical);
        }

        // Ancestors were collected child-most-first; reverse to root-most-first order
        collectionAncestors.Reverse();
        return [.. collectionAncestors];
    }

    /// <summary>
    /// Extracts semantic identity relative paths from the collection merge plan.
    /// Returns empty for non-collection scopes.
    /// </summary>
    private static ImmutableArray<string> BuildSemanticIdentityPaths(CollectionMergePlan? collectionMergePlan)
    {
        if (collectionMergePlan is null || collectionMergePlan.SemanticIdentityBindings.Length == 0)
        {
            return [];
        }

        return [.. collectionMergePlan.SemanticIdentityBindings.Select(b => b.RelativePath.Canonical)];
    }

    /// <summary>
    /// Extracts canonical scope-relative member paths from the table's columns
    /// where <see cref="DbColumnModel.SourceJsonPath"/> is non-null.
    /// </summary>
    private static ImmutableArray<string> BuildCanonicalMemberPaths(DbTableModel tableModel)
    {
        return
        [
            .. tableModel
                .Columns.Where(c => c.SourceJsonPath.HasValue)
                .Select(c => c.SourceJsonPath!.Value.Canonical),
        ];
    }
}
