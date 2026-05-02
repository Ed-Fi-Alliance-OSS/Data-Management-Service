// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Request contract for the profile merge synthesizer. Root-level
/// <see cref="CollectionWriteCandidate"/>s are accepted only for root-attached base
/// collection candidates (<see cref="DbTableKind.Collection"/>). Nested
/// <c>CollectionCandidates</c> under those root-attached base collection candidates,
/// attached-aligned scope data, and collection candidates under
/// <see cref="RootExtensionWriteRowBuffer"/> are supported. Non-Collection root table
/// kinds remain structurally invalid.
/// </summary>
internal sealed record RelationalWriteProfileMergeRequest
{
    public RelationalWriteProfileMergeRequest(
        ResourceWritePlan writePlan,
        FlattenedWriteSet flattenedWriteSet,
        JsonNode writableRequestBody,
        RelationalWriteCurrentState? currentState,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        ResolvedReferenceSet resolvedReferences
    )
    {
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        FlattenedWriteSet = flattenedWriteSet ?? throw new ArgumentNullException(nameof(flattenedWriteSet));
        WritableRequestBody =
            writableRequestBody ?? throw new ArgumentNullException(nameof(writableRequestBody));
        CurrentState = currentState;
        ProfileRequest = profileRequest ?? throw new ArgumentNullException(nameof(profileRequest));
        ProfileAppliedContext = profileAppliedContext;
        ResolvedReferences =
            resolvedReferences ?? throw new ArgumentNullException(nameof(resolvedReferences));

        if (
            !ReferenceEquals(
                WritePlan.TablePlansInDependencyOrder[0],
                FlattenedWriteSet.RootRow.TableWritePlan
            )
        )
        {
            throw new ArgumentException(
                $"{nameof(flattenedWriteSet)} must use the root table from the supplied {nameof(writePlan)}.",
                nameof(flattenedWriteSet)
            );
        }
        var invalidRootCollectionCandidate = FlattenedWriteSet.RootRow.CollectionCandidates.FirstOrDefault(
            candidate =>
                candidate.TableWritePlan.TableModel.IdentityMetadata.TableKind is not DbTableKind.Collection
        );
        if (invalidRootCollectionCandidate is not null)
        {
            throw new ArgumentException(
                "Profile merge top-level collection candidates must carry "
                    + "DbTableKind.Collection (root-attached base collection). Other root-candidate table kinds must be handled by their own merge paths.",
                nameof(flattenedWriteSet)
            );
        }
        var invalidRootExtensionTablePlan = FlattenedWriteSet
            .RootRow.RootExtensionRows.Select(extensionRow => extensionRow.TableWritePlan)
            .FirstOrDefault(tablePlan =>
                tablePlan.TableModel.IdentityMetadata.TableKind is not DbTableKind.RootExtension
            );
        if (invalidRootExtensionTablePlan is not null)
        {
            throw new ArgumentException(
                "Profile merge requires every root-extension row to use a "
                    + $"{nameof(DbTableKind.RootExtension)} table plan; got "
                    + $"'{invalidRootExtensionTablePlan.TableModel.IdentityMetadata.TableKind}' "
                    + $"for table '{ProfileBindingClassificationCore.FormatTable(invalidRootExtensionTablePlan)}'.",
                nameof(flattenedWriteSet)
            );
        }
        if ((CurrentState is null) != (ProfileAppliedContext is null))
        {
            throw new ArgumentException(
                $"{nameof(currentState)} and {nameof(profileAppliedContext)} must both be null (create-new) "
                    + "or both be non-null (existing-document)."
            );
        }
    }

    public ResourceWritePlan WritePlan { get; init; }

    public FlattenedWriteSet FlattenedWriteSet { get; init; }

    public JsonNode WritableRequestBody { get; init; }

    public RelationalWriteCurrentState? CurrentState { get; init; }

    public ProfileAppliedWriteRequest ProfileRequest { get; init; }

    public ProfileAppliedWriteContext? ProfileAppliedContext { get; init; }

    public ResolvedReferenceSet ResolvedReferences { get; init; }
}

internal interface IRelationalWriteProfileMergeSynthesizer
{
    ProfileMergeOutcome Synthesize(RelationalWriteProfileMergeRequest request);
}

