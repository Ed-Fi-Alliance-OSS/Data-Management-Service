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
/// Request contract for the profile merge synthesizer. Slice 4 narrows the earlier Slice 3
/// rejection of root-level <see cref="CollectionWriteCandidate"/>s: root-attached base
/// collection candidates (<see cref="DbTableKind.Collection"/>) are now accepted when they
/// carry neither nested <c>CollectionCandidates</c> nor <c>AttachedAlignedScopeData</c>.
/// Nested candidates, attached-aligned scope data, collection candidates under
/// <see cref="RootExtensionWriteRowBuffer"/>, and non-Collection root table kinds remain
/// fenced — they land in Slice 5 or are structurally invalid.
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
        foreach (var candidate in FlattenedWriteSet.RootRow.CollectionCandidates)
        {
            if (!candidate.CollectionCandidates.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    "Slice 4 profile merge does not yet support nested CollectionCandidates under a "
                        + "top-level collection candidate; they will land in Slice 5.",
                    nameof(flattenedWriteSet)
                );
            }
            if (!candidate.AttachedAlignedScopeData.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    "Slice 4 profile merge does not yet support AttachedAlignedScopeData on a "
                        + "top-level collection candidate; they will land in Slice 5.",
                    nameof(flattenedWriteSet)
                );
            }
            if (candidate.TableWritePlan.TableModel.IdentityMetadata.TableKind is not DbTableKind.Collection)
            {
                throw new ArgumentException(
                    "Slice 4 profile merge top-level collection candidates must carry "
                        + "DbTableKind.Collection (root-attached base collection). Other root-candidate table kinds are fenced and must be addressed in a later slice.",
                    nameof(flattenedWriteSet)
                );
            }
            // Root-attached base collection candidate → passes the gate.
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
        var rootTableState = SynthesizeRootTable(request, resolvedReferenceLookups);
        tableStates.Add(rootTableState);

        // Root physical row identity values: the merged root row's values, used as the
        // parent-key-part source for any top-level collection candidates.
        IReadOnlyList<FlattenedWriteValue> rootPhysicalRowIdentityValues =
            rootTableState.MergedRows.Length > 0
                ? rootTableState.MergedRows[0].Values
                : request.FlattenedWriteSet.RootRow.Values;

        // 1a. Top-level collection merge — runs unconditionally so that stored-only
        //     "delete-all-visible" scenarios (Blocker #2 fix: spec Section 7.7) are driven
        //     from the union of request-side, stored-side, and DB-side sources rather than
        //     only when request-side candidates are present.
        var collectionOutcome = SynthesizeTopLevelCollectionScopes(
            request,
            rootPhysicalRowIdentityValues,
            resolvedReferenceLookups,
            tableStates
        );
        if (collectionOutcome is not null)
        {
            return collectionOutcome.Value;
        }

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

    /// <summary>
    /// Synthesizes merged collection rows for all top-level collection candidates attached
    /// to the root row. Called once per synthesizer pass after the separate-table loop
    /// (spec Section 4.2 / 4.3). Returns a non-null <see cref="ProfileMergeOutcome.Reject"/>
    /// on a creatability-denied item; appends collection table states to
    /// <paramref name="tableStates"/> on success and returns <c>null</c>.
    /// </summary>
    private static ProfileMergeOutcome? SynthesizeTopLevelCollectionScopes(
        RelationalWriteProfileMergeRequest request,
        IReadOnlyList<FlattenedWriteValue> rootPhysicalRowIdentityValues,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        List<RelationalWriteMergedTableState> tableStates
    )
    {
        var topLevelCollectionCandidates = request.FlattenedWriteSet.RootRow.CollectionCandidates;
        var profileRequest = request.ProfileRequest;
        var profileAppliedContext = request.ProfileAppliedContext;
        var currentState = request.CurrentState;
        var writableRequestBody = request.WritableRequestBody;
        var resourceWritePlan = request.WritePlan;

        // Group candidates by table (JsonScope) so we process each scope once.
        var candidatesByScope = topLevelCollectionCandidates
            .GroupBy(c => c.TableWritePlan.TableModel.JsonScope.Canonical)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build a scope topology index so we can identify root-attached
        // (TopLevelBaseCollection) tables without relying solely on request-side
        // candidates. This drives Blocker #2 fix: stored-only delete-all-visible scopes
        // are visited even when CollectionCandidates is empty for that scope.
        var scopeTopology = ScopeTopologyIndex.BuildFromWritePlan(resourceWritePlan);

        // Iterate in TablePlansInDependencyOrder so first-rejection-wins is deterministic across runs.
        foreach (var tablePlan in resourceWritePlan.TablePlansInDependencyOrder)
        {
            var jsonScope = tablePlan.TableModel.JsonScope.Canonical;

            // Only process root-attached base collection tables (Blocker #2: iterate from
            // topology, not solely from request-side candidates).
            if (scopeTopology.GetTopology(jsonScope) is not ScopeTopologyKind.TopLevelBaseCollection)
            {
                continue;
            }

            candidatesByScope.TryGetValue(jsonScope, out var candidates);
            var requestCandidatesForScope = candidates is null
                ? ImmutableArray<CollectionWriteCandidate>.Empty
                : candidates.ToImmutableArray();

            var visibleRequestItemsForScope = profileRequest
                .VisibleRequestCollectionItems.Where(i => i.Address.JsonScope == jsonScope)
                .ToImmutableArray();

            var visibleStoredRowsForScope = profileAppliedContext is null
                ? ImmutableArray<VisibleStoredCollectionRow>.Empty
                : profileAppliedContext
                    .VisibleStoredCollectionRows.Where(r => r.Address.JsonScope == jsonScope)
                    .ToImmutableArray();

            var currentRowsForScope = currentState is null
                ? ImmutableArray<CurrentCollectionRowSnapshot>.Empty
                : ProjectCurrentRowsForScope(currentState, tablePlan, rootPhysicalRowIdentityValues);

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

            // Defense-in-depth shape fence: nested candidates or attached aligned scope
            // data must not reach the emission site (constructor gate already rejects, but
            // this catches any path that might slip through).
            foreach (var candidate in requestCandidatesForScope)
            {
                if (!candidate.CollectionCandidates.IsDefaultOrEmpty)
                {
                    throw new InvalidOperationException(
                        $"Top-level collection candidate for scope '{jsonScope}' carries nested "
                            + "CollectionCandidates. Defense-in-depth emission fence triggered."
                    );
                }
                if (!candidate.AttachedAlignedScopeData.IsDefaultOrEmpty)
                {
                    throw new InvalidOperationException(
                        $"Top-level collection candidate for scope '{jsonScope}' carries "
                            + "AttachedAlignedScopeData. Defense-in-depth emission fence triggered."
                    );
                }
            }

            // 1. Build scope-local planner input from the unified source set.
            //    Reference- and descriptor-backed semantic identity parts arrive from Core
            //    as document natural-key values / descriptor URI strings, but the planner
            //    matches against backend-side Int64 ids. Canonicalize the Core-emitted
            //    streams before handing them to the planner.
            var parentScopeAddress = new ScopeInstanceAddress(
                "$",
                ImmutableArray<AncestorCollectionInstance>.Empty
            );

            var documentIdentityParts = ResolveDocumentReferenceIdentityParts(resourceWritePlan, tablePlan);
            var descriptorIdentityIndices = ResolveDescriptorIdentityIndices(tablePlan);

            var canonicalizedVisibleRequestItems = visibleRequestItemsForScope;
            if (documentIdentityParts.Count > 0)
            {
                canonicalizedVisibleRequestItems = CanonicalizeDocumentReferenceRequestItems(
                    canonicalizedVisibleRequestItems,
                    documentIdentityParts,
                    resolvedReferenceLookups
                );
            }
            if (descriptorIdentityIndices.Count > 0)
            {
                canonicalizedVisibleRequestItems = CanonicalizeDescriptorRequestItems(
                    canonicalizedVisibleRequestItems,
                    tablePlan,
                    descriptorIdentityIndices,
                    resolvedReferenceLookups
                );
            }

            var canonicalizedVisibleStoredRows = visibleStoredRowsForScope;
            if (documentIdentityParts.Count > 0)
            {
                canonicalizedVisibleStoredRows = CanonicalizeDocumentReferenceStoredRows(
                    canonicalizedVisibleStoredRows,
                    documentIdentityParts,
                    tablePlan,
                    resolvedReferenceLookups,
                    currentRowsForScope
                );
            }
            if (descriptorIdentityIndices.Count > 0)
            {
                canonicalizedVisibleStoredRows = CanonicalizeDescriptorStoredRows(
                    canonicalizedVisibleStoredRows,
                    tablePlan,
                    descriptorIdentityIndices,
                    resolvedReferenceLookups,
                    currentRowsForScope
                );
            }

            var input = new ProfileTopLevelCollectionScopeInput(
                JsonScope: jsonScope,
                ParentScopeAddress: parentScopeAddress,
                RequestCandidates: requestCandidatesForScope,
                VisibleRequestItems: canonicalizedVisibleRequestItems,
                VisibleStoredRows: canonicalizedVisibleStoredRows,
                CurrentRows: currentRowsForScope
            );

            // 2. Call the planner. Invariant violations throw and propagate as fail-closed.
            var planResult = ProfileTopLevelCollectionPlanner.Plan(input);

            // 3. Handle result.
            if (planResult is ProfileTopLevelCollectionPlanResult.CreatabilityRejection rejection)
            {
                return ProfileMergeOutcome.Reject(
                    new ProfileCreatabilityRejection(jsonScope, rejection.Reason)
                );
            }

            if (planResult is not ProfileTopLevelCollectionPlanResult.Success success)
            {
                throw new InvalidOperationException(
                    $"Unhandled ProfileTopLevelCollectionPlanResult type '{planResult?.GetType().Name}' for scope '{jsonScope}'."
                );
            }

            // 4. Translate plan entries to merged rows in sequence order.
            var mergedRows = new List<RelationalWriteMergedTableRow>(success.Plan.Sequence.Length);

            // Blocker #1 fix: CurrentRows must contain ALL rows currently in the DB for
            // this scope — including omitted-visible rows that the planner excluded from
            // Sequence. The persister's delete-by-absence logic relies on the set-difference
            // (StableRowIdentity in CurrentRows but not in MergedRows → delete). Building
            // currentCollectionRows only from plan Sequence entries means omitted-visible
            // rows are invisible to the persister and are never deleted.
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
                    case ProfileTopLevelCollectionPlanEntry.MatchedUpdateEntry matchedEntry:
                        // Resolve the concrete request item node from the writable request body.
                        var candidateKey = BuildCandidateIdentityKey(
                            matchedEntry.RequestCandidate.SemanticIdentityValues
                        );
                        if (!candidateKeyToRequestItem.TryGetValue(candidateKey, out var requestItem))
                        {
                            throw new InvalidOperationException(
                                $"MatchedUpdateEntry for scope '{jsonScope}' could not locate a "
                                    + "VisibleRequestCollectionItem for the candidate's semantic identity."
                            );
                        }

                        var concreteRequestItemNode = ResolveCollectionItemNode(
                            writableRequestBody,
                            requestItem.RequestJsonPath
                        );

                        if (concreteRequestItemNode is null)
                        {
                            throw new InvalidOperationException(
                                $"Top-level collection merge for scope '{jsonScope}' could not navigate "
                                    + $"the request body to item path '{requestItem.RequestJsonPath}'. "
                                    + "The visible request item must correspond to an existing array element."
                            );
                        }

                        var mergedRow = ProfileTopLevelCollectionMatchedRowOverlay.BuildMatchedRowEmission(
                            resourceWritePlan,
                            tablePlan,
                            profileRequest,
                            matchedEntry.StoredRow,
                            matchedEntry.RequestCandidate,
                            matchedEntry.HiddenMemberPaths,
                            finalOrdinal,
                            rootPhysicalRowIdentityValues,
                            concreteRequestItemNode,
                            resolvedReferenceLookups
                        );
                        mergedRows.Add(mergedRow);
                        break;

                    case ProfileTopLevelCollectionPlanEntry.HiddenPreserveEntry hiddenEntry:
                        // Clone stored row values and overwrite ordinal column.
                        var cloned = hiddenEntry.StoredRow.ProjectedCurrentRow.Values.ToArray();
                        cloned[tablePlan.CollectionMergePlan!.OrdinalBindingIndex] =
                            new FlattenedWriteValue.Literal(finalOrdinal);
                        var hiddenMergedRow = RelationalWriteRowHelpers.CreateMergedTableRow(
                            tablePlan,
                            cloned
                        );
                        mergedRows.Add(hiddenMergedRow);
                        break;

                    case ProfileTopLevelCollectionPlanEntry.VisibleInsertEntry insertEntry:
                        // Rewrite parent key parts, stamp ordinal, build merged row.
                        var withParentKey = RelationalWriteRowHelpers.RewriteParentKeyPartValues(
                            tablePlan,
                            insertEntry.RequestCandidate.Values,
                            rootPhysicalRowIdentityValues
                        );
                        var stamped = withParentKey.ToArray();
                        stamped[tablePlan.CollectionMergePlan!.OrdinalBindingIndex] =
                            new FlattenedWriteValue.Literal(finalOrdinal);
                        var insertMergedRow = RelationalWriteRowHelpers.CreateMergedTableRow(
                            tablePlan,
                            stamped
                        );
                        mergedRows.Add(insertMergedRow);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled plan entry type '{entry?.GetType().Name}' for scope '{jsonScope}'."
                        );
                }
            }

            tableStates.Add(
                new RelationalWriteMergedTableState(tablePlan, currentCollectionRows, mergedRows)
            );
        }

        return null; // All scopes processed without rejection.
    }

    /// <summary>
    /// Builds a lookup from candidate identity key (same format as the planner uses) to the
    /// matching <see cref="VisibleRequestCollectionItem"/>. Used to resolve the concrete
    /// request item node for matched-update entries.
    /// </summary>
    private static Dictionary<string, VisibleRequestCollectionItem> BuildCandidateKeyToRequestItemLookup(
        ImmutableArray<VisibleRequestCollectionItem> visibleRequestItems
    )
    {
        var result = new Dictionary<string, VisibleRequestCollectionItem>(StringComparer.Ordinal);
        foreach (var item in visibleRequestItems)
        {
            // Build the same key the planner uses for VisibleRequestItems (via AddressKey).
            var addressKey = BuildAddressIdentityKey(item.Address.SemanticIdentityInOrder);
            result.TryAdd(addressKey, item);
        }

        return result;
    }

    /// <summary>
    /// Builds a string key from the candidate's raw CLR semantic identity values by
    /// wrapping each value in a <see cref="JsonValue"/> and serializing to JSON string form,
    /// then joining with a pipe delimiter. Mirrors the planner's <c>BuildCandidateIdentityKey</c>.
    /// </summary>
    private static string BuildCandidateIdentityKey(IReadOnlyList<object?> semanticIdentityValues) =>
        string.Join(
            "|",
            semanticIdentityValues.Select(v =>
                v is null ? "null" : JsonValue.Create(v)?.ToJsonString() ?? "null"
            )
        );

    /// <summary>
    /// Builds a string key from a <see cref="CollectionRowAddress"/>'s semantic identity
    /// parts by serializing each part's JSON value, then joining with a pipe delimiter.
    /// Mirrors the planner's <c>BuildSemanticIdentityKey</c>.
    /// </summary>
    private static string BuildAddressIdentityKey(ImmutableArray<SemanticIdentityPart> identityParts) =>
        string.Join("|", identityParts.Select(p => p.Value?.ToJsonString() ?? "null"));

    // -- Document-reference canonicalization helpers ------------------------

    /// <summary>
    /// Returns the semantic-identity positions backed by a document-reference FK column, paired with the
    /// reference binding metadata needed to resolve Core-emitted natural-key parts to the stored document id.
    /// </summary>
    private static IReadOnlyList<DocumentReferenceIdentityPart> ResolveDocumentReferenceIdentityParts(
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
    private static ImmutableArray<VisibleRequestCollectionItem> CanonicalizeDocumentReferenceRequestItems(
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
    private static ImmutableArray<VisibleStoredCollectionRow> CanonicalizeDocumentReferenceStoredRows(
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
                matched = null;
                break;
            }

            matched = currentRow;
        }

        if (matched is not null)
        {
            return TryGetInt64CurrentRowValue(matched, targetPart.Binding.FkColumn);
        }

        if (currentRows.Length == storedRowsLength && storedRowIndex < currentRows.Length)
        {
            return TryGetInt64CurrentRowValue(currentRows[storedRowIndex], targetPart.Binding.FkColumn);
        }

        return null;
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
            || column.SourceJsonPath is null
            || !resolvedReferenceLookups.TryGetDescriptorIdByUri(
                column.TargetResource.Value,
                column.SourceJsonPath.Value.Canonical,
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

    private sealed record DocumentReferenceIdentityPart(
        int IdentityIndex,
        int BindingIndex,
        DocumentReferenceBinding Binding,
        ReferenceIdentityBinding IdentityBinding
    );

    // ── Descriptor-URI canonicalization helpers ────────────────────────────

    /// <summary>
    /// Returns the zero-based positions within the table's
    /// <see cref="DbTableIdentityMetadata.SemanticIdentityBindings"/> that require
    /// URI-to-Int64 canonicalization (i.e. those backed by a <see cref="ColumnKind.DescriptorFk"/>
    /// column). Returns an empty list when no descriptor-backed parts exist.
    /// </summary>
    private static IReadOnlyList<int> ResolveDescriptorIdentityIndices(TableWritePlan tablePlan)
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
    private static ImmutableArray<VisibleRequestCollectionItem> CanonicalizeDescriptorRequestItems(
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
    private static ImmutableArray<VisibleStoredCollectionRow> CanonicalizeDescriptorStoredRows(
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
    /// <para><b>Slice 4 fence shape:</b>
    /// Slice 4 does not add an executor-level fence on descriptor-backed top-level collection
    /// identity — rejecting at the planner/executor would block the common cases this method
    /// handles correctly (URI in cache; mixed scalar+descriptor identity; descriptor-only
    /// without hidden rows). Instead, the single structurally-ambiguous case — descriptor-only
    /// identity with hidden rows interleaved in current rows and a URI cache miss — is narrowed
    /// to a runtime fail-closed throw below. That combination is rare in standard Ed-Fi
    /// profiles but must not silently return an incorrect descriptor id. A later slice may
    /// widen support by seeding the descriptor resolver from the stored body or adding a
    /// planner-level reject; until then the throw is the Slice 4 fence.</para>
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
    ///   <item>If the identity has non-descriptor (scalar) parts: match by those scalar parts
    ///   across <paramref name="currentRows"/> to find the corresponding current row, then copy
    ///   its descriptor id at the same index position.</item>
    ///   <item>If the identity is descriptor-only AND
    ///   <c>currentRows.Length == storedRows.Length</c>: use positional correspondence —
    ///   <c>VisibleStoredRows[storedRowIndex]</c> maps to <c>currentRows[storedRowIndex]</c>.
    ///   This holds because both arrays are ordered by stored-ordinal (planner invariants
    ///   <c>ValidateStoredOrdinalOrder</c> + <c>ProjectCurrentRowsForScope OrderBy</c>) and no
    ///   hidden rows can interleave when the identity is descriptor-only (all rows are visible
    ///   since there are no scalar parts to hide on).</item>
    ///   <item>If the identity is descriptor-only AND the counts differ (hidden rows are
    ///   interleaved — structurally possible but not expected in practice): throw with a
    ///   diagnostic message rather than silently producing an incorrect result.</item>
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
                    wildcardPath,
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
                        + "This can happen when the identity is descriptor-only, the stored rows contain "
                        + "hidden rows interleaved with visible rows, and the cache does not hold the URI. "
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
    /// <para>Two strategies are tried in order:</para>
    /// <list type="number">
    ///   <item>Scalar-part matching: if the identity has non-descriptor parts at positions
    ///   not in <paramref name="descriptorIndices"/>, find the unique current row whose
    ///   semantic identity matches all scalar parts and copy its descriptor part at the same
    ///   index position.</item>
    ///   <item>Positional matching (descriptor-only): if no scalar parts exist AND
    ///   <paramref name="currentRows"/><c>.Length == </c><paramref name="storedRowsLength"/>
    ///   (all stored rows are covered by current rows one-to-one), use
    ///   <c>currentRows[storedRowIndex]</c> directly. When the counts differ, the positional
    ///   assumption does not hold (a profile Filter has hidden some stored rows while all DB
    ///   rows remain in <paramref name="currentRows"/>), so <c>null</c> is returned and the
    ///   caller throws rather than silently picking the wrong row.</item>
    /// </list>
    /// <para>Returns <c>null</c> when no match is found and the caller should throw.</para>
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

        if (scalarIndices.Count > 0)
        {
            // Strategy 1: match by scalar parts.
            // Find the unique current row whose semantic identity matches all scalar parts at
            // the same index positions as the stored row's scalar parts.
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
                    // Cannot safely choose one; return null and let the caller throw.
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

        // Strategy 2: positional matching (descriptor-only identity).
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
        // currentRows is sorted by StoredOrdinal (see ProjectCurrentRowsForScope). This
        // method does NOT independently verify the correspondence — a structural check
        // would require resolving each current row's Int64 descriptor id back to its URI
        // (a DB roundtrip against the descriptor projection), which is out of scope for
        // Slice 4. If the upstream contract is broken (body out of ordinal order, or
        // VisibleStoredRows mis-ordered), positional rewrite would silently swap descriptor
        // ids before the planner runs and downstream invariants would pass against the
        // rewritten values. A later slice may add the reverse-resolution check; until then
        // this is a documented residual risk fenced behind the count-equality guard.
        //
        // Safe ONLY when currentRows.Length == storedRowsLength, which is a necessary (not
        // sufficient) condition for one-to-one ordinal correspondence. When the counts
        // differ (a profile Filter has restricted the visible stored rows while the DB
        // still holds the full set in currentRows), positional indexing would pick the
        // wrong row — return null so the caller throws with a diagnostic instead of
        // silently corrupting data.
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
    /// Projects the current DB rows for a single top-level collection table into
    /// <see cref="CurrentCollectionRowSnapshot"/> instances. Filters to rows whose
    /// parent physical identity columns match <paramref name="rootPhysicalRowIdentityValues"/>,
    /// then extracts <see cref="CurrentCollectionRowSnapshot.StableRowIdentity"/>,
    /// <see cref="CurrentCollectionRowSnapshot.SemanticIdentityInOrder"/>, and
    /// <see cref="CurrentCollectionRowSnapshot.StoredOrdinal"/> from the table plan's
    /// binding indexes. Rows are returned sorted by ascending <c>StoredOrdinal</c>.
    /// </summary>
    private static ImmutableArray<CurrentCollectionRowSnapshot> ProjectCurrentRowsForScope(
        RelationalWriteCurrentState currentState,
        TableWritePlan tablePlan,
        IReadOnlyList<FlattenedWriteValue> rootPhysicalRowIdentityValues
    )
    {
        var hydratedRows = TryFindHydratedRowsForTable(currentState, tablePlan);
        if (hydratedRows is null || hydratedRows.Count == 0)
        {
            return ImmutableArray<CurrentCollectionRowSnapshot>.Empty;
        }

        var mergePlan =
            tablePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{ProfileBindingClassificationCore.FormatTable(tablePlan)}' does not have a compiled collection merge plan."
            );

        // Build a lookup of parent-scope-locator column names → binding indexes so we can
        // match each DB row against rootPhysicalRowIdentityValues.
        var immediateParentColumns = tablePlan.TableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns;
        var parentBindingIndexes = immediateParentColumns
            .Select(col => RelationalWriteMergeSupport.FindBindingIndex(tablePlan, col))
            .ToArray();

        // Build expected parent identity values from rootPhysicalRowIdentityValues using the
        // parent binding indexes.  rootPhysicalRowIdentityValues is indexed by PhysicalRowIdentity
        // column order on the ROOT table; for top-level collection tables the parent key part
        // index maps directly to the root table's physical identity columns.
        var projectedAll = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, hydratedRows);

        var snapshots = new List<CurrentCollectionRowSnapshot>(projectedAll.Length);
        for (var rowIndex = 0; rowIndex < projectedAll.Length; rowIndex++)
        {
            var projectedRow = projectedAll[rowIndex];
            var hydratedRow = hydratedRows[rowIndex];
            // Filter: every parent-scope-locator column must match rootPhysicalRowIdentityValues.
            var parentMatches = true;
            for (var pi = 0; pi < parentBindingIndexes.Length; pi++)
            {
                var parentBindingIdx = parentBindingIndexes[pi];

                // rootPhysicalRowIdentityValues uses the ParentKeyPart.Index to find the right
                // value. The binding's source carries the ParentKeyPart.Index.
                if (
                    tablePlan.ColumnBindings[parentBindingIdx].Source
                    is WriteValueSource.ParentKeyPart parentKeyPart
                )
                {
                    var expectedValue = rootPhysicalRowIdentityValues[parentKeyPart.Index];
                    var actualValue = projectedRow.Values[parentBindingIdx];

                    if (!FlattenedWriteValueEquals(expectedValue, actualValue))
                    {
                        parentMatches = false;
                        break;
                    }
                }
                // If not a ParentKeyPart source, we still compare by literal equality.
                else if (
                    pi < rootPhysicalRowIdentityValues.Count
                    && !FlattenedWriteValueEquals(
                        rootPhysicalRowIdentityValues[pi],
                        projectedRow.Values[parentBindingIdx]
                    )
                )
                {
                    parentMatches = false;
                    break;
                }
            }

            if (!parentMatches)
            {
                continue;
            }

            // Extract stable row identity. PhysicalRowIdentity columns are NOT NULL in the DB
            // and are projected as a FlattenedWriteValue.Literal carrying a numeric CLR value
            // (typically long, sometimes int from narrower projection paths). Fail closed if the
            // projection produces anything else — silently defaulting would collide identities
            // (multiple rows hashed to 0) and mask upstream binding mis-mapping.
            long stableRowIdentity = ExtractRequiredInt64(
                projectedRow.Values[mergePlan.StableRowIdentityBindingIndex],
                tablePlan,
                mergePlan.StableRowIdentityBindingIndex,
                "stable row identity"
            );

            // Extract ordinal. The ordinal column is NOT NULL in the DB and is projected as a
            // FlattenedWriteValue.Literal carrying an int (sometimes a wider numeric type from
            // certain backends). Fail closed if the projection produces anything else —
            // silently defaulting would tie ordering and mask upstream binding mis-mapping.
            int storedOrdinal = ExtractRequiredInt32(
                projectedRow.Values[mergePlan.OrdinalBindingIndex],
                tablePlan,
                mergePlan.OrdinalBindingIndex,
                "stored ordinal"
            );

            // Extract semantic identity parts.
            var identityParts = mergePlan
                .SemanticIdentityBindings.Select(binding =>
                {
                    var bindingVal = projectedRow.Values[binding.BindingIndex];
                    var rawValue = bindingVal is FlattenedWriteValue.Literal valLit ? valLit.Value : null;
                    JsonNode? jsonNode = rawValue is null ? null : JsonValue.Create(rawValue);
                    return new SemanticIdentityPart(
                        binding.RelativePath.Canonical,
                        jsonNode,
                        IsPresent: rawValue is not null
                    );
                })
                .ToImmutableArray();

            // Build a column-name-keyed view of the hydrated row covering every column on
            // the table model — including UnifiedAlias columns that the binding-indexed
            // projection (above) skips because they are not in ColumnBindings. Hidden
            // key-unification preservation in the matched-row overlay reads MemberPathColumn
            // and PresenceColumn by physical column name; those are required-UnifiedAlias
            // per KeyUnificationWritePlanCompiler and would not be present in a binding-only
            // view.
            var currentRowByColumnName = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
                tablePlan.TableModel,
                hydratedRow
            );

            snapshots.Add(
                new CurrentCollectionRowSnapshot(
                    stableRowIdentity,
                    identityParts,
                    storedOrdinal,
                    projectedRow,
                    currentRowByColumnName
                )
            );
        }

        return [.. snapshots.OrderBy(s => s.StoredOrdinal)];
    }

    /// <summary>
    /// Compares two <see cref="FlattenedWriteValue"/> instances by their underlying literal
    /// values for parent-scope filtering.
    /// </summary>
    private static bool FlattenedWriteValueEquals(FlattenedWriteValue a, FlattenedWriteValue b)
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

            // Convert both to string for comparison to handle numeric type mismatches
            // (e.g., long vs int from different projection paths).
            return string.Equals(
                Convert.ToString(litA.Value),
                Convert.ToString(litB.Value),
                StringComparison.Ordinal
            );
        }

        // Non-literal values don't participate in parent-key filtering.
        return ReferenceEquals(a, b);
    }

    /// <summary>
    /// Describes a projected current-state binding for diagnostic messages. Distinguishes
    /// non-literal binding shapes from null literals and from typed literal values.
    /// </summary>
    private static string DescribeProjectedKind(FlattenedWriteValue value)
    {
        if (value is not FlattenedWriteValue.Literal literal)
        {
            return value.GetType().Name;
        }
        return literal.Value is null ? "null literal" : literal.Value.GetType().Name;
    }

    /// <summary>
    /// Extracts a required Int64 value from a projected current-state binding. Throws when
    /// the binding is not a <see cref="FlattenedWriteValue.Literal"/>, when its value is
    /// <c>null</c>, or when the value cannot be coerced to <see cref="long"/>. Used for
    /// columns that are NOT NULL in the DB (stable row identity, ordinal-keyed columns) so
    /// projection drift surfaces deterministically rather than silently producing 0.
    /// </summary>
    private static long ExtractRequiredInt64(
        FlattenedWriteValue value,
        TableWritePlan tablePlan,
        int bindingIndex,
        string columnRole
    )
    {
        if (value is not FlattenedWriteValue.Literal literal || literal.Value is null)
        {
            throw new InvalidOperationException(
                $"Required {columnRole} column at binding index {bindingIndex} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + $"{DescribeProjectedKind(value)}; "
                    + "expected a non-null numeric literal. Current-state projection drift."
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
                $"Required {columnRole} column at binding index {bindingIndex} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + $"'{literal.Value.GetType().Name}' that cannot be coerced to Int64. "
                    + "Current-state projection drift.",
                ex
            );
        }
    }

    /// <summary>
    /// Extracts a required Int32 value from a projected current-state binding. Same intent as
    /// <see cref="ExtractRequiredInt64"/>: fail closed on projection drift rather than default
    /// to 0 and silently break sort order or duplicate-detection.
    /// </summary>
    private static int ExtractRequiredInt32(
        FlattenedWriteValue value,
        TableWritePlan tablePlan,
        int bindingIndex,
        string columnRole
    )
    {
        if (value is not FlattenedWriteValue.Literal literal || literal.Value is null)
        {
            throw new InvalidOperationException(
                $"Required {columnRole} column at binding index {bindingIndex} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + $"{DescribeProjectedKind(value)}; "
                    + "expected a non-null numeric literal. Current-state projection drift."
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
                $"Required {columnRole} column at binding index {bindingIndex} on table "
                    + $"'{ProfileBindingClassificationCore.FormatTable(tablePlan)}' projected as "
                    + $"'{literal.Value.GetType().Name}' that cannot be coerced to Int32. "
                    + "Current-state projection drift.",
                ex
            );
        }
    }

    /// <summary>
    /// Resolves a concrete JSON array item node from <paramref name="requestBody"/> using a
    /// path like <c>$.classPeriods[0]</c>. Used as a fallback when the built-in
    /// <see cref="RelationalWriteFlattener.TryGetRelativeLeafNode"/> cannot navigate
    /// array-indexed paths directly.
    /// </summary>
    private static JsonNode? ResolveCollectionItemNode(JsonNode requestBody, string requestJsonPath)
    {
        // Simple path walker: splits on '[' and ']' to handle array-indexed paths.
        // E.g. "$.classPeriods[0]" → navigate to "classPeriods" array, then element 0.
        try
        {
            JsonNode? current = requestBody;
            // Strip leading "$." or "$"
            var path = requestJsonPath.StartsWith("$.", StringComparison.Ordinal)
                ? requestJsonPath[2..]
                : requestJsonPath.TrimStart('$').TrimStart('.');

            foreach (var segment in SplitJsonPathSegments(path))
            {
                if (current is null)
                {
                    return null;
                }

                if (int.TryParse(segment, out var arrayIndex))
                {
                    current = current is JsonArray arr && arrayIndex < arr.Count ? arr[arrayIndex] : null;
                }
                else
                {
                    current = current is JsonObject obj ? obj[segment] : null;
                }
            }

            return current;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SplitJsonPathSegments(string path)
    {
        // Split "addresses[0]" into ["addresses", "0"]
        var segments = new List<string>();
        var remaining = path;
        while (!string.IsNullOrEmpty(remaining))
        {
            var dotIdx = remaining.IndexOf('.');
            var bracketIdx = remaining.IndexOf('[');

            if (dotIdx == -1 && bracketIdx == -1)
            {
                segments.Add(remaining);
                break;
            }

            int nextIdx;
            if (dotIdx == -1)
            {
                nextIdx = bracketIdx;
            }
            else if (bracketIdx == -1)
            {
                nextIdx = dotIdx;
            }
            else
            {
                nextIdx = Math.Min(dotIdx, bracketIdx);
            }

            if (nextIdx > 0)
            {
                segments.Add(remaining[..nextIdx]);
            }

            if (nextIdx == bracketIdx)
            {
                var closeIdx = remaining.IndexOf(']', bracketIdx);
                if (closeIdx > bracketIdx + 1)
                {
                    segments.Add(remaining[(bracketIdx + 1)..closeIdx]);
                    remaining = remaining[(closeIdx + 1)..].TrimStart('.');
                }
                else
                {
                    break;
                }
            }
            else
            {
                remaining = remaining[(dotIdx + 1)..];
            }
        }

        return segments;
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
