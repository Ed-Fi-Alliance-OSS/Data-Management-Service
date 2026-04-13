// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Synthesizes <see cref="ProfileAppliedWriteRequest"/>, <see cref="ProfileAppliedWriteContext"/>,
/// and a <see cref="CompiledScopeDescriptor"/> catalog for null-profile callers. All scopes are
/// classified Visible and Creatable; table-backed scopes omitted from the request are VisibleAbsent
/// on the stored side (so the merge's second-pass emits deletes); inlined scopes are always Visible
/// (null-profile has no inlined-clear semantics). Catalog synthesis delegates to
/// <see cref="CompiledScopeAdapterFactory.BuildFromWritePlan"/>.
/// </summary>
internal static class NoProfileSyntheticProfileAdapter
{
    public sealed record AdapterOutput(
        ProfileAppliedWriteRequest Request,
        ProfileAppliedWriteContext? Context,
        ImmutableArray<CompiledScopeDescriptor> Catalog
    );

    /// <summary>
    /// Builds synthetic profile inputs for a null-profile write.
    /// </summary>
    /// <param name="writePlan">The compiled write plan for the resource.</param>
    /// <param name="flattenedWriteSet">The flattened request tree.</param>
    /// <param name="selectedBody">The caller-selected JSON body for the write.</param>
    /// <param name="currentState">
    /// Current stored state of the document, or null for create-new flows.
    /// </param>
    public static AdapterOutput Build(
        ResourceWritePlan writePlan,
        FlattenedWriteSet flattenedWriteSet,
        JsonNode selectedBody,
        RelationalWriteCurrentState? currentState
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(flattenedWriteSet);
        ArgumentNullException.ThrowIfNull(selectedBody);

        // Phase 0: Build catalog
        var additionalScopes = SchemaInlinedScopeDiscovery.Discover(writePlan);
        var catalogArray = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan, additionalScopes);
        var catalog = catalogArray.ToImmutableArray();

        // Build lookup indexes
        var catalogByJsonScope = catalogArray.ToDictionary(d => d.JsonScope, StringComparer.Ordinal);

        // Phase 1: Build presence sets — which table-backed scopes did the flattened set materialize?
        var flattenedScopeKeys = BuildFlattenedPresenceSet(flattenedWriteSet);

        // Phase 2: Build RequestScopeStates
        var requestScopeStates = BuildRequestScopeStates(flattenedWriteSet, catalog, catalogByJsonScope);

        // Phase 3: Build VisibleRequestCollectionItems
        var visibleRequestCollectionItems = BuildVisibleRequestCollectionItems(flattenedWriteSet);

