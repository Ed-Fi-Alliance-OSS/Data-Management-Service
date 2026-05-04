// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

internal delegate SeparateScopeSynthesisResult SynthesizeSeparateScopeInstanceDelegate(
    TableWritePlan tablePlan,
    ScopeInstanceAddress scopeAddress,
    ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
    SeparateScopeBuffer? buffer,
    JsonNode? scopedRequestNode,
    RequestScopeState? requestScope,
    StoredScopeState? storedScope,
    CurrentSeparateScopeRowProjection? currentRowProjection
);

/// <summary>
/// Recursive parent-first walker for profile-aware collection merge. Owns
/// partitioning, hidden-subtree preservation in Preserve mode, and per-parent-instance
/// dispatch to <see cref="ProfileCollectionPlanner"/> and
/// <c>SynthesizeSeparateScopeInstance</c>.
/// </summary>
/// <remarks>
/// Construction builds four read-only per-merge indexes (current collection rows,
/// current separate-scope rows, visible-stored rows, visible-request items) keyed by
/// <c>(table or scope, parent-instance address)</c>. <see cref="WalkChildren"/> reads those
/// indexes directly and dispatches through the planner per scope. Nested cases re-enter
/// <see cref="WalkChildren"/> with a non-root <see cref="ProfileCollectionWalkerContext"/>.
/// </remarks>
internal sealed class ProfileCollectionWalker
{
    private readonly RelationalWriteProfileMergeRequest _request;
    private readonly FlatteningResolvedReferenceLookupSet _resolvedReferenceLookups;
    private readonly IReadOnlyDictionary<DbTableName, ProfileTableStateBuilder> _tableStateBuilders;
    private readonly SynthesizeSeparateScopeInstanceDelegate? _synthesizeSeparateScopeInstance;

    private readonly IReadOnlyDictionary<
        (DbTableName Table, ParentIdentityKey ParentKey),
        IReadOnlyList<CurrentCollectionRowProjection>
    > _currentCollectionRowsByTableAndParentIdentity;

    private readonly IReadOnlyDictionary<
        (DbTableName Table, ParentIdentityKey ParentKey),
        CurrentSeparateScopeRowProjection?
    > _currentSeparateScopeRowsByTableAndParentIdentity;

    private readonly IReadOnlyDictionary<
        (string ChildJsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<VisibleStoredCollectionRow>
    > _visibleStoredRowsByChildScopeAndParent;

    private readonly IReadOnlyDictionary<
        (string ChildJsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<VisibleRequestCollectionItem>
    > _visibleRequestItemsByChildScopeAndParent;

    private readonly HashSet<string> _tableBackedJsonScopes;

    private readonly IReadOnlyDictionary<
        (string JsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<CurrentCollectionRowSnapshot>
    > _currentRowsByJsonScopeAndParent;

    public ProfileCollectionWalker(
        RelationalWriteProfileMergeRequest request,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyDictionary<DbTableName, ProfileTableStateBuilder> tableStateBuilders,
        SynthesizeSeparateScopeInstanceDelegate? synthesizeSeparateScopeInstance = null
    )
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _resolvedReferenceLookups =
            resolvedReferenceLookups ?? throw new ArgumentNullException(nameof(resolvedReferenceLookups));
        _tableStateBuilders =
            tableStateBuilders ?? throw new ArgumentNullException(nameof(tableStateBuilders));
        _synthesizeSeparateScopeInstance = synthesizeSeparateScopeInstance;

        var semanticIdentityPathsByCollectionScope = BuildSemanticIdentityPathsByCollectionScope(_request);
        _currentCollectionRowsByTableAndParentIdentity = BuildCurrentCollectionRowsIndex(
            _request,
            semanticIdentityPathsByCollectionScope
        );
        _currentSeparateScopeRowsByTableAndParentIdentity = BuildCurrentSeparateScopeRowsIndex(_request);

        // Set of table-backed JsonScopes used by EnumerateDirectChildCollectionScopes /
        // ResolveEffectiveChildParentScopeAddress to detect collection tables whose
        // immediate JSON parent is an inlined non-collection scope (e.g.
        // $.parents[*].detail.children[*]). Such children dispatch from the nearest
        // table-backed ancestor (the parent collection row) but their effective parent
        // address carries the inlined JsonScope so the per-(scope, parent-instance)
        // visible-stored / visible-request indexes — which Core keys by the inlined parent
        // address — match.
        _tableBackedJsonScopes = new HashSet<string>(
            _request.WritePlan.TablePlansInDependencyOrder.Select(plan =>
                plan.TableModel.JsonScope.Canonical
            ),
            StringComparer.Ordinal
        );

        // Build a JsonScope→TableWritePlan map and a JsonScope→current-rows map so the
        // visible-stored / visible-request index keys can canonicalize ancestor identities
        // at construction time. Without this, the indexes would be keyed by raw Core-emitted
        // addresses (descriptor URIs / document-reference natural keys in ancestors) while
        // the walker's recursion lookup uses canonicalized backend-id ancestors, causing
        // nested children of descriptor- or reference-backed parents to be invisible to the
        // child planner.
        var tablePlanByJsonScope = BuildTablePlanByJsonScope(_request);
        var currentRowsByJsonScope = BuildCurrentRowsByJsonScope(
            _request,
            _currentCollectionRowsByTableAndParentIdentity
        );
        _currentRowsByJsonScopeAndParent = BuildCurrentRowsByJsonScopeAndParent(
            _request,
            _currentCollectionRowsByTableAndParentIdentity
        );
        var storedRowsByJsonScope = BuildStoredRowsByJsonScope(_request);

        _visibleStoredRowsByChildScopeAndParent = BuildVisibleStoredRowsIndex(
            _request,
            tablePlanByJsonScope,
            currentRowsByJsonScope,
            _currentRowsByJsonScopeAndParent,
            storedRowsByJsonScope,
            _resolvedReferenceLookups
        );
        _visibleRequestItemsByChildScopeAndParent = BuildVisibleRequestItemsIndex(
            _request,
            tablePlanByJsonScope,
            currentRowsByJsonScope,
            _currentRowsByJsonScopeAndParent,
            storedRowsByJsonScope,
            _resolvedReferenceLookups
        );
    }

    /// <summary>
    /// Walk the children of the given parent context. Returns a non-null
    /// <see cref="ProfileMergeOutcome"/> when the walk produced a rejection that the
    /// synthesizer must propagate; returns <c>null</c> on successful completion.
    /// </summary>
    /// <remarks>
    /// Owns the per-(scope, parent-instance) planner dispatch surface for both the root
    /// case and nested cases. The root case treats the synthetic
    /// <c>ScopeInstanceAddress($, [])</c> as the parent address; nested cases re-enter
    /// this method with a non-root <see cref="ProfileCollectionWalkerContext"/>.
    /// </remarks>
    public ProfileMergeOutcome? WalkChildren(ProfileCollectionWalkerContext parentContext, WalkMode mode)
    {
        ArgumentNullException.ThrowIfNull(parentContext);

        if (mode == WalkMode.Preserve)
        {
            return WalkChildrenPreserveMode(parentContext);
        }

        var profileRequest = _request.ProfileRequest;
        var currentState = _request.CurrentState;
        var writableRequestBody = _request.WritableRequestBody;
        var resourceWritePlan = _request.WritePlan;

        // Pull collection candidates for the parent's request substructure: root, matched
        // collection row, inserted collection row, or root-extension row.
        var collectionCandidatesFromParent = ResolveCollectionCandidatesForParent(parentContext);

        // Group candidates by table (JsonScope) so we process each scope once.
        var candidatesByScope = collectionCandidatesFromParent
            .GroupBy(c => c.TableWritePlan.TableModel.JsonScope.Canonical)
            .ToDictionary(g => g.Key, g => g.ToList());

        // The root case treats the synthetic ScopeInstanceAddress($, []) as the parent
        // address; nested cases re-enter this method with a non-root
        // parentContext.ContainingScopeAddress. Per-child the effective parent address
        // may differ from parentContext.ContainingScopeAddress when the child's immediate
        // JSON parent is an inlined non-collection scope (see
        // ResolveEffectiveChildParentScopeAddress).

        // Iterate in TablePlansInDependencyOrder so first-rejection-wins is deterministic
        // across runs. Filter to direct topological children of the parent's JsonScope:
        // the root case yields top-level base collection tables; a nested-collection
        // parent yields its direct nested-base collection tables, CollectionExtensionScope
        // children, and inlined-parent base/extension collection children.
        foreach (var tablePlan in EnumerateDirectChildCollectionScopes(parentContext))
        {
            var jsonScope = tablePlan.TableModel.JsonScope.Canonical;

            if (tablePlan.TableModel.IdentityMetadata.TableKind is DbTableKind.CollectionExtensionScope)
            {
                var alignedScopeOutcome = DispatchAlignedExtensionScope(parentContext, tablePlan);
                if (alignedScopeOutcome is not null)
                {
                    return alignedScopeOutcome;
                }

                continue;
            }

            candidatesByScope.TryGetValue(jsonScope, out var candidates);
            var requestCandidatesForScope = candidates is null
                ? ImmutableArray<CollectionWriteCandidate>.Empty
                : candidates.ToImmutableArray();

            // For inlined-parent children (e.g. $.parents[*].detail.children[*] dispatched
            // from $.parents[*]) the visible-stored / visible-request indexes are keyed by
            // the inlined parent JsonScope ($.parents[*].detail) carrying the parent
            // collection row's ancestor chain. Resolve that effective parent address per
            // child so the lookup matches Core-emitted addresses.
            var effectiveParentScopeAddress = ResolveEffectiveChildParentScopeAddress(
                parentContext,
                jsonScope
            );

            // Read visible-request items and visible-stored rows from the per-merge
            // indexes built at construction. The index is keyed by
            // (childScope, parentAddress).
            var visibleIndexKey = (jsonScope, effectiveParentScopeAddress);
            var visibleRequestItemsForScope = _visibleRequestItemsByChildScopeAndParent.TryGetValue(
                visibleIndexKey,
                out var visibleRequestItemBucket
            )
                ? visibleRequestItemBucket
                : ImmutableArray<VisibleRequestCollectionItem>.Empty;

            var visibleStoredRowsForScope = _visibleStoredRowsByChildScopeAndParent.TryGetValue(
                visibleIndexKey,
                out var visibleStoredRowBucket
            )
                ? visibleStoredRowBucket
                : ImmutableArray<VisibleStoredCollectionRow>.Empty;

            // Read current rows from the projection index, then adapt each projection to
            // the snapshot shape consumed by the planner. The projection is a strict
            // superset of the snapshot, so this is a per-row field projection.
            var currentRowsForScope = ImmutableArray<CurrentCollectionRowSnapshot>.Empty;
            if (currentState is not null)
            {
                // The walker's index is keyed by the *child-relevant* projection of the
                // parent identity: for each immediate-parent-locator column on the child,
                // pull the value at the corresponding parent PhysicalRowIdentity slot
                // (per the binding's ParentKeyPart.Index). This mirrors the now-deleted
                // ProjectCurrentRowsForScope filter, which compared each child locator
                // column to its specific parent slot — slots the child does not reference
                // never participated in matching.
                var parentValuesForLookup = ProjectParentValuesForChildLookup(tablePlan, parentContext);

                var currentIndexKey = (
                    tablePlan.TableModel.Table,
                    new ParentIdentityKey(parentValuesForLookup)
                );
                if (
                    _currentCollectionRowsByTableAndParentIdentity.TryGetValue(
                        currentIndexKey,
                        out var rawProjections
                    )
                )
                {
                    currentRowsForScope = [.. rawProjections.Select(p => p.ToSnapshot())];
                }
            }

            // No-op scope: all four sources empty → nothing to do for this collection table.
            if (
                requestCandidatesForScope.Length == 0
                && visibleRequestItemsForScope.Length == 0
                && visibleStoredRowsForScope.Length == 0
                && currentRowsForScope.Length == 0
            )
            {
                continue;
            }

            // 1. Build scope-local planner input from the unified source set.
            //    Reference- and descriptor-backed semantic identity parts arrive from Core
            //    as document natural-key values / descriptor URI strings, but the planner
            //    matches against backend-side Int64 ids. Canonicalize the Core-emitted
            //    streams before handing them to the planner.
            var documentIdentityParts =
                RelationalWriteProfileMergeSynthesizer.ResolveDocumentReferenceIdentityParts(
                    resourceWritePlan,
                    tablePlan
                );
            var descriptorIdentityIndices =
                RelationalWriteProfileMergeSynthesizer.ResolveDescriptorIdentityIndices(tablePlan);

            var canonicalizedVisibleRequestItems = visibleRequestItemsForScope;
            if (documentIdentityParts.Count > 0)
            {
                canonicalizedVisibleRequestItems =
                    RelationalWriteProfileMergeSynthesizer.CanonicalizeDocumentReferenceRequestItems(
                        canonicalizedVisibleRequestItems,
                        documentIdentityParts,
                        _resolvedReferenceLookups
                    );
            }
            if (descriptorIdentityIndices.Count > 0)
            {
                canonicalizedVisibleRequestItems =
                    RelationalWriteProfileMergeSynthesizer.CanonicalizeDescriptorRequestItems(
                        canonicalizedVisibleRequestItems,
                        tablePlan,
                        descriptorIdentityIndices,
                        _resolvedReferenceLookups
                    );
            }

            var canonicalizedVisibleStoredRows = visibleStoredRowsForScope;
            if (documentIdentityParts.Count > 0)
            {
                canonicalizedVisibleStoredRows =
                    RelationalWriteProfileMergeSynthesizer.CanonicalizeDocumentReferenceStoredRows(
                        canonicalizedVisibleStoredRows,
                        documentIdentityParts,
                        tablePlan,
                        _resolvedReferenceLookups,
                        currentRowsForScope
                    );
            }
            if (descriptorIdentityIndices.Count > 0)
            {
                canonicalizedVisibleStoredRows =
                    RelationalWriteProfileMergeSynthesizer.CanonicalizeDescriptorStoredRows(
                        canonicalizedVisibleStoredRows,
                        tablePlan,
                        descriptorIdentityIndices,
                        _resolvedReferenceLookups,
                        currentRowsForScope
                    );
            }

            var input = new ProfileCollectionScopeInput(
                JsonScope: jsonScope,
                ParentScopeAddress: effectiveParentScopeAddress,
                RequestCandidates: requestCandidatesForScope,
                VisibleRequestItems: canonicalizedVisibleRequestItems,
                VisibleStoredRows: canonicalizedVisibleStoredRows,
                CurrentRows: currentRowsForScope
            );

            // 2. Call the planner. Invariant violations throw and propagate as fail-closed.
            var planResult = ProfileCollectionPlanner.Plan(input);

            // 3. Handle result.
            if (planResult is ProfileCollectionPlanResult.CreatabilityRejection rejection)
            {
                return ProfileMergeOutcome.Reject(
                    new ProfileCreatabilityRejection(jsonScope, rejection.Reason)
                );
            }

            if (planResult is not ProfileCollectionPlanResult.Success success)
            {
                throw new InvalidOperationException(
                    $"Unhandled ProfileCollectionPlanResult type '{planResult?.GetType().Name}' for scope '{jsonScope}'."
                );
            }

            // 4. Translate plan entries to merged rows in sequence order.
            var mergedRows = new List<RelationalWriteMergedTableRow>(success.Plan.Sequence.Length);

            // Invariant: CurrentRows must contain ALL rows currently in the DB for this
            // scope — including omitted-visible rows that the planner excluded from
            // Sequence. The persister's delete-by-absence logic relies on the set-difference
            // (StableRowIdentity in CurrentRows but not in MergedRows → delete). Building
            // currentCollectionRows only from plan Sequence entries would make
            // omitted-visible rows invisible to the persister, so they would never be
            // deleted.
            var currentCollectionRows = currentRowsForScope.Select(snap => snap.ProjectedCurrentRow).ToList();

            // Build a lookup from candidate identity key → VisibleRequestCollectionItem so we
            // can find the concrete request item node for matched-update entries without a
            // separate linear scan each time. Use the canonicalized items so descriptor
            // identity parts (URI → Int64) match the candidate's already-resolved Int64 values.
            var candidateKeyToRequestItem = BuildCandidateKeyToRequestItemLookup(
                canonicalizedVisibleRequestItems
            );

            for (var i = 0; i < success.Plan.Sequence.Length; i++)
            {
                var finalOrdinal = i + 1;
                var entry = success.Plan.Sequence[i];

                switch (entry)
                {
                    case ProfileCollectionPlanEntry.MatchedUpdateEntry matchedEntry:
                        // Resolve the concrete request item node from the writable request body.
                        var (_, concreteRequestItemNode) = ResolveCandidateRequestItem(
                            nameof(ProfileCollectionPlanEntry.MatchedUpdateEntry),
                            jsonScope,
                            writableRequestBody,
                            matchedEntry.RequestCandidate,
                            candidateKeyToRequestItem
                        );

                        var mergedRow = ProfileCollectionMatchedRowOverlay.BuildMatchedRowEmission(
                            resourceWritePlan,
                            tablePlan,
                            profileRequest,
                            matchedEntry.StoredRow,
                            matchedEntry.RequestCandidate,
                            matchedEntry.HiddenMemberPaths,
                            finalOrdinal,
                            parentContext.ParentPhysicalIdentityValues,
                            concreteRequestItemNode,
                            _resolvedReferenceLookups
                        );
                        mergedRows.Add(mergedRow);

                        // Recurse into nested children for this matched row. Direct
                        // child collection scopes are enumerated from the compiled
                        // topology (TablePlansInDependencyOrder filtered to direct
                        // topological children of the matched row's JsonScope), not
                        // from request substructure. Even when no candidates are
                        // attached for a child scope, stored visible rows under the
                        // matched parent must still be reverse-coverage-checked.
                        var matchedRowPhysicalIdentity =
                            RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                                tablePlan,
                                mergedRow.Values
                            );
                        var matchedRowContainingAddress = BuildContainingScopeAddress(
                            parentContext,
                            jsonScope,
                            matchedEntry.StoredRow.SemanticIdentityInOrder
                        );
                        var matchedRowContext = new ProfileCollectionWalkerContext(
                            ContainingScopeAddress: matchedRowContainingAddress,
                            ParentPhysicalIdentityValues: matchedRowPhysicalIdentity,
                            RequestSubstructure: matchedEntry.RequestCandidate,
                            ParentRequestNode: concreteRequestItemNode
                        );
                        var matchedNestedOutcome = WalkChildren(matchedRowContext, WalkMode.Normal);
                        if (matchedNestedOutcome is not null)
                        {
                            return matchedNestedOutcome;
                        }
                        break;

                    case ProfileCollectionPlanEntry.HiddenPreserveEntry hiddenEntry:
                        // Clone stored row values and overwrite ordinal column.
                        var cloned = hiddenEntry.StoredRow.ProjectedCurrentRow.Values.ToArray();
                        cloned[tablePlan.CollectionMergePlan!.OrdinalBindingIndex] =
                            new FlattenedWriteValue.Literal(finalOrdinal);
                        var hiddenMergedRow = RelationalWriteRowHelpers.CreateMergedTableRow(
                            tablePlan,
                            cloned
                        );
                        mergedRows.Add(hiddenMergedRow);

                        // Recurse into descendants of this hidden parent in Preserve mode.
                        // The hidden parent's clone-with-recomputed-ordinal merged row above
                        // is the only row inside the hidden subtree whose ordinal is
                        // rewritten; descendants keep their stored ordinals unchanged. The
                        // walker enumerates direct child collection scopes from the compiled
                        // topology (no request substructure to walk under a hidden row).
                        var hiddenContainingAddress = BuildContainingScopeAddress(
                            parentContext,
                            jsonScope,
                            hiddenEntry.StoredRow.SemanticIdentityInOrder
                        );
                        var hiddenPhysicalIdentity =
                            RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                                tablePlan,
                                hiddenEntry.StoredRow.ProjectedCurrentRow.Values
                            );
                        var hiddenContext = new ProfileCollectionWalkerContext(
                            ContainingScopeAddress: hiddenContainingAddress,
                            ParentPhysicalIdentityValues: hiddenPhysicalIdentity,
                            RequestSubstructure: null,
                            ParentRequestNode: null
                        );
                        var hiddenNestedOutcome = WalkChildren(hiddenContext, WalkMode.Preserve);
                        if (hiddenNestedOutcome is not null)
                        {
                            return hiddenNestedOutcome;
                        }
                        break;

                    case ProfileCollectionPlanEntry.VisibleInsertEntry insertEntry:
                        // Rewrite parent key parts, stamp ordinal, build merged row.
                        var withParentKey = RelationalWriteRowHelpers.RewriteParentKeyPartValues(
                            tablePlan,
                            insertEntry.RequestCandidate.Values,
                            parentContext.ParentPhysicalIdentityValues
                        );
                        var stamped = withParentKey.ToArray();
                        stamped[tablePlan.CollectionMergePlan!.OrdinalBindingIndex] =
                            new FlattenedWriteValue.Literal(finalOrdinal);
                        var insertMergedRow = RelationalWriteRowHelpers.CreateMergedTableRow(
                            tablePlan,
                            stamped
                        );
                        mergedRows.Add(insertMergedRow);

                        // Recurse into nested children for this inserted row, seeding
                        // the recursion with the inserted row's PhysicalRowIdentity.
                        // Brand-new rows have no DB descendants, so the nested planner
                        // runs on request-side substructure only.
                        var insertedPhysicalIdentity =
                            RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                                tablePlan,
                                insertMergedRow.Values
                            );
                        var insertedSemanticIdentity = insertEntry.RequestCandidate.SemanticIdentityInOrder;
                        var insertedContainingAddress = BuildContainingScopeAddress(
                            parentContext,
                            jsonScope,
                            insertedSemanticIdentity
                        );
                        // For an insert, the request item node was resolved earlier
                        // when planning ran; recover it from the candidate-key lookup
                        // we already built. The lookup must contain it because every
                        // VisibleInsertEntry corresponds to a creatable VisibleRequestItem.
                        var (_, insertConcreteRequestItemNode) = ResolveCandidateRequestItem(
                            nameof(ProfileCollectionPlanEntry.VisibleInsertEntry),
                            jsonScope,
                            writableRequestBody,
                            insertEntry.RequestCandidate,
                            candidateKeyToRequestItem
                        );
                        var insertedContext = new ProfileCollectionWalkerContext(
                            ContainingScopeAddress: insertedContainingAddress,
                            ParentPhysicalIdentityValues: insertedPhysicalIdentity,
                            RequestSubstructure: insertEntry.RequestCandidate,
                            ParentRequestNode: insertConcreteRequestItemNode
                        );
                        var insertedNestedOutcome = WalkChildren(insertedContext, WalkMode.Normal);
                        if (insertedNestedOutcome is not null)
                        {
                            return insertedNestedOutcome;
                        }
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled plan entry type '{entry?.GetType().Name}' for scope '{jsonScope}'."
                        );
                }
            }

