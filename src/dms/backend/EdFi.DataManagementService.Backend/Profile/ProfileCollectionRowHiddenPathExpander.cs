// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Folds hidden member paths from inlined non-collection descendant scopes onto the
/// matched row's <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>. A descendant
/// scope is considered inlined when its closest table-backed ancestor is the collection's
/// own table (i.e., its members live as columns on that table rather than on a separate
/// table). Each contributed path is prefixed with the collection-scope-relative path to
/// the descendant scope so it matches the collection table's bindings exactly under the
/// matched-row classifier's <c>Exact</c> rule.
/// </summary>
/// <remarks>
/// Slice 5 CP4 retired the executor fence on collection-descendant inlined non-collection
/// scopes. Without this expansion the row's <c>HiddenMemberPaths</c> only reflects the
/// collection scope's own member filter — Core's <c>StoredSideExistenceLookupBuilder</c>
/// emits descendant inlined scopes' hidden paths in separate <c>StoredScopeStates</c>,
/// which the row-level classifier never consults. A profile that pairs a permissive
/// collection-level rule with a restrictive sub-rule on an inlined child object would
/// classify hidden child members as <see cref="RootBindingDisposition.VisibleWritable"/>,
/// allowing flattened-null candidates to overwrite stored values on update.
/// </remarks>
internal static class ProfileCollectionRowHiddenPathExpander
{
    /// <summary>
    /// Returns <paramref name="rows"/> with each row's <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>
    /// augmented by the collection-scope-relative hidden paths of every inlined non-collection
    /// descendant scope whose <see cref="StoredScopeState.Address"/> matches the row's
    /// containing-collection identity. Returns the input array unchanged when no descendant
    /// state contributes paths.
    /// </summary>
    /// <param name="rows">
    /// Stored visible collection rows for the current scope, with their original (un-canonicalized)
    /// semantic identity values. The expansion matches descendant <see cref="StoredScopeState"/>
    /// ancestor identities against these original values, so the caller MUST invoke this method
    /// before identity canonicalization (descriptor URI → Int64 id, document-reference natural
    /// key → Int64 id) so both sides remain comparable.
    /// </param>
    /// <param name="storedScopeStates">
    /// All stored scope states from the profile-applied write context. Only descendants of
    /// <paramref name="collectionScope"/> whose owning table equals <paramref name="collectionTablePlan"/>
    /// participate in the expansion.
    /// </param>
    /// <param name="collectionScope">
    /// Compiled JSON scope of the collection currently being walked (e.g. <c>$.addresses[*]</c>).
    /// </param>
    /// <param name="collectionTablePlan">
    /// Write plan for the collection's backing table. A descendant scope is treated as
    /// inlined when <see cref="ProfileBindingClassificationCore.ResolveOwnerTablePlan"/> for
    /// that scope returns this plan's table.
    /// </param>
    /// <param name="writePlan">Resource write plan used by the owner-table resolver.</param>
    public static ImmutableArray<VisibleStoredCollectionRow> Expand(
        ImmutableArray<VisibleStoredCollectionRow> rows,
        ImmutableArray<StoredScopeState> storedScopeStates,
        string collectionScope,
        TableWritePlan collectionTablePlan,
        ResourceWritePlan writePlan
    )
    {
        ArgumentNullException.ThrowIfNull(collectionScope);
        ArgumentNullException.ThrowIfNull(collectionTablePlan);
        ArgumentNullException.ThrowIfNull(writePlan);

        if (rows.IsDefaultOrEmpty || storedScopeStates.IsDefaultOrEmpty)
        {
            return rows;
        }

        var collectionTableJsonScope = collectionTablePlan.TableModel.JsonScope.Canonical;
        var scopePrefix = collectionScope + ".";

        // Bucket additions by the row's semantic-identity key. A descendant scope's last
        // AncestorCollectionInstance pins it to a specific row in this collection; the key
        // string captures that identity. Identities here are the un-canonicalized values
        // emitted by Core (URIs / natural keys), which match the un-canonicalized values
        // on the supplied rows.
        Dictionary<string, List<string>>? additionsByRowIdentityKey = null;

        foreach (var state in storedScopeStates)
        {
            if (state.HiddenMemberPaths.IsDefaultOrEmpty)
            {
                continue;
            }

            var stateScope = state.Address.JsonScope;
            if (!stateScope.StartsWith(scopePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var ownerTable = ProfileBindingClassificationCore.ResolveOwnerTablePlan(stateScope, writePlan);
            if (
                ownerTable is null
                || !string.Equals(
                    ownerTable.TableModel.JsonScope.Canonical,
                    collectionTableJsonScope,
                    StringComparison.Ordinal
                )
            )
            {
                // Descendant lives on its own table (separate-table). Its hidden paths are
                // covered by that table's own classification pass, not the matched-row overlay.
                continue;
            }

            if (state.Address.AncestorCollectionInstances.IsDefaultOrEmpty)
            {
                continue;
            }

            var lastAncestor = state.Address.AncestorCollectionInstances[^1];
            if (!string.Equals(lastAncestor.JsonScope, collectionScope, StringComparison.Ordinal))
            {
                // Defense-in-depth: a descendant under a different collection scope could not
                // have its closest table-backed ancestor on this table, but the explicit check
                // makes the per-row matching contract obvious.
                continue;
            }

            var rowIdentityKey = BuildSemanticIdentityKey(lastAncestor.SemanticIdentityInOrder);
            var relativeScopePath = stateScope[scopePrefix.Length..];

            additionsByRowIdentityKey ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!additionsByRowIdentityKey.TryGetValue(rowIdentityKey, out var bucket))
            {
                bucket = [];
                additionsByRowIdentityKey[rowIdentityKey] = bucket;
            }

            foreach (var memberPath in state.HiddenMemberPaths)
            {
                bucket.Add(relativeScopePath + "." + memberPath);
            }
        }

        if (additionsByRowIdentityKey is null)
        {
            return rows;
        }

        var builder = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>(rows.Length);
        foreach (var row in rows)
        {
            var rowKey = BuildSemanticIdentityKey(row.Address.SemanticIdentityInOrder);
            if (additionsByRowIdentityKey.TryGetValue(rowKey, out var additions) && additions.Count > 0)
            {
                var combined = new HashSet<string>(StringComparer.Ordinal);
                if (!row.HiddenMemberPaths.IsDefaultOrEmpty)
                {
                    foreach (var existing in row.HiddenMemberPaths)
                    {
                        combined.Add(existing);
                    }
                }
                foreach (var addition in additions)
                {
                    combined.Add(addition);
                }
                builder.Add(row with { HiddenMemberPaths = [.. combined] });
            }
            else
            {
                builder.Add(row);
            }
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Serializes a semantic-identity sequence to a stable string key by joining each part's
    /// JSON-string representation with a pipe delimiter. Mirrors
    /// <see cref="ProfileCollectionPlanner"/>'s key shape so descendant ancestor identities and
    /// row identities compare under the same normalization.
    /// </summary>
    private static string BuildSemanticIdentityKey(ImmutableArray<SemanticIdentityPart> identity) =>
        string.Join("|", identity.Select(p => p.Value?.ToJsonString() ?? "null"));
}