        // Phase 4: Build the request
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: selectedBody,
            RootResourceCreatable: true,
            RequestScopeStates: requestScopeStates,
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );

        // Phase 5: Build context (null for create-new)
        ProfileAppliedWriteContext? context = null;
        if (currentState is not null)
        {
            var storedScopeStates = BuildStoredScopeStates(currentState, flattenedScopeKeys);

            var visibleStoredCollectionRows = BuildVisibleStoredCollectionRows(currentState, writePlan);

            context = new ProfileAppliedWriteContext(
                Request: request,
                VisibleStoredBody: currentState.ReconstitutedDocument ?? new JsonObject(),
                StoredScopeStates: storedScopeStates,
                VisibleStoredCollectionRows: visibleStoredCollectionRows
            );
        }

        return new AdapterOutput(request, context, catalog);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Phase 1: Flattened presence set
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a set of canonical scope keys that the flattened request materialized.
    /// Key format: "{jsonScope}|{ancestorKey}" — root and root-extension scopes have empty ancestor key.
    /// </summary>
    private static HashSet<string> BuildFlattenedPresenceSet(FlattenedWriteSet flattenedWriteSet)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        // Root scope is always present
        var rootJsonScope = flattenedWriteSet.RootRow.TableWritePlan.TableModel.JsonScope.Canonical;
        keys.Add($"{rootJsonScope}|");

        // Root extension rows
        foreach (var extRow in flattenedWriteSet.RootRow.RootExtensionRows)
        {
            var extJsonScope = extRow.TableWritePlan.TableModel.JsonScope.Canonical;
            keys.Add($"{extJsonScope}|");

            AddCollectionCandidateKeys(keys, extRow.CollectionCandidates, ancestorContextKey: "");
        }

        // Collection candidates (recursive) and attached aligned scopes
        AddCollectionCandidateKeys(
            keys,
            flattenedWriteSet.RootRow.CollectionCandidates,
            ancestorContextKey: ""
        );

        return keys;
    }

    /// <summary>
    /// Recursively adds collection candidate and attached-aligned-scope keys to the presence set.
    /// </summary>
    private static void AddCollectionCandidateKeys(
        HashSet<string> keys,
        IReadOnlyList<CollectionWriteCandidate> candidates,
        string ancestorContextKey
    )
    {
        foreach (var candidate in candidates)
        {
            var jsonScope = candidate.TableWritePlan.TableModel.JsonScope.Canonical;

            // Build the candidate's own ancestor context key (extends the parent's key)
            var candidateAncestorKey = ExtendAncestorContextKeyFromCandidate(
                ancestorContextKey,
                jsonScope,
                candidate.SemanticIdentityValues,
                candidate.SemanticIdentityPresenceFlags
            );

            // The collection table itself is a scope
            keys.Add($"{jsonScope}|{candidateAncestorKey}");

            // Attached aligned scopes
            foreach (var attachedScope in candidate.AttachedAlignedScopeData)
            {
                var attachedJsonScope = attachedScope.TableWritePlan.TableModel.JsonScope.Canonical;
                keys.Add($"{attachedJsonScope}|{candidateAncestorKey}");

                // Recurse into collections under attached aligned scopes
                AddCollectionCandidateKeys(keys, attachedScope.CollectionCandidates, candidateAncestorKey);
            }

            // Recurse into nested collections
            AddCollectionCandidateKeys(keys, candidate.CollectionCandidates, candidateAncestorKey);
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Phase 2: RequestScopeStates
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds RequestScopeState entries for every non-collection scope instance
    /// the flattened set materialized. For collection-context inlined scopes,
    /// emits one entry per collection row (per ancestor collection instance).
    /// </summary>
    private static ImmutableArray<RequestScopeState> BuildRequestScopeStates(
        FlattenedWriteSet flattenedWriteSet,
        ImmutableArray<CompiledScopeDescriptor> catalog,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> catalogByJsonScope
    )
    {
        var states = new List<RequestScopeState>();

        // Root scope — always present
        var rootAddress = new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty);
        states.Add(new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true));

        // Root extension scopes
        foreach (var extRow in flattenedWriteSet.RootRow.RootExtensionRows)
        {
            var extJsonScope = extRow.TableWritePlan.TableModel.JsonScope.Canonical;
            var extAddress = new ScopeInstanceAddress(
                extJsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );
            states.Add(
                new RequestScopeState(extAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true)
            );

            AddCollectionContextScopeStates(
                states,
                extRow.CollectionCandidates,
                ImmutableArray<AncestorCollectionInstance>.Empty,
                catalog,
                catalogByJsonScope
            );
        }

        // Root-level inlined scopes (under root scope, not under any collection)
        var rootLevelInlinedScopes = catalog.Where(d =>
            d.ScopeKind == ScopeKind.NonCollection
            && d.CollectionAncestorsInOrder.IsEmpty
            && d.JsonScope != "$"
            && !IsTableBacked(d.JsonScope, flattenedWriteSet)
        );
        foreach (var inlinedScope in rootLevelInlinedScopes)
        {
            var address = new ScopeInstanceAddress(
                inlinedScope.JsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );
            states.Add(new RequestScopeState(address, ProfileVisibilityKind.VisiblePresent, Creatable: true));
        }

        // Collection-context scopes: walk through collection candidates recursively
        AddCollectionContextScopeStates(
            states,
            flattenedWriteSet.RootRow.CollectionCandidates,
            ImmutableArray<AncestorCollectionInstance>.Empty,
            catalog,
            catalogByJsonScope
        );

        return [.. states];
    }

    /// <summary>
    /// Recursively walks collection candidates and emits inlined-scope RequestScopeState
    /// entries per collection row instance.
    /// </summary>
    private static void AddCollectionContextScopeStates(
        List<RequestScopeState> states,
        IReadOnlyList<CollectionWriteCandidate> candidates,
        ImmutableArray<AncestorCollectionInstance> parentAncestors,
        ImmutableArray<CompiledScopeDescriptor> catalog,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> catalogByJsonScope
    )
    {
        foreach (var candidate in candidates)
        {
            var collectionJsonScope = candidate.TableWritePlan.TableModel.JsonScope.Canonical;
            var mergePlan = candidate.TableWritePlan.CollectionMergePlan;

            // Build the ancestor chain for scopes nested under this collection item
            var semanticIdentityParts = BuildSemanticIdentityParts(candidate, mergePlan);
            var ancestorInstance = new AncestorCollectionInstance(collectionJsonScope, semanticIdentityParts);
            var childAncestors = parentAncestors.Add(ancestorInstance);

            // Find inlined scopes under this collection
            var inlinedScopesUnderCollection = catalog.Where(d =>
                d.ScopeKind == ScopeKind.NonCollection
                && d.CollectionAncestorsInOrder.Length > 0
                && d.CollectionAncestorsInOrder[^1] == collectionJsonScope
            );

            foreach (var inlinedScope in inlinedScopesUnderCollection)
            {
                var address = new ScopeInstanceAddress(inlinedScope.JsonScope, childAncestors);
                states.Add(
                    new RequestScopeState(address, ProfileVisibilityKind.VisiblePresent, Creatable: true)
                );
            }

            // Attached aligned scopes
            foreach (var attachedScope in candidate.AttachedAlignedScopeData)
            {
                var attachedJsonScope = attachedScope.TableWritePlan.TableModel.JsonScope.Canonical;
                var attachedAddress = new ScopeInstanceAddress(attachedJsonScope, childAncestors);
                states.Add(
                    new RequestScopeState(
                        attachedAddress,
                        ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    )
                );

                // Recurse into collections under attached aligned scopes
                AddCollectionContextScopeStates(
                    states,
                    attachedScope.CollectionCandidates,
                    childAncestors,
                    catalog,
                    catalogByJsonScope
                );
            }

            // Recurse into nested collections
            AddCollectionContextScopeStates(
                states,
                candidate.CollectionCandidates,
                childAncestors,
                catalog,
                catalogByJsonScope
            );
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Phase 3: VisibleRequestCollectionItems
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds one VisibleRequestCollectionItem per request collection row.
    /// </summary>
    private static ImmutableArray<VisibleRequestCollectionItem> BuildVisibleRequestCollectionItems(
        FlattenedWriteSet flattenedWriteSet
    )
    {
        var items = new List<VisibleRequestCollectionItem>();

        AddCollectionItems(
            items,
            flattenedWriteSet.RootRow.CollectionCandidates,
            ImmutableArray<AncestorCollectionInstance>.Empty,
            parentScopeAddress: new ScopeInstanceAddress(
                "$",
                ImmutableArray<AncestorCollectionInstance>.Empty
            )
        );

        foreach (var extRow in flattenedWriteSet.RootRow.RootExtensionRows)
        {
            AddCollectionItems(
                items,
                extRow.CollectionCandidates,
                ImmutableArray<AncestorCollectionInstance>.Empty,
                new ScopeInstanceAddress(
                    extRow.TableWritePlan.TableModel.JsonScope.Canonical,
                    ImmutableArray<AncestorCollectionInstance>.Empty
                )
            );
        }

        return [.. items];
    }

    /// <summary>
    /// Recursively adds VisibleRequestCollectionItem entries for all collection candidates.
    /// </summary>
    private static void AddCollectionItems(
        List<VisibleRequestCollectionItem> items,
        IReadOnlyList<CollectionWriteCandidate> candidates,
        ImmutableArray<AncestorCollectionInstance> parentAncestors,
        ScopeInstanceAddress parentScopeAddress
    )
    {
        foreach (var candidate in candidates)
        {
            var collectionJsonScope = candidate.TableWritePlan.TableModel.JsonScope.Canonical;
            var mergePlan = candidate.TableWritePlan.CollectionMergePlan;
            var semanticIdentityParts = BuildSemanticIdentityParts(candidate, mergePlan);

            var rowAddress = new CollectionRowAddress(
                collectionJsonScope,
                parentScopeAddress,
                semanticIdentityParts
            );

            // Build JSON path from ordinal path for diagnostics
            var requestJsonPath = BuildRequestJsonPath(collectionJsonScope, candidate.OrdinalPath);

            items.Add(new VisibleRequestCollectionItem(rowAddress, Creatable: true, requestJsonPath));

            // Build the ancestor chain for nested scopes
            var ancestorInstance = new AncestorCollectionInstance(collectionJsonScope, semanticIdentityParts);
            var childAncestors = parentAncestors.Add(ancestorInstance);
            var childScopeAddress = new ScopeInstanceAddress(collectionJsonScope, childAncestors);

            // Recurse into attached aligned scope collections
            foreach (var attachedScope in candidate.AttachedAlignedScopeData)
            {
                var attachedScopeAddress = new ScopeInstanceAddress(
                    attachedScope.TableWritePlan.TableModel.JsonScope.Canonical,
                    childAncestors
                );

                AddCollectionItems(
                    items,
                    attachedScope.CollectionCandidates,
                    childAncestors,
                    attachedScopeAddress
                );
            }

            // Recurse into nested collections
            AddCollectionItems(items, candidate.CollectionCandidates, childAncestors, childScopeAddress);
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Phase 4: StoredScopeStates (for update flows)
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds StoredScopeState entries for every table-backed scope instance
    /// in the current state. Classifies each as Visible (if the flattened set
    /// materialized a row for it) or VisibleAbsent (if omitted, triggering
    /// the merge's second-pass delete).
    /// Inlined scopes are always Visible.
    /// </summary>
    private static ImmutableArray<StoredScopeState> BuildStoredScopeStates(
        RelationalWriteCurrentState currentState,
        HashSet<string> flattenedScopeKeys
    )
    {
        var states = new List<StoredScopeState>();

        foreach (var tableRows in currentState.TableRowsInDependencyOrder)
        {
            var jsonScope = tableRows.TableModel.JsonScope.Canonical;
            var tableKind = tableRows.TableModel.IdentityMetadata.TableKind;

            // Skip collection tables and collection-aligned extension scopes —
            // they are handled by VisibleStoredCollectionRows
            if (
                tableKind
                is DbTableKind.Collection
                    or DbTableKind.ExtensionCollection
                    or DbTableKind.CollectionExtensionScope
            )
            {
                continue;
            }

            if (tableRows.Rows.Count == 0)
            {
                continue;
            }

            // Non-collection, table-backed scope
            var canonicalKey = $"{jsonScope}|";
            var isPresent = flattenedScopeKeys.Contains(canonicalKey);

            var visibility = isPresent
                ? ProfileVisibilityKind.VisiblePresent
                : ProfileVisibilityKind.VisibleAbsent;

            var address = new ScopeInstanceAddress(
                jsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );

            states.Add(new StoredScopeState(address, visibility, HiddenMemberPaths: []));
        }

        return [.. states];
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Phase 5: VisibleStoredCollectionRows (for update flows)
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds one VisibleStoredCollectionRow per current-state collection row.
    /// All rows are Visible with empty HiddenMemberPaths (null-profile has no hidden members).
    /// Semantic identity parts are populated from the actual stored column values so that
    /// <see cref="RelationalWriteMergeSynthesizer"/> can correctly classify each current row
    /// as visible (rather than hidden) when partitioning for merge.
    /// Nested collection rows are emitted with the correct parent ancestor context key so
    /// that <c>MergeCollectionLookup</c> indexes them under the right parent scope instance.
    /// </summary>
    private static ImmutableArray<VisibleStoredCollectionRow> BuildVisibleStoredCollectionRows(
        RelationalWriteCurrentState currentState,
        ResourceWritePlan writePlan
    )
    {
        // Build lookups from table name and JSON scope → TableWritePlan.
        var writePlanByTable = writePlan.TablePlansInDependencyOrder.ToDictionary(
            p => p.TableModel.Table,
            p => p
        );
        var writePlanByScope = writePlan.TablePlansInDependencyOrder.ToDictionary(
            p => p.TableModel.JsonScope.Canonical,
            StringComparer.Ordinal
        );

        // Track each collection row's full ancestor chain (including itself) keyed by
        // (tableName, stableRowId) so that child rows can build correct parent addresses.
        // Tables are processed in dependency order (parents before children), so parent rows
        // are always registered before any child row needs them.
        var rowAncestorChain =
            new Dictionary<(DbTableName Table, long StableId), ImmutableArray<AncestorCollectionInstance>>();

        var rows = new List<VisibleStoredCollectionRow>();

        foreach (var tableRows in currentState.TableRowsInDependencyOrder)
        {
            var jsonScope = tableRows.TableModel.JsonScope.Canonical;
            var tableKind = tableRows.TableModel.IdentityMetadata.TableKind;

            if (tableKind is not (DbTableKind.Collection or DbTableKind.ExtensionCollection))
            {
                continue;
            }

            // Resolve the write plan for this table to get the merge plan and binding indices.
            if (!writePlanByTable.TryGetValue(tableRows.TableModel.Table, out var tableWritePlan))
            {
                continue;
            }

            var mergePlan = tableWritePlan.CollectionMergePlan;
            if (mergePlan is null)
            {
                continue;
            }

            // Determine whether this collection is nested under another collection
            // or is a top-level collection whose parent is the root scope.
            var parentScopeJsonScope = GetImmediateParentScopeJsonScope(jsonScope);
            var ancestorCollectionJsonScope = GetNearestEnclosingCollectionJsonScope(parentScopeJsonScope);

            // Column ordinal of the stable row identity within the raw stored row array.
            var stableIdColName = tableWritePlan
                .ColumnBindings[mergePlan.StableRowIdentityBindingIndex]
                .Column
                .ColumnName;
            var stableIdOrdinal = FindColumnOrdinalByName(tableRows.TableModel, stableIdColName);

            // Column ordinal(s) in the child row that hold the parent table's stable row identity.
            // Only used for nested collections (parentJsonScope != null).
            var parentKeyOrdinals = ancestorCollectionJsonScope is not null
                ? tableRows
                    .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(col =>
                        FindColumnOrdinalByName(tableRows.TableModel, col)
                    )
                    .ToArray()
                : [];

            // Resolve parent table name for ancestor chain lookup.
            DbTableName? parentTableName =
                ancestorCollectionJsonScope is not null
                && writePlanByScope.TryGetValue(ancestorCollectionJsonScope, out var parentTablePlan)
                    ? parentTablePlan.TableModel.Table
                    : null;

            foreach (var storedRow in tableRows.Rows)
            {
                var semanticParts = BuildStoredRowActualSemanticIdentityParts(storedRow, mergePlan);

                // Build parent address. For a top-level collection the parent is root ("$", []).
                // For a nested collection, use the registered ancestor chain of the parent row.
                ImmutableArray<AncestorCollectionInstance> parentAncestors;
                if (ancestorCollectionJsonScope is null || parentTableName is null)
                {
                    parentAncestors = ImmutableArray<AncestorCollectionInstance>.Empty;
                }
                else
                {
                    var parentKeyRaw = parentKeyOrdinals.Length > 0 ? storedRow[parentKeyOrdinals[0]] : null;
                    var parentKeyLong = parentKeyRaw switch
                    {
                        long l => l,
                        int i => (long)i,
                        _ => (long?)null,
                    };

                    parentAncestors =
                        parentKeyLong.HasValue
                        && rowAncestorChain.TryGetValue(
                            (parentTableName.Value, parentKeyLong.Value),
                            out var chain
                        )
                            ? chain
                            : ImmutableArray<AncestorCollectionInstance>.Empty;
                }

                var parentAddress = new ScopeInstanceAddress(parentScopeJsonScope, parentAncestors);

                var rowAddress = new CollectionRowAddress(jsonScope, parentAddress, semanticParts);
                rows.Add(new VisibleStoredCollectionRow(rowAddress, HiddenMemberPaths: []));

                // Register this row's ancestor chain (parent ancestors + this row itself) so
                // that child rows in nested scopes can build their correct parent addresses.
                var stableIdRaw = storedRow[stableIdOrdinal];
                var stableId = stableIdRaw switch
                {
                    long l => l,
                    int i => (long)i,
                    _ => (long?)null,
                };

                if (stableId.HasValue)
                {
                    var thisInstance = new AncestorCollectionInstance(jsonScope, semanticParts);
                    rowAncestorChain[(tableRows.TableModel.Table, stableId.Value)] = parentAncestors.Add(
                        thisInstance
                    );
                }
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// Returns the JSON scope of the immediate parent scope for a collection scope.
    /// E.g. "$.addresses[*].periods[*]" → "$.addresses[*]";
    ///      "$.addresses[*]" → "$";
    ///      "$.addresses[*]._ext.sample.services[*]" → "$.addresses[*]._ext.sample".
    /// </summary>
    private static string GetImmediateParentScopeJsonScope(string jsonScope)
    {
        var lastDot = jsonScope.LastIndexOf('.');
        if (lastDot <= 1)
        {
            return "$";
        }

        return jsonScope[..lastDot];
    }

    private static string? GetNearestEnclosingCollectionJsonScope(string jsonScope)
    {
        var candidate = jsonScope;

        while (candidate != "$")
        {
            if (candidate.EndsWith("[*]", StringComparison.Ordinal))
            {
                return candidate;
            }

            var lastDot = candidate.LastIndexOf('.');
            if (lastDot <= 1)
            {
                return null;
            }

            candidate = candidate[..lastDot];
        }

        return null;
    }

    /// <summary>
    /// Finds the zero-based ordinal of a column by name within a <see cref="DbTableModel"/>'s
    /// <c>Columns</c> list. Throws if the column is not found.
    /// </summary>
    private static int FindColumnOrdinalByName(DbTableModel tableModel, DbColumnName columnName)
    {
        for (var i = 0; i < tableModel.Columns.Count; i++)
        {
            if (tableModel.Columns[i].ColumnName.Equals(columnName))
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"Column '{columnName.Value}' not found in table "
                + $"'{tableModel.Table.Schema.Value}.{tableModel.Table.Name}'."
        );
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Shared helpers
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds semantic identity parts from a collection candidate's identity values
    /// and the merge plan's identity bindings.
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> BuildSemanticIdentityParts(
        CollectionWriteCandidate candidate,
        CollectionMergePlan? mergePlan
    )
    {
        if (mergePlan is null)
        {
            return [];
        }

        var parts = new SemanticIdentityPart[mergePlan.SemanticIdentityBindings.Length];

        for (var i = 0; i < mergePlan.SemanticIdentityBindings.Length; i++)
        {
            var binding = mergePlan.SemanticIdentityBindings[i];
            var relativePath = binding.RelativePath.Canonical;

            // Strip the "$." prefix from the relative path for the SemanticIdentityPart
            var partRelativePath = relativePath.StartsWith("$.", StringComparison.Ordinal)
                ? relativePath[2..]
                : relativePath;

            var clrValue = candidate.SemanticIdentityValues[i];
            var isPresent = candidate.SemanticIdentityPresenceFlags[i];

            // Use the shared converter so that date/time types (DateOnly, TimeOnly) are
            // serialized with invariant ISO strings, matching ConvertClrValueToSemanticIdentityJsonNode
            // and NormalizeClrValueForIdentity in RelationalWriteMergeSynthesizer.
            JsonNode? jsonNodeValue = ConvertClrValueToSemanticIdentityJsonNode(clrValue);

            parts[i] = new SemanticIdentityPart(partRelativePath, jsonNodeValue, isPresent);
        }

        return [.. parts];
    }

    /// <summary>
    /// Builds semantic identity parts from an actual stored collection row's column values.
    /// Uses the merge plan's <see cref="CollectionMergePlan.SemanticIdentityBindings"/> to
    /// extract the correct column values by index. This is required so that
    /// <see cref="RelationalWriteMergeSynthesizer.TryMatchCurrentRowToVisibleStored"/> can
    /// correctly classify each current row as visible rather than hidden.
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> BuildStoredRowActualSemanticIdentityParts(
        object?[] storedRow,
        CollectionMergePlan mergePlan
    )
    {
        var parts = new SemanticIdentityPart[mergePlan.SemanticIdentityBindings.Length];

        for (var i = 0; i < mergePlan.SemanticIdentityBindings.Length; i++)
        {
            var binding = mergePlan.SemanticIdentityBindings[i];
            var relativePath = binding.RelativePath.Canonical;

            // Strip the "$." prefix from the relative path for the SemanticIdentityPart
            var partRelativePath = relativePath.StartsWith("$.", StringComparison.Ordinal)
                ? relativePath[2..]
                : relativePath;

            var clrValue = storedRow[binding.BindingIndex];
            var isPresent = clrValue is not null;

            JsonNode? jsonNodeValue = ConvertClrValueToSemanticIdentityJsonNode(clrValue);

            parts[i] = new SemanticIdentityPart(partRelativePath, jsonNodeValue, isPresent);
        }

        return [.. parts];
    }

    /// <summary>
    /// Converts a CLR value to a <see cref="JsonNode"/> suitable for use in a
    /// <see cref="SemanticIdentityPart"/>. SQL Server date/time types
    /// (<see cref="DateTime"/> for Date columns, <see cref="TimeSpan"/> for Time columns)
    /// are normalized to canonical ISO strings that match the output of
    /// <c>RelationalWriteMergeSynthesizer.NormalizeClrValueForIdentity</c>.
    /// </summary>
    private static JsonNode? ConvertClrValueToSemanticIdentityJsonNode(object? clrValue) =>
        clrValue switch
        {
            null => null,
            string s => JsonValue.Create(s),
            int n => JsonValue.Create(n),
            long n => JsonValue.Create(n),
            bool b => JsonValue.Create(b),
            DateTime dt => JsonValue.Create(
                DateOnly
                    .FromDateTime(dt)
                    .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            ),
            TimeSpan ts => JsonValue.Create(
                new TimeOnly(ts.Ticks).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            ),
            DateOnly d => JsonValue.Create(
                d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            ),
            TimeOnly t => JsonValue.Create(
                t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            ),
            _ => JsonValue.Create(clrValue.ToString()!),
        };

    /// <summary>
    /// Builds a concrete JSON path from a collection scope and ordinal path.
    /// E.g., "$.classPeriods[*]" with ordinal [0] becomes "$.classPeriods[0]".
    /// </summary>
    private static string BuildRequestJsonPath(string collectionJsonScope, ImmutableArray<int> ordinalPath)
    {
        // Replace [*] with [ordinalIndex]
        var lastOrdinal = ordinalPath[^1];
        return collectionJsonScope.Replace("[*]", $"[{lastOrdinal}]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a scope is backed by a table in the write plan
    /// (i.e., appears as a root extension row in the flattened set).
    /// Inlined scopes are NOT table-backed.
    /// </summary>
    private static bool IsTableBacked(string jsonScope, FlattenedWriteSet flattenedWriteSet)
    {
        // Check root extension rows
        foreach (var extRow in flattenedWriteSet.RootRow.RootExtensionRows)
        {
            if (extRow.TableWritePlan.TableModel.JsonScope.Canonical == jsonScope)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extends an ancestor context key with a new collection item's identity.
    /// Mirrors <c>RelationalWriteMergeSynthesizer.ExtendAncestorContextKey</c>
    /// but uses CLR-domain values from the flattened candidate.
    /// </summary>
    private static string ExtendAncestorContextKeyFromCandidate(
        string currentKey,
        string collectionJsonScope,
        IReadOnlyList<object?> semanticIdentityValues,
        ImmutableArray<bool> semanticIdentityPresenceFlags
    )
    {
        var sb = new System.Text.StringBuilder();

        if (currentKey.Length > 0)
        {
            sb.Append(currentKey);
            sb.Append('\0');
        }

        sb.Append(collectionJsonScope);

        for (var i = 0; i < semanticIdentityValues.Count; i++)
        {
            sb.Append('\0');
            sb.Append(semanticIdentityPresenceFlags[i] ? '1' : '0');
            sb.Append('\0');
            sb.Append(NormalizeClrValueForIdentity(semanticIdentityValues[i]));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalizes a CLR value for use in canonical key construction.
    /// Mirrors <c>RelationalWriteMergeSynthesizer.NormalizeClrValueForIdentity</c>.
    /// </summary>
    private static string NormalizeClrValueForIdentity(object? value) =>
        value switch
        {
            null => "",
            string s => s,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? "",
        };
}
