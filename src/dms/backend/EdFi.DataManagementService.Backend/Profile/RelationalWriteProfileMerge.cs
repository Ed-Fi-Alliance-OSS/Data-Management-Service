// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Input contract for the profile merge synthesizer. Slice 3 supports the root-table
/// scope and any root-attached separate-table non-collection
/// (<see cref="DbTableKind.RootExtension"/>) scopes. Collection candidates remain
/// fenced out in slice 3 at BOTH levels: top-level <see cref="RootWriteRowBuffer.CollectionCandidates"/>
/// and nested <see cref="RootExtensionWriteRowBuffer.CollectionCandidates"/> under any
/// root-extension row. Any non-<see cref="DbTableKind.RootExtension"/> buffer kind under
/// <see cref="RootWriteRowBuffer.RootExtensionRows"/> is rejected fail-closed; additional
/// table plans carrying collection or deeper scopes belong to later slices.
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
        if (!FlattenedWriteSet.RootRow.CollectionCandidates.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Slice 3 profile merge fences root-level collection candidates; they must be "
                    + "addressed in a later slice.",
                nameof(flattenedWriteSet)
            );
        }
        foreach (var extensionRow in FlattenedWriteSet.RootRow.RootExtensionRows)
        {
            if (
                extensionRow.TableWritePlan.TableModel.IdentityMetadata.TableKind
                is not DbTableKind.RootExtension
            )
            {
                throw new ArgumentException(
                    "Slice 3 profile merge requires every root-extension row to use a "
                        + $"{nameof(DbTableKind.RootExtension)} table plan; got "
                        + $"'{extensionRow.TableWritePlan.TableModel.IdentityMetadata.TableKind}' "
                        + $"for table '{ProfileBindingClassificationCore.FormatTable(extensionRow.TableWritePlan)}'.",
                    nameof(flattenedWriteSet)
                );
            }
            if (!extensionRow.CollectionCandidates.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    "Slice 3 profile merge fences collection candidates nested under root-extension "
                        + "rows; they must be addressed in a later slice.",
                    nameof(flattenedWriteSet)
                );
            }
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
/// composing their own classifier/resolver plus the separate-table decider. Slice 3 does not
/// support guarded no-op; <see cref="RelationalWriteMergeResult.SupportsGuardedNoOp"/> is
/// always <c>false</c>. Returns a <see cref="ProfileMergeOutcome"/> discriminated union so the
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

        var tableStates = new List<RelationalWriteMergedTableState>();

        // Build the resolved-reference lookups once and share them with both the root-table
        // synthesis and each separate-table synthesis so we avoid redundant construction.
        var resolvedReferenceLookups = FlatteningResolvedReferenceLookupSet.Create(
            request.WritePlan,
            request.ResolvedReferences
        );

        // 1. Root-table merge.
        tableStates.Add(SynthesizeRootTable(request, resolvedReferenceLookups));

        // 2. Per-separate-table merge for every non-root table in dependency order. Plans may
        //    legitimately carry non-RootExtension tables that the executor's slice-fence has
        //    fenced out of the request path; those are silently skipped here so the
        //    no-profile persister can handle their rows unchanged.

        for (
            var tableIndex = 1;
            tableIndex < request.WritePlan.TablePlansInDependencyOrder.Length;
            tableIndex++
        )
        {
            var tablePlan = request.WritePlan.TablePlansInDependencyOrder[tableIndex];
            if (tablePlan.TableModel.IdentityMetadata.TableKind is not DbTableKind.RootExtension)
            {
                // Slice 3 handles only root-attached RootExtension tables. Plans may
                // carry unused Collection / ExtensionCollection / CollectionExtensionScope
                // tables (e.g., a multi-table School plan where the profiled request touches
                // only root scopes). The executor's slice-fence ensures the request itself
                // does not exercise those scopes; the synthesizer silently leaves them
                // untouched so their rows flow through the no-profile persister path
                // unchanged (matching slice 2's multi-table-but-root-only-runtime behavior).
                continue;
            }

            var separateTableResult = SynthesizeSeparateTable(request, tablePlan, resolvedReferenceLookups);

            if (separateTableResult.Rejection is not null)
            {
                return ProfileMergeOutcome.Reject(separateTableResult.Rejection);
            }

            if (separateTableResult.TableState is not null)
            {
                tableStates.Add(separateTableResult.TableState);
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

    private SeparateTableSynthesisResult SynthesizeSeparateTable(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        var scope = tablePlan.TableModel.JsonScope.Canonical;
        var requestScope = ProfileMemberGovernanceRules.LookupRequestScope(request.ProfileRequest, scope);
        var storedScope = request.ProfileAppliedContext is null
            ? null
            : ProfileMemberGovernanceRules.LookupStoredScope(request.ProfileAppliedContext, scope);

        var hydratedRows = TryFindHydratedRowsForTable(request.CurrentState, tablePlan);
        var storedRowExists = hydratedRows is { Count: > 0 };
        var bufferExists = request.FlattenedWriteSet.RootRow.RootExtensionRows.Any(row =>
            ReferenceEquals(row.TableWritePlan, tablePlan)
        );

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

        // Express the Slice 3 decision matrix's "actionable" conditions directly so genuine
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
            return SeparateTableSynthesisResult.Skipped;
        }

        var outcome = _separateTableDecider.Decide(scope, requestScope, storedScope, storedRowExists);

        return outcome switch
        {
            ProfileSeparateTableMergeOutcome.Insert => SeparateTableSynthesisResult.Table(
                BuildInsertState(request, tablePlan)
            ),
            ProfileSeparateTableMergeOutcome.Update => SeparateTableSynthesisResult.Table(
                BuildUpdateState(request, tablePlan, hydratedRows!, resolvedReferenceLookups)
            ),
            ProfileSeparateTableMergeOutcome.Delete => SeparateTableSynthesisResult.Table(
                BuildDeleteState(tablePlan, hydratedRows!)
            ),
            ProfileSeparateTableMergeOutcome.Preserve => SeparateTableSynthesisResult.Table(
                BuildPreserveState(tablePlan, hydratedRows!)
            ),
            ProfileSeparateTableMergeOutcome.RejectCreateDenied => SeparateTableSynthesisResult.Reject(
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
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan
    )
    {
        var extensionRow = LocateRootExtensionRow(request, tablePlan);
        var mergedValues = extensionRow.Values.ToArray();
        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(tablePlan, mergedValues);
        var mergedRow = new RelationalWriteMergedTableRow(mergedValues, comparableValues);
        return new RelationalWriteMergedTableState(tablePlan, currentRows: [], mergedRows: [mergedRow]);
    }

    private RelationalWriteMergedTableState BuildUpdateState(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan,
        IReadOnlyList<object?[]> hydratedRows,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups
    )
    {
        if (hydratedRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has "
                    + $"{hydratedRows.Count} current rows for profiled Update; expected exactly one."
            );
        }

        var projectedCurrentRow = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydratedRows)[0];
        var currentRowByColumnName = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
            tablePlan.TableModel,
            hydratedRows[0]
        );

        var classification = _separateTableClassifier.Classify(
            request.WritePlan,
            tablePlan,
            request.ProfileRequest,
            request.ProfileAppliedContext
        );

        var extensionRow = LocateRootExtensionRow(request, tablePlan);
        var mergedValues = OverlayByDisposition(
            tablePlan,
            extensionRow.Values,
            projectedCurrentRow,
            classification.BindingsByIndex,
            classification.ResolverOwnedBindingIndices
        );

        // Separate-table key-unification member paths are production-compiled
        // scope-relative (e.g. "$.memberA" under scope "$._ext.sample"), so the
        // resolver must evaluate them against the table-scoped sub-node of the
        // request body, not the root body.
        var scopedRequestNode = ResolveScopedRequestNode(request.WritableRequestBody, tablePlan);

        var resolverContext = new ProfileSeparateTableKeyUnificationContext(
            WritableRequestBody: scopedRequestNode,
            CurrentState: request.CurrentState,
            CurrentRowByColumnName: currentRowByColumnName,
            ResolvedReferenceLookups: resolvedReferenceLookups,
            ProfileRequest: request.ProfileRequest,
            ProfileAppliedContext: request.ProfileAppliedContext
        );

        _separateTableResolver.Resolve(
            tablePlan,
            resolverContext,
            mergedValues,
            classification.ResolverOwnedBindingIndices
        );

        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(tablePlan, mergedValues);
        var mergedRow = new RelationalWriteMergedTableRow(mergedValues, comparableValues);
        return new RelationalWriteMergedTableState(tablePlan, [projectedCurrentRow], [mergedRow]);
    }

    private static RelationalWriteMergedTableState BuildDeleteState(
        TableWritePlan tablePlan,
        IReadOnlyList<object?[]> hydratedRows
    )
    {
        if (hydratedRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has "
                    + $"{hydratedRows.Count} current rows for profiled Delete; expected exactly one."
            );
        }
        var projectedCurrentRow = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydratedRows)[0];
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
        IReadOnlyList<object?[]> hydratedRows
    )
    {
        if (hydratedRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Separate table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' has "
                    + $"{hydratedRows.Count} current rows for profiled Preserve; expected exactly one."
            );
        }
        var projectedCurrentRow = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydratedRows)[0];
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

    private static RootExtensionWriteRowBuffer LocateRootExtensionRow(
        RelationalWriteProfileMergeRequest request,
        TableWritePlan tablePlan
    )
    {
        var match = request.FlattenedWriteSet.RootRow.RootExtensionRows.FirstOrDefault(row =>
            ReferenceEquals(row.TableWritePlan, tablePlan)
        );
        return match
            ?? throw new InvalidOperationException(
                $"Flattened write set does not carry a root-extension row for table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' in scope "
                    + $"'{tablePlan.TableModel.JsonScope.Canonical}'. The decider selected Insert/Update "
                    + "but the flattener produced no matching buffer — upstream contract violation."
            );
    }

    /// <summary>
    /// Navigate the root writable request body down to the separate-table plan's JSON
    /// scope so key-unification member paths — which are scope-relative in production —
    /// evaluate against the right node. The decider only routes Update when the request
    /// view classifies the scope as VisiblePresent, so the scope is always expected to
    /// exist in the body; a missing scope is treated as an upstream contract violation.
    /// </summary>
    private static JsonNode ResolveScopedRequestNode(JsonNode rootBody, TableWritePlan tablePlan)
    {
        if (
            !RelationalWriteFlattener.TryGetRelativeLeafNode(
                rootBody,
                tablePlan.TableModel.JsonScope,
                out var scopeNode
            ) || scopeNode is null
        )
        {
            throw new InvalidOperationException(
                $"Separate-table Update path could not navigate the request body to scope "
                    + $"'{tablePlan.TableModel.JsonScope.Canonical}' on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}'. "
                    + "The decider selected Update but the request body does not contain the "
                    + "scope — upstream contract violation between the projected request and "
                    + "the decider."
            );
        }
        return scopeNode;
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

    private readonly record struct SeparateTableSynthesisResult(
        RelationalWriteMergedTableState? TableState,
        ProfileCreatabilityRejection? Rejection
    )
    {
        public static SeparateTableSynthesisResult Skipped => new(null, null);

        public static SeparateTableSynthesisResult Table(RelationalWriteMergedTableState state) =>
            new(state, null);

        public static SeparateTableSynthesisResult Reject(ProfileCreatabilityRejection rejection) =>
            new(null, rejection);
    }
}