            // Append into the per-table builder rather than constructing a TableState
            // here. The synthesizer finalizes one TableState per touched table after the
            // walk returns, in TablePlansInDependencyOrder. This is the structural
            // prerequisite for nested recursion: multiple recursion calls for the same
            // nested-children table aggregate into one consolidated TableState.
            if (!_tableStateBuilders.TryGetValue(tablePlan.TableModel.Table, out var builder))
            {
                throw new InvalidOperationException(
                    $"Profile collection walker has no pre-seeded TableStateBuilder for table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}'. The synthesizer "
                        + "must seed a builder for every TableWritePlan in TablePlansInDependencyOrder "
                        + "before invoking WalkChildren."
                );
            }
            foreach (var currentRow in currentCollectionRows)
            {
                builder.AddCurrentRow(currentRow);
            }
            foreach (var mergedRow in mergedRows)
            {
                builder.AddMergedRow(mergedRow);
            }
        }

        return null; // All scopes processed without rejection.
    }

    /// <summary>
    /// Implements <see cref="WalkMode.Preserve"/> recursion: for each direct topological
    /// child collection scope of <paramref name="parentContext"/>, emits identity merged-rows
    /// for every current row under the parent's physical identity (same projected values,
    /// stored ordinal preserved unchanged), then recurses in Preserve mode for each
    /// preserved row's descendants.
    /// </summary>
    /// <remarks>
    /// <para>Preserve mode skips the planner and the decider entirely. It never:
    /// canonicalizes URIs, recomputes ordinals, evaluates creatability, or applies
    /// binding-disposition overlays. Every emitted row is byte-identical to the current
    /// row, so the persister's set-difference (current present + merged identical = no-op)
    /// preserves the row in storage.</para>
    /// <para>Both collection child scopes and collection-aligned extension scopes are
    /// preserved here: collection children emit identity merged-rows under the parent's
    /// physical identity and recurse in Preserve mode for each preserved row's
    /// descendants, while <see cref="DbTableKind.CollectionExtensionScope"/> children
    /// dispatch to <see cref="PreserveAlignedExtensionScope"/>, which preserves the
    /// aligned extension row and recurses in Preserve mode under it.</para>
    /// </remarks>
    private ProfileMergeOutcome? WalkChildrenPreserveMode(ProfileCollectionWalkerContext parentContext)
    {
        foreach (var childTablePlan in EnumerateDirectChildCollectionScopes(parentContext))
        {
            if (childTablePlan.TableModel.IdentityMetadata.TableKind is DbTableKind.CollectionExtensionScope)
            {
                var alignedScopeOutcome = PreserveAlignedExtensionScope(parentContext, childTablePlan);
                if (alignedScopeOutcome is not null)
                {
                    return alignedScopeOutcome;
                }

                continue;
            }

            // Locate current rows under this parent's physical identity. The walker's
            // current-rows index is keyed by the *child-relevant* projection of the parent
            // identity, mirroring the Normal-mode lookup symmetry.
            var parentValuesForLookup = ProjectParentValuesForChildLookup(childTablePlan, parentContext);

            if (
                !_currentCollectionRowsByTableAndParentIdentity.TryGetValue(
                    (childTablePlan.TableModel.Table, new ParentIdentityKey(parentValuesForLookup)),
                    out var currentRows
                )
            )
            {
                continue;
            }

            if (!_tableStateBuilders.TryGetValue(childTablePlan.TableModel.Table, out var builder))
            {
                throw new InvalidOperationException(
                    $"Profile collection walker has no pre-seeded TableStateBuilder for table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(childTablePlan)}'. The synthesizer "
                        + "must seed a builder for every TableWritePlan in TablePlansInDependencyOrder "
                        + "before invoking WalkChildren."
                );
            }

            foreach (var currentRow in currentRows)
            {
                // Identity emission: same projected row values, stored ordinal preserved.
                // Both Current and Merged sides get the projected row so the persister's
                // set-difference treats it as unchanged.
                builder.AddCurrentRow(currentRow.ProjectedRow);
                builder.AddMergedRow(currentRow.ProjectedRow);

                // Recurse into descendants of this preserved row, still in Preserve mode.
                var preservedRowContainingAddress = BuildContainingScopeAddress(
                    parentContext,
                    childTablePlan.TableModel.JsonScope.Canonical,
                    currentRow.SemanticIdentityInOrder
                );
                var preservedRowPhysicalIdentity =
                    RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                        childTablePlan,
                        currentRow.ProjectedRow.Values
                    );
                var preservedRowContext = new ProfileCollectionWalkerContext(
                    ContainingScopeAddress: preservedRowContainingAddress,
                    ParentPhysicalIdentityValues: preservedRowPhysicalIdentity,
                    RequestSubstructure: null,
                    ParentRequestNode: null
                );
                var preservedNestedOutcome = WalkChildren(preservedRowContext, WalkMode.Preserve);
                if (preservedNestedOutcome is not null)
                {
                    return preservedNestedOutcome;
                }
            }
        }

        return null;
    }

    private ProfileMergeOutcome? DispatchAlignedExtensionScope(
        ProfileCollectionWalkerContext parentContext,
        TableWritePlan alignedScopeTablePlan
    )
    {
        var synthesizeSeparateScopeInstance =
            _synthesizeSeparateScopeInstance
            ?? throw new InvalidOperationException(
                "ProfileCollectionWalker encountered a CollectionExtensionScope child but no "
                    + "SynthesizeSeparateScopeInstance delegate was supplied."
            );

        var scopeAddress = BuildAlignedScopeAddress(parentContext, alignedScopeTablePlan);
        var requestScope = ProfileMemberGovernanceRules.LookupRequestScope(
            _request.ProfileRequest,
            scopeAddress
        );
        var storedScope = _request.ProfileAppliedContext is null
            ? null
            : ProfileMemberGovernanceRules.LookupStoredScope(_request.ProfileAppliedContext, scopeAddress);

        var attachedScopeData = TryLocateAttachedAlignedScopeData(parentContext, alignedScopeTablePlan);
        var scopedRequestNode = TryResolveAlignedScopeRequestNode(parentContext, alignedScopeTablePlan);
        var result = synthesizeSeparateScopeInstance(
            alignedScopeTablePlan,
            scopeAddress,
            parentContext.ParentPhysicalIdentityValues,
            attachedScopeData is null ? null : SeparateScopeBuffer.From(attachedScopeData),
            scopedRequestNode,
            requestScope,
            storedScope,
            TryLookupCurrentSeparateScopeRowProjection(alignedScopeTablePlan, parentContext)
        );

        return HandleAlignedScopeSynthesisResult(
            alignedScopeTablePlan,
            scopeAddress,
            attachedScopeData,
            scopedRequestNode,
            result
        );
    }

    private ProfileMergeOutcome? PreserveAlignedExtensionScope(
        ProfileCollectionWalkerContext parentContext,
        TableWritePlan alignedScopeTablePlan
    )
    {
        var currentRowProjection = TryLookupCurrentSeparateScopeRowProjection(
            alignedScopeTablePlan,
            parentContext
        );
        if (currentRowProjection is null)
        {
            return null;
        }

        var currentRow = currentRowProjection.ProjectedRow;
        AppendSeparateScopeTableState(
            alignedScopeTablePlan,
            new RelationalWriteMergedTableState(
                alignedScopeTablePlan,
                currentRows: [currentRow],
                mergedRows: [currentRow]
            )
        );

        var alignedContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: BuildAlignedScopeAddress(parentContext, alignedScopeTablePlan),
            ParentPhysicalIdentityValues: RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                alignedScopeTablePlan,
                currentRow.Values
            ),
            RequestSubstructure: null,
            ParentRequestNode: null
        );

        return WalkChildren(alignedContext, WalkMode.Preserve);
    }

    private ProfileMergeOutcome? HandleAlignedScopeSynthesisResult(
        TableWritePlan alignedScopeTablePlan,
        ScopeInstanceAddress alignedScopeAddress,
        CandidateAttachedAlignedScopeData? attachedScopeData,
        JsonNode? scopedRequestNode,
        SeparateScopeSynthesisResult result
    )
    {
        if (result.Rejection is not null)
        {
            return ProfileMergeOutcome.Reject(result.Rejection);
        }

        if (result.IsSkipped)
        {
            return null;
        }

        if (result.TableState is null || result.Outcome is null)
        {
            throw new InvalidOperationException(
                $"Aligned separate-scope synthesis for table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(alignedScopeTablePlan)}' "
                    + "returned neither a rejection, a skip, nor a table state."
            );
        }

        AppendSeparateScopeTableState(alignedScopeTablePlan, result.TableState);

        if (result.Outcome is ProfileSeparateTableMergeOutcome.Delete)
        {
            return null;
        }

        if (
            result.Outcome
            is not (
                ProfileSeparateTableMergeOutcome.Insert
                or ProfileSeparateTableMergeOutcome.Update
                or ProfileSeparateTableMergeOutcome.Preserve
            )
        )
        {
            throw new InvalidOperationException(
                $"Unhandled aligned separate-scope outcome '{result.Outcome}' for table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(alignedScopeTablePlan)}'."
            );
        }

        var recursionSourceRow =
            result.TableState.MergedRows.Length == 1
                ? result.TableState.MergedRows[0]
                : throw new InvalidOperationException(
                    $"Aligned separate-scope outcome '{result.Outcome}' for table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(alignedScopeTablePlan)}' "
                        + $"returned {result.TableState.MergedRows.Length} merged rows; expected exactly one "
                        + "for recursion into child scopes."
                );

        var recursionMode =
            result.Outcome is ProfileSeparateTableMergeOutcome.Preserve ? WalkMode.Preserve : WalkMode.Normal;

        var alignedContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: alignedScopeAddress,
            ParentPhysicalIdentityValues: RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                alignedScopeTablePlan,
                recursionSourceRow.Values
            ),
            RequestSubstructure: recursionMode == WalkMode.Normal ? attachedScopeData : null,
            ParentRequestNode: recursionMode == WalkMode.Normal ? scopedRequestNode : null
        );

        return WalkChildren(alignedContext, recursionMode);
    }

    private void AppendSeparateScopeTableState(
        TableWritePlan tablePlan,
        RelationalWriteMergedTableState tableState
    )
    {
        if (!_tableStateBuilders.TryGetValue(tablePlan.TableModel.Table, out var builder))
        {
            throw new InvalidOperationException(
                $"Profile collection walker has no pre-seeded TableStateBuilder for table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}'. The synthesizer "
                    + "must seed a builder for every TableWritePlan in TablePlansInDependencyOrder "
                    + "before invoking WalkChildren."
            );
        }

        foreach (var currentRow in tableState.CurrentRows)
        {
            builder.AddCurrentRow(currentRow);
        }
        foreach (var mergedRow in tableState.MergedRows)
        {
            builder.AddMergedRow(mergedRow);
        }
    }

    private CurrentSeparateScopeRowProjection? TryLookupCurrentSeparateScopeRowProjection(
        TableWritePlan tablePlan,
        ProfileCollectionWalkerContext parentContext
    )
    {
        var parentValuesForLookup = ProjectParentValuesForChildLookup(tablePlan, parentContext);
        return _currentSeparateScopeRowsByTableAndParentIdentity.TryGetValue(
            (tablePlan.TableModel.Table, new ParentIdentityKey(parentValuesForLookup)),
            out var currentRowProjection
        )
            ? currentRowProjection
            : null;
    }

    private static ImmutableArray<FlattenedWriteValue> ProjectParentValuesForChildLookup(
        TableWritePlan childTablePlan,
        ProfileCollectionWalkerContext parentContext
    )
    {
        var parentSlotMap = ResolveParentKeyPartSlotsForChild(childTablePlan);
        if (parentSlotMap.Length == 0)
        {
            return ImmutableArray<FlattenedWriteValue>.Empty;
        }

        var parentValues = parentContext.ParentPhysicalIdentityValues;
        var projected = new FlattenedWriteValue[parentSlotMap.Length];
        for (var i = 0; i < parentSlotMap.Length; i++)
        {
            var slot = parentSlotMap[i];
            if (slot < 0 || slot >= parentValues.Length)
            {
                throw new InvalidOperationException(
                    $"Compiled ParentKeyPart slot {slot} for child table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(childTablePlan)}' is out of range "
                        + $"for parent physical-identity buffer of length {parentValues.Length}. "
                        + "Walker invariant violated: structural drift between compiled write plan "
                        + "and walker parent-context shape."
                );
            }
            projected[i] = parentValues[slot];
        }
        return ImmutableArray.Create(projected);
    }

    private static ScopeInstanceAddress BuildAlignedScopeAddress(
        ProfileCollectionWalkerContext parentContext,
        TableWritePlan alignedScopeTablePlan
    ) =>
        new(
            alignedScopeTablePlan.TableModel.JsonScope.Canonical,
            parentContext.ContainingScopeAddress.AncestorCollectionInstances
        );

    private static CandidateAttachedAlignedScopeData? TryLocateAttachedAlignedScopeData(
        ProfileCollectionWalkerContext parentContext,
        TableWritePlan alignedScopeTablePlan
    )
    {
        if (
            parentContext.RequestSubstructure is not CollectionWriteCandidate parentCandidate
            || parentCandidate.AttachedAlignedScopeData.IsDefaultOrEmpty
        )
        {
            return null;
        }

        var alignedScope = alignedScopeTablePlan.TableModel.JsonScope.Canonical;
        foreach (var scopeData in parentCandidate.AttachedAlignedScopeData)
        {
            if (
                ReferenceEquals(scopeData.TableWritePlan, alignedScopeTablePlan)
                || scopeData.TableWritePlan.TableModel.Table.Equals(alignedScopeTablePlan.TableModel.Table)
                || string.Equals(
                    scopeData.TableWritePlan.TableModel.JsonScope.Canonical,
                    alignedScope,
                    StringComparison.Ordinal
                )
            )
            {
                return scopeData;
            }
        }

        return null;
    }

    private JsonNode? TryResolveAlignedScopeRequestNode(
        ProfileCollectionWalkerContext parentContext,
        TableWritePlan alignedScopeTablePlan
    )
    {
        var parentScope = parentContext.ContainingScopeAddress.JsonScope;
        var alignedScope = alignedScopeTablePlan.TableModel.JsonScope.Canonical;

        // Mirrored shape: the aligned scope lives at "$._ext.<extName>.<parentRemainder>._ext.<extName>"
        // and is materialized by the flattener via TryNavigateConcreteNode against the request
        // body root using the parent collection candidate's ordinal path. Dispatch mirrors that
        // navigation here so the key-unification resolver receives the mirrored object instead
        // of trying to evaluate a relative path against the base parent's request node.
        if (IsDirectMirroredCollectionExtensionScopeChild(parentScope, alignedScope))
        {
            if (parentContext.RequestSubstructure is not CollectionWriteCandidate parentCandidate)
            {
                return null;
            }

            var alignedScopeSegments = RelationalJsonPathSupport.GetRestrictedSegments(
                alignedScopeTablePlan.TableModel.JsonScope
            );

            return RelationalWriteFlattener.TryNavigateConcreteNode(
                _request.WritableRequestBody,
                alignedScopeSegments,
                parentCandidate.OrdinalPath.AsSpan(),
                out var mirroredNode
            )
                ? mirroredNode
                : null;
        }

        if (parentContext.ParentRequestNode is null)
        {
            return null;
        }

        if (!alignedScope.StartsWith(parentScope, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Aligned scope '{alignedScope}' is not under parent scope '{parentScope}'."
            );
        }

        var remainder = alignedScope[parentScope.Length..];
        if (!remainder.StartsWith('.') && !remainder.StartsWith('['))
        {
            throw new InvalidOperationException(
                $"Aligned scope '{alignedScope}' is not a relative child of parent scope '{parentScope}'."
            );
        }

        var relativePath = new JsonPathExpression("$" + remainder, []);
        return RelationalWriteFlattener.TryGetRelativeLeafNode(
            parentContext.ParentRequestNode,
            relativePath,
            out var scopeNode
        )
            ? scopeNode
            : null;
    }

    /// <summary>
    /// Builds a lookup from candidate identity key (the shared
    /// <see cref="SemanticIdentityKeys"/> shape used by the planner) to the matching
    /// <see cref="VisibleRequestCollectionItem"/>. Used to resolve the concrete request item
    /// node for matched-update entries.
    /// </summary>
    private static Dictionary<string, VisibleRequestCollectionItem> BuildCandidateKeyToRequestItemLookup(
        ImmutableArray<VisibleRequestCollectionItem> visibleRequestItems
    )
    {
        var result = new Dictionary<string, VisibleRequestCollectionItem>(StringComparer.Ordinal);
        foreach (var item in visibleRequestItems)
        {
            var addressKey = SemanticIdentityKeys.BuildKey(item.Address.SemanticIdentityInOrder);
            result.TryAdd(addressKey, item);
        }

        return result;
    }

    /// <summary>
    /// Looks up the <see cref="VisibleRequestCollectionItem"/> matching the supplied
    /// <paramref name="requestCandidate"/> in <paramref name="candidateKeyToRequestItem"/>
    /// and resolves the concrete request item JSON node from the writable request body.
    /// Both the lookup miss and the navigation miss fail closed with an
    /// <see cref="InvalidOperationException"/> tagged by <paramref name="entryKind"/> so
    /// each plan-entry switch arm gets a self-describing error without duplicating the
    /// resolution chain.
    /// </summary>
    private static (
        VisibleRequestCollectionItem RequestItem,
        JsonNode ConcreteRequestItemNode
    ) ResolveCandidateRequestItem(
        string entryKind,
        string jsonScope,
        JsonNode writableRequestBody,
        CollectionWriteCandidate requestCandidate,
        IReadOnlyDictionary<string, VisibleRequestCollectionItem> candidateKeyToRequestItem
    )
    {
        var candidateKey = SemanticIdentityKeys.BuildKey(requestCandidate);
        if (!candidateKeyToRequestItem.TryGetValue(candidateKey, out var requestItem))
        {
            throw new InvalidOperationException(
                $"{entryKind} for scope '{jsonScope}' could not locate a "
                    + "VisibleRequestCollectionItem for the candidate's semantic identity."
            );
        }

        var concreteRequestItemNode = RelationalWriteProfileMergeSynthesizer.ResolveCollectionItemNode(
            writableRequestBody,
            requestItem.RequestJsonPath
        );

        if (concreteRequestItemNode is null)
        {
            throw new InvalidOperationException(
                $"{entryKind} for scope '{jsonScope}' could not navigate "
                    + $"the request body to item path '{requestItem.RequestJsonPath}'. "
                    + "The visible request item must correspond to an existing array element."
            );
        }

        return (requestItem, concreteRequestItemNode);
    }

    /// <summary>
    /// Returns the collection candidates available under the given parent context. For the
    /// root parent, this is the <see cref="RootWriteRowBuffer"/>'s top-level
    /// <c>CollectionCandidates</c>. For a matched/inserted collection-row parent, this is
    /// the <see cref="CollectionWriteCandidate"/>'s nested <c>CollectionCandidates</c>.
    /// For a root-extension parent, this is the <see cref="RootExtensionWriteRowBuffer"/>'s
    /// child <c>CollectionCandidates</c>. For an aligned-extension scope parent, this is
    /// the <see cref="CandidateAttachedAlignedScopeData"/>'s child
    /// <c>CollectionCandidates</c>. Other <c>RequestSubstructure</c> shapes and
    /// <c>null</c> request substructure return an empty array.
    /// </summary>
    private static ImmutableArray<CollectionWriteCandidate> ResolveCollectionCandidatesForParent(
        ProfileCollectionWalkerContext parentContext
    ) =>
        parentContext.RequestSubstructure switch
        {
            RootWriteRowBuffer rootBuffer => rootBuffer.CollectionCandidates,
            RootExtensionWriteRowBuffer extensionBuffer => extensionBuffer.CollectionCandidates,
            CollectionWriteCandidate candidate => candidate.CollectionCandidates.IsDefaultOrEmpty
                ? ImmutableArray<CollectionWriteCandidate>.Empty
                : candidate.CollectionCandidates,
            CandidateAttachedAlignedScopeData attachedScope => attachedScope
                .CollectionCandidates
                .IsDefaultOrEmpty
                ? ImmutableArray<CollectionWriteCandidate>.Empty
                : attachedScope.CollectionCandidates,
            _ => ImmutableArray<CollectionWriteCandidate>.Empty,
        };

    /// <summary>
    /// Enumerates direct topological child collection scopes of <paramref name="parentContext"/>.
    /// Direct child = a base- or extension-collection table whose JsonScope is the parent's
    /// JsonScope plus exactly one additional path segment (e.g., <c>$.parents[*]</c> is a
    /// direct child of <c>$</c>; <c>$.parents[*].children[*]</c> is a direct child of
    /// <c>$.parents[*]</c>). Iterates in compiled <c>TablePlansInDependencyOrder</c> so the
    /// first-rejection-wins semantics carry over from the root case.
    /// </summary>
    private IEnumerable<TableWritePlan> EnumerateDirectChildCollectionScopes(
        ProfileCollectionWalkerContext parentContext
    )
    {
        var parentScope = parentContext.ContainingScopeAddress.JsonScope;
        foreach (var tablePlan in _request.WritePlan.TablePlansInDependencyOrder)
        {
            var tableKind = tablePlan.TableModel.IdentityMetadata.TableKind;
            if (
                tableKind
                is not (
                    DbTableKind.Collection
                    or DbTableKind.ExtensionCollection
                    or DbTableKind.CollectionExtensionScope
                )
            )
            {
                continue;
            }

            var childScope = tablePlan.TableModel.JsonScope.Canonical;
            if (
                tableKind is DbTableKind.CollectionExtensionScope
                    ? IsDirectCollectionExtensionScopeChild(parentScope, childScope)
                        || IsDirectMirroredCollectionExtensionScopeChild(parentScope, childScope)
                    : IsTopologicallyOwnedCollectionChild(parentScope, childScope)
            )
            {
                yield return tablePlan;
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="childScope"/> is a base- or extension-
    /// collection child whose nearest table-backed ancestor JsonScope equals
    /// <paramref name="parentScope"/>. Generalizes <see cref="IsDirectTopologicalChild"/>
    /// to admit shapes where the immediate JSON parent of the child collection is an
    /// inlined non-collection scope (e.g.
    /// <c>$.parents[*].detail.children[*]</c> from <c>$.parents[*]</c>): the
    /// <c>detail</c> intermediate has no backing table plan, so the child must dispatch
    /// from the parent collection row whose JsonScope is its nearest table-backed
    /// ancestor.
    /// </summary>
    private bool IsTopologicallyOwnedCollectionChild(string parentScope, string childScope)
    {
        if (IsDirectTopologicalChild(parentScope, childScope))
        {
            return true;
        }

        if (!_tableBackedJsonScopes.Contains(parentScope))
        {
            return false;
        }

        return string.Equals(
            ResolveNearestTableBackedAncestorScope(childScope),
            parentScope,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// Walks up from a collection scope's immediate JSON parent (per
    /// <see cref="ComputeParentJsonScope"/>) toward the document root, stripping inlined
    /// property segments until a table-backed scope is reached. The root scope <c>$</c>
    /// is always table-backed, so the walk is guaranteed to terminate.
    /// </summary>
    private string ResolveNearestTableBackedAncestorScope(string childScope)
    {
        var ancestor = ComputeParentJsonScope(childScope);
        while (
            !_tableBackedJsonScopes.Contains(ancestor)
            && !string.Equals(ancestor, "$", StringComparison.Ordinal)
        )
        {
            var lastDot = ancestor.LastIndexOf('.');
            if (lastDot < 0)
            {
                return "$";
            }
            ancestor = ancestor[..lastDot];
        }
        return ancestor;
    }

    /// <summary>
    /// Returns the effective parent <see cref="ScopeInstanceAddress"/> for dispatching the
    /// child collection at <paramref name="childJsonScope"/> from
    /// <paramref name="parentContext"/>. When the child's immediate JSON parent equals the
    /// parent context's JsonScope (the standard direct-child case), the parent's own
    /// containing-scope address is returned unchanged. When the child's immediate JSON
    /// parent is an inlined non-collection scope below the parent context (e.g. child
    /// <c>$.parents[*].detail.children[*]</c> dispatched from <c>$.parents[*]</c>), the
    /// returned address carries the inlined JsonScope and the parent context's ancestor
    /// chain — the inlined scope contributes no
    /// <see cref="AncestorCollectionInstance"/> because it is not a collection instance.
    /// This matches Core's <see cref="CollectionRowAddress.ParentAddress"/> shape for
    /// such children, which the per-(scope, parent-instance) visible-stored /
    /// visible-request indexes are keyed by.
    /// </summary>
    private static ScopeInstanceAddress ResolveEffectiveChildParentScopeAddress(
        ProfileCollectionWalkerContext parentContext,
        string childJsonScope
    )
    {
        var immediateParentJsonScope = ComputeParentJsonScope(childJsonScope);
        if (
            string.Equals(
                immediateParentJsonScope,
                parentContext.ContainingScopeAddress.JsonScope,
                StringComparison.Ordinal
            )
        )
        {
            return parentContext.ContainingScopeAddress;
        }

        return new ScopeInstanceAddress(
            immediateParentJsonScope,
            parentContext.ContainingScopeAddress.AncestorCollectionInstances
        );
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="childScope"/> is the parent's JsonScope plus
    /// exactly one additional path segment. The child's remainder must start with a
    /// separator (a dot or array bracket) and contain no further dotted property steps.
    /// </summary>
    /// <remarks>
    /// Examples:
    /// <list type="bullet">
    /// <item><description><c>"$"</c> + <c>".parents[*]"</c> → direct child <c>"$.parents[*]"</c>.</description></item>
    /// <item><description><c>"$.parents[*]"</c> + <c>".children[*]"</c> → direct child <c>"$.parents[*].children[*]"</c>.</description></item>
    /// <item><description><c>"$"</c> + <c>".parents[*].children[*]"</c> → not a direct child (two segments).</description></item>
    /// </list>
    /// </remarks>
    private static bool IsDirectTopologicalChild(string parentScope, string childScope)
    {
        if (!childScope.StartsWith(parentScope, StringComparison.Ordinal))
        {
            return false;
        }
        if (childScope.Length == parentScope.Length)
        {
            return false; // Same scope, not a child.
        }
        var remainder = childScope[parentScope.Length..];
        if (!remainder.StartsWith('.') && !remainder.StartsWith('['))
        {
            return false;
        }
        // After stripping the leading separator, the remainder must contain no further
        // dot-separated segment. A single dotted segment like ".children[*]" becomes
        // "children[*]" — no inner '.', so it's a direct child.
        var afterSep = remainder.StartsWith('.') ? remainder[1..] : remainder;
        return !afterSep.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> when a <see cref="DbTableKind.CollectionExtensionScope"/> is the
    /// aligned 1:1 extension scope directly attached to a collection-row parent. The
    /// aligned scope path is a special topological child shape: it appends
    /// <c>._ext.&lt;extensionName&gt;</c> to the parent collection scope, not another array
    /// segment.
    /// </summary>
    private static bool IsDirectCollectionExtensionScopeChild(string parentScope, string childScope)
    {
        if (!parentScope.Contains("[*]", StringComparison.Ordinal))
        {
            return false;
        }
        if (!childScope.StartsWith(parentScope, StringComparison.Ordinal))
        {
            return false;
        }
        if (childScope.Length == parentScope.Length)
        {
            return false;
        }

        const string alignedExtensionPrefix = "._ext.";
        var remainder = childScope[parentScope.Length..];
        if (!remainder.StartsWith(alignedExtensionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var extensionName = remainder[alignedExtensionPrefix.Length..];
        return extensionName.Length > 0
            && !extensionName.Contains('.', StringComparison.Ordinal)
            && !extensionName.Contains('[', StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="childScope"/> is a mirrored collection-aligned
    /// extension scope of the form
    /// <c>$._ext.&lt;extensionName&gt;.&lt;parentRemainder&gt;._ext.&lt;extensionName&gt;</c>
    /// whose implied parent collection scope equals <paramref name="parentScope"/>.
    /// Delegates the strict shape contract to <see cref="AlignedExtensionScopeSupport"/>
    /// so the flattener and the walker classify mirrored scopes identically.
    /// </summary>
    internal static bool IsDirectMirroredCollectionExtensionScopeChild(string parentScope, string childScope)
    {
        if (
            !parentScope.StartsWith("$.", StringComparison.Ordinal)
            || !parentScope.Contains("[*]", StringComparison.Ordinal)
        )
        {
            return false;
        }

        return AlignedExtensionScopeSupport.Classify(childScope)
                is { IsMirrored: true, ParentCollectionScope: var impliedParentScope }
            && string.Equals(impliedParentScope, parentScope, StringComparison.Ordinal);
    }

    /// <summary>
    /// Structurally builds the <see cref="ScopeInstanceAddress"/> that direct children of a
    /// matched/inserted/hidden collection row see as their <c>Address.ParentAddress</c>. The
    /// new address's JsonScope is the row's collection JsonScope; its
    /// <see cref="ScopeInstanceAddress.AncestorCollectionInstances"/> is the parent's
    /// existing ancestor chain extended by an <see cref="AncestorCollectionInstance"/> for
    /// this row.
    /// </summary>
    private static ScopeInstanceAddress BuildContainingScopeAddress(
        ProfileCollectionWalkerContext parentContext,
        string rowJsonScope,
        ImmutableArray<SemanticIdentityPart> rowSemanticIdentity
    )
    {
        var extendedAncestors = parentContext.ContainingScopeAddress.AncestorCollectionInstances.Add(
            new AncestorCollectionInstance(rowJsonScope, rowSemanticIdentity)
        );
        return new ScopeInstanceAddress(rowJsonScope, extendedAncestors);
    }

    // ── Test-only accessors ────────────────────────────────────────────────
    //
    // These mirror the four private indexes for unit-test verification. The indexes are
    // consumed by WalkChildren for both the root case and nested cases.

    /// <summary>For testing only. Exposes the per-merge collection-row index.</summary>
    internal IReadOnlyDictionary<
        (DbTableName Table, ParentIdentityKey ParentKey),
        IReadOnlyList<CurrentCollectionRowProjection>
    > CurrentCollectionRowsByTableAndParentIdentity => _currentCollectionRowsByTableAndParentIdentity;

    /// <summary>For testing only. Exposes the per-merge separate-scope-row index.</summary>
    internal IReadOnlyDictionary<
        (DbTableName Table, ParentIdentityKey ParentKey),
        CurrentSeparateScopeRowProjection?
    > CurrentSeparateScopeRowsByTableAndParentIdentity => _currentSeparateScopeRowsByTableAndParentIdentity;

    /// <summary>For testing only. Exposes the visible-stored-row index.</summary>
    internal IReadOnlyDictionary<
        (string ChildJsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<VisibleStoredCollectionRow>
    > VisibleStoredRowsByChildScopeAndParent => _visibleStoredRowsByChildScopeAndParent;

    /// <summary>For testing only. Exposes the visible-request-item index.</summary>
    internal IReadOnlyDictionary<
        (string ChildJsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<VisibleRequestCollectionItem>
    > VisibleRequestItemsByChildScopeAndParent => _visibleRequestItemsByChildScopeAndParent;

    /// <summary>
    /// For testing only. Exposes the per-(scope, parent containing-scope address) current-
    /// row partition index. Consumed by the canonicalize helpers to do partitioned
    /// positional fallback within a single parent partition.
    /// </summary>
    internal IReadOnlyDictionary<
        (string JsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<CurrentCollectionRowSnapshot>
    > CurrentRowsByJsonScopeAndParent => _currentRowsByJsonScopeAndParent;

    // ── Index construction ─────────────────────────────────────────────────
    //
    // The walker owns the per-merge projection indexes. WalkChildren reads them at
    // dispatch time without re-projecting hydrated rows on every call.

    private static IReadOnlyDictionary<
        string,
        ImmutableArray<string>
    > BuildSemanticIdentityPathsByCollectionScope(RelationalWriteProfileMergeRequest request)
    {
        var result = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);

        foreach (var item in request.ProfileRequest.VisibleRequestCollectionItems)
        {
            RecordCollectionRowAddressShape(result, item.Address);
        }

        if (request.ProfileAppliedContext is not null)
        {
            foreach (var row in request.ProfileAppliedContext.VisibleStoredCollectionRows)
            {
                RecordCollectionRowAddressShape(result, row.Address);
            }

            foreach (var storedScope in request.ProfileAppliedContext.StoredScopeStates)
            {
                RecordAncestorShapes(result, storedScope.Address);
            }
        }

        foreach (var requestScope in request.ProfileRequest.RequestScopeStates)
        {
            RecordAncestorShapes(result, requestScope.Address);
        }

        return result;
    }

    private static void RecordCollectionRowAddressShape(
        Dictionary<string, ImmutableArray<string>> result,
        CollectionRowAddress address
    )
    {
        RecordIdentityShape(result, address.JsonScope, address.SemanticIdentityInOrder);
        RecordAncestorShapes(result, address.ParentAddress);
    }

    private static void RecordAncestorShapes(
        Dictionary<string, ImmutableArray<string>> result,
        ScopeInstanceAddress address
    )
    {
        foreach (var ancestor in address.AncestorCollectionInstances)
        {
            RecordIdentityShape(result, ancestor.JsonScope, ancestor.SemanticIdentityInOrder);
        }
    }

    private static void RecordIdentityShape(
        Dictionary<string, ImmutableArray<string>> result,
        string jsonScope,
        ImmutableArray<SemanticIdentityPart> identity
    )
    {
        if (identity.IsDefaultOrEmpty || result.ContainsKey(jsonScope))
        {
            return;
        }

        result[jsonScope] = identity.Select(part => part.RelativePath).ToImmutableArray();
    }

    private static IReadOnlyDictionary<
        (DbTableName Table, ParentIdentityKey ParentKey),
        IReadOnlyList<CurrentCollectionRowProjection>
    > BuildCurrentCollectionRowsIndex(
        RelationalWriteProfileMergeRequest request,
        IReadOnlyDictionary<string, ImmutableArray<string>> semanticIdentityPathsByCollectionScope
    )
    {
        var result = new Dictionary<(DbTableName, ParentIdentityKey), List<CurrentCollectionRowProjection>>();

        var currentState = request.CurrentState;
        if (currentState is null)
        {
            return new Dictionary<
                (DbTableName, ParentIdentityKey),
                IReadOnlyList<CurrentCollectionRowProjection>
            >();
        }

        foreach (var tablePlan in request.WritePlan.TablePlansInDependencyOrder)
        {
            var kind = tablePlan.TableModel.IdentityMetadata.TableKind;
            if (kind is not (DbTableKind.Collection or DbTableKind.ExtensionCollection))
            {
                continue;
            }

            var hydrated = currentState.TableRowsInDependencyOrder.FirstOrDefault(h =>
                h.TableModel.Table.Equals(tablePlan.TableModel.Table)
            );
            if (hydrated is null || hydrated.Rows.Count == 0)
            {
                continue;
            }

            var mergePlan =
                tablePlan.CollectionMergePlan
                ?? throw new InvalidOperationException(
                    $"Collection table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' "
                        + "does not have a compiled collection merge plan."
                );

            var parentBindingIndexes = tablePlan
                .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(col =>
                    RelationalWriteMergeSupport.FindBindingIndex(tablePlan, col)
                )
                .ToArray();

            var projectedAll = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydrated.Rows);

            for (var rowIndex = 0; rowIndex < projectedAll.Length; rowIndex++)
            {
                var projectedRow = projectedAll[rowIndex];
                var hydratedRow = hydrated.Rows[rowIndex];

                // Index key is the projected parent-locator values in
                // ImmediateParentScopeLocatorColumns order. Lookup at WalkChildren time
                // builds the same shape from parentPhysicalRowIdentityValues using the
                // walker's ResolveParentKeyPartSlotsForChild helper, so both sides are
                // symmetric.
                var parentValues = parentBindingIndexes
                    .Select(bi => projectedRow.Values[bi])
                    .ToImmutableArray();

                // Walker-inline DB-row SemanticIdentityPart builder. Counterparts:
                // RelationalWriteFlattener.MaterializeSemanticIdentityParts (request-side,
                // presence-probed against the request JSON) and
                // RelationalWriteNoProfileMerge.BuildCurrentRowSemanticIdentityParts (DB-row,
                // pulls raw values via ExtractLiteralValue). The three remain separate
                // because this variant prefers Core-emitted identity paths from
                // semanticIdentityPathsByCollectionScope before falling back to scope-relative
                // normalization, which the other two do not. Only the shared scope-relative
                // path normalization is centralized in RelationalWriteMergeSupport.ToScopeRelativePath.
                var identityParts = mergePlan
                    .SemanticIdentityBindings.Select(
                        (binding, identityPartIndex) =>
                        {
                            var bindingVal = projectedRow.Values[binding.BindingIndex];
                            var rawValue = bindingVal is FlattenedWriteValue.Literal valLit
                                ? valLit.Value
                                : null;
                            JsonNode? jsonNode = rawValue is null ? null : JsonValue.Create(rawValue);
                            var identityPath =
                                semanticIdentityPathsByCollectionScope.TryGetValue(
                                    tablePlan.TableModel.JsonScope.Canonical,
                                    out var emittedPaths
                                )
                                && identityPartIndex < emittedPaths.Length
                                    ? emittedPaths[identityPartIndex]
                                    : RelationalWriteMergeSupport.ToScopeRelativePath(
                                        binding.RelativePath.Canonical,
                                        tablePlan.TableModel.JsonScope.Canonical
                                    );
                            // Presence mirrors Core's stored-side projection. The Core path
                            // is: DocumentReconstituter.EmitScalars omits SQL NULL columns
                            // from the reconstituted JSON document, then
                            // AddressDerivationEngine.ReadSemanticIdentity walks that
                            // reconstituted JSON and returns IsPresent=true only when the
                            // property exists at the canonical relative path (explicit JSON
                            // null is also IsPresent=true). For DB-projected rows the
                            // walker has only the column value; a non-null value mirrors a
                            // present property in the reconstituted JSON, while a SQL NULL
                            // mirrors an omitted property — so IsPresent must track
                            // rawValue is not null to keep the walker's
                            // CurrentCollectionRowSnapshot key consistent with the Core
                            // VisibleStoredCollectionRow key under presence-aware
                            // SemanticIdentityKeys.BuildKey. The earlier hardcoded
                            // IsPresent=true broke reverse stored coverage on nullable
                            // identity columns whenever the column persisted SQL NULL.
                            return new SemanticIdentityPart(
                                identityPath,
                                jsonNode,
                                IsPresent: rawValue is not null
                            );
                        }
                    )
                    .ToImmutableArray();

                var ordinalLiteral = projectedRow.Values[mergePlan.OrdinalBindingIndex];
                var storedOrdinal = ExtractRequiredInt32(ordinalLiteral, tablePlan, "stored ordinal");

                // Stable row identity (long) and column-name-keyed hydrated row are required
                // by the planner-input contract for the walker's top-level body. Both are
                // derivable here at index-construction time:
                //   StableRowIdentity: ProjectedRow.Values[mergePlan.StableRowIdentityBindingIndex].
                //   CurrentRowByColumnName: covers every column on the table model (including
                //     UnifiedAlias columns absent from ColumnBindings) for hidden key-unification
                //     preservation in the matched-row overlay.
                var stableRowIdentity = ExtractRequiredInt64(
                    projectedRow.Values[mergePlan.StableRowIdentityBindingIndex],
                    tablePlan,
                    "stable row identity"
                );
                var currentRowByColumnName = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
                    tablePlan.TableModel,
                    hydratedRow
                );

                var key = (tablePlan.TableModel.Table, new ParentIdentityKey(parentValues));
                if (!result.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    result[key] = bucket;
                }
                bucket.Add(
                    new CurrentCollectionRowProjection(
                        ProjectedRow: projectedRow,
                        SemanticIdentityInOrder: identityParts,
                        StoredOrdinal: storedOrdinal,
                        ParentPhysicalIdentityValues: parentValues,
                        StableRowIdentity: stableRowIdentity,
                        CurrentRowByColumnName: currentRowByColumnName
                    )
                );
            }
        }

        // Materialize each bucket in stored-ordinal order so consumers get a deterministic
        // shape: the planner's ValidateStoredOrdinalOrder relies on ascending order.
        var finalized =
            new Dictionary<(DbTableName, ParentIdentityKey), IReadOnlyList<CurrentCollectionRowProjection>>();
        foreach (var (key, bucket) in result)
        {
            finalized[key] = [.. bucket.OrderBy(p => p.StoredOrdinal)];
        }
        return finalized;
    }

    private static IReadOnlyDictionary<
        (DbTableName Table, ParentIdentityKey ParentKey),
        CurrentSeparateScopeRowProjection?
    > BuildCurrentSeparateScopeRowsIndex(RelationalWriteProfileMergeRequest request)
    {
        var result = new Dictionary<(DbTableName, ParentIdentityKey), CurrentSeparateScopeRowProjection?>();

        var currentState = request.CurrentState;
        if (currentState is null)
        {
            return result;
        }

        foreach (var tablePlan in request.WritePlan.TablePlansInDependencyOrder)
        {
            var kind = tablePlan.TableModel.IdentityMetadata.TableKind;
            if (kind is not (DbTableKind.RootExtension or DbTableKind.CollectionExtensionScope))
            {
                continue;
            }

            var hydrated = currentState.TableRowsInDependencyOrder.FirstOrDefault(h =>
                h.TableModel.Table.Equals(tablePlan.TableModel.Table)
            );
            if (hydrated is null || hydrated.Rows.Count == 0)
            {
                continue;
            }

            var parentBindingIndexes = tablePlan
                .TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Select(col =>
                    RelationalWriteMergeSupport.FindBindingIndex(tablePlan, col)
                )
                .ToArray();

            var projectedAll = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydrated.Rows);

            // Group projected rows by their parent identity. >1 row per (table, parent)
            // is a fail-closed shape error: a 1:1 separate scope cannot have a duplicate
            // under one parent.
            var byParent = new Dictionary<ParentIdentityKey, List<int>>();
            for (var rowIndex = 0; rowIndex < projectedAll.Length; rowIndex++)
            {
                var projectedRow = projectedAll[rowIndex];
                var parentValues = parentBindingIndexes
                    .Select(bi => projectedRow.Values[bi])
                    .ToImmutableArray();

                var pk = new ParentIdentityKey(parentValues);
                if (!byParent.TryGetValue(pk, out var indexes))
                {
                    indexes = [];
                    byParent[pk] = indexes;
                }
                indexes.Add(rowIndex);
            }

            foreach (var (parentKey, rowIndexes) in byParent)
            {
                if (rowIndexes.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"Separate-scope table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' "
                            + $"has {rowIndexes.Count} current rows under a single parent identity; "
                            + "a 1:1 separate scope cannot have duplicates under one parent."
                    );
                }

                var rowIndex = rowIndexes[0];
                var projectedRow = projectedAll[rowIndex];
                var hydratedRow = hydrated.Rows[rowIndex];
                var columnNameProjection = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
                    tablePlan.TableModel,
                    hydratedRow
                );

                result[(tablePlan.TableModel.Table, parentKey)] = new CurrentSeparateScopeRowProjection(
                    ProjectedRow: projectedRow,
                    ColumnNameProjection: columnNameProjection,
                    ParentPhysicalIdentityValues: parentKey.Values
                );
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a <c>JsonScope → TableWritePlan</c> dictionary used by ancestor-identity
    /// canonicalization at index-build time. Ancestor identities arrive in raw Core-emitted
    /// form (descriptor URIs / document-reference natural keys); the canonicalize helpers
    /// need the ancestor's table plan to discover descriptor / document-reference identity
    /// bindings.
    /// </summary>
    private static IReadOnlyDictionary<string, TableWritePlan> BuildTablePlanByJsonScope(
        RelationalWriteProfileMergeRequest request
    )
    {
        var result = new Dictionary<string, TableWritePlan>(StringComparer.Ordinal);
        foreach (var tablePlan in request.WritePlan.TablePlansInDependencyOrder)
        {
            result[tablePlan.TableModel.JsonScope.Canonical] = tablePlan;
        }
        return result;
    }

    /// <summary>
    /// Builds a <c>JsonScope → current rows</c> dictionary used by ancestor descriptor
    /// canonicalization when a URI is missing from the request-cycle resolved-reference
    /// cache (e.g. delete-by-absence parents whose descriptor URI was never resolved as
    /// part of the current request body). The ancestor canonicalize helper scans these
    /// rows for a <em>unique</em> scalar-match and copies the descriptor id back.
    /// </summary>
    /// <remarks>
    /// This index is keyed by JsonScope alone and covers all current rows on the table —
    /// index-build time has no per-(scope, parent-instance) partitioning available
    /// because we are constructing the parent-keyed indexes themselves. The scope-wide
    /// scan is used only for the unique-scalar-match step. If the scalar match is
    /// ambiguous, callers fall through to count-equal positional pairing within a single
    /// parent partition (see <see cref="BuildCurrentRowsByJsonScopeAndParent"/>); when
    /// neither path resolves the URI the helper fails closed at the call site rather
    /// than leaving the URI form in place, because a URI-form ancestor identity
    /// silently mis-buckets the row in the walker's address-keyed visible-stored
    /// index.
    /// </remarks>
    private static IReadOnlyDictionary<
        string,
        ImmutableArray<CurrentCollectionRowSnapshot>
    > BuildCurrentRowsByJsonScope(
        RelationalWriteProfileMergeRequest request,
        IReadOnlyDictionary<
            (DbTableName Table, ParentIdentityKey ParentKey),
            IReadOnlyList<CurrentCollectionRowProjection>
        > collectionRowsIndex
    )
    {
        var result = new Dictionary<string, ImmutableArray<CurrentCollectionRowSnapshot>>(
            StringComparer.Ordinal
        );

        if (request.CurrentState is null)
        {
            return result;
        }

        var byTable = new Dictionary<DbTableName, List<CurrentCollectionRowSnapshot>>();
        foreach (var ((table, _), bucket) in collectionRowsIndex)
        {
            if (!byTable.TryGetValue(table, out var rows))
            {
                rows = [];
                byTable[table] = rows;
            }
            foreach (var projection in bucket)
            {
                rows.Add(projection.ToSnapshot());
            }
        }

        var jsonScopeByTable = request.WritePlan.TablePlansInDependencyOrder.ToDictionary(
            tp => tp.TableModel.Table,
            tp => tp.TableModel.JsonScope.Canonical
        );

        foreach (var (table, rows) in byTable)
        {
            if (jsonScopeByTable.TryGetValue(table, out var jsonScope))
            {
                result[jsonScope] = [.. rows];
            }
        }

        return result;
    }

    /// <summary>
    /// Per-(scope, parent containing-scope address) index of current rows. Gives
    /// ancestor descriptor canonicalization a way to do count-equal positional
    /// fallback within a single parent partition. The scope-wide index alone intermixes
    /// partitions in dictionary-iteration order, so positional pairing across partitions
    /// is unsafe; this index slices each scope by the same canonical parent address that
    /// stored rows carry once their ancestor chain is canonicalized.
    /// <para>
    /// Built in <c>TablePlansInDependencyOrder</c> so each row's containing-scope address
    /// is computed once parent rows have been registered, and reused for descendants.
    /// Each row's containing-scope address is the parent's containing-scope address
    /// extended by an <see cref="AncestorCollectionInstance"/> for the row itself — the
    /// same shape <see cref="BuildContainingScopeAddress"/> produces during walk and that
    /// stored rows' <c>Address.ParentAddress</c> carries upstream.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<
        (string JsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<CurrentCollectionRowSnapshot>
    > BuildCurrentRowsByJsonScopeAndParent(
        RelationalWriteProfileMergeRequest request,
        IReadOnlyDictionary<
            (DbTableName Table, ParentIdentityKey ParentKey),
            IReadOnlyList<CurrentCollectionRowProjection>
        > collectionRowsIndex
    )
    {
        var result = new Dictionary<
            (string, ScopeInstanceAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        >(ChildScopeAndParentComparer.Instance);

        if (request.CurrentState is null)
        {
            return result;
        }

        var tableModels = request.WritePlan.TablePlansInDependencyOrder.Select(tp => tp.TableModel).ToList();
        var tableByJsonScope = tableModels.ToDictionary(
            tm => tm.JsonScope.Canonical,
            tm => tm.Table,
            StringComparer.Ordinal
        );
        var tableKindByJsonScope = tableModels.ToDictionary(
            tm => tm.JsonScope.Canonical,
            tm => tm.IdentityMetadata.TableKind,
            StringComparer.Ordinal
        );

        // (table, stableRowIdentity) -> the row's containing-scope address. Built incrementally
        // as we walk dependency order, so each row's parent address is already registered
        // when we process the row's children.
        var addressByTableAndStableId =
            new Dictionary<(DbTableName Table, long StableId), ScopeInstanceAddress>();

        foreach (var tableModel in request.WritePlan.TablePlansInDependencyOrder.Select(tp => tp.TableModel))
        {
            var tableKind = tableModel.IdentityMetadata.TableKind;
            if (tableKind is not (DbTableKind.Collection or DbTableKind.ExtensionCollection))
            {
                continue;
            }

            var thisJsonScope = tableModel.JsonScope.Canonical;
            var parentJsonScope = ComputeParentJsonScope(thisJsonScope);

            foreach (var ((bucketTable, parentKey), bucket) in collectionRowsIndex)
            {
                if (!bucketTable.Equals(tableModel.Table))
                {
                    continue;
                }

                var parentContainingAddress = ResolveParentContainingScopeAddress(
                    parentJsonScope,
                    parentKey,
                    tableByJsonScope,
                    tableKindByJsonScope,
                    addressByTableAndStableId
                );
                if (parentContainingAddress is null)
                {
                    continue;
                }

                var snapshots = new List<CurrentCollectionRowSnapshot>(bucket.Count);
                foreach (var projection in bucket)
                {
                    snapshots.Add(projection.ToSnapshot());

                    var selfAncestor = new AncestorCollectionInstance(
                        thisJsonScope,
                        projection.SemanticIdentityInOrder
                    );
                    var thisContainingAddress = new ScopeInstanceAddress(
                        thisJsonScope,
                        parentContainingAddress.AncestorCollectionInstances.Add(selfAncestor)
                    );
                    addressByTableAndStableId[(tableModel.Table, projection.StableRowIdentity)] =
                        thisContainingAddress;
                }

                var key = (thisJsonScope, parentContainingAddress);
                if (result.TryGetValue(key, out var existing))
                {
                    result[key] = [.. existing, .. snapshots];
                }
                else
                {
                    result[key] = [.. snapshots];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the parent's containing-scope address from a child bucket's
    /// <see cref="ParentIdentityKey"/>.
    /// <list type="bullet">
    ///   <item><description>Document root or root-extension parent: 1:1 with the document, so
    ///   the address is <c>(parentJsonScope, [])</c> regardless of the bucket's
    ///   <c>ParentIdentityKey</c>. Extension-child collections like
    ///   <c>$._ext.sample.children[*]</c> sit under root-extension parents; without this
    ///   case they would silently lose their partition entry.</description></item>
    ///   <item><description>Nested collection parent: the bucket key's first value is the
    ///   parent's <c>StableRowIdentity</c>; that long looks up the parent row's
    ///   previously-registered address.</description></item>
    ///   <item><description>Inlined non-collection parent (e.g.
    ///   <c>$.parents[*].detail</c> for child <c>$.parents[*].detail.children[*]</c>):
    ///   the inlined scope has no backing table plan, so the partition's address borrows
    ///   the nearest table-backed ancestor's ancestor chain and substitutes the inlined
    ///   parent JsonScope. Without this, the per-(scope, parent-instance) partition for
    ///   the inlined-parent children scope is missing and ancestor descriptor / document-
    ///   reference canonicalization fails closed for any descendant.</description></item>
    /// </list>
    /// Returns <c>null</c> when the parent table or row cannot be located — the caller skips
    /// those buckets and the partitioned positional fallback is unavailable for descendants
    /// of those parents.
    /// </summary>
    private static ScopeInstanceAddress? ResolveParentContainingScopeAddress(
        string parentJsonScope,
        ParentIdentityKey parentKey,
        IReadOnlyDictionary<string, DbTableName> tableByJsonScope,
        IReadOnlyDictionary<string, DbTableKind> tableKindByJsonScope,
        IReadOnlyDictionary<(DbTableName, long), ScopeInstanceAddress> addressByTableAndStableId
    )
    {
        if (string.Equals(parentJsonScope, "$", StringComparison.Ordinal))
        {
            return new ScopeInstanceAddress("$", ImmutableArray<AncestorCollectionInstance>.Empty);
        }

        if (
            tableKindByJsonScope.TryGetValue(parentJsonScope, out var parentKind)
            && parentKind is DbTableKind.RootExtension
        )
        {
            return new ScopeInstanceAddress(
                parentJsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );
        }

        // Aligned-extension-scope parent: 1:1 with the underlying base collection row, so
        // the partition's containing-scope address borrows the parent collection's ancestor
        // chain (which already includes the parent collection's self-entry) under the
        // aligned-scope's JsonScope. Without this, extension-child collections nested under
        // an aligned scope (e.g., $.parents[*]._ext.sample.things[*]) would lose their
        // partition entry and ancestor descriptor canonicalization would fail closed for
        // any descendant.
        if (parentKind is DbTableKind.CollectionExtensionScope)
        {
            return TryResolveAlignedExtensionParentAddress(
                alignedScope: parentJsonScope,
                outputJsonScope: parentJsonScope,
                parentKey,
                tableByJsonScope,
                addressByTableAndStableId
            );
        }

        if (!tableByJsonScope.TryGetValue(parentJsonScope, out var parentTable))
        {
            return ResolveInlinedParentContainingScopeAddress(
                parentJsonScope,
                parentKey,
                tableByJsonScope,
                tableKindByJsonScope,
                addressByTableAndStableId
            );
        }

        if (parentKey.Values.IsDefaultOrEmpty)
        {
            return null;
        }

        if (parentKey.Values[0] is not FlattenedWriteValue.Literal { Value: long parentStableId })
        {
            return null;
        }

        return addressByTableAndStableId.TryGetValue((parentTable, parentStableId), out var addr)
            ? addr
            : null;
    }

    /// <summary>
    /// Resolves the partition address when <paramref name="parentJsonScope"/> is an inlined
    /// non-collection scope below a table-backed ancestor (e.g.
    /// <c>$.parents[*].detail</c> for child <c>$.parents[*].detail.children[*]</c>;
    /// <c>$._ext.sample.detail</c> below a root-extension scope; or
    /// <c>$.parents[*]._ext.aligned.detail</c> below an aligned-extension scope). Walks up
    /// to the nearest table-backed ancestor and dispatches per-kind so the partition shape
    /// mirrors the corresponding direct-parent branch of
    /// <see cref="ResolveParentContainingScopeAddress"/>:
    /// <list type="bullet">
    ///   <item><description>Document root: empty ancestor chain (1:1 with the document).</description></item>
    ///   <item><description>Root extension: empty ancestor chain (1:1 with the document) — required because
    ///   <see cref="DbTableKind.RootExtension"/> rows are not registered in
    ///   <paramref name="addressByTableAndStableId"/>.</description></item>
    ///   <item><description>Aligned extension scope: borrow the underlying base collection row's chain
    ///   (which already includes the parent collection's self-entry) — required because
    ///   <see cref="DbTableKind.CollectionExtensionScope"/> rows are not registered in
    ///   <paramref name="addressByTableAndStableId"/>.</description></item>
    ///   <item><description>Collection / extension collection: existing
    ///   <c>StableRowIdentity</c> lookup, with the inlined JsonScope substituted onto the
    ///   ancestor row's chain.</description></item>
    /// </list>
    /// The inlined scope contributes no <see cref="AncestorCollectionInstance"/> because
    /// it is not a collection instance, mirroring the runtime-walk behavior of
    /// <see cref="ResolveEffectiveChildParentScopeAddress"/>.
    /// </summary>
    private static ScopeInstanceAddress? ResolveInlinedParentContainingScopeAddress(
        string parentJsonScope,
        ParentIdentityKey parentKey,
        IReadOnlyDictionary<string, DbTableName> tableByJsonScope,
        IReadOnlyDictionary<string, DbTableKind> tableKindByJsonScope,
        IReadOnlyDictionary<(DbTableName, long), ScopeInstanceAddress> addressByTableAndStableId
    )
    {
        // Walk up the inlined parent's JSON path stripping property segments until we
        // reach a table-backed scope (or the document root). Inlined property segments
        // never carry [*], so stripping the trailing dotted property segment is a
        // structural step toward the nearest table-backed ancestor.
        var nearestTableBacked = parentJsonScope;
        while (
            !tableByJsonScope.ContainsKey(nearestTableBacked)
            && !string.Equals(nearestTableBacked, "$", StringComparison.Ordinal)
        )
        {
            var lastDot = nearestTableBacked.LastIndexOf('.');
            if (lastDot < 0)
            {
                nearestTableBacked = "$";
                break;
            }
            nearestTableBacked = nearestTableBacked[..lastDot];
        }

        if (string.Equals(nearestTableBacked, "$", StringComparison.Ordinal))
        {
            // Inlined scope hanging directly off the document root. The 1:1 root parent
            // contributes no ancestor instances, mirroring the RootExtension shape.
            return new ScopeInstanceAddress(
                parentJsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );
        }

        var nearestKind = tableKindByJsonScope.TryGetValue(nearestTableBacked, out var resolvedKind)
            ? resolvedKind
            : DbTableKind.Unspecified;

        if (nearestKind is DbTableKind.RootExtension)
        {
            // Inlined scope below a root-extension scope (e.g. $._ext.sample.detail when
            // children live at $._ext.sample.detail.children[*]). The root extension is
            // 1:1 with the document, so the inlined-detail scope inherits its empty
            // ancestor chain.
            return new ScopeInstanceAddress(
                parentJsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );
        }

        if (nearestKind is DbTableKind.CollectionExtensionScope)
        {
            // Inlined scope below an aligned-extension scope (e.g.
            // $.parents[*]._ext.aligned.detail when children live at
            // $.parents[*]._ext.aligned.detail.children[*]). The aligned scope is 1:1
            // with the underlying base collection row, so the inlined-detail scope
            // borrows that collection row's previously-registered ancestor chain (which
            // already includes the parent collection's self-entry).
            return TryResolveAlignedExtensionParentAddress(
                alignedScope: nearestTableBacked,
                outputJsonScope: parentJsonScope,
                parentKey,
                tableByJsonScope,
                addressByTableAndStableId
            );
        }

        // Collection / extension-collection nearest ancestor: the child's parentKey FK
        // points at that collection row's StableRowIdentity, which is registered in
        // addressByTableAndStableId.
        if (
            !tableByJsonScope.TryGetValue(nearestTableBacked, out var nearestTable)
            || parentKey.Values.IsDefaultOrEmpty
            || parentKey.Values[0] is not FlattenedWriteValue.Literal { Value: long inlinedParentStableId }
            || !addressByTableAndStableId.TryGetValue(
                (nearestTable, inlinedParentStableId),
                out var nearestAddr
            )
        )
        {
            return null;
        }

        return new ScopeInstanceAddress(parentJsonScope, nearestAddr.AncestorCollectionInstances);
    }

    /// <summary>
    /// Strips the trailing aligned-extension marker from a
    /// <see cref="DbTableKind.CollectionExtensionScope"/> JsonScope to yield the underlying
    /// parent collection's JsonScope. Handles both shapes:
    /// <list type="bullet">
    ///   <item><description>Standard: <c>"$.A[*]._ext.sample"</c> → <c>"$.A[*]"</c>.</description></item>
    ///   <item><description>Mirrored (per <see cref="AlignedExtensionScopeSupport"/>'s
    ///   matching-name contract): <c>"$._ext.sample.A[*]._ext.sample"</c> →
    ///   <c>"$.A[*]"</c>.</description></item>
    /// </list>
    /// Returns <c>null</c> when the scope does not match the aligned-extension trailing
    /// pattern.
    /// </summary>
    internal static string? StripAlignedScopeToParentCollectionScope(string alignedScope) =>
        AlignedExtensionScopeSupport.Classify(alignedScope)?.ParentCollectionScope;

    /// <summary>
    /// Resolves the partition address for an aligned-extension scope (or for an inlined
    /// scope below one) by stripping <paramref name="alignedScope"/> to its underlying
    /// base collection JsonScope, looking up that collection's table, extracting the
    /// stable-id locator from <paramref name="parentKey"/>, and returning the matched
    /// address rebased onto <paramref name="outputJsonScope"/>. Returns <c>null</c> when
    /// any link in the chain misses; aligned-extension rows are not registered in
    /// <paramref name="addressByTableAndStableId"/> directly, so this borrowing path is
    /// the only way to derive their partition address.
    /// </summary>
    private static ScopeInstanceAddress? TryResolveAlignedExtensionParentAddress(
        string alignedScope,
        string outputJsonScope,
        ParentIdentityKey parentKey,
        IReadOnlyDictionary<string, DbTableName> tableByJsonScope,
        IReadOnlyDictionary<(DbTableName, long), ScopeInstanceAddress> addressByTableAndStableId
    )
    {
        var parentCollectionScope = StripAlignedScopeToParentCollectionScope(alignedScope);
        if (
            parentCollectionScope is null
            || !tableByJsonScope.TryGetValue(parentCollectionScope, out var parentCollectionTable)
            || parentKey.Values.IsDefaultOrEmpty
            || parentKey.Values[0] is not FlattenedWriteValue.Literal { Value: long alignedStableId }
            || !addressByTableAndStableId.TryGetValue(
                (parentCollectionTable, alignedStableId),
                out var parentCollectionAddr
            )
        )
        {
            return null;
        }

        return new ScopeInstanceAddress(outputJsonScope, parentCollectionAddr.AncestorCollectionInstances);
    }

    /// <summary>
    /// Strips the trailing array-element segment from a canonical JsonScope to yield the
    /// immediate parent scope. <c>"$.A[*]"</c> → <c>"$"</c>;
    /// <c>"$.A[*].B[*]"</c> → <c>"$.A[*]"</c>;
    /// <c>"$._ext.sample.addresses[*]"</c> → <c>"$._ext.sample"</c>.
    /// </summary>
    internal static string ComputeParentJsonScope(string childJsonScope)
    {
        var lastBracket = childJsonScope.LastIndexOf("[*]", StringComparison.Ordinal);
        if (lastBracket < 0)
        {
            return childJsonScope;
        }

        var lastDot = childJsonScope.LastIndexOf('.', lastBracket - 1);
        return lastDot < 0 ? "$" : childJsonScope[..lastDot];
    }

    private static IReadOnlyDictionary<
        string,
        ImmutableArray<VisibleStoredCollectionRow>
    > BuildStoredRowsByJsonScope(RelationalWriteProfileMergeRequest request)
    {
        var storedRows =
            request.ProfileAppliedContext?.VisibleStoredCollectionRows
            ?? ImmutableArray<VisibleStoredCollectionRow>.Empty;

        if (storedRows.IsDefaultOrEmpty)
        {
            return new Dictionary<string, ImmutableArray<VisibleStoredCollectionRow>>(StringComparer.Ordinal);
        }

        var grouped = new Dictionary<string, List<VisibleStoredCollectionRow>>(StringComparer.Ordinal);
        foreach (var row in storedRows)
        {
            if (!grouped.TryGetValue(row.Address.JsonScope, out var bucket))
            {
                bucket = [];
                grouped[row.Address.JsonScope] = bucket;
            }
            bucket.Add(row);
        }

        var result = new Dictionary<string, ImmutableArray<VisibleStoredCollectionRow>>(
            StringComparer.Ordinal
        );
        foreach (var (scope, bucket) in grouped)
        {
            result[scope] = [.. bucket];
        }
        return result;
    }

    private static IReadOnlyDictionary<
        (string ChildJsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<VisibleStoredCollectionRow>
    > BuildVisibleStoredRowsIndex(
        RelationalWriteProfileMergeRequest request,
        IReadOnlyDictionary<string, TableWritePlan> tablePlanByJsonScope,
        IReadOnlyDictionary<string, ImmutableArray<CurrentCollectionRowSnapshot>> currentRowsByJsonScope,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent,
        IReadOnlyDictionary<string, ImmutableArray<VisibleStoredCollectionRow>> storedRowsByJsonScope,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        var grouped = new Dictionary<(string, ScopeInstanceAddress), List<VisibleStoredCollectionRow>>(
            ChildScopeAndParentComparer.Instance
        );

        var storedRows =
            request.ProfileAppliedContext?.VisibleStoredCollectionRows
            ?? ImmutableArray<VisibleStoredCollectionRow>.Empty;

        // Fold inlined non-collection descendant scope hidden paths onto each row before
        // ancestor canonicalization. ProfileCollectionRowHiddenPathExpander reconstructs
        // each row's CollectionRowAddress from the descendant StoredScopeState's raw
        // ancestor chain (URIs / document-reference natural keys). If we canonicalized
        // first, the row's own ParentAddress would carry backend-id ancestors that no
        // longer compare equal to the raw reconstructed key, and inlined hidden paths
        // under a descriptor- or reference-backed parent would silently fail to attach.
        var expandedStoredRows = ExpandHiddenPathsBeforeAncestorCanonicalization(
            storedRows,
            request.ProfileAppliedContext?.StoredScopeStates ?? ImmutableArray<StoredScopeState>.Empty,
            tablePlanByJsonScope,
            request.WritePlan
        );

        foreach (var row in expandedStoredRows)
        {
            // Canonicalize ancestor identities so both the index key and the row's own
            // Address.ParentAddress carry backend-id ancestors that match the walker's
            // recursion-side lookup keys. The planner enforces structural equality between
            // input.ParentScopeAddress (built from canonicalized stored row identity during
            // recursion) and each row's Address.ParentAddress, so both sides of the
            // structural match must use the canonicalized form.
            var canonicalizedParent = RelationalWriteProfileMergeSynthesizer.CanonicalizeAddressAncestors(
                row.Address.ParentAddress,
                tablePlanByJsonScope,
                currentRowsByJsonScope,
                currentRowsByJsonScopeAndParent,
                storedRowsByJsonScope,
                resolvedReferenceLookups,
                request.WritePlan
            );
            var canonicalizedRow = ReferenceEquals(canonicalizedParent, row.Address.ParentAddress)
                ? row
                : row with
                {
                    Address = row.Address with { ParentAddress = canonicalizedParent },
                };
            var key = (row.Address.JsonScope, canonicalizedParent);
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = [];
                grouped[key] = bucket;
            }
            bucket.Add(canonicalizedRow);
        }

        var result = new Dictionary<
            (string, ScopeInstanceAddress),
            ImmutableArray<VisibleStoredCollectionRow>
        >(ChildScopeAndParentComparer.Instance);
        foreach (var (key, bucket) in grouped)
        {
            result[key] = [.. bucket];
        }
        return result;
    }

    /// <summary>
    /// Returns <paramref name="rows"/> with each row's <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>
    /// augmented by <see cref="ProfileCollectionRowHiddenPathExpander.Expand"/>, grouping rows
    /// by collection JsonScope so the expander runs once per scope. Original ordering is
    /// preserved so downstream per-(scope, parent) bucket ordering — which the planner relies
    /// on for deterministic ordinal recomputation — stays unchanged.
    /// </summary>
    private static ImmutableArray<VisibleStoredCollectionRow> ExpandHiddenPathsBeforeAncestorCanonicalization(
        ImmutableArray<VisibleStoredCollectionRow> rows,
        ImmutableArray<StoredScopeState> storedScopeStates,
        IReadOnlyDictionary<string, TableWritePlan> tablePlanByJsonScope,
        ResourceWritePlan writePlan
    )
    {
        if (rows.IsDefaultOrEmpty || storedScopeStates.IsDefaultOrEmpty)
        {
            return rows;
        }

        // Group row positions by collection JsonScope so a single Expand call covers all
        // rows in that scope. Tracking the original index lets us write each expanded row
        // back into the result at its original position.
        var indexesByScope = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Length; i++)
        {
            var scope = rows[i].Address.JsonScope;
            if (!indexesByScope.TryGetValue(scope, out var bucket))
            {
                bucket = [];
                indexesByScope[scope] = bucket;
            }
            bucket.Add(i);
        }

        VisibleStoredCollectionRow[]? mutated = null;
        foreach (var (scope, indexes) in indexesByScope)
        {
            if (!tablePlanByJsonScope.TryGetValue(scope, out var tablePlan))
            {
                continue;
            }

            var bucketBuilder = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>(indexes.Count);
            foreach (var idx in indexes)
            {
                bucketBuilder.Add(rows[idx]);
            }
            var bucket = bucketBuilder.MoveToImmutable();

            var expanded = ProfileCollectionRowHiddenPathExpander.Expand(
                bucket,
                storedScopeStates,
                scope,
                tablePlan,
                writePlan
            );

            for (var i = 0; i < indexes.Count; i++)
            {
                if (!ReferenceEquals(bucket[i], expanded[i]))
                {
                    mutated ??= rows.ToArray();
                    mutated[indexes[i]] = expanded[i];
                }
            }
        }

        return mutated is null ? rows : ImmutableArray.Create(mutated);
    }

    private static IReadOnlyDictionary<
        (string ChildJsonScope, ScopeInstanceAddress ParentAddress),
        ImmutableArray<VisibleRequestCollectionItem>
    > BuildVisibleRequestItemsIndex(
        RelationalWriteProfileMergeRequest request,
        IReadOnlyDictionary<string, TableWritePlan> tablePlanByJsonScope,
        IReadOnlyDictionary<string, ImmutableArray<CurrentCollectionRowSnapshot>> currentRowsByJsonScope,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent,
        IReadOnlyDictionary<string, ImmutableArray<VisibleStoredCollectionRow>> storedRowsByJsonScope,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        var grouped = new Dictionary<(string, ScopeInstanceAddress), List<VisibleRequestCollectionItem>>(
            ChildScopeAndParentComparer.Instance
        );

        foreach (var item in request.ProfileRequest.VisibleRequestCollectionItems)
        {
            // Canonicalize ancestor identities (see BuildVisibleStoredRowsIndex). Use the
            // request-side canonicalization path so document-reference ancestors backed by
            // an INSERTED parent (no current row yet) resolve via the request-cycle
            // reference cache (FlatteningResolvedReferenceLookupSet) using the child item's
            // RequestJsonPath ordinal-path prefix matching the ancestor's wildcard count.
            // The stored-side index keeps using the current-row scan via the
            // no-RequestJsonPath overload — stored ancestors always have a current row.
            var canonicalizedParent =
                RelationalWriteProfileMergeSynthesizer.CanonicalizeAddressAncestorsForRequestItem(
                    item.Address.ParentAddress,
                    item.RequestJsonPath,
                    tablePlanByJsonScope,
                    currentRowsByJsonScope,
                    currentRowsByJsonScopeAndParent,
                    storedRowsByJsonScope,
                    resolvedReferenceLookups,
                    request.WritePlan
                );
            var canonicalizedItem = ReferenceEquals(canonicalizedParent, item.Address.ParentAddress)
                ? item
                : item with
                {
                    Address = item.Address with { ParentAddress = canonicalizedParent },
                };
            var key = (item.Address.JsonScope, canonicalizedParent);
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = [];
                grouped[key] = bucket;
            }
            bucket.Add(canonicalizedItem);
        }

        var result = new Dictionary<
            (string, ScopeInstanceAddress),
            ImmutableArray<VisibleRequestCollectionItem>
        >(ChildScopeAndParentComparer.Instance);
        foreach (var (key, bucket) in grouped)
        {
            result[key] = [.. bucket];
        }
        return result;
    }

    /// <summary>
    /// For a child collection table, returns the parent-PhysicalRowIdentity slot index for
    /// each <c>ImmediateParentScopeLocatorColumn</c>, in the same order the column appears
    /// on the child. <c>ParentKeyPart.Index</c> from each binding's source identifies the
    /// slot directly; bindings with a different source kind fall back to positional order
    /// (mirroring the now-deleted <c>ProjectCurrentRowsForScope</c> filter's behavior).
    /// </summary>
    private static int[] ResolveParentKeyPartSlotsForChild(TableWritePlan tablePlan)
    {
        var immediateParentColumns = tablePlan.TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns;
        var slots = new int[immediateParentColumns.Count];
        for (var i = 0; i < immediateParentColumns.Count; i++)
        {
            var bindingIndex = RelationalWriteMergeSupport.FindBindingIndex(
                tablePlan,
                immediateParentColumns[i]
            );
            slots[i] = tablePlan.ColumnBindings[bindingIndex].Source
                is WriteValueSource.ParentKeyPart parentKeyPart
                ? parentKeyPart.Index
                : i;
        }
        return slots;
    }

    private static int ExtractRequiredInt32(
        FlattenedWriteValue value,
        TableWritePlan tablePlan,
        string columnRole
    )
    {
        if (value is not FlattenedWriteValue.Literal literal || literal.Value is null)
        {
            throw new InvalidOperationException(
                $"Required {columnRole} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + "a non-literal or null literal; expected a non-null numeric literal. "
                    + "Current-state projection drift."
            );
        }
        if (literal.Value is int i)
        {
            return i;
        }
        try
        {
            return Convert.ToInt32(literal.Value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Required {columnRole} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + $"'{literal.Value.GetType().Name}' that cannot be coerced to Int32. "
                    + "Current-state projection drift.",
                ex
            );
        }
    }

    /// <summary>
    /// Mirror of <see cref="RelationalWriteProfileMerge"/>'s <c>ExtractRequiredInt64</c> for
    /// stable row identity extraction at index-construction time. PhysicalRowIdentity
    /// columns are NOT NULL in the DB and projected as a numeric literal — fail closed if
    /// the projection produces anything else so identity collisions (multiple rows hashing
    /// to 0) and binding mis-mapping surface deterministically.
    /// </summary>
    private static long ExtractRequiredInt64(
        FlattenedWriteValue value,
        TableWritePlan tablePlan,
        string columnRole
    )
    {
        if (value is not FlattenedWriteValue.Literal literal || literal.Value is null)
        {
            throw new InvalidOperationException(
                $"Required {columnRole} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + "a non-literal or null literal; expected a non-null numeric literal. "
                    + "Current-state projection drift."
            );
        }
        if (literal.Value is long l)
        {
            return l;
        }
        try
        {
            return Convert.ToInt64(literal.Value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Required {columnRole} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + $"'{literal.Value.GetType().Name}' that cannot be coerced to Int64. "
                    + "Current-state projection drift.",
                ex
            );
        }
    }

    /// <summary>
    /// Custom equality comparer for the <c>(childJsonScope, parentAddress)</c> tuple keys
    /// used by the visible-rows / visible-items indexes. Delegates to
    /// <see cref="ScopeInstanceAddressComparer"/> for the address part so structural
    /// equality matches the structural address semantics used by the profile indexes.
    /// </summary>
    internal sealed class ChildScopeAndParentComparer
        : IEqualityComparer<(string ChildJsonScope, ScopeInstanceAddress ParentAddress)>
    {
        public static readonly ChildScopeAndParentComparer Instance = new();

        public bool Equals(
            (string ChildJsonScope, ScopeInstanceAddress ParentAddress) x,
            (string ChildJsonScope, ScopeInstanceAddress ParentAddress) y
        ) =>
            StringComparer.Ordinal.Equals(x.ChildJsonScope, y.ChildJsonScope)
            && ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(x.ParentAddress, y.ParentAddress);

        public int GetHashCode((string ChildJsonScope, ScopeInstanceAddress ParentAddress) obj) =>
            // Mix the child scope with the canonical structural hash of the parent address,
            // so sibling parent instances at the same depth and JsonScope hash to distinct
            // buckets when their ancestor identity contents differ. Delegating to
            // ScopeInstanceAddressComparer.GetHashCode keeps this consistent with the
            // structural equality used by Equals (and with CollectionRowAddressComparer's
            // hash composition).
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ChildJsonScope),
                ScopeInstanceAddressComparer.Instance.GetHashCode(obj.ParentAddress)
            );
    }
}

/// <summary>
/// Structural-equality wrapper around <see cref="ImmutableArray{T}"/> of
/// <see cref="FlattenedWriteValue"/> so the per-merge indexes can key dictionaries on
/// parent-physical-identity column values.
/// </summary>
/// <remarks>
/// Equality must tolerate numeric-type drift between projection paths (e.g. <c>long</c>
/// from one path vs <c>int</c> from another carrying the same underlying value): the
/// pre-walker top-level body compared via <c>Convert.ToString</c> for exactly this reason.
/// <see cref="FlattenedWriteValue.Literal"/>'s record value-equality is reference-strict
/// on the boxed CLR type, so <c>1L != 1</c> under the auto-generated comparer. This wrapper
/// reproduces the lenient string comparison so the per-merge index lookup matches the
/// behavior of the now-deleted <c>ProjectCurrentRowsForScope</c> filter.
/// </remarks>
internal sealed class ParentIdentityKey : IEquatable<ParentIdentityKey>
{
    private readonly ImmutableArray<FlattenedWriteValue> _values;
    private readonly int _hashCode;

    public ParentIdentityKey(ImmutableArray<FlattenedWriteValue> values)
    {
        _values = values;
        var hash = new HashCode();
        foreach (var v in values)
        {
            // Hash on the literal value's stringified form so numeric type drift
            // (long vs int with the same magnitude) collides into the same bucket as
            // the loose Equals below.
            hash.Add(NormalizedLiteralKey(v));
        }
        _hashCode = hash.ToHashCode();
    }

    /// <summary>The wrapped parent-identity values, in parent's PhysicalRowIdentity order.</summary>
    public ImmutableArray<FlattenedWriteValue> Values => _values;

    public bool Equals(ParentIdentityKey? other)
    {
        if (other is null || _values.Length != other._values.Length)
        {
            return false;
        }
        for (var i = 0; i < _values.Length; i++)
        {
            if (!ValuesEquivalent(_values[i], other._values[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ParentIdentityKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    /// <summary>
    /// Mirrors the now-deleted <c>FlattenedWriteValueEquals</c> helper: literal values
    /// compare via stringified form (so numeric type drift does not matter), and
    /// non-literal values compare by reference.
    /// </summary>
    private static bool ValuesEquivalent(FlattenedWriteValue a, FlattenedWriteValue b)
    {
        if (a is FlattenedWriteValue.Literal litA && b is FlattenedWriteValue.Literal litB)
        {
            if (litA.Value is null && litB.Value is null)
            {
                return true;
            }
            if (litA.Value is null || litB.Value is null)
            {
                return false;
            }
            return string.Equals(
                Convert.ToString(litA.Value, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(litB.Value, System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            );
        }
        return ReferenceEquals(a, b);
    }

    private static string NormalizedLiteralKey(FlattenedWriteValue v)
    {
        if (v is not FlattenedWriteValue.Literal lit)
        {
            return "R:"
                + RuntimeHelpers.GetHashCode(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (lit.Value is null)
        {
            return "L:null";
        }
        return "L:"
            + (Convert.ToString(lit.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "null");
    }
}

/// <summary>
/// Per-table accumulator for profile-merge synthesis. The walker appends merged + current
/// rows here (one builder per <see cref="DbTableName"/>) and the synthesizer finalizes one
/// <see cref="RelationalWriteMergedTableState"/> per touched table after the walk
/// completes. Modeled on the no-profile path's <c>TableStateBuilder</c>, but adapted to
/// the profile case where current rows are produced per-scope by the walker (not
/// pre-projected) and where untouched tables are dropped from the output (not emitted as
/// empty TableStates).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HasContent"/> tracks whether either <see cref="AddMergedRow"/> or
/// <see cref="AddCurrentRow"/> has been called. The synthesizer iterates
/// <c>TablePlansInDependencyOrder</c> after the walk and only emits TableStates for
/// builders with <c>HasContent == true</c>; this preserves the existing top-level
/// behavior where a no-op scope produces no TableState (the persister sees nothing for
/// that table).
/// </para>
/// <para>
/// Multiple recursion calls for the same table feed the same builder, so the finalized
/// TableState carries the union of all merged + current rows from every recursion path.
/// This is the spec's per-table aggregation requirement (Section 3 "Table-state
/// aggregation").
/// </para>
/// </remarks>
internal sealed class ProfileTableStateBuilder
{
    private readonly TableWritePlan _tableWritePlan;
    private readonly List<RelationalWriteMergedTableRow> _currentRows = [];
    private readonly List<RelationalWriteMergedTableRow> _mergedRows = [];
    private bool _hasContent;

    public ProfileTableStateBuilder(TableWritePlan tableWritePlan)
    {
        _tableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
    }

    public TableWritePlan TableWritePlan => _tableWritePlan;

    /// <summary>
    /// <c>true</c> when either a merged row or a current row has been appended via this
    /// builder. Used by the synthesizer to skip untouched tables during finalization so the
    /// output matches the existing top-level behavior of dropping no-op scopes.
    /// </summary>
    public bool HasContent => _hasContent;

    public void AddCurrentRow(RelationalWriteMergedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _currentRows.Add(row);
        _hasContent = true;
    }

    public void AddMergedRow(RelationalWriteMergedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _mergedRows.Add(row);
        _hasContent = true;
    }

    /// <summary>
    /// Finalizes the accumulated rows into a <see cref="RelationalWriteMergedTableState"/>.
    /// Caller is responsible for skipping builders where <see cref="HasContent"/> is
    /// <c>false</c> (no-op tables produce no TableState in the profile-merge path).
    /// </summary>
    public RelationalWriteMergedTableState Build() =>
        new(_tableWritePlan, [.. _currentRows], [.. _mergedRows]);
}
