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
/// When additional scopes are provided (e.g. inlined scopes discovered from a profile's content
/// type tree), synthesizes descriptors for them as well.
/// </summary>
public static class CompiledScopeAdapterFactory
{
    /// <summary>
    /// Builds compiled scope descriptors from the given <see cref="ResourceWritePlan"/>.
    /// When <paramref name="additionalScopes"/> is provided, also produces descriptors for
    /// scopes that have no backing table (inlined into a parent table), ensuring that
    /// <c>IsScopeKnown</c> returns true for them in the profile pipeline.
    /// </summary>
    /// <param name="plan">The resource write plan containing table-backed scopes.</param>
    /// <param name="additionalScopes">
    /// Scopes discovered from the content type tree that are not represented in the table set.
    /// Each entry is a (JsonScope, ScopeKind) tuple. Null or empty to use table plans only.
    /// </param>
    public static CompiledScopeDescriptor[] BuildFromWritePlan(
        ResourceWritePlan plan,
        IReadOnlyList<(string JsonScope, ScopeKind Kind)>? additionalScopes = null
    )
    {
        // Build a lookup of JsonScope canonical string -> ScopeKind for parent resolution.
        // Starts with table-backed scopes; additional scopes are added below.
        var scopeKindByCanonical = plan
            .TablePlansInDependencyOrder.Select(tp => tp.TableModel)
            .ToDictionary(tm => tm.JsonScope.Canonical, tm => ToScopeKind(tm.IdentityMetadata.TableKind));

        // Register additional scopes so parent/ancestor resolution includes them
        if (additionalScopes is { Count: > 0 })
        {
            foreach (var (jsonScope, kind) in additionalScopes)
            {
                scopeKindByCanonical.TryAdd(jsonScope, kind);
            }
        }

        // Build descriptors for table-backed scopes (existing behavior)
        var descriptors = new List<CompiledScopeDescriptor>(
            plan.TablePlansInDependencyOrder.Select(tp => BuildDescriptor(tp, scopeKindByCanonical))
        );

        // Build descriptors for additional (inlined) scopes
        if (additionalScopes is { Count: > 0 })
        {
            var tableByScope = plan.TablePlansInDependencyOrder.ToDictionary(
                tp => tp.TableModel.JsonScope.Canonical,
                tp => tp.TableModel
            );

            foreach (var (jsonScope, kind) in additionalScopes)
            {
                descriptors.Add(BuildInlinedDescriptor(jsonScope, kind, scopeKindByCanonical, tableByScope));
            }
        }

        return [.. descriptors];
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Table-backed descriptor building
    // ───────────────────────────────────────────────────────────────────────

    private static CompiledScopeDescriptor BuildDescriptor(
        TableWritePlan tablePlan,
        IReadOnlyDictionary<string, ScopeKind> scopeKindByCanonical
    )
    {
        var tableModel = tablePlan.TableModel;
        var jsonScopeCanonical = tableModel.JsonScope.Canonical;
        var scopeKind = ToScopeKind(tableModel.IdentityMetadata.TableKind);

        var immediateParentJsonScope =
            tableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope
                ? ResolveAlignedBaseJsonScope(jsonScopeCanonical)
                : ResolveImmediateParentJsonScope(jsonScopeCanonical, scopeKindByCanonical);

        var collectionAncestorsInOrder = BuildCollectionAncestors(
            immediateParentJsonScope,
            scopeKindByCanonical
        );

        var semanticIdentityPaths = BuildSemanticIdentityPaths(
            tablePlan.CollectionMergePlan,
            jsonScopeCanonical
        );

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

    // ───────────────────────────────────────────────────────────────────────
    //  Inlined-scope descriptor building
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="CompiledScopeDescriptor"/> for an inlined scope that has
    /// no backing table. Member paths are derived from the closest ancestor table's
    /// columns whose <c>SourceJsonPath</c> falls under this scope.
    /// </summary>
    private static CompiledScopeDescriptor BuildInlinedDescriptor(
        string jsonScope,
        ScopeKind scopeKind,
        IReadOnlyDictionary<string, ScopeKind> scopeKindByCanonical,
        Dictionary<string, DbTableModel> tableByScope
    )
    {
        var immediateParentJsonScope = ResolveImmediateParentJsonScope(jsonScope, scopeKindByCanonical);

        var collectionAncestorsInOrder = BuildCollectionAncestors(
            immediateParentJsonScope,
            scopeKindByCanonical
        );

        var canonicalMemberPaths = BuildInlinedMemberPaths(jsonScope, tableByScope);

        return new CompiledScopeDescriptor(
            JsonScope: jsonScope,
            ScopeKind: scopeKind,
            ImmediateParentJsonScope: immediateParentJsonScope,
            CollectionAncestorsInOrder: collectionAncestorsInOrder,
            SemanticIdentityRelativePathsInOrder: [],
            CanonicalScopeRelativeMemberPaths: canonicalMemberPaths
        );
    }

    /// <summary>
    /// Derives canonical member paths for an inlined scope by scanning the closest
    /// ancestor table's columns for <c>SourceJsonPath</c> values under this scope.
    /// Only direct child members are included — deeper nested paths belong to their
    /// own scope entries and are excluded by the <c>!Contains('.')</c> filter.
    /// </summary>
    /// <remarks>
    /// Column <c>SourceJsonPath</c> values use two conventions:
    /// <list type="bullet">
    ///   <item>Root-backed tables: absolute paths (e.g. <c>$.calendarReference.schoolYear</c>).</item>
    ///   <item>Collection-backed tables: scope-relative paths starting with <c>$.</c>
    ///         (e.g. <c>$.calendarReference.schoolId</c> relative to <c>$.addresses[*]</c>).</item>
    /// </list>
    /// When the parent table is a collection, the absolute inlined-scope prefix won't match
    /// scope-relative column paths. This method computes a scope-relative prefix for that case,
    /// stripping the parent table scope from the inlined scope and prepending <c>$.</c>.
    /// </remarks>
    private static ImmutableArray<string> BuildInlinedMemberPaths(
        string jsonScope,
        Dictionary<string, DbTableModel> tableByScope
    )
    {
        var parentTableScope = FindClosestTableAncestor(jsonScope, tableByScope);
        if (parentTableScope is null)
            return [];

        var parentTable = tableByScope[parentTableScope];
        var absolutePrefix = jsonScope + ".";

        // For non-root parent tables, columns may use scope-relative paths. Compute the
        // scope-relative form of the inlined scope prefix for matching those columns.
        // E.g. inlined scope "$.addresses[*].calendarReference" under parent "$.addresses[*]"
        //   → relative part = "calendarReference" → scope-relative prefix = "$.calendarReference."
        string? scopeRelativePrefix = null;
        string parentScopePrefix = parentTableScope + ".";
        if (parentTableScope != "$" && jsonScope.StartsWith(parentScopePrefix, StringComparison.Ordinal))
        {
            var relativePart = jsonScope[parentScopePrefix.Length..];
            scopeRelativePrefix = "$." + relativePart + ".";
        }

        return
        [
            .. parentTable
                .Columns.Where(c =>
                    c.SourceJsonPath.HasValue
                    && (
                        c.SourceJsonPath.Value.Canonical.StartsWith(absolutePrefix, StringComparison.Ordinal)
                        || (
                            scopeRelativePrefix is not null
                            && c.SourceJsonPath.Value.Canonical.StartsWith(
                                scopeRelativePrefix,
                                StringComparison.Ordinal
                            )
                        )
                    )
                )
                .Select(c =>
                    StripToDirectMember(
                        c.SourceJsonPath!.Value.Canonical,
                        absolutePrefix,
                        scopeRelativePrefix
                    )
                )
                .Where(p => p is not null && !p.Contains('.'))
                .Select(p => p!)
                .Distinct(),
        ];
    }

    /// <summary>
    /// Strips a column's SourceJsonPath to a direct member name relative to the inlined scope,
    /// trying the absolute prefix first, then the scope-relative prefix.
    /// </summary>
    private static string? StripToDirectMember(
        string canonicalPath,
        string absolutePrefix,
        string? scopeRelativePrefix
    )
    {
        if (canonicalPath.StartsWith(absolutePrefix, StringComparison.Ordinal))
        {
            return canonicalPath[absolutePrefix.Length..];
        }

        if (
            scopeRelativePrefix is not null
            && canonicalPath.StartsWith(scopeRelativePrefix, StringComparison.Ordinal)
        )
        {
            return canonicalPath[scopeRelativePrefix.Length..];
        }

        return null;
    }

    /// <summary>
    /// Walks up the scope path to find the closest ancestor that has a backing table.
    /// </summary>
    private static string? FindClosestTableAncestor(
        string jsonScope,
        Dictionary<string, DbTableModel> tableByScope
    )
    {
        var segments = jsonScope.Split('.');
        for (var len = segments.Length - 1; len >= 1; len--)
        {
            var candidate = string.Join(".", segments[..len]);
            if (tableByScope.ContainsKey(candidate))
                return candidate;
        }

        return tableByScope.ContainsKey("$") ? "$" : null;
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Shared helpers
    // ───────────────────────────────────────────────────────────────────────

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
    /// and finding the closest ancestor that exists in the scope set. Returns null for root.
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
        // that is in the scope set.
        // e.g. "$.addresses[*]._ext.sample" -> try "$._ext" (not in set), try "$.addresses[*]" (in set)
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

    private static string? ResolveAlignedBaseJsonScope(string jsonScopeCanonical)
    {
        if (!jsonScopeCanonical.Contains("._ext.", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = jsonScopeCanonical.Split('.');
        List<string> baseScopeSegments = [];
        var index = 0;

        while (index < segments.Length)
        {
            if (string.Equals(segments[index], "_ext", StringComparison.Ordinal))
            {
                if (index + 1 >= segments.Length)
                {
                    return null;
                }

                index += 2;
                continue;
            }

            baseScopeSegments.Add(segments[index]);
            index++;
        }

        return baseScopeSegments.Count > 0 ? string.Join(".", baseScopeSegments) : null;
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
    private static ImmutableArray<string> BuildSemanticIdentityPaths(
        CollectionMergePlan? collectionMergePlan,
        string scopeCanonical
    )
    {
        if (collectionMergePlan is null || collectionMergePlan.SemanticIdentityBindings.Length == 0)
        {
            return [];
        }

        return
        [
            .. collectionMergePlan.SemanticIdentityBindings.Select(b =>
                ToScopeRelativePath(b.RelativePath.Canonical, scopeCanonical)
            ),
        ];
    }

    /// <summary>
    /// Extracts canonical scope-relative member paths from the table's columns
    /// where <see cref="DbColumnModel.SourceJsonPath"/> is non-null.
    /// </summary>
    private static ImmutableArray<string> BuildCanonicalMemberPaths(DbTableModel tableModel)
    {
        var scopeCanonical = tableModel.JsonScope.Canonical;
        return
        [
            .. tableModel
                .Columns.Where(c => c.SourceJsonPath.HasValue)
                .Select(c => ToScopeRelativePath(c.SourceJsonPath!.Value.Canonical, scopeCanonical)),
        ];
    }

    /// <summary>
    /// Converts a <see cref="JsonPathExpression"/> canonical string to a scope-relative
    /// member path by stripping the JSON scope prefix. Core consumers expect bare member
    /// names (e.g., "addressType") rather than JSONPath-prefixed strings ("$.addressType").
    /// </summary>
    private static string ToScopeRelativePath(string canonicalPath, string scopeCanonical)
    {
        string scopePrefix = scopeCanonical + ".";
        if (canonicalPath.StartsWith(scopePrefix, StringComparison.Ordinal))
        {
            return canonicalPath[scopePrefix.Length..];
        }

        // Collection columns: SourceJsonPath is already scope-relative with $ root marker
        return canonicalPath.StartsWith("$.", StringComparison.Ordinal) ? canonicalPath[2..] : canonicalPath;
    }
}