/// <summary>
/// Profile merge synthesizer. Composes the root-table binding classifier, the per-disposition
/// overlay, and the post-overlay key-unification resolver for the root table, then iterates
/// root-attached separate-table non-collection (<see cref="DbTableKind.RootExtension"/>) plans
/// composing their own classifier/resolver plus the separate-table decider. Guarded no-op is
/// not supported; <see cref="RelationalWriteMergeResult.SupportsGuardedNoOp"/> is always
/// <c>false</c>. Returns a <see cref="ProfileMergeOutcome"/> discriminated union so the
/// executor can short-circuit to a typed <see cref="UpsertResult.UpsertFailureProfileDataPolicy"/>
/// / <see cref="UpdateResult.UpdateFailureProfileDataPolicy"/> on a separate-table
/// create-denied outcome without throwing.
/// </summary>
internal sealed class RelationalWriteProfileMergeSynthesizer(
    IProfileRootTableBindingClassifier classifier,
    IProfileRootKeyUnificationResolver resolver,
    IProfileSeparateTableBindingClassifier separateTableClassifier,
    IProfileSeparateTableKeyUnificationResolver separateTableResolver,
    IProfileSeparateTableMergeDecider separateTableDecider
) : IRelationalWriteProfileMergeSynthesizer
{
    private readonly IProfileRootTableBindingClassifier _classifier =
        classifier ?? throw new ArgumentNullException(nameof(classifier));
    private readonly IProfileRootKeyUnificationResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IProfileSeparateTableBindingClassifier _separateTableClassifier =
        separateTableClassifier ?? throw new ArgumentNullException(nameof(separateTableClassifier));
    private readonly IProfileSeparateTableKeyUnificationResolver _separateTableResolver =
        separateTableResolver ?? throw new ArgumentNullException(nameof(separateTableResolver));
    private readonly IProfileSeparateTableMergeDecider _separateTableDecider =
        separateTableDecider ?? throw new ArgumentNullException(nameof(separateTableDecider));

    public ProfileMergeOutcome Synthesize(RelationalWriteProfileMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Per-table accumulators keyed by DbTableName. Pre-seed one builder per
        // TableWritePlan so the walker, root-table merge, and per-separate-table merge can
        // each append into the table's builder. Finalization iterates
        // TablePlansInDependencyOrder and emits one TableState per touched table; this is
        // the spec's Section 3 "Table-state aggregation" requirement and the structural
        // prerequisite for the walker's nested recursion.
        var tableStateBuilders = new Dictionary<DbTableName, ProfileTableStateBuilder>();
        foreach (var plan in request.WritePlan.TablePlansInDependencyOrder)
        {
            tableStateBuilders[plan.TableModel.Table] = new ProfileTableStateBuilder(plan);
        }

        // Build the resolved-reference lookups once and share them with both the root-table
        // synthesis and each separate-table synthesis so we avoid redundant construction.
        var resolvedReferenceLookups = FlatteningResolvedReferenceLookupSet.Create(
            request.WritePlan,
            request.ResolvedReferences
        );

        // 1. Root-table merge — append merged + current rows into the root builder.
        var rootTable = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootBuilder = tableStateBuilders[rootTable.TableModel.Table];
        var rootTableState = SynthesizeRootTable(request, resolvedReferenceLookups);
        foreach (var currentRow in rootTableState.CurrentRows)
        {
            rootBuilder.AddCurrentRow(currentRow);
        }
        foreach (var mergedRow in rootTableState.MergedRows)
        {
            rootBuilder.AddMergedRow(mergedRow);
        }

        // Parent physical row identity values for top-level collections: the merged root
        // row's values, used as the parent-key-part source for any top-level collection
        // candidates.
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues =
            rootTableState.MergedRows.Length > 0
                ? rootTableState.MergedRows[0].Values
                : request.FlattenedWriteSet.RootRow.Values;

        // 1a. Top-level collection merge — runs unconditionally so that stored-only
        //     "delete-all-visible" scenarios (Blocker #2 fix: spec Section 7.7) are driven
        //     from the union of request-side, stored-side, and DB-side sources rather than
        //     only when request-side candidates are present.
        // Build the root walker context. The walker owns top-level collection synthesis
        // directly: WalkChildren reads the per-merge indexes and dispatches through the
        // planner per scope.
        var rootContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress("$", []),
            ParentPhysicalIdentityValues: [.. parentPhysicalRowIdentityValues],
            RequestSubstructure: request.FlattenedWriteSet.RootRow,
            ParentRequestNode: request.WritableRequestBody
        );

        var walker = new ProfileCollectionWalker(
            request,
            resolvedReferenceLookups,
            tableStateBuilders,
            (
                tablePlan,
                scopeAddress,
                separateScopeParentPhysicalIdentityValues,
                buffer,
                scopedRequestNode,
                requestScope,
                storedScope,
                currentRowProjection
            ) =>
                SynthesizeSeparateScopeInstance(
                    request,
                    tablePlan,
                    scopeAddress,
                    separateScopeParentPhysicalIdentityValues,
                    buffer,
                    scopedRequestNode,
                    requestScope,
                    storedScope,
                    currentRowProjection,
                    resolvedReferenceLookups
                )
        );

        var collectionOutcome = walker.WalkChildren(rootContext, WalkMode.Normal);
        if (collectionOutcome is not null)
        {
            return collectionOutcome.Value;
        }

        // 2. Per-separate-table merge for every non-root table in dependency order. Plans may
        //    legitimately carry non-RootExtension tables that the current request did not
        //    exercise; those are silently skipped here so the no-profile persister can handle
        //    their rows unchanged.

        for (
            var tableIndex = 1;
            tableIndex < request.WritePlan.TablePlansInDependencyOrder.Length;
            tableIndex++
        )
        {
            var tablePlan = request.WritePlan.TablePlansInDependencyOrder[tableIndex];
            if (tablePlan.TableModel.IdentityMetadata.TableKind is not DbTableKind.RootExtension)
            {
                // This loop handles only root-attached RootExtension tables. Plans may carry
                // unused Collection / ExtensionCollection / CollectionExtensionScope tables
                // (e.g., a multi-table School plan where the profiled request touches only
                // root scopes). The synthesizer silently leaves them untouched so their rows
                // flow through the no-profile persister path unchanged.
                continue;
            }

            var separateTableResult = SynthesizeSeparateTable(
                request,
                tablePlan,
                [.. parentPhysicalRowIdentityValues],
                resolvedReferenceLookups
            );

            if (separateTableResult.Rejection is not null)
            {
                return ProfileMergeOutcome.Reject(separateTableResult.Rejection);
            }

            if (separateTableResult.TableState is not null)
            {
                var separateBuilder = tableStateBuilders[tablePlan.TableModel.Table];
                foreach (var currentRow in separateTableResult.TableState.CurrentRows)
                {
                    separateBuilder.AddCurrentRow(currentRow);
                }
                foreach (var mergedRow in separateTableResult.TableState.MergedRows)
                {
                    separateBuilder.AddMergedRow(mergedRow);
                }
            }

            var rootExtensionCollectionOutcome = WalkRootExtensionCollectionChildren(
                request,
                tablePlan,
                separateTableResult,
                walker
            );
            if (rootExtensionCollectionOutcome is not null)
            {
                return rootExtensionCollectionOutcome.Value;
            }
        }

        // 3. Finalize: iterate TablePlansInDependencyOrder and emit one TableState per
        //    touched table. Untouched tables (HasContent == false) are dropped so the output
        //    matches the existing top-level behavior of skipping no-op scopes.
        var tableStates = new List<RelationalWriteMergedTableState>();
        foreach (var plan in request.WritePlan.TablePlansInDependencyOrder)
        {
            var builder = tableStateBuilders[plan.TableModel.Table];
            if (builder.HasContent)
            {
                tableStates.Add(builder.Build());
            }
        }

        return ProfileMergeOutcome.Success(
            new RelationalWriteMergeResult([.. tableStates], supportsGuardedNoOp: false)
        );
    }

    private RelationalWriteMergedTableState SynthesizeRootTable(
        RelationalWriteProfileMergeRequest request,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        var rootTable = request.WritePlan.TablePlansInDependencyOrder[0];

        // 1. Project the current root row (binding-indexed) and the column-name projection for
        //    the resolver context. Both come from the same hydrated row via shared support
        //    helpers so downstream normalization stays symmetric.
        RelationalWriteMergedTableRow? projectedCurrentRootRow = null;
        IReadOnlyDictionary<DbColumnName, object?> currentRootRowByColumnName = ImmutableDictionary<
            DbColumnName,
            object?
        >.Empty;

        if (request.CurrentState is not null)
        {
            var hydrated = request.CurrentState.TableRowsInDependencyOrder.Single(h =>
                h.TableModel.Table.Equals(rootTable.TableModel.Table)
            );
            if (hydrated.Rows.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Root table '{ProfileBindingClassificationCore.FormatTable(rootTable)}' has {hydrated.Rows.Count} current rows "
                        + "for profiled existing-document merge; expected exactly one."
                );
            }

            var projected = RelationalWriteMergeSupport.ProjectCurrentRows(rootTable, hydrated.Rows);
            projectedCurrentRootRow = projected[0];
            currentRootRowByColumnName = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
                rootTable.TableModel,
                hydrated.Rows[0]
            );
        }

        // 2. Classify root-table bindings.
        var classification = _classifier.Classify(
            request.WritePlan,
            request.ProfileRequest,
            request.ProfileAppliedContext
        );

        // 3. Overlay per disposition. Skip resolver-owned bindings; the resolver writes them
        //    in step 4.
        var mergedValues = OverlayByDisposition(
            rootTable,
            request.FlattenedWriteSet.RootRow.Values,
            projectedCurrentRootRow,
            classification.BindingsByIndex,
            classification.ResolverOwnedBindingIndices
        );

        // 4. Build the resolver context and recompute canonical + synthetic-presence bindings.
        //    The resolved-reference lookups are shared with the caller so we avoid rebuilding.
        var resolverContext = new ProfileRootKeyUnificationContext(
            WritableRequestBody: request.WritableRequestBody,
            CurrentState: request.CurrentState,
            CurrentRootRowByColumnName: currentRootRowByColumnName,
            ResolvedReferenceLookups: resolvedReferenceLookups,
            ProfileRequest: request.ProfileRequest,
            ProfileAppliedContext: request.ProfileAppliedContext
        );

        _resolver.Resolve(
            rootTable,
            resolverContext,
            mergedValues,
            classification.ResolverOwnedBindingIndices
        );

        // 5. Assemble the merge result.
        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(rootTable, mergedValues);
        var mergedRow = new RelationalWriteMergedTableRow(mergedValues, comparableValues);

        ImmutableArray<RelationalWriteMergedTableRow> currentRows = projectedCurrentRootRow is null
            ? []
            : [projectedCurrentRootRow];

        return new RelationalWriteMergedTableState(rootTable, currentRows, [mergedRow]);
    }

    private SeparateScopeSynthesisResult SynthesizeSeparateTable(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        var scope = tablePlan.TableModel.JsonScope.Canonical;
        var scopeAddress = new ScopeInstanceAddress(scope, []);
        var requestScope = ProfileMemberGovernanceRules.LookupRequestScope(
            request.ProfileRequest,
            scopeAddress
        );
        var storedScope = request.ProfileAppliedContext is null
            ? null
            : ProfileMemberGovernanceRules.LookupStoredScope(request.ProfileAppliedContext, scopeAddress);
        var hydratedRows = TryFindHydratedRowsForTable(request.CurrentState, tablePlan);
        var currentRowProjection = BuildCurrentSeparateScopeRowProjection(
            tablePlan,
            hydratedRows,
            parentPhysicalIdentityValues
        );
        var extensionRow = TryLocateRootExtensionRow(request, tablePlan);
        var scopedRequestNode = TryResolveScopedRequestNode(request.WritableRequestBody, tablePlan);

        return SynthesizeSeparateScopeInstance(
            request,
            tablePlan,
            scopeAddress,
            parentPhysicalIdentityValues,
            extensionRow is null ? null : SeparateScopeBuffer.From(extensionRow),
            scopedRequestNode,
            requestScope,
            storedScope,
            currentRowProjection,
            resolvedReferenceLookups
        );
    }

    private static ProfileMergeOutcome? WalkRootExtensionCollectionChildren(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan,
        SeparateScopeSynthesisResult separateTableResult,
        ProfileCollectionWalker walker
    )
    {
        if (
            separateTableResult.Outcome
            is not (
                ProfileSeparateTableMergeOutcome.Insert
                or ProfileSeparateTableMergeOutcome.Update
                or ProfileSeparateTableMergeOutcome.Preserve
            )
        )
        {
            return null;
        }

        var tableState =
            separateTableResult.TableState
            ?? throw new InvalidOperationException(
                $"Root-extension separate-scope outcome '{separateTableResult.Outcome}' for table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' returned no table state."
            );

        var recursionSourceRow =
            tableState.MergedRows.Length == 1
                ? tableState.MergedRows[0]
                : throw new InvalidOperationException(
                    $"Root-extension separate-scope outcome '{separateTableResult.Outcome}' for table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' returned "
                        + $"{tableState.MergedRows.Length} merged rows; expected exactly one for recursion "
                        + "into child scopes."
                );

        var recursionMode =
            separateTableResult.Outcome is ProfileSeparateTableMergeOutcome.Preserve
                ? WalkMode.Preserve
                : WalkMode.Normal;
        var extensionRow =
            recursionMode == WalkMode.Normal ? TryLocateRootExtensionRow(request, tablePlan) : null;
        var scopedRequestNode =
            recursionMode == WalkMode.Normal
                ? TryResolveScopedRequestNode(request.WritableRequestBody, tablePlan)
                : null;

        var rootExtensionContext = new ProfileCollectionWalkerContext(
            ContainingScopeAddress: new ScopeInstanceAddress(tablePlan.TableModel.JsonScope.Canonical, []),
            ParentPhysicalIdentityValues: RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(
                tablePlan,
                recursionSourceRow.Values
            ),
            RequestSubstructure: recursionMode == WalkMode.Normal ? extensionRow : null,
            ParentRequestNode: recursionMode == WalkMode.Normal ? scopedRequestNode : null
        );

        return walker.WalkChildren(rootExtensionContext, recursionMode);
    }

    internal SeparateScopeSynthesisResult SynthesizeSeparateScopeInstance(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan,
        ScopeInstanceAddress scopeAddress,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
        SeparateScopeBuffer? buffer,
        JsonNode? scopedRequestNode,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        CurrentSeparateScopeRowProjection? currentRowProjection,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tablePlan);
        ArgumentNullException.ThrowIfNull(scopeAddress);
        ArgumentNullException.ThrowIfNull(resolvedReferenceLookups);

        var scope = scopeAddress.JsonScope;
        var storedRowExists = currentRowProjection is not null;
        var bufferExists = buffer is not null;

        // Contract validation: Core must emit a RequestScopeState entry for every compiled
        // non-collection scope (per C3 `01a-c3-request-visibility-and-writable-shaping.md`).
        // If the flattener produced a root-extension buffer for this scope but
        // RequestScopeStates has no entry, we cannot classify the scope at all — fail closed
        // rather than silently treating the authoritative metadata absence as a no-op.
        if (bufferExists && requestScope is null)
        {
            throw new InvalidOperationException(
                $"Profile separate-table merge for scope '{scope}' on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}': a flattened root-extension "
                    + "buffer exists but ProfileAppliedWriteRequest.RequestScopeStates has no entry for this scope. "
                    + "Every compiled non-collection scope must have a RequestScopeState entry — upstream Core "
                    + "contract violation."
            );
        }

        // Contract validation: Core must emit a StoredScopeState entry for every compiled
        // non-collection scope when a profile applies (per C6
        // `01a-c6-stored-state-projection-and-hidden-member-paths.md`). If a current
        // separate-table row exists but StoredScopeStates has no entry, silently skipping
        // would preserve the row on a VisibleAbsent request instead of deleting or failing
        // closed — stored scope metadata is authoritative, so fail closed here.
        if (storedRowExists && storedScope is null)
        {
            throw new InvalidOperationException(
                $"Profile separate-table merge for scope '{scope}' on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}': a current stored row exists "
                    + "but ProfileAppliedWriteContext.StoredScopeStates has no entry for this scope. "
                    + "Every compiled non-collection scope must have a StoredScopeState entry when a profile "
                    + "applies — upstream Core contract violation."
            );
        }

        if (
            requestScope is { Visibility: ProfileVisibilityKind.Hidden }
            && HiddenRequestBufferCarriesProfileData(tablePlan, buffer)
        )
        {
            return SeparateScopeSynthesisResult.Reject(
                new ProfileCreatabilityRejection(scope, $"Profile forbids writing hidden scope '{scope}'.")
            );
        }

        // Express the separate-table matrix's "actionable" conditions directly so genuine
        // no-ops never reach the decider. Skip covers only the matrix cells that have no
        // decider-side outcome:
        //   - VisibleAbsent request with no matched stored visible row (no-op delete target).
        //   - Hidden/null request with no visible stored row (nothing to preserve or act on).
        //   - Both sides absent entirely (trivially nothing to do).
        // Skip does NOT cover any matched visible stored row: those cases are routed to the
        // decider so VisiblePresent request → Update and VisibleAbsent request → Delete take
        // effect. Inconsistent request-side tuples must ALSO reach the decider so they fail
        // closed rather than being silently preserved:
        //   - Hidden/null request paired with a matched visible stored row.
        //   - VisiblePresent request paired with a Hidden stored scope + row (impossible
        //     under a consistent writable profile; the decider narrows "Preserve dominates"
        //     so this tuple throws instead of silently discarding the request's values).
        bool preserveActionable =
            storedScope is { Visibility: ProfileVisibilityKind.Hidden } && storedRowExists;
        bool visiblePresentActionable = requestScope is { Visibility: ProfileVisibilityKind.VisiblePresent };
        bool matchedVisibleStoredActionable =
            storedScope is { Visibility: ProfileVisibilityKind.VisiblePresent } && storedRowExists;

        if (!preserveActionable && !visiblePresentActionable && !matchedVisibleStoredActionable)
        {
            return SeparateScopeSynthesisResult.Skipped;
        }

        var outcome = _separateTableDecider.Decide(scope, requestScope, storedScope, storedRowExists);

        return outcome switch
        {
            ProfileSeparateTableMergeOutcome.Insert => SeparateScopeSynthesisResult.Table(
                outcome,
                BuildInsertState(tablePlan, parentPhysicalIdentityValues, buffer)
            ),
            ProfileSeparateTableMergeOutcome.Update => SeparateScopeSynthesisResult.Table(
                outcome,
                BuildUpdateState(
                    request,
                    tablePlan,
                    scopeAddress,
                    parentPhysicalIdentityValues,
                    buffer,
                    scopedRequestNode,
                    requestScope,
                    storedScope,
                    currentRowProjection,
                    resolvedReferenceLookups
                )
            ),
            ProfileSeparateTableMergeOutcome.Delete => SeparateScopeSynthesisResult.Table(
                outcome,
                BuildDeleteState(tablePlan, currentRowProjection)
            ),
            ProfileSeparateTableMergeOutcome.Preserve => SeparateScopeSynthesisResult.Table(
                outcome,
                BuildPreserveState(tablePlan, currentRowProjection)
            ),
            ProfileSeparateTableMergeOutcome.RejectCreateDenied => SeparateScopeSynthesisResult.Reject(
                new ProfileCreatabilityRejection(
                    scope,
                    $"Profile forbids creation of new visible scope '{scope}'."
                )
            ),
            _ => throw new InvalidOperationException(
                $"Unhandled ProfileSeparateTableMergeOutcome '{outcome}' for scope '{scope}'."
            ),
        };
    }

    /// <summary>
    /// Build the merge state for a separate-table Insert. This intentionally diverges from
    /// <see cref="BuildUpdateState"/>: the buffer values are copied verbatim without running
    /// the per-disposition overlay because create-new has no stored row, which means
    /// <see cref="RootBindingDisposition.HiddenPreserved"/> has no source to preserve from
    /// and <see cref="RootBindingDisposition.ClearOnVisibleAbsent"/> is meaningless (there
    /// is nothing to clear). The flattener already honors the profile's visible request
    /// view, so the buffer values are exactly what should be inserted.
    /// </summary>
    private static RelationalWriteMergedTableState BuildInsertState(
        TableWritePlan tablePlan,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
        SeparateScopeBuffer? buffer
    )
    {
        var separateScopeBuffer = RequireSeparateScopeBuffer(tablePlan, buffer, "Insert");
        var mergedValues = RewriteSeparateScopeParentKeyParts(
                tablePlan,
                separateScopeBuffer.Values,
                parentPhysicalIdentityValues
            )
            .ToArray();
        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(tablePlan, mergedValues);
        var mergedRow = new RelationalWriteMergedTableRow(mergedValues, comparableValues);
        return new RelationalWriteMergedTableState(tablePlan, currentRows: [], mergedRows: [mergedRow]);
    }

    private RelationalWriteMergedTableState BuildUpdateState(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan,
        ScopeInstanceAddress scopeAddress,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues,
        SeparateScopeBuffer? buffer,
        JsonNode? scopedRequestNode,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        CurrentSeparateScopeRowProjection? currentRowProjection,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (currentRowProjection is null)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has no "
                    + "current row projection for profiled Update; expected exactly one."
            );
        }

        var separateScopeBuffer = RequireSeparateScopeBuffer(tablePlan, buffer, "Update");

        if (scopedRequestNode is null)
        {
            throw new InvalidOperationException(
                $"Separate-table Update path for scope '{tablePlan.TableModel.JsonScope.Canonical}' on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' requires a scoped request node. "
                    + "The decider selected Update but the request body does not contain the scope — upstream "
                    + "contract violation between the projected request and the decider."
            );
        }

        var projectedCurrentRow = currentRowProjection.ProjectedRow;
        var currentRowByColumnName = currentRowProjection.ColumnNameProjection;

        // Collect non-collection descendant inlined scope states once and feed the same
        // envelope to both the classifier and the resolver. A descendant scope whose owner
        // table is this same physical table contributes its own stored hidden-member paths
        // and visibility to bindings on this table, ensuring the matched-row overlay and
        // key-unification resolution honor descendant-scope governance instead of falling
        // through to the direct scope.
        var descendantStates = ProfileSeparateScopeDescendantStates.Collect(
            request.WritePlan,
            tablePlan,
            scopeAddress,
            request.ProfileRequest,
            request.ProfileAppliedContext
        );

        var classification = _separateTableClassifier.Classify(
            request.WritePlan,
            tablePlan,
            scopeAddress,
            requestScope,
            storedScope,
            descendantStates
        );

        var mergedValues = OverlayByDisposition(
            tablePlan,
            separateScopeBuffer.Values,
            projectedCurrentRow,
            classification.BindingsByIndex,
            classification.ResolverOwnedBindingIndices
        );

        // Separate-table key-unification member paths are production-compiled
        // scope-relative (e.g. "$.memberA" under scope "$._ext.sample"), so the
        // resolver must evaluate them against the table-scoped sub-node of the
        // request body, not the root body.
        var resolverContext = new ProfileSeparateTableKeyUnificationContext(
            WritableRequestBody: scopedRequestNode,
            CurrentRowByColumnName: currentRowByColumnName,
            ResolvedReferenceLookups: resolvedReferenceLookups,
            ProfileRequest: request.ProfileRequest,
            ProfileAppliedContext: request.ProfileAppliedContext
        );

        _separateTableResolver.Resolve(
            tablePlan,
            resolverContext,
            scopeAddress,
            requestScope,
            storedScope,
            descendantStates,
            mergedValues,
            classification.ResolverOwnedBindingIndices
        );

        var valuesWithParentKey = RewriteSeparateScopeParentKeyParts(
            tablePlan,
            mergedValues,
            parentPhysicalIdentityValues
        );
        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(
            tablePlan,
            valuesWithParentKey
        );
        var mergedRow = new RelationalWriteMergedTableRow(valuesWithParentKey, comparableValues);
        return new RelationalWriteMergedTableState(tablePlan, [projectedCurrentRow], [mergedRow]);
    }

    private static bool HiddenRequestBufferCarriesProfileData(
        TableWritePlan tablePlan,
        SeparateScopeBuffer? buffer
    )
    {
        if (buffer is null)
        {
            return false;
        }

        // The flattener sets HasSubmittedScopeData when the request body actually contained
        // at least one bound property at this scope — which is the only reliable signal that
        // tells "submitted with explicit null fields" apart from a buffer the flattener
        // synthesized for an absent scope under EmitEmptyRootExtensionBuffers. Relying on
        // FlattenedWriteValue.Literal nullability misses the explicit-null-only case the
        // backend guard is meant to catch as defense-in-depth behind WritableRequestShaper.
        if (buffer.Value.HasSubmittedScopeData)
        {
            return true;
        }

        // CollectionCandidates are only emitted by the flattener for arrays that have
        // request items (RelationalWriteFlattener.MaterializeCollectionCandidates), so a
        // non-empty list is itself proof of hidden-scope request data even when every
        // direct scalar/descriptor/reference binding is null. Without this check, a hidden
        // aligned/root-extension scope carrying only child-collection data would bypass
        // the rejection and be silently dropped at the walker's IsSkipped branch.
        if (!buffer.Value.CollectionCandidates.IsDefaultOrEmpty)
        {
            return true;
        }

        for (var bindingIndex = 0; bindingIndex < tablePlan.ColumnBindings.Length; bindingIndex++)
        {
            if (!IsProfileDataSource(tablePlan.ColumnBindings[bindingIndex].Source))
            {
                continue;
            }

            if (buffer.Value.Values[bindingIndex] is FlattenedWriteValue.Literal { Value: not null })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProfileDataSource(WriteValueSource source) =>
        source
            is WriteValueSource.Scalar
                or WriteValueSource.DescriptorReference
                or WriteValueSource.DocumentReference
                or WriteValueSource.ReferenceDerived;

    private static ImmutableArray<FlattenedWriteValue> RewriteSeparateScopeParentKeyParts(
        TableWritePlan tablePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues
    ) =>
        tablePlan.TableModel.IdentityMetadata.TableKind is DbTableKind.CollectionExtensionScope
            ? RelationalWriteRowHelpers.RewriteParentKeyPartValues(
                tablePlan,
                values,
                parentPhysicalIdentityValues
            )
            : values.ToImmutableArray();

    private static RelationalWriteMergedTableState BuildDeleteState(
        TableWritePlan tablePlan,
        CurrentSeparateScopeRowProjection? currentRowProjection
    )
    {
        if (currentRowProjection is null)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has no "
                    + "current row projection for profiled Delete; expected exactly one."
            );
        }
        var projectedCurrentRow = currentRowProjection.ProjectedRow;
        return new RelationalWriteMergedTableState(tablePlan, [projectedCurrentRow], mergedRows: []);
    }

    /// <summary>
    /// Preserve: emit the current row as an IDENTICAL merged row. Omitting the merged row
    /// would be interpreted by the shared no-profile persister as a non-collection delete
    /// (current + no merged = delete), silently discarding stored hidden data. This is the
    /// critical correctness invariant of the Preserve outcome.
    /// </summary>
    private static RelationalWriteMergedTableState BuildPreserveState(
        TableWritePlan tablePlan,
        CurrentSeparateScopeRowProjection? currentRowProjection
    )
    {
        if (currentRowProjection is null)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has no "
                    + "current row projection for profiled Preserve; expected exactly one."
            );
        }
        var projectedCurrentRow = currentRowProjection.ProjectedRow;
        // Reuse the SAME row instance for both sides so the persister sees identical values
        // and treats the row as unchanged, preserving the stored hidden data.
        return new RelationalWriteMergedTableState(tablePlan, [projectedCurrentRow], [projectedCurrentRow]);
    }

    /// <summary>
    /// Apply the per-binding disposition to compute the merged row values. Skips
    /// resolver-owned bindings (left as default so the resolver can overwrite them).
    /// Shared between the root table and the separate-table Update paths.
    /// </summary>
    private static FlattenedWriteValue[] OverlayByDisposition(
        TableWritePlan tablePlan,
        ImmutableArray<FlattenedWriteValue> bufferValues,
        RelationalWriteMergedTableRow? projectedCurrentRow,
        ImmutableArray<RootBindingDisposition> bindingsByIndex,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(tablePlan);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        var mergedValues = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var bindingIndex = 0; bindingIndex < mergedValues.Length; bindingIndex++)
        {
            if (resolverOwnedBindingIndices.Contains(bindingIndex))
            {
                // Resolver will write; leave a default so the array is fully populated before
                // the resolver call (the resolver overwrites these indices).
                continue;
            }
            switch (bindingsByIndex[bindingIndex])
            {
                case RootBindingDisposition.VisibleWritable:
                case RootBindingDisposition.StorageManaged:
                    mergedValues[bindingIndex] = bufferValues[bindingIndex];
                    break;
                case RootBindingDisposition.HiddenPreserved:
                    if (projectedCurrentRow is null)
                    {
                        throw new InvalidOperationException(
                            $"Table binding at index {bindingIndex} on table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' "
                                + "classified HiddenPreserved, but no current row is available. "
                                + "Upstream contract violation between classifier and synthesizer."
                        );
                    }
                    mergedValues[bindingIndex] = projectedCurrentRow.Values[bindingIndex];
                    break;
                case RootBindingDisposition.ClearOnVisibleAbsent:
                    mergedValues[bindingIndex] = new FlattenedWriteValue.Literal(null);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected RootBindingDisposition '{bindingsByIndex[bindingIndex]}' "
                            + $"at index {bindingIndex} on table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}'."
                    );
            }
        }
        return mergedValues;
    }

    private static SeparateScopeBuffer RequireSeparateScopeBuffer(
        TableWritePlan tablePlan,
        SeparateScopeBuffer? buffer,
        string outcomeName
    )
    {
        if (buffer is null)
        {
            throw new InvalidOperationException(
                $"Flattened write set does not carry a separate-scope row buffer for table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' in scope "
                    + $"'{tablePlan.TableModel.JsonScope.Canonical}'. The decider selected {outcomeName} "
                    + "but the flattener produced no matching buffer — upstream contract violation."
            );
        }

        if (!ReferenceEquals(buffer.Value.TableWritePlan, tablePlan))
        {
            throw new InvalidOperationException(
                $"Separate-scope buffer table plan mismatch for scope "
                    + $"'{tablePlan.TableModel.JsonScope.Canonical}'. Expected table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}'."
            );
        }

        return buffer.Value;
    }

    private static RootExtensionWriteRowBuffer? TryLocateRootExtensionRow(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan
    )
    {
        return request.FlattenedWriteSet.RootRow.RootExtensionRows.FirstOrDefault(row =>
            ReferenceEquals(row.TableWritePlan, tablePlan)
        );
    }

    /// <summary>
    /// Navigate the root writable request body down to the separate-table plan's JSON
    /// scope when that scope is present. The extracted helper accepts a nullable scoped
    /// request node because Delete/Preserve/Skip paths do not require request-body scope
    /// content; the Update path fails closed if the node is absent.
    /// </summary>
    private static JsonNode? TryResolveScopedRequestNode(JsonNode rootBody, TableWritePlan tablePlan)
    {
        return RelationalWriteFlattener.TryGetRelativeLeafNode(
            rootBody,
            tablePlan.TableModel.JsonScope,
            out var scopeNode
        )
            ? scopeNode
            : null;
    }

    // -- Document-reference canonicalization helpers ------------------------

    /// <summary>
    /// Returns the semantic-identity positions backed by a document-reference FK column, paired with the
    /// reference binding metadata needed to resolve Core-emitted natural-key parts to the stored document id.
    /// </summary>
    internal static IReadOnlyList<DocumentReferenceIdentityPart> ResolveDocumentReferenceIdentityParts(
        ResourceWritePlan resourceWritePlan,
        TableWritePlan tablePlan
    )
    {
        var semanticBindings = tablePlan.TableModel.IdentityMetadata.SemanticIdentityBindings;

        if (semanticBindings.Count == 0)
        {
            return [];
        }

        List<DocumentReferenceIdentityPart>? result = null;

        for (var identityIndex = 0; identityIndex < semanticBindings.Count; identityIndex++)
        {
            var semanticBinding = semanticBindings[identityIndex];
            var column = tablePlan.TableModel.Columns.FirstOrDefault(c =>
                c.ColumnName.Equals(semanticBinding.ColumnName)
            );

            if (column?.Kind != ColumnKind.DocumentFk)
            {
                continue;
            }

            result ??= [];
            result.Add(
                ResolveDocumentReferenceIdentityPart(
                    resourceWritePlan,
                    tablePlan,
                    semanticBinding,
                    identityIndex
                )
            );
        }

        return result ?? (IReadOnlyList<DocumentReferenceIdentityPart>)[];
    }

    private static DocumentReferenceIdentityPart ResolveDocumentReferenceIdentityPart(
        ResourceWritePlan resourceWritePlan,
        TableWritePlan tablePlan,
        CollectionSemanticIdentityBinding semanticBinding,
        int identityIndex
    )
    {
        var documentBindings = resourceWritePlan.Model.DocumentReferenceBindings;

        for (var bindingIndex = 0; bindingIndex < documentBindings.Count; bindingIndex++)
        {
            var documentBinding = documentBindings[bindingIndex];

            if (
                !documentBinding.Table.Equals(tablePlan.TableModel.Table)
                || !documentBinding.FkColumn.Equals(semanticBinding.ColumnName)
            )
            {
                continue;
            }

            foreach (var identityBinding in documentBinding.IdentityBindings)
            {
                var relativePath = BuildScopeRelativeCanonical(
                    tablePlan.TableModel.JsonScope,
                    identityBinding.ReferenceJsonPath
                );

                if (
                    string.Equals(
                        relativePath,
                        semanticBinding.RelativePath.Canonical,
                        StringComparison.Ordinal
                    )
                )
                {
                    return new DocumentReferenceIdentityPart(
                        identityIndex,
                        bindingIndex,
                        documentBinding,
                        identityBinding
                    );
                }
            }
        }

        throw new InvalidOperationException(
            $"Document-reference semantic identity binding '{semanticBinding.RelativePath.Canonical}' "
                + $"on table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' could not be "
                + $"matched to document-reference metadata for FK column '{semanticBinding.ColumnName.Value}'."
        );
    }

    private static string BuildScopeRelativeCanonical(JsonPathExpression jsonScope, JsonPathExpression path)
    {
        var scopeCanonical = jsonScope.Canonical;
        var pathCanonical = path.Canonical;

        if (string.Equals(pathCanonical, scopeCanonical, StringComparison.Ordinal))
        {
            return "$";
        }

        if (string.Equals(scopeCanonical, "$", StringComparison.Ordinal))
        {
            return pathCanonical;
        }

        var scopePrefix = scopeCanonical + ".";

        return pathCanonical.StartsWith(scopePrefix, StringComparison.Ordinal)
            ? "$." + pathCanonical[scopePrefix.Length..]
            : pathCanonical;
    }

    /// <summary>
    /// Replaces request-side document-reference natural-key identity parts with the resolved referenced
    /// document id from the request-cycle reference cache.
    /// </summary>
    internal static ImmutableArray<VisibleRequestCollectionItem> CanonicalizeDocumentReferenceRequestItems(
        ImmutableArray<VisibleRequestCollectionItem> requestItems,
        IReadOnlyList<DocumentReferenceIdentityPart> documentIdentityParts,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (requestItems.IsDefaultOrEmpty)
        {
            return requestItems;
        }

        var builder = ImmutableArray.CreateBuilder<VisibleRequestCollectionItem>(requestItems.Length);

        foreach (var item in requestItems)
        {
            var ordinalPath = RelationalJsonPathSupport
                .ParseConcretePath(new JsonPath(item.RequestJsonPath))
                .OrdinalPath;
            var canonicalized = CanonicalizeRequestDocumentReferenceIdentityParts(
                item.Address.SemanticIdentityInOrder,
                documentIdentityParts,
                resolvedReferenceLookups,
                ordinalPath
            );

            builder.Add(
                item with
                {
                    Address = item.Address with { SemanticIdentityInOrder = canonicalized },
                }
            );
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<SemanticIdentityPart> CanonicalizeRequestDocumentReferenceIdentityParts(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> documentIdentityParts,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ReadOnlySpan<int> ordinalPath
    )
    {
        ImmutableArray<SemanticIdentityPart>.Builder? builder = null;

        foreach (var documentIdentityPart in documentIdentityParts)
        {
            if (documentIdentityPart.IdentityIndex >= identity.Length)
            {
                continue;
            }

            var part = identity[documentIdentityPart.IdentityIndex];

            if (!part.IsPresent)
            {
                continue;
            }

            var documentId = resolvedReferenceLookups.GetDocumentId(
                documentIdentityPart.BindingIndex,
                ordinalPath
            );

            if (documentId is null)
            {
                continue;
            }

            builder ??= identity.ToBuilder();
            builder[documentIdentityPart.IdentityIndex] = new SemanticIdentityPart(
                part.RelativePath,
                JsonValue.Create(documentId.Value),
                IsPresent: true
            );
        }

        return builder is null ? identity : builder.MoveToImmutable();
    }

    /// <summary>
    /// Replaces stored-side document-reference natural-key identity parts with the stored referenced
    /// document id by matching against the current row projection.
    /// </summary>
    internal static ImmutableArray<VisibleStoredCollectionRow> CanonicalizeDocumentReferenceStoredRows(
        ImmutableArray<VisibleStoredCollectionRow> storedRows,
        IReadOnlyList<DocumentReferenceIdentityPart> documentIdentityParts,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows
    )
    {
        if (storedRows.IsDefaultOrEmpty)
        {
            return storedRows;
        }

        var builder = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>(storedRows.Length);

        for (var storedRowIndex = 0; storedRowIndex < storedRows.Length; storedRowIndex++)
        {
            var row = storedRows[storedRowIndex];
            var canonicalized = CanonicalizeStoredDocumentReferenceIdentityParts(
                row.Address.SemanticIdentityInOrder,
                documentIdentityParts,
                tablePlan,
                resolvedReferenceLookups,
                currentRows,
                storedRowIndex,
                storedRows.Length
            );

            builder.Add(row with { Address = row.Address with { SemanticIdentityInOrder = canonicalized } });
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<SemanticIdentityPart> CanonicalizeStoredDocumentReferenceIdentityParts(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> documentIdentityParts,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows,
        int storedRowIndex,
        int storedRowsLength
    )
    {
        ImmutableArray<SemanticIdentityPart>.Builder? builder = null;

        foreach (var documentIdentityPart in documentIdentityParts)
        {
            if (documentIdentityPart.IdentityIndex >= identity.Length)
            {
                continue;
            }

            var part = identity[documentIdentityPart.IdentityIndex];

            if (!part.IsPresent)
            {
                continue;
            }

            var documentId = TryResolveDocumentIdFromCurrentRows(
                identity,
                documentIdentityParts,
                documentIdentityPart,
                tablePlan,
                resolvedReferenceLookups,
                currentRows,
                storedRowIndex,
                storedRowsLength
            );

            if (documentId is null)
            {
                throw new InvalidOperationException(
                    $"document reference not resolvable at merge boundary: stored reference "
                        + $"'{documentIdentityPart.Binding.ReferenceObjectPath.Canonical}' for FK column "
                        + $"'{documentIdentityPart.Binding.FkColumn.Value}' on table "
                        + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' could not be "
                        + "matched against current rows. "
                        + "This can happen when the reference natural-key contains a descriptor URI absent "
                        + "from the request-cycle cache, scalar-parts-only matching is ambiguous or absent, "
                        + "and the current-row count differs from the stored-row count so positional "
                        + "correspondence does not hold. "
                        + $"Current rows count: {currentRows.Length}, stored rows count: {storedRowsLength}, stored row index: {storedRowIndex}."
                );
            }

            builder ??= identity.ToBuilder();
            builder[documentIdentityPart.IdentityIndex] = new SemanticIdentityPart(
                part.RelativePath,
                JsonValue.Create(documentId.Value),
                IsPresent: true
            );
        }

        return builder is null ? identity : builder.MoveToImmutable();
    }

    /// <summary>
    /// Resolves the referenced document id for a stored-side reference natural-key identity
    /// by matching against current rows. Three strategies are tried in order, regardless of
    /// whether the reference natural-key contains scalar parts, descriptor parts, or both:
    ///
    /// <list type="number">
    ///   <item>Full natural-key match: every reference natural-key part (scalar + descriptor)
    ///   must match. Used when the descriptor URI cache holds every descriptor part of the
    ///   stored row's reference natural-key.</item>
    ///   <item>Scalar-parts-only match: when a descriptor URI cache miss makes Strategy 1
    ///   fail, match on the non-DescriptorFk natural-key parts and skip the descriptor parts
    ///   entirely. If the remaining scalar parts uniquely identify a current row, the
    ///   document id is read directly from that row's FK column. Mirrors the Strategy-1
    ///   scalar fallback that <see cref="TryResolveDescriptorIdFromCurrentRows"/> uses for
    ///   direct descriptor identity in the duplicate-scalar/different-descriptor shape.</item>
    ///   <item>Positional fallback: when neither full-match nor scalar-only match yields a
    ///   unique row, fall back to <c>currentRows[storedRowIndex]</c> when
    ///   <c>currentRows.Length == storedRowsLength</c> (no hidden rows interleaved).</item>
    /// </list>
    ///
    /// <para>Returns <c>null</c> when no strategy resolves; the caller throws.</para>
    /// </summary>
    private static long? TryResolveDocumentIdFromCurrentRows(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> documentIdentityParts,
        DocumentReferenceIdentityPart targetPart,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows,
        int storedRowIndex,
        int storedRowsLength
    )
    {
        if (currentRows.IsDefaultOrEmpty)
        {
            return null;
        }

        var sameReferenceParts = documentIdentityParts
            .Where(part =>
                part.BindingIndex == targetPart.BindingIndex && part.IdentityIndex < identity.Length
            )
            .ToArray();

        // Strategy 1: full natural-key match. Returns null on no-match or ambiguous match —
        // the latter is rare for natural-key matching (natural-key uniqueness should
        // disambiguate) but the helper guards it explicitly. Cache miss on any descriptor
        // part also surfaces here as no-match.
        var fullMatchId = TryResolveByReferenceFullMatch(
            identity,
            sameReferenceParts,
            targetPart,
            tablePlan,
            resolvedReferenceLookups,
            currentRows
        );

        if (fullMatchId is not null)
        {
            return fullMatchId;
        }

        // Strategy 2: scalar-parts-only match. When Strategy 1 fails because a descriptor URI
        // is absent from the request-cycle cache, the remaining scalar parts of the reference
        // natural-key may still uniquely identify the row. Skip every DescriptorFk part and
        // match only on the scalar parts; if a unique row is found, use its FK column.
        var scalarMatchId = TryResolveByReferenceScalarMatch(
            identity,
            sameReferenceParts,
            targetPart,
            tablePlan,
            currentRows
        );

        if (scalarMatchId is not null)
        {
            return scalarMatchId;
        }

        // Strategy 3: positional fallback. Safe only when current and stored row counts
        // agree (no hidden rows interleaved) — same fence as the descriptor identity path.
        if (currentRows.Length == storedRowsLength && storedRowIndex < currentRows.Length)
        {
            return TryGetInt64CurrentRowValue(currentRows[storedRowIndex], targetPart.Binding.FkColumn);
        }

        return null;
    }

    /// <summary>
    /// Strategy 1 of <see cref="TryResolveDocumentIdFromCurrentRows"/>: locate a unique
    /// current row that matches every reference natural-key part (scalar + descriptor) in
    /// <paramref name="identity"/>, then return that row's FK column value. Returns
    /// <c>null</c> when no row matches, when more than one row matches, or when a stored
    /// descriptor URI cannot be resolved against the request-cycle cache.
    /// </summary>
    private static long? TryResolveByReferenceFullMatch(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> sameReferenceParts,
        DocumentReferenceIdentityPart targetPart,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows
    )
    {
        CurrentCollectionRowSnapshot? matched = null;

        foreach (var currentRow in currentRows)
        {
            if (
                !DocumentReferenceIdentityPartsMatch(
                    identity,
                    sameReferenceParts,
                    tablePlan,
                    resolvedReferenceLookups,
                    currentRow
                )
            )
            {
                continue;
            }

            if (matched is not null)
            {
                return null;
            }

            matched = currentRow;
        }

        return matched is null ? null : TryGetInt64CurrentRowValue(matched, targetPart.Binding.FkColumn);
    }

    /// <summary>
    /// Strategy 2 of <see cref="TryResolveDocumentIdFromCurrentRows"/>: locate a unique
    /// current row that matches every NON-descriptor reference natural-key part in
    /// <paramref name="identity"/>, ignoring any DescriptorFk parts whose URI may be absent
    /// from the request-cycle cache. Returns the matched row's FK column value, or
    /// <c>null</c> when there are no scalar parts to match on, when no row matches, or when
    /// more than one row matches (in which case the caller falls through to positional
    /// matching).
    /// </summary>
    private static long? TryResolveByReferenceScalarMatch(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> sameReferenceParts,
        DocumentReferenceIdentityPart targetPart,
        TableWritePlan tablePlan,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows
    )
    {
        var scalarParts = sameReferenceParts
            .Where(part =>
            {
                var column = tablePlan.TableModel.Columns.FirstOrDefault(c =>
                    c.ColumnName.Equals(part.IdentityBinding.Column)
                );
                return column is not null && column.Kind != ColumnKind.DescriptorFk;
            })
            .ToArray();

        if (scalarParts.Length == 0)
        {
            return null;
        }

        CurrentCollectionRowSnapshot? matched = null;

        foreach (var currentRow in currentRows)
        {
            var allScalarsMatch = true;

            foreach (var scalarPart in scalarParts)
            {
                var storedPart = identity[scalarPart.IdentityIndex];

                if (!storedPart.IsPresent)
                {
                    allScalarsMatch = false;
                    break;
                }

                if (
                    !currentRow.CurrentRowByColumnName.TryGetValue(
                        scalarPart.IdentityBinding.Column,
                        out var currentValue
                    )
                )
                {
                    allScalarsMatch = false;
                    break;
                }

                var storedJson = storedPart.Value?.ToJsonString() ?? "null";
                var currentJson = currentValue is null
                    ? "null"
                    : JsonValue.Create(currentValue)?.ToJsonString() ?? "null";

                if (!string.Equals(storedJson, currentJson, StringComparison.Ordinal))
                {
                    allScalarsMatch = false;
                    break;
                }
            }

            if (!allScalarsMatch)
            {
                continue;
            }

            if (matched is not null)
            {
                return null;
            }

            matched = currentRow;
        }

        return matched is null ? null : TryGetInt64CurrentRowValue(matched, targetPart.Binding.FkColumn);
    }

    private static bool DocumentReferenceIdentityPartsMatch(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> documentIdentityParts,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        CurrentCollectionRowSnapshot currentRow
    )
    {
        foreach (var documentIdentityPart in documentIdentityParts)
        {
            var storedPart = identity[documentIdentityPart.IdentityIndex];

            if (
                !TryBuildStoredDocumentReferenceIdentityJson(
                    storedPart,
                    documentIdentityPart,
                    tablePlan,
                    resolvedReferenceLookups,
                    out var storedJson
                )
            )
            {
                return false;
            }

            if (
                !currentRow.CurrentRowByColumnName.TryGetValue(
                    documentIdentityPart.IdentityBinding.Column,
                    out var currentValue
                )
            )
            {
                return false;
            }

            var currentJson = currentValue is null
                ? "null"
                : JsonValue.Create(currentValue)?.ToJsonString() ?? "null";

            if (!string.Equals(storedJson, currentJson, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildStoredDocumentReferenceIdentityJson(
        SemanticIdentityPart storedPart,
        DocumentReferenceIdentityPart documentIdentityPart,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        out string storedJson
    )
    {
        storedJson = "null";

        if (!storedPart.IsPresent)
        {
            return false;
        }

        var column = tablePlan.TableModel.Columns.FirstOrDefault(c =>
            c.ColumnName.Equals(documentIdentityPart.IdentityBinding.Column)
        );

        if (column?.Kind != ColumnKind.DescriptorFk)
        {
            storedJson = storedPart.Value?.ToJsonString() ?? "null";
            return true;
        }

        if (storedPart.Value is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var descriptorId))
        {
            storedJson = JsonValue.Create(descriptorId)?.ToJsonString() ?? "null";
            return true;
        }

        var uri = storedPart.Value?.ToString();

        if (
            string.IsNullOrEmpty(uri)
            || column.TargetResource is null
            || !resolvedReferenceLookups.TryGetDescriptorIdByUri(
                column.TargetResource.Value,
                uri,
                out descriptorId
            )
        )
        {
            return false;
        }

        storedJson = JsonValue.Create(descriptorId)?.ToJsonString() ?? "null";
        return true;
    }

    private static long? TryGetInt64CurrentRowValue(
        CurrentCollectionRowSnapshot currentRow,
        DbColumnName columnName
    )
    {
        if (!currentRow.CurrentRowByColumnName.TryGetValue(columnName, out var value) || value is null)
        {
            return null;
        }

        if (value is long longValue)
        {
            return longValue;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    internal sealed record DocumentReferenceIdentityPart(
        int IdentityIndex,
        int BindingIndex,
        DocumentReferenceBinding Binding,
        ReferenceIdentityBinding IdentityBinding
    );

    // ── Descriptor-URI canonicalization helpers ────────────────────────────

    /// <summary>
    /// Rewrites each ancestor's <see cref="AncestorCollectionInstance.SemanticIdentityInOrder"/>
    /// in <paramref name="address"/> so descriptor-URI parts are replaced by their resolved
    /// Int64 ids and document-reference natural-key parts are replaced by their resolved
    /// document ids. This makes ancestor-keyed index lookups (built at walker construction
    /// from raw Core-emitted addresses) symmetric with recursion-side lookup keys (which the
    /// walker constructs from already-canonicalized current row identities).
    /// </summary>
    /// <remarks>
    /// <para>For each ancestor, this helper looks up the ancestor's scope's
    /// <see cref="TableWritePlan"/> via <paramref name="tablePlanByJsonScope"/>, then applies
    /// the same descriptor-URI / document-reference canonicalization logic that the per-row
    /// helpers (<see cref="CanonicalizeRequestDocumentReferenceIdentityParts"/>,
    /// <see cref="CanonicalizeIdentityParts"/>) use for the row's own identity.</para>
    ///
    /// <para>For descriptor parts, the URI cache (<see cref="FlatteningResolvedReferenceLookupSet.TryGetDescriptorIdByUri"/>)
    /// is the primary resolution path. URIs absent from the cache fall back to scanning all
    /// current rows for the ancestor's table (passed in <paramref name="currentRowsByJsonScope"/>)
    /// for a row whose semantic identity matches all non-descriptor parts; if that match is
    /// unique, its descriptor id at the same index position is used. Same-shape fallback as
    /// the per-row helpers, narrowed to scope-wide current rows because ancestor canonicalization
    /// happens at index-build time without per-(scope, parent-instance) partitioning.</para>
    ///
    /// <para>Document-reference ancestor parts are also canonicalized deterministically.
    /// The per-row request/stored helpers use <c>(bindingIndex, ordinalPath)</c> via
    /// <see cref="FlatteningResolvedReferenceLookupSet.GetDocumentId"/> for request-side
    /// resolution; the ancestor pass mirrors that on the request side via the child's
    /// derived ordinal-path prefix and otherwise scans the current rows for a unique row
    /// whose natural-key columns match the stored natural-key parts and reads the resolved
    /// backend document id directly from the matched row's FK column. The current-row scan
    /// is restricted to the target parent partition (looked up in
    /// <paramref name="currentRowsByJsonScopeAndParent"/> by <c>(ancestor scope, canonical
    /// target parent address)</c>) so siblings in different parent instances are not
    /// treated as ambiguous matches, mirroring the descriptor pass's partitioning. When
    /// the natural-key parts cannot be uniquely resolved within that partition (and the
    /// request-side cache also misses), the helper fails closed rather than leaving the
    /// URI/natural-key in place silently, avoiding the same lookup-miss-via-form-mismatch
    /// shape the descriptor pass closes.</para>
    ///
    /// <para>If <paramref name="address"/> has no ancestor instances, returns the original
    /// reference unchanged. If no ancestor changes, returns the original reference.</para>
    /// </remarks>
    internal static ScopeInstanceAddress CanonicalizeAddressAncestors(
        ScopeInstanceAddress address,
        IReadOnlyDictionary<string, TableWritePlan> tablePlanByJsonScope,
        IReadOnlyDictionary<string, ImmutableArray<CurrentCollectionRowSnapshot>> currentRowsByJsonScope,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent,
        IReadOnlyDictionary<string, ImmutableArray<VisibleStoredCollectionRow>> storedRowsByJsonScope,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ResourceWritePlan resourceWritePlan
    )
    {
        return CanonicalizeAddressAncestorsCore(
            address,
            tablePlanByJsonScope,
            currentRowsByJsonScope,
            currentRowsByJsonScopeAndParent,
            storedRowsByJsonScope,
            resolvedReferenceLookups,
            resourceWritePlan,
            requestOrdinalPath: default,
            hasRequestOrdinalPath: false
        );
    }

    /// <summary>
    /// Request-side variant of <see cref="CanonicalizeAddressAncestors"/>. Accepts the child
    /// item's <c>RequestJsonPath</c> so document-reference ancestor identities can be resolved
    /// via the request-cycle reference cache (<see cref="FlatteningResolvedReferenceLookupSet.GetDocumentId"/>)
    /// when no current row exists yet for an inserted parent — the fresh-insert case where
    /// the stored-side current-row scan returns null.
    ///
    /// <para>The ancestor's ordinal path within the request is derived as the prefix of the
    /// child's parsed ordinal path matching the wildcard count of the ancestor's
    /// <see cref="AncestorCollectionInstance.JsonScope"/>. For child path
    /// <c>$.parents[0].children[1]</c> and ancestor scope <c>$.parents[*]</c>, the ancestor
    /// ordinal path is <c>[0]</c>. The reference cache is keyed by
    /// <c>(BindingIndex, ordinalPath)</c>, so this matches how the per-row request-side
    /// helper (<see cref="CanonicalizeRequestDocumentReferenceIdentityParts"/>) resolves
    /// document references for the row's own identity.</para>
    ///
    /// <para>If the request-cycle cache yields no resolved document id for the ancestor's
    /// natural-key (e.g., the request body did not exercise that reference path), the helper
    /// falls back to the same current-row-based scan used by the stored-side variant. This
    /// preserves the matched-update path's behavior while extending coverage to the
    /// fresh-insert path.</para>
    /// </summary>
    internal static ScopeInstanceAddress CanonicalizeAddressAncestorsForRequestItem(
        ScopeInstanceAddress address,
        string requestJsonPath,
        IReadOnlyDictionary<string, TableWritePlan> tablePlanByJsonScope,
        IReadOnlyDictionary<string, ImmutableArray<CurrentCollectionRowSnapshot>> currentRowsByJsonScope,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent,
        IReadOnlyDictionary<string, ImmutableArray<VisibleStoredCollectionRow>> storedRowsByJsonScope,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ResourceWritePlan resourceWritePlan
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJsonPath);

        if (address.AncestorCollectionInstances.IsDefaultOrEmpty)
        {
            return address;
        }

        var parsed = RelationalJsonPathSupport.ParseConcretePath(new JsonPath(requestJsonPath));

        return CanonicalizeAddressAncestorsCore(
            address,
            tablePlanByJsonScope,
            currentRowsByJsonScope,
            currentRowsByJsonScopeAndParent,
            storedRowsByJsonScope,
            resolvedReferenceLookups,
            resourceWritePlan,
            requestOrdinalPath: parsed.OrdinalPath,
            hasRequestOrdinalPath: true
        );
    }

    private static ScopeInstanceAddress CanonicalizeAddressAncestorsCore(
        ScopeInstanceAddress address,
        IReadOnlyDictionary<string, TableWritePlan> tablePlanByJsonScope,
        IReadOnlyDictionary<string, ImmutableArray<CurrentCollectionRowSnapshot>> currentRowsByJsonScope,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent,
        IReadOnlyDictionary<string, ImmutableArray<VisibleStoredCollectionRow>> storedRowsByJsonScope,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ResourceWritePlan resourceWritePlan,
        ReadOnlySpan<int> requestOrdinalPath,
        bool hasRequestOrdinalPath
    )
    {
        if (address.AncestorCollectionInstances.IsDefaultOrEmpty)
        {
            return address;
        }

        ImmutableArray<AncestorCollectionInstance>.Builder? builder = null;

        for (var i = 0; i < address.AncestorCollectionInstances.Length; i++)
        {
            var ancestor = address.AncestorCollectionInstances[i];

            if (!tablePlanByJsonScope.TryGetValue(ancestor.JsonScope, out var ancestorTablePlan))
            {
                continue;
            }

            var descriptorIndices = ResolveDescriptorIdentityIndices(ancestorTablePlan);
            var documentReferenceParts = ResolveDocumentReferenceIdentityParts(
                resourceWritePlan,
                ancestorTablePlan
            );

            if (descriptorIndices.Count == 0 && documentReferenceParts.Count == 0)
            {
                continue;
            }

            currentRowsByJsonScope.TryGetValue(ancestor.JsonScope, out var ancestorCurrentRows);
            var ancestorRows = ancestorCurrentRows.IsDefault
                ? ImmutableArray<CurrentCollectionRowSnapshot>.Empty
                : ancestorCurrentRows;

            storedRowsByJsonScope.TryGetValue(ancestor.JsonScope, out var ancestorStoredRows);
            var ancestorVisibleStoredRows = ancestorStoredRows.IsDefault
                ? ImmutableArray<VisibleStoredCollectionRow>.Empty
                : ancestorStoredRows;

            // Build the target parent address pair so per-partition positional fallback can
            // intersect ancestor stored rows (raw URI form) with the right
            // partition's current rows (canonical Int64 form). Raw form is built from the
            // input chain; canonical form uses the in-progress builder so far, mirroring how
            // BuildContainingScopeAddress walks ancestors during the walker's recursion.
            var rawParentAddress = BuildAncestorTargetParentAddress(
                address.AncestorCollectionInstances,
                builder,
                i,
                useCanonicalChain: false
            );
            var canonicalParentAddress = BuildAncestorTargetParentAddress(
                address.AncestorCollectionInstances,
                builder,
                i,
                useCanonicalChain: true
            );

            var identity = ancestor.SemanticIdentityInOrder;

            if (descriptorIndices.Count > 0)
            {
                identity = CanonicalizeAncestorDescriptorParts(
                    identity,
                    ancestorTablePlan,
                    descriptorIndices,
                    resolvedReferenceLookups,
                    ancestorRows,
                    ancestorVisibleStoredRows,
                    rawParentAddress,
                    canonicalParentAddress,
                    currentRowsByJsonScopeAndParent
                );
            }

            if (documentReferenceParts.Count > 0)
            {
                // For the request-side path, derive the ancestor's ordinal path within the
                // request as the prefix of the child's parsed ordinal path matching the
                // wildcard count of the ancestor's JsonScope. This lets the resolver use the
                // request-cycle reference cache for inserted parents (no current row exists
                // yet). The stored-side path passes hasRequestOrdinalPath = false so the
                // cache lookup is skipped and only the current-row scan is used (stored
                // ancestors always have a current row).
                ReadOnlySpan<int> ancestorRequestOrdinalPath = default;
                bool ancestorHasRequestOrdinalPath = false;

                if (hasRequestOrdinalPath)
                {
                    var ancestorWildcardCount = CountWildcardSegments(ancestor.JsonScope);
                    if (ancestorWildcardCount <= requestOrdinalPath.Length)
                    {
                        ancestorRequestOrdinalPath = requestOrdinalPath[..ancestorWildcardCount];
                        ancestorHasRequestOrdinalPath = true;
                    }
                }

                identity = CanonicalizeAncestorDocumentReferenceParts(
                    identity,
                    ancestorTablePlan,
                    documentReferenceParts,
                    ancestor.JsonScope,
                    resolvedReferenceLookups,
                    canonicalParentAddress,
                    currentRowsByJsonScopeAndParent,
                    ancestorRequestOrdinalPath,
                    ancestorHasRequestOrdinalPath
                );
            }

            // Either pass returns the input array unchanged when no part was rewritten.
            // ImmutableArray<T> is a value-type wrapper; equality is structural via the
            // underlying array reference, so identity comparison is the correct "no-change"
            // signal here.
            if (
                ScopeInstanceAddressComparer.SemanticIdentityEquals(
                    identity,
                    ancestor.SemanticIdentityInOrder
                )
            )
            {
                continue;
            }

            builder ??= address.AncestorCollectionInstances.ToBuilder();
            builder[i] = new AncestorCollectionInstance(ancestor.JsonScope, identity);
        }

        return builder is null
            ? address
            : new ScopeInstanceAddress(address.JsonScope, builder.MoveToImmutable());
    }

    /// <summary>
    /// Counts wildcard <c>[*]</c> segments in a canonical JsonScope (e.g., the canonical
    /// scope <c>$.parents[*].children[*]</c> has 2 wildcards). Used to derive the prefix of
    /// a child item's request ordinal path that corresponds to the ancestor's scope.
    /// </summary>
    private static int CountWildcardSegments(string jsonScope)
    {
        if (string.IsNullOrEmpty(jsonScope))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = jsonScope.IndexOf("[*]", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 3;
        }
        return count;
    }

    /// <summary>
    /// Canonicalizes descriptor-URI parts of an ancestor's
    /// <see cref="AncestorCollectionInstance.SemanticIdentityInOrder"/>. URI cache hits are
    /// preferred; URI cache misses fall back to scanning <paramref name="currentRows"/> for a
    /// scalar-match row whose descriptor id is then copied at the same index position. If
    /// neither the resolver cache nor scalar-match yields a unique descriptor id and
    /// count-equal positional pairing cannot resolve the URI, the helper fails closed via
    /// <see cref="InvalidOperationException"/> rather than leaving the URI form in place,
    /// because a URI-form ancestor identity silently mis-buckets the row in the walker's
    /// address-keyed visible-stored index — recursion looks up by canonical Int64 and a
    /// URI-form bucket and the lookup key would carry different forms.
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> CanonicalizeAncestorDescriptorParts(
        ImmutableArray<SemanticIdentityPart> identity,
        TableWritePlan ancestorTablePlan,
        IReadOnlyList<int> descriptorIndices,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows,
        ImmutableArray<VisibleStoredCollectionRow> ancestorVisibleStoredRows,
        ScopeInstanceAddress rawTargetParentAddress,
        ScopeInstanceAddress canonicalTargetParentAddress,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent
    )
    {
        var bindings = ancestorTablePlan.TableModel.IdentityMetadata.SemanticIdentityBindings;
        ImmutableArray<SemanticIdentityPart>.Builder? builder = null;

        foreach (var idx in descriptorIndices)
        {
            if (idx >= identity.Length || idx >= bindings.Count)
            {
                continue;
            }

            var part = identity[idx];

            if (!part.IsPresent || part.Value is null)
            {
                continue;
            }

            // Already canonicalized (Int64). The ancestor came from a path that already
            // applied canonicalization (e.g., walker-recursion-built address). Skip.
            if (part.Value is JsonValue jvAlready && jvAlready.TryGetValue<long>(out _))
            {
                continue;
            }

            var uri = part.Value.ToString();

            if (string.IsNullOrEmpty(uri))
            {
                continue;
            }

            var binding = bindings[idx];
            var column = ancestorTablePlan.TableModel.Columns.FirstOrDefault(c =>
                c.ColumnName.Equals(binding.ColumnName)
            );

            if (column?.TargetResource is null)
            {
                continue;
            }

            long descriptorId;
            if (
                resolvedReferenceLookups.TryGetDescriptorIdByUri(
                    column.TargetResource.Value,
                    uri,
                    out descriptorId
                )
            )
            {
                builder ??= identity.ToBuilder();
                builder[idx] = new SemanticIdentityPart(
                    part.RelativePath,
                    JsonValue.Create(descriptorId),
                    IsPresent: true
                );
                continue;
            }

            // Cache miss: scan current rows for a scalar-match. This covers delete-by-absence
            // ancestor scenarios where the URI was never resolved as part of the current request.
            var fallbackId = TryResolveAncestorDescriptorIdFromCurrentRows(
                identity,
                descriptorIndices,
                idx,
                currentRows,
                ancestorVisibleStoredRows,
                rawTargetParentAddress,
                canonicalTargetParentAddress,
                ancestorTablePlan.TableModel.JsonScope.Canonical,
                currentRowsByJsonScopeAndParent
            );

            if (fallbackId is null)
            {
                // Fail closed when the ancestor descriptor URI cannot be canonicalized.
                // Leaving the URI form in place silently mis-buckets the row in the walker's
                // address-keyed visible-stored index because recursion looks up by canonical
                // Int64 — the bucket and the lookup carry different forms and the planner
                // mistakes unmatched current rows for hidden preserves. The throw fires only
                // when the cache misses, scalar matching is absent or ambiguous, and
                // count-equal positional pairing cannot resolve (counts diverge or the URI
                // is absent from the visible-stored ancestor list).
                throw new InvalidOperationException(
                    "Cannot canonicalize descriptor-URI ancestor identity for scope "
                        + $"'{LogSanitizer.SanitizeForLog(ancestorTablePlan.TableModel.JsonScope.Canonical)}': "
                        + $"stored URI '{LogSanitizer.SanitizeForLog(uri)}' for column "
                        + $"'{LogSanitizer.SanitizeForLog(binding.ColumnName.Value)}' on table "
                        + $"'{LogSanitizer.SanitizeForLog(ProfileBindingClassificationCore.FormatTable(ancestorTablePlan))}' "
                        + "is absent from the request-cycle resolver cache, scalar-match yielded "
                        + "no unique row, and count-equal positional pairing cannot resolve. "
                        + $"Ancestor visible-stored rows count: {ancestorVisibleStoredRows.Length}, "
                        + $"current rows count: {currentRows.Length}."
                );
            }

            builder ??= identity.ToBuilder();
            builder[idx] = new SemanticIdentityPart(
                part.RelativePath,
                JsonValue.Create(fallbackId.Value),
                IsPresent: true
            );
        }

        return builder is null ? identity : builder.MoveToImmutable();
    }

    /// <summary>
    /// Canonicalizes document-reference natural-key parts of an ancestor's
    /// <see cref="AncestorCollectionInstance.SemanticIdentityInOrder"/>. Each
    /// <see cref="DocumentReferenceIdentityPart"/> identifies an FK-backed identity slot
    /// and the reference natural-key bindings on the ancestor's table. Resolution scans
    /// the current rows scoped to the target parent partition (looked up in
    /// <paramref name="currentRowsByJsonScopeAndParent"/> by
    /// <c>(ancestorJsonScope, canonicalTargetParentAddress)</c>) for a unique row whose
    /// natural-key columns match the stored identity parts (Strategy-2 scalar match
    /// adapted from <see cref="TryResolveByReferenceScalarMatch"/>); the matched row's
    /// FK column yields the backend document id.
    /// <para>
    /// Parent-partitioned resolution — and the descriptor-pass mirror at
    /// <see cref="TryResolveAncestorDescriptorIdFromCurrentRows"/> — require the scan
    /// to be parent-partitioned, not scope-wide. A valid nested shape can have the same
    /// referenced document natural key under two different parent instances; a scope-wide
    /// scan would treat that as ambiguous and fail closed even though each parent
    /// partition has exactly one valid match. When the partition map has no entry for the
    /// target (e.g., extension-child parents whose containing-scope address could not be
    /// derived), the partition lookup returns empty and the row scan resolves to null —
    /// the request-cycle reference cache may still resolve (inserted parents), otherwise
    /// the helper fails closed below.
    /// </para>
    /// <para>
    /// Natural-key matching is descriptor-aware. When the natural key contains a
    /// <see cref="ColumnKind.DescriptorFk"/> part (e.g.,
    /// <c>programReference.programTypeDescriptor</c>), the stored ancestor identity carries
    /// the URI string while the current row carries the canonical Int64 descriptor id;
    /// matching uses the request-cycle URI cache
    /// (<paramref name="resolvedReferenceLookups"/>) to canonicalize the stored URI before
    /// comparing it to the current row's column value. This mirrors what
    /// <see cref="TryResolveByReferenceFullMatch"/> already does for the per-row path via
    /// <see cref="DocumentReferenceIdentityPartsMatch"/>.
    /// </para>
    /// <para>
    /// When deterministic resolution fails — no partition rows, no scalar parts, ambiguous
    /// or no match — the helper fails closed (Shape B) with a descriptive
    /// <see cref="InvalidOperationException"/>. Silent partial canonicalization is
    /// explicitly avoided: a stale natural-key form would mask the same lookup-miss shape
    /// in nested recursion that the descriptor pass closes for descriptor URIs.
    /// </para>
    /// </summary>
    internal static ImmutableArray<SemanticIdentityPart> CanonicalizeAncestorDocumentReferenceParts(
        ImmutableArray<SemanticIdentityPart> identity,
        TableWritePlan ancestorTablePlan,
        IReadOnlyList<DocumentReferenceIdentityPart> documentReferenceParts,
        string ancestorJsonScope,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ScopeInstanceAddress canonicalTargetParentAddress,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent,
        ReadOnlySpan<int> ancestorRequestOrdinalPath,
        bool hasRequestOrdinalPath
    )
    {
        ImmutableArray<SemanticIdentityPart>.Builder? builder = null;

        // Partition the row scan by the target parent address so siblings in different
        // parent instances are not treated as ambiguous matches. The request-cycle
        // reference cache lookup below stays unchanged — it is keyed by (BindingIndex,
        // ordinalPath) and is already partition-correct by construction.
        var partitionCurrentRows =
            currentRowsByJsonScopeAndParent.TryGetValue(
                (ancestorJsonScope, canonicalTargetParentAddress),
                out var partitionRows
            ) && !partitionRows.IsDefault
                ? partitionRows
                : ImmutableArray<CurrentCollectionRowSnapshot>.Empty;

        foreach (var documentIdentityPart in documentReferenceParts)
        {
            if (documentIdentityPart.IdentityIndex >= identity.Length)
            {
                continue;
            }

            var part = identity[documentIdentityPart.IdentityIndex];

            if (!part.IsPresent || part.Value is null)
            {
                continue;
            }

            // Always run resolver-based canonicalization. A previous TryGetValue<long>
            // short-circuit was unsafe: Ed-Fi document-reference natural keys are frequently
            // numeric long values (e.g., schoolId, educationOrganizationId, programId), which
            // parse as long but still require natural-key -> backend DocumentId
            // canonicalization. Skipping based on long-parseability left the index keyed by
            // the natural-key form while the walker's recursion lookup is built from the
            // canonicalized DocumentId form, producing the same lookup-miss class fixed for
            // descriptor URIs by ancestor descriptor canonicalization.
            //
            // Idempotency at recursion time is not a concern: the walker's recursion does not
            // re-canonicalize ancestor parts — it only looks up the indexes that this method
            // populates at construction. If a future code path needs to re-canonicalize
            // already-canonicalized values, that should be tracked with an explicit
            // canonical-vs-raw flag rather than type-parseability.
            long? resolvedDocumentId = null;

            // On the request-side path, try the request-cycle reference cache first. This
            // succeeds for inserted parents (no current row exists yet) where the
            // stored-side current-row scan would return null. The ordinal path is the
            // prefix of the child item's parsed ordinal path that corresponds to the
            // ancestor's JsonScope wildcards.
            if (hasRequestOrdinalPath)
            {
                resolvedDocumentId = resolvedReferenceLookups.GetDocumentId(
                    documentIdentityPart.BindingIndex,
                    ancestorRequestOrdinalPath
                );
            }

            // Stored-side path or request-side cache miss: fall back to the current-row scan
            // restricted to the target parent partition. Matched-update parents always have
            // a current row in their partition, so this preserves the prior behavior for the
            // single-partition case while closing the false-ambiguity gap when the same
            // referenced document natural key appears under two different parent instances.
            // For the request-side path, the cache hit above takes precedence so inserted
            // parents resolve via the cache instead of failing closed against an empty partition.
            resolvedDocumentId ??= TryResolveAncestorDocumentReferenceIdFromCurrentRows(
                identity,
                documentReferenceParts,
                documentIdentityPart,
                ancestorTablePlan,
                resolvedReferenceLookups,
                partitionCurrentRows
            );

            if (resolvedDocumentId is null)
            {
                throw new InvalidOperationException(
                    "Cannot canonicalize document-reference ancestor identity for scope "
                        + $"'{LogSanitizer.SanitizeForLog(ancestorJsonScope)}': natural-key parts "
                        + $"{LogSanitizer.SanitizeForLog(FormatIdentity(identity))} could not be "
                        + "uniquely resolved against the target parent partition's current rows "
                        + $"for FK column '{LogSanitizer.SanitizeForLog(documentIdentityPart.Binding.FkColumn.Value)}' "
                        + $"on table '{LogSanitizer.SanitizeForLog(ProfileBindingClassificationCore.FormatTable(ancestorTablePlan))}'. "
                        + "Ancestor document-reference canonicalization fails closed here to avoid "
                        + "lookup misses caused by mixed canonical and natural-key identity forms. "
                        + "This typically indicates either partition coverage is incomplete for "
                        + "the ancestor's parent address or the natural-key parts "
                        + $"(count: {partitionCurrentRows.Length} "
                        + "current rows in the target parent partition) are ambiguous within it."
                );
            }

            builder ??= identity.ToBuilder();
            builder[documentIdentityPart.IdentityIndex] = new SemanticIdentityPart(
                part.RelativePath,
                JsonValue.Create(resolvedDocumentId.Value),
                IsPresent: true
            );
        }

        return builder is null ? identity : builder.MoveToImmutable();
    }

    /// <summary>
    /// Scans <paramref name="currentRows"/> for a unique row whose natural-key columns match
    /// the reference natural-key parts in <paramref name="identity"/>, then returns the
    /// matched row's FK column value at <paramref name="targetPart"/>. Mirrors
    /// <see cref="TryResolveDocumentIdFromCurrentRows"/>'s full-match then scalar-fallback
    /// strategies. Callers (the ancestor canonicalization path) restrict
    /// <paramref name="currentRows"/> to a single parent partition so the uniqueness checks
    /// here do not conflate siblings in different parent instances; positional fallback is
    /// not available at ancestor canonicalization time.
    /// <para>
    /// Natural-key matching is descriptor-aware — when the natural key contains a
    /// <see cref="ColumnKind.DescriptorFk"/> part, the stored URI is canonicalized via
    /// <paramref name="resolvedReferenceLookups"/> before comparing against the current
    /// row's Int64 descriptor id. The shared comparison helper
    /// <see cref="DocumentReferenceIdentityPartsMatch"/> performs this canonicalization for
    /// both this ancestor path and the per-row <see cref="TryResolveByReferenceFullMatch"/>
    /// path. Without descriptor-aware comparison, a composite natural key with a descriptor
    /// part (e.g., <c>programId + programTypeDescriptor</c>) would never match: stored
    /// identity carries the URI string and the current row carries the descriptor Int64.
    /// </para>
    /// </summary>
    private static long? TryResolveAncestorDocumentReferenceIdFromCurrentRows(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<DocumentReferenceIdentityPart> documentReferenceParts,
        DocumentReferenceIdentityPart targetPart,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows
    )
    {
        if (currentRows.IsDefaultOrEmpty)
        {
            return null;
        }

        // Restrict to natural-key parts that share the same reference (BindingIndex) as the
        // target part — these are the columns that uniquely identify the same parent row's
        // reference. This mirrors the per-row helper's same-reference partitioning.
        var sameReferenceParts = documentReferenceParts
            .Where(part =>
                part.BindingIndex == targetPart.BindingIndex && part.IdentityIndex < identity.Length
            )
            .ToArray();

        if (sameReferenceParts.Length == 0)
        {
            return null;
        }

        var fullMatchId = TryResolveByReferenceFullMatch(
            identity,
            sameReferenceParts,
            targetPart,
            tablePlan,
            resolvedReferenceLookups,
            currentRows
        );

        if (fullMatchId is not null)
        {
            return fullMatchId;
        }

        return TryResolveByReferenceScalarMatch(
            identity,
            sameReferenceParts,
            targetPart,
            tablePlan,
            currentRows
        );
    }

    /// <summary>
    /// Formats <paramref name="identity"/> for fail-closed exception messages. The output is
    /// passed through <see cref="LogSanitizer.SanitizeForLog"/> at the call site to prevent
    /// log forging via schema-sourced control characters.
    /// </summary>
    private static string FormatIdentity(ImmutableArray<SemanticIdentityPart> identity) =>
        string.Join(",", identity.Select(p => $"{p.RelativePath}={p.Value?.ToJsonString() ?? "null"}"));

    /// <summary>
    /// Resolves the canonical Int64 descriptor id for an ancestor's URI-form identity within
    /// the target parent partition. Mirrors the per-row descriptor resolution chain
    /// (cache → scalar → positional → fail-closed throw at the caller) adapted to ancestor canonicalization
    /// using the per-(scope, parent address) partition map so both strategies operate on
    /// the same single parent partition required by nested and extension-child matching.
    /// <list type="number">
    ///   <item>Strategy 1 — scalar match scoped to the target partition's current rows.
    ///   Looks up <c>(ancestorJsonScope, canonicalTargetParentAddress)</c> in
    ///   <paramref name="currentRowsByJsonScopeAndParent"/> and applies
    ///   <see cref="TryResolveByScalarMatch"/> within that partition only. Scope-wide scalar
    ///   matching would falsely treat siblings in different parent partitions as ambiguous
    ///   (e.g., two parents each with a <c>code = "A"</c> row).</item>
    ///   <item>Strategy 2 — count-equal positional within the same partition. Filters
    ///   <paramref name="ancestorVisibleStoredRows"/> by
    ///   <paramref name="rawTargetParentAddress"/> (raw URI form mirrors how upstream emits
    ///   stored rows' <c>Address.ParentAddress</c>) and pairs the result 1:1 with the
    ///   partition's current rows when counts are equal — both sides are sorted by stored
    ///   ordinal within the partition (planner invariant <c>ValidateStoredOrdinalOrder</c>
    ///   on stored, per-parent bucket sorting on current). Locates
    ///   <paramref name="identity"/> in the partition's stored rows by raw-value match and
    ///   copies the descriptor id from the same-position current row.</item>
    /// </list>
    /// <para>
    /// Returns <c>null</c> when no strategy resolves (or when the partition map has no
    /// entry for the target — e.g., extension-collection parents whose containing-scope
    /// address could not be derived); the caller then throws fail-closed, preserving the
    /// fail-closed descriptor-resolution constraint at
    /// <c>05-nested-and-extension-collection-merge.md:56</c> together with
    /// the per-parent partitioning rule at <c>05-nested-and-extension-collection-merge.md:47</c>.
    /// </para>
    /// </summary>
    internal static long? TryResolveAncestorDescriptorIdFromCurrentRows(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<int> descriptorIndices,
        int descriptorIdx,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows,
        ImmutableArray<VisibleStoredCollectionRow> ancestorVisibleStoredRows,
        ScopeInstanceAddress rawTargetParentAddress,
        ScopeInstanceAddress canonicalTargetParentAddress,
        string ancestorJsonScope,
        IReadOnlyDictionary<
            (string JsonScope, ScopeInstanceAddress ParentAddress),
            ImmutableArray<CurrentCollectionRowSnapshot>
        > currentRowsByJsonScopeAndParent
    )
    {
        if (currentRows.IsDefaultOrEmpty)
        {
            return null;
        }

        if (
            !currentRowsByJsonScopeAndParent.TryGetValue(
                (ancestorJsonScope, canonicalTargetParentAddress),
                out var partitionCurrent
            ) || partitionCurrent.IsDefaultOrEmpty
        )
        {
            return null;
        }

        var scalarIndices = Enumerable
            .Range(0, identity.Length)
            .Where(i => !descriptorIndices.Contains(i))
            .ToList();

        // Strategy 1: scalar match scoped to the target parent partition. Nested and
        // extension-child matching uses stable parent address plus ancestor context, not
        // scope-wide state — siblings in different partitions can share scalar values (e.g.,
        // `code = "A"` under two different parents) and a scope-wide scan would treat them
        // as ambiguous.
        if (scalarIndices.Count > 0)
        {
            var scalarMatchId = TryResolveByScalarMatch(
                identity,
                scalarIndices,
                descriptorIdx,
                partitionCurrent
            );
            if (scalarMatchId is not null)
            {
                return scalarMatchId;
            }
        }

        var partitionStored = ancestorVisibleStoredRows.IsDefaultOrEmpty
            ? ImmutableArray<VisibleStoredCollectionRow>.Empty
            :
            [
                .. ancestorVisibleStoredRows.Where(r =>
                    ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(
                        r.Address.ParentAddress,
                        rawTargetParentAddress
                    )
                ),
            ];

        if (partitionStored.IsDefaultOrEmpty || partitionCurrent.Length != partitionStored.Length)
        {
            return null;
        }

        for (var i = 0; i < partitionStored.Length; i++)
        {
            if (!SemanticIdentityRawValuesMatch(partitionStored[i].Address.SemanticIdentityInOrder, identity))
            {
                continue;
            }

            if (descriptorIdx >= partitionCurrent[i].SemanticIdentityInOrder.Length)
            {
                return null;
            }

            var positionalPart = partitionCurrent[i].SemanticIdentityInOrder[descriptorIdx];
            if (
                positionalPart.IsPresent
                && positionalPart.Value is JsonValue jv
                && jv.TryGetValue<long>(out var positionalId)
            )
            {
                return positionalId;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Builds the parent address for ancestor at index <paramref name="ancestorIndex"/> in
    /// <paramref name="chain"/>. The address mirrors what
    /// <c>BuildContainingScopeAddress</c> produces during walker recursion: the parent
    /// JsonScope is derived from <paramref name="chain"/>[ancestorIndex]'s OWN scope (so an
    /// extension-child ancestor under a root-extension yields <c>$._ext.&lt;name&gt;</c>, an
    /// extension-child ancestor under an aligned scope yields the aligned scope, etc.) and
    /// the parent's <see cref="ScopeInstanceAddress.AncestorCollectionInstances"/> is
    /// <paramref name="chain"/>[0..ancestorIndex] — every preceding collection ancestor
    /// inclusive of the immediate parent's self-entry. Aligned scopes are transparent in
    /// the chain (they don't add their own <see cref="AncestorCollectionInstance"/>
    /// entries) so this slice matches both stored rows' raw <c>Address.ParentAddress</c>
    /// and the partition map's canonical keys.
    /// <para>
    /// <paramref name="useCanonicalChain"/> controls whether already-processed ancestors
    /// come from <paramref name="canonicalBuilder"/> (canonical Int64 form for descriptors)
    /// or from <paramref name="chain"/> directly (raw URI form). The raw form aligns with
    /// <c>VisibleStoredCollectionRow.Address.ParentAddress</c> for filtering ancestor
    /// stored rows; the canonical form aligns with the parent addresses keyed by current
    /// rows' canonical semantic identities.
    /// </para>
    /// </summary>
    internal static ScopeInstanceAddress BuildAncestorTargetParentAddress(
        ImmutableArray<AncestorCollectionInstance> chain,
        ImmutableArray<AncestorCollectionInstance>.Builder? canonicalBuilder,
        int ancestorIndex,
        bool useCanonicalChain
    )
    {
        AncestorCollectionInstance Pick(int k) =>
            useCanonicalChain && canonicalBuilder is not null ? canonicalBuilder[k] : chain[k];

        var parentJsonScope = ProfileCollectionWalker.ComputeParentJsonScope(chain[ancestorIndex].JsonScope);

        if (ancestorIndex == 0)
        {
            return new ScopeInstanceAddress(
                parentJsonScope,
                ImmutableArray<AncestorCollectionInstance>.Empty
            );
        }

        var parentAncestors = ImmutableArray.CreateBuilder<AncestorCollectionInstance>(ancestorIndex);
        for (var k = 0; k < ancestorIndex; k++)
        {
            parentAncestors.Add(Pick(k));
        }
        return new ScopeInstanceAddress(parentJsonScope, parentAncestors.MoveToImmutable());
    }

    /// <summary>
    /// Structural equality on <see cref="SemanticIdentityPart"/> sequences using the
    /// serialized JSON form of <see cref="SemanticIdentityPart.Value"/>. Required because
    /// <see cref="JsonNode"/> uses reference equality, so record-default <c>Equals</c> on
    /// <c>SemanticIdentityPart</c> rejects two stored URIs that came from different
    /// <c>JsonValue</c> instances even when their content is identical.
    /// </summary>
    private static bool SemanticIdentityRawValuesMatch(
        ImmutableArray<SemanticIdentityPart> left,
        ImmutableArray<SemanticIdentityPart> right
    )
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].IsPresent != right[i].IsPresent)
            {
                return false;
            }
            if (!string.Equals(left[i].RelativePath, right[i].RelativePath, StringComparison.Ordinal))
            {
                return false;
            }
            var leftJson = left[i].Value?.ToJsonString();
            var rightJson = right[i].Value?.ToJsonString();
            if (!string.Equals(leftJson, rightJson, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the zero-based positions within the table's
    /// <see cref="DbTableIdentityMetadata.SemanticIdentityBindings"/> that require
    /// URI-to-Int64 canonicalization (i.e. those backed by a <see cref="ColumnKind.DescriptorFk"/>
    /// column). Returns an empty list when no descriptor-backed parts exist.
    /// </summary>
    internal static IReadOnlyList<int> ResolveDescriptorIdentityIndices(TableWritePlan tablePlan)
    {
        var bindings = tablePlan.TableModel.IdentityMetadata.SemanticIdentityBindings;

        if (bindings.Count == 0)
        {
            return [];
        }

        List<int>? result = null;

        for (var i = 0; i < bindings.Count; i++)
        {
            var column = tablePlan.TableModel.Columns.FirstOrDefault(c =>
                c.ColumnName.Equals(bindings[i].ColumnName)
            );

            if (column?.Kind == ColumnKind.DescriptorFk)
            {
                if (result is null)
                {
                    result = [];
                }

                result.Add(i);
            }
        }

        return result ?? (IReadOnlyList<int>)[];
    }

    /// <summary>
    /// For each <see cref="VisibleRequestCollectionItem"/> in
    /// <paramref name="requestItems"/>, replaces the <see cref="SemanticIdentityPart"/>
    /// values at every descriptor-identity index with the resolved Int64 descriptor id.
    /// Items whose descriptor lookup returns null are left unchanged (the planner's
    /// invariant check will surface the mismatch as a fail-closed error).
    /// </summary>
    internal static ImmutableArray<VisibleRequestCollectionItem> CanonicalizeDescriptorRequestItems(
        ImmutableArray<VisibleRequestCollectionItem> requestItems,
        TableWritePlan tablePlan,
        IReadOnlyList<int> descriptorIdentityIndices,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (requestItems.IsDefaultOrEmpty)
        {
            return requestItems;
        }

        var bindings = tablePlan.TableModel.IdentityMetadata.SemanticIdentityBindings;
        var builder = ImmutableArray.CreateBuilder<VisibleRequestCollectionItem>(requestItems.Length);

        foreach (var item in requestItems)
        {
            var identity = item.Address.SemanticIdentityInOrder;
            var ordinalPath = RelationalJsonPathSupport
                .ParseConcretePath(new JsonPath(item.RequestJsonPath))
                .OrdinalPath;
            var canonicalized = CanonicalizeIdentityParts(
                identity,
                bindings,
                descriptorIdentityIndices,
                tablePlan,
                ordinalPath,
                resolvedReferenceLookups
            );

            builder.Add(
                item with
                {
                    Address = item.Address with { SemanticIdentityInOrder = canonicalized },
                }
            );
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// For each <see cref="VisibleStoredCollectionRow"/> in <paramref name="storedRows"/>,
    /// replaces the <see cref="SemanticIdentityPart"/> values at every descriptor-identity
    /// index with the resolved Int64 descriptor id, looked up by URI from the request-cycle
    /// cache. When a stored URI is not in the cache (e.g. delete-by-absence: the row was
    /// omitted from the request body), falls back to extracting the Int64 from the matching
    /// <see cref="CurrentCollectionRowSnapshot"/> — see <see cref="CanonicalizeStoredIdentityParts"/>
    /// for the fallback strategy and its failure conditions.
    /// </summary>
    internal static ImmutableArray<VisibleStoredCollectionRow> CanonicalizeDescriptorStoredRows(
        ImmutableArray<VisibleStoredCollectionRow> storedRows,
        TableWritePlan tablePlan,
        IReadOnlyList<int> descriptorIdentityIndices,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows
    )
    {
        if (storedRows.IsDefaultOrEmpty)
        {
            return storedRows;
        }

        var bindings = tablePlan.TableModel.IdentityMetadata.SemanticIdentityBindings;
        var builder = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>(storedRows.Length);

        for (var storedRowIndex = 0; storedRowIndex < storedRows.Length; storedRowIndex++)
        {
            var row = storedRows[storedRowIndex];
            var identity = row.Address.SemanticIdentityInOrder;
            var canonicalized = CanonicalizeStoredIdentityParts(
                identity,
                bindings,
                descriptorIdentityIndices,
                tablePlan,
                resolvedReferenceLookups,
                currentRows,
                storedRowIndex,
                storedRows.Length
            );

            builder.Add(row with { Address = row.Address with { SemanticIdentityInOrder = canonicalized } });
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Rewrites the descriptor-backed parts of <paramref name="identity"/> in-place for
    /// request-side items. Positions not in <paramref name="descriptorIdentityIndices"/>
    /// are copied unchanged. Returns the same array reference when no part changes.
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> CanonicalizeIdentityParts(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<CollectionSemanticIdentityBinding> bindings,
        IReadOnlyList<int> descriptorIdentityIndices,
        TableWritePlan tablePlan,
        ReadOnlySpan<int> ordinalPath,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        ImmutableArray<SemanticIdentityPart>.Builder? builder = null;

        foreach (var idx in descriptorIdentityIndices)
        {
            if (idx >= identity.Length || idx >= bindings.Count)
            {
                continue;
            }

            var part = identity[idx];

            if (!part.IsPresent || part.Value is null)
            {
                continue;
            }

            var binding = bindings[idx];
            var column = tablePlan.TableModel.Columns.FirstOrDefault(c =>
                c.ColumnName.Equals(binding.ColumnName)
            );

            if (column?.TargetResource is null || column.SourceJsonPath is null)
            {
                continue;
            }

            var wildcardPath = column.SourceJsonPath.Value.Canonical;
            var descriptorId = resolvedReferenceLookups.GetDescriptorId(
                column.TargetResource.Value,
                wildcardPath,
                ordinalPath
            );

            if (descriptorId is null)
            {
                continue;
            }

            builder ??= identity.ToBuilder();
            builder[idx] = new SemanticIdentityPart(
                part.RelativePath,
                JsonValue.Create(descriptorId.Value),
                IsPresent: true
            );
        }

        return builder is null ? identity : builder.MoveToImmutable();
    }

    /// <summary>
    /// Rewrites the descriptor-backed parts of <paramref name="identity"/> for stored-side
    /// rows by looking up URI → Int64 from the request-cycle cache.
    ///
    /// <para><b>Fail-closed boundary:</b>
    /// Descriptor-backed top-level collection identity is supported for the common cases
    /// this method handles correctly (URI in cache; mixed scalar+descriptor identity;
    /// descriptor-only without hidden rows). The structurally ambiguous case — hidden rows
    /// interleaved in current rows plus a URI cache miss for a stored row whose scalar match
    /// is ambiguous or absent — fails closed at runtime. That combination is rare in standard
    /// Ed-Fi profiles but must not silently return an incorrect descriptor id.</para>
    ///
    /// <para><b>Cache-miss fallback (delete-by-absence support):</b>
    /// When a stored URI is not in the request-cycle cache — which happens during a PUT that
    /// omits a previously-stored collection item, because the omitted item's descriptor URI
    /// was never resolved as part of the current request body — the method falls back to
    /// extracting the Int64 descriptor id directly from the matching
    /// <see cref="CurrentCollectionRowSnapshot"/>.</para>
    ///
    /// <para><b>Matching strategy:</b>
    /// <list type="number">
    ///   <item>Scalar-part match (when identity has scalar parts): find the unique current
    ///   row whose semantic identity matches all scalar parts and copy its descriptor id at
    ///   the same index position. If multiple current rows share the same scalar values
    ///   (duplicate-scalar/different-descriptor shape) or none match, fall through to
    ///   positional matching.</item>
    ///   <item>Positional match: when
    ///   <c>currentRows.Length == storedRows.Length</c> (no hidden rows in scope), use
    ///   <c>currentRows[storedRowIndex]</c> directly. Both arrays are ordered by
    ///   stored-ordinal (planner invariants <c>ValidateStoredOrdinalOrder</c> +
    ///   walker projection-index ordering), and count equality is equivalent to "no
    ///   hidden rows interleaved", so positional correspondence holds for both
    ///   descriptor-only and mixed scalar+descriptor identity.</item>
    ///   <item>If counts differ (hidden rows are interleaved — structurally possible but not
    ///   expected in practice): throw with a diagnostic message rather than silently
    ///   producing an incorrect result.</item>
    /// </list></para>
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> CanonicalizeStoredIdentityParts(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<CollectionSemanticIdentityBinding> bindings,
        IReadOnlyList<int> descriptorIdentityIndices,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows,
        int storedRowIndex,
        int storedRowsLength
    )
    {
        ImmutableArray<SemanticIdentityPart>.Builder? builder = null;

        foreach (var idx in descriptorIdentityIndices)
        {
            if (idx >= identity.Length || idx >= bindings.Count)
            {
                continue;
            }

            var part = identity[idx];

            if (!part.IsPresent || part.Value is null)
            {
                continue;
            }

            // Check if the value is already an Int64 (already canonicalized or numeric).
            if (part.Value is JsonValue jv && jv.TryGetValue<long>(out _))
            {
                continue;
            }

            var uri = part.Value.ToString();

            if (string.IsNullOrEmpty(uri))
            {
                continue;
            }

            var binding = bindings[idx];
            var column = tablePlan.TableModel.Columns.FirstOrDefault(c =>
                c.ColumnName.Equals(binding.ColumnName)
            );

            if (column?.TargetResource is null || column.SourceJsonPath is null)
            {
                continue;
            }

            var wildcardPath = column.SourceJsonPath.Value.Canonical;

            if (
                resolvedReferenceLookups.TryGetDescriptorIdByUri(
                    column.TargetResource.Value,
                    uri,
                    out var descriptorId
                )
            )
            {
                // Cache hit: use the request-cycle resolved id.
                builder ??= identity.ToBuilder();
                builder[idx] = new SemanticIdentityPart(
                    part.RelativePath,
                    JsonValue.Create(descriptorId),
                    IsPresent: true
                );
                continue;
            }

            // Cache miss: the stored row references a descriptor URI that was not resolved
            // as part of the current request (typical in delete-by-absence). Fall back to
            // extracting the Int64 id from the matching CurrentCollectionRowSnapshot.
            var fallbackId = TryResolveDescriptorIdFromCurrentRows(
                identity,
                descriptorIdentityIndices,
                idx,
                currentRows,
                storedRowIndex,
                storedRowsLength
            );

            if (fallbackId is null)
            {
                throw new InvalidOperationException(
                    $"descriptor URI not resolvable at merge boundary: stored descriptor URI '{uri}' "
                        + $"for column '{column.ColumnName.Value}' (path '{wildcardPath}') "
                        + "was not found in the request-cycle descriptor resolution cache and could not "
                        + "be matched against current rows. "
                        + "This can happen when scalar matching is ambiguous or absent (or the identity is "
                        + "descriptor-only), the stored rows contain hidden rows interleaved with visible "
                        + "rows (current row count differs from stored row count), and the cache does not "
                        + "hold the URI. "
                        + $"Current rows count: {currentRows.Length}, stored rows count: {storedRowsLength}, stored row index: {storedRowIndex}."
                );
            }

            builder ??= identity.ToBuilder();
            builder[idx] = new SemanticIdentityPart(
                part.RelativePath,
                JsonValue.Create(fallbackId.Value),
                IsPresent: true
            );
        }

        return builder is null ? identity : builder.MoveToImmutable();
    }

    /// <summary>
    /// Attempts to resolve a descriptor Int64 id for <paramref name="descriptorIdx"/> in
    /// <paramref name="identity"/> by matching against <paramref name="currentRows"/>.
    ///
    /// <para>Two strategies are tried in order, regardless of identity shape:</para>
    /// <list type="number">
    ///   <item>Scalar-part matching: if the identity has non-descriptor parts at positions
    ///   not in <paramref name="descriptorIndices"/>, find the unique current row whose
    ///   semantic identity matches all scalar parts and copy its descriptor part at the same
    ///   index position. If no scalar parts exist, this strategy is skipped. If multiple
    ///   current rows share the same scalar values (duplicate scalar parts that differ only
    ///   on the descriptor part) or no scalar match is found, fall through to Strategy 2.</item>
    ///   <item>Positional matching: if
    ///   <paramref name="currentRows"/><c>.Length == </c><paramref name="storedRowsLength"/>
    ///   (all stored rows are covered by current rows one-to-one — equivalent to "no hidden
    ///   rows in scope"), use <c>currentRows[storedRowIndex]</c> directly. When the counts
    ///   differ, hidden rows interleave with visible rows and positional correspondence does
    ///   not hold, so <c>null</c> is returned and the caller throws rather than silently
    ///   picking the wrong row.</item>
    /// </list>
    /// <para>Returns <c>null</c> when neither strategy produces a result and the caller
    /// should throw.</para>
    /// </summary>
    private static long? TryResolveDescriptorIdFromCurrentRows(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<int> descriptorIndices,
        int descriptorIdx,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows,
        int storedRowIndex,
        int storedRowsLength
    )
    {
        if (currentRows.IsDefault || currentRows.Length == 0)
        {
            return null;
        }

        // Build the set of non-descriptor index positions (scalar parts).
        var scalarIndices = Enumerable
            .Range(0, identity.Length)
            .Where(i => !descriptorIndices.Contains(i))
            .ToList();

        // Strategy 1: match by scalar parts (skipped when identity is descriptor-only).
        // Find the unique current row whose semantic identity matches all scalar parts at
        // the same index positions as the stored row's scalar parts. If the match is
        // ambiguous (two rows share the same scalar values but differ on the descriptor
        // part) or no row matches, fall through to Strategy 2 — count-equal positional
        // correspondence safely disambiguates duplicate-scalar/different-descriptor rows
        // when no hidden rows are interleaved.
        if (scalarIndices.Count > 0)
        {
            var scalarMatchId = TryResolveByScalarMatch(identity, scalarIndices, descriptorIdx, currentRows);

            if (scalarMatchId is not null)
            {
                return scalarMatchId;
            }
        }

        // Strategy 2: positional matching.
        //
        // This branch is an inference under an upstream ordering contract, not a validation
        // mechanism. It assumes BOTH:
        //   1. Core emits VisibleStoredCollectionRows in stored-body iteration order
        //      (StoredSideExistenceLookupBuilder.WalkCollection iterates the stored JSON
        //      array in index order), AND
        //   2. The stored JSON body's array order matches the DB's stored-ordinal column
        //      order for this collection.
        // Both hold under the current Core/projection contract; together they imply that
        // VisibleStoredRows[storedRowIndex] corresponds to currentRows[storedRowIndex] when
        // currentRows is sorted by StoredOrdinal (see the walker's projection index). This
        // method does NOT independently verify the correspondence — a structural check
        // would require resolving each current row's Int64 descriptor id back to its URI
        // (a DB roundtrip against the descriptor projection). If the upstream contract is
        // broken (body out of ordinal order, or VisibleStoredRows mis-ordered), positional
        // rewrite would silently swap descriptor ids before the planner runs and downstream
        // invariants would pass against the rewritten values. This residual risk is fenced
        // behind the count-equality guard.
        //
        // Safe ONLY when currentRows.Length == storedRowsLength. Count equality is a
        // necessary (not sufficient) condition for one-to-one ordinal correspondence and is
        // also equivalent here to "no hidden rows in scope" — currentRows holds all current
        // DB rows, storedRowsLength counts visible stored rows, and equality between them
        // means no row is hidden from the visible set. This makes positional fallback safe
        // for both descriptor-only identity and mixed scalar+descriptor identity (including
        // the duplicate-scalar/different-descriptor shape that Strategy 1 cannot
        // disambiguate). When counts differ (a profile Filter has restricted the visible
        // stored rows while the DB still holds the full set in currentRows), positional
        // indexing would pick the wrong row — return null so the caller throws with a
        // diagnostic instead of silently corrupting data.
        if (currentRows.Length != storedRowsLength)
        {
            return null;
        }

        if (storedRowIndex < currentRows.Length)
        {
            var positionalRow = currentRows[storedRowIndex];
            var positionalIdentity = positionalRow.SemanticIdentityInOrder;
            if (descriptorIdx < positionalIdentity.Length)
            {
                var positionalPart = positionalIdentity[descriptorIdx];
                if (
                    positionalPart.IsPresent
                    && positionalPart.Value is JsonValue positionalJv
                    && positionalJv.TryGetValue<long>(out var positionalId)
                )
                {
                    return positionalId;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Strategy 1 of <see cref="TryResolveDescriptorIdFromCurrentRows"/>: locate a unique
    /// current row whose semantic identity matches <paramref name="identity"/> on every
    /// scalar (non-descriptor) part, then return that row's descriptor id at
    /// <paramref name="descriptorIdx"/>.
    ///
    /// <para>Returns <c>null</c> when no current row matches or when more than one current
    /// row shares the same scalar values (the ambiguous-scalar case where rows differ only
    /// on the descriptor part). Caller falls through to positional matching in both
    /// cases.</para>
    /// </summary>
    private static long? TryResolveByScalarMatch(
        ImmutableArray<SemanticIdentityPart> identity,
        IReadOnlyList<int> scalarIndices,
        int descriptorIdx,
        ImmutableArray<CurrentCollectionRowSnapshot> currentRows
    )
    {
        CurrentCollectionRowSnapshot? matched = null;
        foreach (var currentRow in currentRows)
        {
            var currentIdentity = currentRow.SemanticIdentityInOrder;
            if (currentIdentity.Length != identity.Length)
            {
                continue;
            }

            var allScalarsMatch = true;
            foreach (var scalarIdx in scalarIndices)
            {
                var storedPart = identity[scalarIdx];
                var currentPart = currentIdentity[scalarIdx];

                // Both must be present and have the same value.
                if (!storedPart.IsPresent || !currentPart.IsPresent)
                {
                    allScalarsMatch = false;
                    break;
                }

                var storedJson = storedPart.Value?.ToJsonString();
                var currentJson = currentPart.Value?.ToJsonString();
                if (!string.Equals(storedJson, currentJson, StringComparison.Ordinal))
                {
                    allScalarsMatch = false;
                    break;
                }
            }

            if (!allScalarsMatch)
            {
                continue;
            }

            if (matched is not null)
            {
                // Ambiguous match — more than one current row has the same scalar parts.
                // Cannot safely choose one here; let the caller fall through to positional
                // matching, which can disambiguate via stored-ordinal correspondence when
                // counts are equal.
                return null;
            }

            matched = currentRow;
        }

        if (matched is null)
        {
            return null;
        }

        // Extract the descriptor id at the same index position from the matched current row.
        var matchedIdentity = matched.SemanticIdentityInOrder;
        if (descriptorIdx >= matchedIdentity.Length)
        {
            return null;
        }

        var matchedPart = matchedIdentity[descriptorIdx];
        if (!matchedPart.IsPresent || matchedPart.Value is null)
        {
            return null;
        }

        if (matchedPart.Value is JsonValue matchedJv && matchedJv.TryGetValue<long>(out var matchedId))
        {
            return matchedId;
        }

        return null;
    }

    /// <summary>
    /// Resolves a concrete JSON array item node from <paramref name="requestBody"/> using a
    /// path like <c>$.classPeriods[0]</c>. Delegates to
    /// <see cref="RelationalJsonPathSupport.ParseConcretePath"/> +
    /// <see cref="RelationalWriteFlattener.TryNavigateConcreteNode"/> so concrete-path
    /// semantics live in one place. Returns <c>null</c> for malformed paths or paths that
    /// do not resolve to a node, matching the previous local-walker behavior.
    /// </summary>
    internal static JsonNode? ResolveCollectionItemNode(JsonNode requestBody, string requestJsonPath)
    {
        try
        {
            var parsed = RelationalJsonPathSupport.ParseConcretePath(new JsonPath(requestJsonPath));
            var segments = RelationalJsonPathSupport.GetRestrictedSegments(
                new JsonPathExpression(parsed.WildcardPath, [])
            );
            return RelationalWriteFlattener.TryNavigateConcreteNode(
                requestBody,
                segments,
                parsed.OrdinalPath.AsSpan(),
                out var resolved
            )
                ? resolved
                : null;
        }
        catch (InvalidOperationException)
        {
            // ParseConcretePath / GetRestrictedSegments throws for malformed input; preserve
            // the previous null-on-not-resolved contract.
            return null;
        }
    }

    private static IReadOnlyList<object?[]>? TryFindHydratedRowsForTable(
        RelationalWriteCurrentState? currentState,
        TableWritePlan tablePlan
    ) =>
        currentState
            ?.TableRowsInDependencyOrder.FirstOrDefault(hydrated =>
                hydrated.TableModel.Table.Equals(tablePlan.TableModel.Table)
            )
            ?.Rows;

    private static CurrentSeparateScopeRowProjection? BuildCurrentSeparateScopeRowProjection(
        TableWritePlan tablePlan,
        IReadOnlyList<object?[]>? hydratedRows,
        ImmutableArray<FlattenedWriteValue> parentPhysicalIdentityValues
    )
    {
        if (hydratedRows is null || hydratedRows.Count == 0)
        {
            return null;
        }

        if (hydratedRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has "
                    + $"{hydratedRows.Count} current rows for profiled separate-scope merge; expected zero or one."
            );
        }

        var projectedCurrentRow = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydratedRows)[0];
        var currentRowByColumnName = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
            tablePlan.TableModel,
            hydratedRows[0]
        );

        return new CurrentSeparateScopeRowProjection(
            projectedCurrentRow,
            currentRowByColumnName,
            parentPhysicalIdentityValues
        );
    }
}
