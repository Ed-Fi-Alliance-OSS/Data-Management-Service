// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared compare/persist row shape used by the unified merge and persister pipeline.
/// </summary>
internal sealed record MergeTableRow
{
    public MergeTableRow(
        IEnumerable<FlattenedWriteValue> values,
        IEnumerable<FlattenedWriteValue> comparableValues
    )
    {
        Values = FlattenedWriteContractSupport.ToImmutableArray(values, nameof(values));
        ComparableValues = FlattenedWriteContractSupport.ToImmutableArray(
            comparableValues,
            nameof(comparableValues)
        );
    }

    public ImmutableArray<FlattenedWriteValue> Values { get; init; }

    public ImmutableArray<FlattenedWriteValue> ComparableValues { get; init; }
}

/// <summary>
/// A new visible row to be inserted.
/// </summary>
internal sealed record MergeRowInsert(ImmutableArray<FlattenedWriteValue> Values);

/// <summary>
/// A matched visible row to update. Values contain the overlay result.
/// StableRowIdentityValue is the CollectionItemId for collection rows (null for non-collection).
/// </summary>
internal sealed record MergeRowUpdate(
    ImmutableArray<FlattenedWriteValue> Values,
    long? StableRowIdentityValue
);

/// <summary>
/// A row to delete. StableRowIdentityValue is used for collection rows. Values carries the
/// concrete current-row bindings for non-collection delete-by-parent operations.
/// </summary>
internal sealed record MergeRowDelete(
    long? StableRowIdentityValue,
    ImmutableArray<FlattenedWriteValue>? Values = null
);

/// <summary>
/// A hidden row preserved unchanged. Not consumed by the persister at runtime —
/// the persister drives from Inserts/Updates/Deletes and ComparableCurrentRowset/
/// ComparableMergedRowset. This type exists as test-observable state: merge
/// synthesizer tests assert on PreservedRows to verify that hidden rows are
/// correctly classified and their ordinals are tracked for interleaving.
/// </summary>
internal sealed record MergePreservedRow(ImmutableArray<FlattenedWriteValue> Values, int OriginalOrdinal);

/// <summary>
/// Per-table state produced by the profile merge synthesizer, with explicit action classification
/// so that hidden rows are never accidentally deleted.
/// </summary>
internal sealed record RelationalWriteMergeTableState
{
    public RelationalWriteMergeTableState(
        TableWritePlan tableWritePlan,
        IEnumerable<MergeRowInsert> inserts,
        IEnumerable<MergeRowUpdate> updates,
        IEnumerable<MergeRowDelete> deletes,
        IEnumerable<MergePreservedRow> preservedRows,
        IEnumerable<MergeTableRow> comparableCurrentRowset,
        IEnumerable<MergeTableRow> comparableMergedRowset
    )
    {
        TableWritePlan = tableWritePlan ?? throw new ArgumentNullException(nameof(tableWritePlan));
        Inserts = FlattenedWriteContractSupport.ToImmutableArray(inserts, nameof(inserts));
        Updates = FlattenedWriteContractSupport.ToImmutableArray(updates, nameof(updates));
        Deletes = FlattenedWriteContractSupport.ToImmutableArray(deletes, nameof(deletes));
        PreservedRows = FlattenedWriteContractSupport.ToImmutableArray(preservedRows, nameof(preservedRows));
        ComparableCurrentRowset = FlattenedWriteContractSupport.ToImmutableArray(
            comparableCurrentRowset,
            nameof(comparableCurrentRowset)
        );
        ComparableMergedRowset = FlattenedWriteContractSupport.ToImmutableArray(
            comparableMergedRowset,
            nameof(comparableMergedRowset)
        );
    }

    public TableWritePlan TableWritePlan { get; init; }

    public ImmutableArray<MergeRowInsert> Inserts { get; init; }

    public ImmutableArray<MergeRowUpdate> Updates { get; init; }

    public ImmutableArray<MergeRowDelete> Deletes { get; init; }

    /// <summary>
    /// Test-observable state only — not consumed by the persister. See
    /// <see cref="MergePreservedRow"/> for rationale.
    /// </summary>
    public ImmutableArray<MergePreservedRow> PreservedRows { get; init; }

    /// <summary>
    /// Current rows projected for no-op comparison (same type as no-profile path).
    /// </summary>
    public ImmutableArray<MergeTableRow> ComparableCurrentRowset { get; init; }

    /// <summary>
    /// Post-merge rows projected for no-op comparison (same type as no-profile path).
    /// </summary>
    public ImmutableArray<MergeTableRow> ComparableMergedRowset { get; init; }
}

/// <summary>
/// The full result of profile merge synthesis, containing per-table state in dependency order.
/// </summary>
internal sealed record RelationalWriteMergeResult
{
    public RelationalWriteMergeResult(IEnumerable<RelationalWriteMergeTableState> tablesInDependencyOrder)
    {
        TablesInDependencyOrder = FlattenedWriteContractSupport.ToImmutableArray(
            tablesInDependencyOrder,
            nameof(tablesInDependencyOrder)
        );
    }

    public ImmutableArray<RelationalWriteMergeTableState> TablesInDependencyOrder { get; init; }
}

/// <summary>
/// Request to synthesize a profile-aware merge result.
/// </summary>
/// <remarks>
/// Null-profile invariant (enforced in constructor): if <see cref="ProfileRequest"/> is
/// <c>null</c>, then <see cref="ProfileContext"/> and <see cref="CompiledScopeCatalog"/>
/// must also both be <c>null</c>. The synthesizer populates all three atomically via
/// <see cref="NoProfileSyntheticProfileAdapter"/> at the entry point of
/// <see cref="RelationalWriteMergeSynthesizer.Synthesize"/>.
/// <para>
/// Profile-mode semantics (not enforced in constructor, handled by synthesizer logic):
/// for create-new flows (<see cref="CurrentState"/> is <c>null</c>), <see cref="ProfileContext"/>
/// is typically <c>null</c>; for existing-document flows, it should be non-<c>null</c>.
/// </para>
/// </remarks>
internal sealed record RelationalWriteMergeRequest
{
    public RelationalWriteMergeRequest(
        ResourceWritePlan WritePlan,
        FlattenedWriteSet FlattenedWriteSet,
        RelationalWriteCurrentState? CurrentState,
        ProfileAppliedWriteRequest? ProfileRequest,
        ProfileAppliedWriteContext? ProfileContext,
        IReadOnlyList<CompiledScopeDescriptor>? CompiledScopeCatalog,
        JsonNode? SelectedBody = null
    )
    {
        ArgumentNullException.ThrowIfNull(WritePlan);
        ArgumentNullException.ThrowIfNull(FlattenedWriteSet);

        // Null-profile invariant: all three must be null together.
        // The adapter populates all three atomically inside Synthesize.
        if (ProfileRequest is null)
        {
            if (ProfileContext is not null)
            {
                throw new ArgumentException(
                    $"When {nameof(ProfileRequest)} is null, {nameof(ProfileContext)} must also be null. "
                        + "The adapter populates all three atomically.",
                    nameof(ProfileContext)
                );
            }
            if (CompiledScopeCatalog is not null)
            {
                throw new ArgumentException(
                    $"When {nameof(ProfileRequest)} is null, {nameof(CompiledScopeCatalog)} must also be null. "
                        + "The adapter populates all three atomically.",
                    nameof(CompiledScopeCatalog)
                );
            }
        }

        this.WritePlan = WritePlan;
        this.FlattenedWriteSet = FlattenedWriteSet;
        this.CurrentState = CurrentState;
        this.ProfileRequest = ProfileRequest;
        this.ProfileContext = ProfileContext;
        this.CompiledScopeCatalog = CompiledScopeCatalog;
        this.SelectedBody = SelectedBody;
    }

    public ResourceWritePlan WritePlan { get; init; }
    public FlattenedWriteSet FlattenedWriteSet { get; init; }
    public RelationalWriteCurrentState? CurrentState { get; init; }

    /// <summary>
    /// The profile-applied write request. Null for null-profile callers; the synthesizer will
    /// route through <see cref="NoProfileSyntheticProfileAdapter"/> to populate this field before
    /// running the main synthesis logic.
    /// </summary>
    public ProfileAppliedWriteRequest? ProfileRequest { get; init; }

    public ProfileAppliedWriteContext? ProfileContext { get; init; }

    /// <summary>
    /// The compiled scope catalog. Null for null-profile callers (populated by the adapter).
    /// </summary>
    public IReadOnlyList<CompiledScopeDescriptor>? CompiledScopeCatalog { get; init; }

    /// <summary>
    /// The selected request body. Required for null-profile callers so the adapter can populate
    /// <see cref="ProfileRequest.WritableRequestBody"/>. Profile callers set this to <c>null</c>
    /// (they carry the body in <see cref="ProfileRequest"/>).
    /// </summary>
    public JsonNode? SelectedBody { get; init; }
}

/// <summary>
/// A single validation failure from profile merge synthesis.
/// </summary>
internal sealed record MergeValidationFailure(string Message);

/// <summary>
/// Discriminated union of outcomes from profile merge synthesis.
/// </summary>
internal abstract record RelationalWriteMergeSynthesisOutcome
{
    internal sealed record Success(RelationalWriteMergeResult MergeResult)
        : RelationalWriteMergeSynthesisOutcome;

    internal sealed record ValidationFailure(ImmutableArray<MergeValidationFailure> Failures)
        : RelationalWriteMergeSynthesisOutcome;

    /// <summary>
    /// Deterministic Core-backend contract mismatch detected during merge synthesis.
    /// Distinct from <see cref="ValidationFailure"/> (user-caused) — these represent a
    /// pipeline bug where Core emitted incomplete profile metadata (missing RequestScopeState,
    /// missing VisibleRequestCollectionItem, duplicate visible candidate, etc.). The executor
    /// maps this to a category-5 <c>UnknownFailure</c> result rather than letting an
    /// <see cref="InvalidOperationException"/> bubble up as a generic 500.
    /// </summary>
    internal sealed record ContractMismatch(ImmutableArray<string> Messages)
        : RelationalWriteMergeSynthesisOutcome;
}

/// <summary>
/// Synthesizes a profile-aware merge result from a profile merge request.
/// </summary>
internal interface IRelationalWriteMergeSynthesizer
{
    RelationalWriteMergeSynthesisOutcome Synthesize(RelationalWriteMergeRequest request);
}

internal sealed class RelationalWriteMergeSynthesizer : IRelationalWriteMergeSynthesizer
{
    public RelationalWriteMergeSynthesisOutcome Synthesize(RelationalWriteMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isNullProfile = request.ProfileRequest is null;

        // Null-profile callers: use the adapter to synthesize profile-shape inputs so
        // the rest of this method runs a single code path for both modes.
        if (isNullProfile)
        {
            var synthetic = NoProfileSyntheticProfileAdapter.Build(
                request.WritePlan,
                request.FlattenedWriteSet,
                request.SelectedBody ?? new JsonObject(),
                request.CurrentState
            );

            request = request with
            {
                ProfileRequest = synthetic.Request,
                ProfileContext = synthetic.Context,
                CompiledScopeCatalog = synthetic.Catalog,
            };
        }

        // After adapter routing, ProfileRequest and CompiledScopeCatalog are always non-null.
        var compiledScopeCatalog =
            request.CompiledScopeCatalog
            ?? throw new InvalidOperationException(
                "CompiledScopeCatalog must be non-null after adapter routing."
            );

        var scopeLookup = MergeScopeLookup.Create(request);
        var currentStateProjection = MergeCurrentStateProjection.Create(request);
        var tableStateBuilders = CreateTableStateBuilders(request.WritePlan, currentStateProjection);

        bool isCreate = request.CurrentState is null;

        // Precompute inlined visible-absent scope paths for all table plans.
        // Each table may host inlined non-collection scopes whose visible-absent members
        // must be cleared (or whose hidden members must be preserved).
        var inlinedScopePathsByTable = PrecomputeInlinedScopePathsForAllTables(
            request.WritePlan,
            scopeLookup,
            compiledScopeCatalog
        );

        // --- Root table (always present) ---
        var rootPlan = request.FlattenedWriteSet.RootRow.TableWritePlan;
        var rootStoredScopeState = scopeLookup.TryGetStoredScopeState("$");
        var rootHiddenMemberPaths = HiddenMemberPathVocabulary.ToJsonPathRelative(
            rootStoredScopeState?.HiddenMemberPaths ?? []
        );
        var docRefBindings = request.WritePlan.Model.DocumentReferenceBindings;

        var (inlinedClearablePaths, inlinedHiddenPaths) = GetInlinedScopePaths(
            rootPlan,
            inlinedScopePathsByTable
        );

        var augmentedHiddenPaths =
            inlinedHiddenPaths.Length > 0
                ? rootHiddenMemberPaths.AddRange(inlinedHiddenPaths)
                : rootHiddenMemberPaths;

        var rootClassifications = RelationalWriteBindingClassifier.Classify(
            rootPlan,
            augmentedHiddenPaths,
            inlinedClearablePaths,
            docRefBindings
        );

        var rootCurrentValues = isCreate ? null : currentStateProjection.GetCurrentRowValues(rootPlan);

        var rootOverlaidValues = OverlayValues(
            rootPlan,
            rootClassifications,
            request.FlattenedWriteSet.RootRow.Values,
            rootCurrentValues,
            ImmutableArray<FlattenedWriteValue>.Empty
        );
        var (adjustedRootValues, rootKeyUnificationFailures) = AdjustKeyUnificationOverlayForHiddenMembers(
            rootPlan,
            rootOverlaidValues,
            rootCurrentValues,
            augmentedHiddenPaths
        );

        if (rootKeyUnificationFailures.Length > 0)
        {
            return new RelationalWriteMergeSynthesisOutcome.ValidationFailure(rootKeyUnificationFailures);
        }

        rootOverlaidValues = adjustedRootValues;

        var rootComparableValues = ProjectComparableValues(rootPlan, rootOverlaidValues);

        if (isCreate)
        {
            tableStateBuilders[rootPlan.TableModel.Table].AddInsert(new MergeRowInsert(rootOverlaidValues));
        }
        else
        {
            tableStateBuilders[rootPlan.TableModel.Table]
                .AddUpdate(new MergeRowUpdate(rootOverlaidValues, StableRowIdentityValue: null));
        }

        tableStateBuilders[rootPlan.TableModel.Table]
            .AddComparableMergedRow(new MergeTableRow(rootOverlaidValues, rootComparableValues));

        var rootPhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(rootPlan, rootOverlaidValues);
        var profileCollectionLookup = MergeCollectionLookup.Create(request);

        // Track which scope keys were visited by buffer iteration so the second pass
        // can skip them (avoiding duplicate deletes). Keyed by "{jsonScope}|{ancestorKey}".
        var visitedScopeKeys = new HashSet<string>(StringComparer.Ordinal);

        // --- Root extension rows ---
        var extensionFailure = SynthesizeRootExtensionRows(
            request.FlattenedWriteSet.RootRow.RootExtensionRows,
            rootPhysicalRowIdentityValues,
            scopeLookup,
            profileCollectionLookup,
            currentStateProjection,
            tableStateBuilders,
            isCreate,
            docRefBindings,
            inlinedScopePathsByTable,
            request.WritePlan,
            compiledScopeCatalog,
            useLegacyRequestOrderForVisibleRows: isNullProfile,
            visitedScopeKeys
        );

        if (extensionFailure is not null)
        {
            return extensionFailure;
        }

        // --- Collection candidates (profile-aware merge) ---
        var collectionFailure = SynthesizeProfileCollectionCandidates(
            request.FlattenedWriteSet.RootRow.CollectionCandidates,
            rootPhysicalRowIdentityValues,
            scopeLookup,
            profileCollectionLookup,
            currentStateProjection,
            tableStateBuilders,
            isCreate,
            docRefBindings,
            inlinedScopePathsByTable,
            request.WritePlan,
            compiledScopeCatalog,
            useLegacyRequestOrderForVisibleRows: isNullProfile,
            visitedScopeKeys: visitedScopeKeys
        );

        if (collectionFailure is not null)
        {
            return collectionFailure;
        }

        // --- Reverse contract validation for non-collection separate-table scopes ---
        // Mirrors the collection reverse check (VisibleRequestCollectionItems ↔ candidates).
        // Every VisiblePresent RequestScopeState targeting a separate-table non-collection
        // scope must have been visited by buffer iteration. If not, the flattener dropped
        // the buffer row (e.g. empty scope object) and the scope would silently fall through
        // the cracks — preserving stale data on update or skipping insert/creatability on create.
        // Only applies to real profiled requests — the null-profile adapter synthesizes broad
        // VisiblePresent states that don't imply buffer presence for all table-backed scopes.
        if (!isNullProfile && request.ProfileRequest is not null)
        {
            var reverseValidationOutcome = ValidateNonCollectionScopeReverseCoverage(
                request.ProfileRequest.RequestScopeStates,
                request.ProfileRequest.WritableRequestBody,
                request.WritePlan,
                visitedScopeKeys
            );

            if (reverseValidationOutcome is not null)
            {
                return reverseValidationOutcome;
            }
        }

        // --- Second pass: emit deletes for StoredScopeStates not visited by buffer iteration ---
        // This closes a profile-mode gap where omitted separate-table scopes with current-state
        // data were never deleted because the flattener drops omitted scopes, leaving no buffer
        // entry for the merge's buffer-iteration branches to fire.
        if (request.ProfileContext is not null && !isCreate)
        {
            var secondPassOutcome = ApplyStoredScopeStatesSecondPass(
                request.ProfileContext.StoredScopeStates,
                currentStateProjection,
                tableStateBuilders,
                request.WritePlan,
                compiledScopeCatalog,
                visitedScopeKeys,
                isNullProfile
            );

            if (secondPassOutcome is not null)
            {
                return secondPassOutcome;
            }
        }

        return new RelationalWriteMergeSynthesisOutcome.Success(
            new RelationalWriteMergeResult(
                request.WritePlan.TablePlansInDependencyOrder.Select(tableWritePlan =>
                    tableStateBuilders[tableWritePlan.TableModel.Table].Build()
                )
            )
        );
    }

    private static Dictionary<DbTableName, MergeTableStateBuilder> CreateTableStateBuilders(
        ResourceWritePlan writePlan,
        MergeCurrentStateProjection currentStateProjection
    )
    {
        Dictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders = [];

        foreach (var tableWritePlan in writePlan.TablePlansInDependencyOrder)
        {
            tableStateBuilders.Add(
                tableWritePlan.TableModel.Table,
                new MergeTableStateBuilder(
                    tableWritePlan,
                    currentStateProjection.GetCurrentRows(tableWritePlan)
                )
            );
        }

        return tableStateBuilders;
    }

    private static RelationalWriteMergeSynthesisOutcome? SynthesizeRootExtensionRows(
        IReadOnlyList<RootExtensionWriteRowBuffer> rootExtensionRows,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        MergeScopeLookup scopeLookup,
        MergeCollectionLookup profileCollectionLookup,
        MergeCurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders,
        bool isCreate,
        IReadOnlyList<DocumentReferenceBinding> docRefBindings,
        IReadOnlyDictionary<
            DbTableName,
            (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)
        > inlinedScopePathsByTable,
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        bool useLegacyRequestOrderForVisibleRows,
        HashSet<string>? visitedScopeKeys = null
    )
    {
        foreach (var rootExtensionRow in rootExtensionRows)
        {
            var tablePlan = rootExtensionRow.TableWritePlan;
            var jsonScope = tablePlan.TableModel.JsonScope.Canonical;

            // Track this scope as visited by buffer iteration (root-level = empty ancestor key)
            visitedScopeKeys?.Add($"{jsonScope}|");

            var requestScopeState = scopeLookup.TryGetRequestScopeState(jsonScope);
            var storedScopeState = scopeLookup.TryGetStoredScopeState(jsonScope);

            var rewrittenValues = RewriteParentKeyPartValues(
                tablePlan,
                rootExtensionRow.Values,
                parentPhysicalRowIdentityValues
            );

            var scopePhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(tablePlan, rewrittenValues);

            // Decision matrix for non-collection separate-table scopes
            if (storedScopeState is { Visibility: ProfileVisibilityKind.Hidden })
            {
                // Hidden scope: preserve current row and all descendant child collection rows.
                // ExtensionCollection tables are excluded from root-level collection discovery,
                // so they must be preserved explicitly here.
                var currentValues = currentStateProjection.GetCurrentRowValues(tablePlan);

                if (currentValues is not null)
                {
                    var comparableValues = ProjectComparableValues(tablePlan, currentValues.Value);
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddPreservedRow(new MergePreservedRow(currentValues.Value, OriginalOrdinal: 0));

                    var hiddenRow = new MergeTableRow(currentValues.Value, comparableValues);
                    tableStateBuilders[tablePlan.TableModel.Table].AddComparableMergedRow(hiddenRow);
                    PreserveHiddenRowDescendants(
                        tablePlan,
                        hiddenRow,
                        currentStateProjection,
                        tableStateBuilders,
                        compiledScopeCatalog
                    );
                }

                continue;
            }

            if (requestScopeState is null)
            {
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"Profile merge encountered scope '{jsonScope}' with no RequestScopeState. "
                        + "This indicates a Core-backend contract gap.",
                ]);
            }

            if (requestScopeState is { Visibility: ProfileVisibilityKind.VisiblePresent })
            {
                // Visible present: classify bindings and overlay hidden values from current state
                var hiddenMemberPaths = HiddenMemberPathVocabulary.ToJsonPathRelative(
                    storedScopeState?.HiddenMemberPaths ?? []
                );
                var (extClearable, extHidden) = GetInlinedScopePaths(tablePlan, inlinedScopePathsByTable);
                var augmentedHidden =
                    extHidden.Length > 0 ? hiddenMemberPaths.AddRange(extHidden) : hiddenMemberPaths;
                var classifications = RelationalWriteBindingClassifier.Classify(
                    tablePlan,
                    augmentedHidden,
                    extClearable,
                    docRefBindings
                );

                var currentValues = isCreate ? null : currentStateProjection.GetCurrentRowValues(tablePlan);

                bool storedRowExists = currentValues is not null;

                // Gate 2: Reject non-creatable scope insert before overlay — overlay
                // requires current values for hidden bindings, which don't exist for
                // a new scope row.
                if (!storedRowExists && requestScopeState is { Creatable: false })
                {
                    return new RelationalWriteMergeSynthesisOutcome.ValidationFailure([
                        new MergeValidationFailure(
                            $"Profile does not allow creating a new scope instance at '{jsonScope}'."
                        ),
                    ]);
                }

                var overlaidValues = OverlayValues(
                    tablePlan,
                    classifications,
                    rewrittenValues,
                    currentValues,
                    parentPhysicalRowIdentityValues
                );
                var (adjustedExtValues, extKeyUnificationFailures) =
                    AdjustKeyUnificationOverlayForHiddenMembers(
                        tablePlan,
                        overlaidValues,
                        currentValues,
                        augmentedHidden
                    );

                if (extKeyUnificationFailures.Length > 0)
                {
                    return new RelationalWriteMergeSynthesisOutcome.ValidationFailure(
                        extKeyUnificationFailures
                    );
                }

                overlaidValues = adjustedExtValues;

                var comparableValues = ProjectComparableValues(tablePlan, overlaidValues);

                if (storedRowExists)
                {
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddUpdate(new MergeRowUpdate(overlaidValues, StableRowIdentityValue: null));
                }
                else
                {
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddInsert(new MergeRowInsert(overlaidValues));
                }

                tableStateBuilders[tablePlan.TableModel.Table]
                    .AddComparableMergedRow(new MergeTableRow(overlaidValues, comparableValues));

                // Process child collections under this extension scope
                var childCollectionFailure = SynthesizeProfileCollectionCandidates(
                    rootExtensionRow.CollectionCandidates,
                    scopePhysicalRowIdentityValues,
                    scopeLookup,
                    profileCollectionLookup,
                    currentStateProjection,
                    tableStateBuilders,
                    isCreate,
                    docRefBindings,
                    inlinedScopePathsByTable,
                    writePlan,
                    compiledScopeCatalog,
                    useLegacyRequestOrderForVisibleRows: useLegacyRequestOrderForVisibleRows,
                    parentJsonScope: jsonScope,
                    visitedScopeKeys: visitedScopeKeys
                );

                if (childCollectionFailure is not null)
                {
                    return childCollectionFailure;
                }
            }
            else if (requestScopeState is { Visibility: ProfileVisibilityKind.VisibleAbsent })
            {
                // Visible absent: delete stored row if it exists
                var currentValues = currentStateProjection.GetCurrentRowValues(tablePlan);

                if (currentValues is not null)
                {
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddDelete(
                            new MergeRowDelete(StableRowIdentityValue: null, Values: currentValues.Value)
                        );
                }

                // No comparable merged row when deleting; child collections are implicitly removed
            }
            else
            {
                // Expected unreachable on a well-formed contract. The flattener only
                // materializes root extension rows when the scope node is in the writable
                // request body, so hidden scopes produce no extension rows, and the
                // branches above cover every valid request-side visibility. The only way
                // to land here is a contract violation where Core emitted a request scope
                // state with a visibility value the merge cannot resolve.
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"Profile merge encountered unexpected state for root extension scope '{jsonScope}': "
                        + $"storedVisibility={storedScopeState?.Visibility}, "
                        + $"requestVisibility={requestScopeState?.Visibility}. "
                        + "This indicates a Core-backend contract violation.",
                ]);
            }
        }

        return null;
    }

    // --- Collection-aligned scope synthesis (profile-aware) ---
    // Structurally similar to SynthesizeRootExtensionRows but differs in scope lookup
    // (instance-disambiguated vs root-level), current-row retrieval (parent-key matching
    // vs first-row), and delete semantics (absence-from-merged vs explicit AddDelete).
    // Kept separate to avoid an abstraction layer that would obscure these differences.

    private static RelationalWriteMergeSynthesisOutcome? SynthesizeProfileAttachedAlignedScopeRows(
        IReadOnlyList<CandidateAttachedAlignedScopeData> attachedAlignedScopeData,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        MergeScopeLookup scopeLookup,
        MergeCollectionLookup profileCollectionLookup,
        MergeCurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders,
        bool isCreate,
        IReadOnlyList<DocumentReferenceBinding> docRefBindings,
        IReadOnlyDictionary<
            DbTableName,
            (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)
        > inlinedScopePathsByTable,
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        bool useLegacyRequestOrderForVisibleRows,
        string ancestorContextKey = "",
        HashSet<string>? visitedScopeKeys = null
    )
    {
        // Precompute table-backed scope set for per-instance inlined scope path lookups.
        // Collection-aligned extension scope tables can have per-instance inlined child
        // visibility, so we must use instance-specific scope lookups when in a collection context.
        HashSet<string>? tableBackedScopes = null;
        if (ancestorContextKey.Length > 0)
        {
            tableBackedScopes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plan in writePlan.TablePlansInDependencyOrder)
            {
                tableBackedScopes.Add(plan.TableModel.JsonScope.Canonical);
            }
        }

        foreach (var alignedScopeData in attachedAlignedScopeData)
        {
            var tablePlan = alignedScopeData.TableWritePlan;
            var jsonScope = tablePlan.TableModel.JsonScope.Canonical;

            // Track this scope instance as visited by buffer iteration
            visitedScopeKeys?.Add($"{jsonScope}|{ancestorContextKey}");

            var requestScopeState = scopeLookup.TryGetRequestScopeStateForInstance(
                jsonScope,
                ancestorContextKey
            );
            var storedScopeState = scopeLookup.TryGetStoredScopeStateForInstance(
                jsonScope,
                ancestorContextKey
            );

            var rewrittenValues = RewriteParentKeyPartValues(
                tablePlan,
                alignedScopeData.Values,
                parentPhysicalRowIdentityValues
            );

            // Hidden scope: preserve current row unchanged and preserve all descendants
            if (storedScopeState is { Visibility: ProfileVisibilityKind.Hidden })
            {
                var currentRow = currentStateProjection.TryMatchAlignedScopeRow(
                    tablePlan,
                    parentPhysicalRowIdentityValues
                );

                if (currentRow is not null)
                {
                    var comparableValues = ProjectComparableValues(tablePlan, currentRow.Values);
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddPreservedRow(new MergePreservedRow(currentRow.Values, OriginalOrdinal: 0));

                    var hiddenRow = new MergeTableRow(currentRow.Values, comparableValues);
                    tableStateBuilders[tablePlan.TableModel.Table].AddComparableMergedRow(hiddenRow);
                    PreserveHiddenRowDescendants(
                        tablePlan,
                        hiddenRow,
                        currentStateProjection,
                        tableStateBuilders,
                        compiledScopeCatalog
                    );
                }

                continue;
            }

            if (requestScopeState is null)
            {
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"Profile merge encountered scope '{jsonScope}' with no RequestScopeState. "
                        + "This indicates a Core-backend contract gap.",
                ]);
            }

            if (requestScopeState is { Visibility: ProfileVisibilityKind.VisiblePresent })
            {
                // Visible present: classify bindings and overlay hidden values from current state
                var hiddenMemberPaths = HiddenMemberPathVocabulary.ToJsonPathRelative(
                    storedScopeState?.HiddenMemberPaths ?? []
                );

                // When inside a collection context, use per-instance inlined scope path
                // computation so that different collection items can have different inlined
                // child visibility (e.g. VisiblePresent for one item, VisibleAbsent for another).
                ImmutableArray<string> scopeClearable;
                ImmutableArray<string> scopeHidden;
                if (tableBackedScopes is not null)
                {
                    var (clearable, hidden, mismatch) = CollectInlinedScopePathsForCollectionInstance(
                        tablePlan,
                        tableBackedScopes,
                        scopeLookup,
                        compiledScopeCatalog,
                        ancestorContextKey
                    );
                    if (mismatch is not null)
                    {
                        return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([mismatch]);
                    }
                    scopeClearable = clearable;
                    scopeHidden = hidden;
                }
                else
                {
                    (scopeClearable, scopeHidden) = GetInlinedScopePaths(tablePlan, inlinedScopePathsByTable);
                }

                var augmentedScopeHidden =
                    scopeHidden.Length > 0 ? hiddenMemberPaths.AddRange(scopeHidden) : hiddenMemberPaths;
                var classifications = RelationalWriteBindingClassifier.Classify(
                    tablePlan,
                    augmentedScopeHidden,
                    scopeClearable,
                    docRefBindings
                );

                var currentRow = currentStateProjection.TryMatchAlignedScopeRow(
                    tablePlan,
                    parentPhysicalRowIdentityValues
                );

                // Reject non-creatable scope insert before overlay — overlay requires
                // current values for hidden bindings, which don't exist for a new scope row.
                if (currentRow is null && requestScopeState is { Creatable: false })
                {
                    return new RelationalWriteMergeSynthesisOutcome.ValidationFailure([
                        new MergeValidationFailure(
                            $"Profile does not allow creating a new scope instance at '{jsonScope}'."
                        ),
                    ]);
                }

                var overlaidValues = OverlayValues(
                    tablePlan,
                    classifications,
                    rewrittenValues,
                    currentRow?.Values,
                    parentPhysicalRowIdentityValues
                );
                var (adjustedAlignedValues, alignedKeyUnificationFailures) =
                    AdjustKeyUnificationOverlayForHiddenMembers(
                        tablePlan,
                        overlaidValues,
                        currentRow?.Values,
                        augmentedScopeHidden
                    );

                if (alignedKeyUnificationFailures.Length > 0)
                {
                    return new RelationalWriteMergeSynthesisOutcome.ValidationFailure(
                        alignedKeyUnificationFailures
                    );
                }

                overlaidValues = adjustedAlignedValues;

                var comparableValues = ProjectComparableValues(tablePlan, overlaidValues);

                if (currentRow is not null)
                {
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddUpdate(new MergeRowUpdate(overlaidValues, StableRowIdentityValue: null));
                }
                else
                {
                    tableStateBuilders[tablePlan.TableModel.Table]
                        .AddInsert(new MergeRowInsert(overlaidValues));
                }

                tableStateBuilders[tablePlan.TableModel.Table]
                    .AddComparableMergedRow(new MergeTableRow(overlaidValues, comparableValues));

                var scopePhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(
                    tablePlan,
                    overlaidValues
                );

                var alignedCollectionFailure = SynthesizeProfileCollectionCandidates(
                    alignedScopeData.CollectionCandidates,
                    scopePhysicalRowIdentityValues,
                    scopeLookup,
                    profileCollectionLookup,
                    currentStateProjection,
                    tableStateBuilders,
                    isCreate,
                    docRefBindings,
                    inlinedScopePathsByTable,
                    writePlan,
                    compiledScopeCatalog,
                    useLegacyRequestOrderForVisibleRows: useLegacyRequestOrderForVisibleRows,
                    parentJsonScope: jsonScope,
                    ancestorContextKey: ancestorContextKey,
                    visitedScopeKeys: visitedScopeKeys
                );

                if (alignedCollectionFailure is not null)
                {
                    return alignedCollectionFailure;
                }
            }
            else if (requestScopeState is { Visibility: ProfileVisibilityKind.VisibleAbsent })
            {
                // Visible absent: deletion is handled by the persister's
                // DeleteCollectionAlignedScopeRowsByPhysicalIdentityAsync, which diffs
                // ComparableCurrentRowset vs ComparableMergedRowset. No explicit AddDelete
                // is needed — the absence of a merged row for this scope drives the delete.
            }
            else
            {
                // Expected unreachable on a well-formed contract. The flattener only
                // materializes aligned scope rows when the scope node is in the writable
                // request body, so hidden scopes produce no aligned scope data, and the
                // branches above cover every valid request-side visibility. The only way
                // to land here is a contract violation where Core emitted a request scope
                // state with a visibility value the merge cannot resolve.
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"Profile merge encountered unexpected state for aligned scope '{jsonScope}': "
                        + $"storedVisibility={storedScopeState?.Visibility}, "
                        + $"requestVisibility={requestScopeState.Visibility}. "
                        + "This indicates a Core-backend contract violation.",
                ]);
            }
        }

        return null;
    }

    /// <summary>
    /// Profile-aware collection merge. For each collection table scope instance, partitions current
    /// rows into visible/hidden, matches visible rows to request candidates by semantic identity,
    /// and produces update/insert/delete/preserve actions with deterministic ordinal recomputation.
    /// </summary>
    private static RelationalWriteMergeSynthesisOutcome? SynthesizeProfileCollectionCandidates(
        IReadOnlyList<CollectionWriteCandidate> collectionCandidates,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        MergeScopeLookup scopeLookup,
        MergeCollectionLookup profileCollectionLookup,
        MergeCurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders,
        bool isCreate,
        IReadOnlyList<DocumentReferenceBinding> docRefBindings,
        IReadOnlyDictionary<
            DbTableName,
            (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)
        > inlinedScopePathsByTable,
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        bool useLegacyRequestOrderForVisibleRows,
        string parentJsonScope = "$",
        string ancestorContextKey = "",
        HashSet<string>? visitedScopeKeys = null
    )
    {
        // Group collection candidates by table (same JsonScope + same parent = same scope instance)
        var candidatesByTable = new Dictionary<DbTableName, List<CollectionWriteCandidate>>();

        foreach (var candidate in collectionCandidates)
        {
            var tableName = candidate.TableWritePlan.TableModel.Table;

            if (!candidatesByTable.TryGetValue(tableName, out var list))
            {
                list = [];
                candidatesByTable[tableName] = list;
            }

            list.Add(candidate);
        }

        // Build the set of collection scopes that are compiled immediate children of the
        // current parent scope. This replaces the column-count heuristic, which could
        // incorrectly include sibling collection tables when literal CollectionItemIds collide.
        var childCollectionScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in compiledScopeCatalog)
        {
            if (
                scope.ScopeKind == ScopeKind.Collection
                && string.Equals(scope.ImmediateParentJsonScope, parentJsonScope, StringComparison.Ordinal)
            )
            {
                childCollectionScopes.Add(scope.JsonScope);
            }
        }

        // Process each collection table scope instance
        // Also discover collection tables that have current rows but zero request candidates
        var allCollectionTableNames = new HashSet<DbTableName>(candidatesByTable.Keys);

        foreach (
            var tableWritePlan in tableStateBuilders
                .Values.Select(b => b.TableWritePlan)
                .Where(twp => twp.CollectionMergePlan is not null)
        )
        {
            // Only include tables that are compiled immediate children of this parent scope
            if (!childCollectionScopes.Contains(tableWritePlan.TableModel.JsonScope.Canonical))
            {
                continue;
            }

            // Only include tables that have current rows under this parent
            var currentRows = currentStateProjection.GetCurrentRowsForParent(
                tableWritePlan,
                parentPhysicalRowIdentityValues
            );

            if (currentRows.Count > 0)
            {
                allCollectionTableNames.Add(tableWritePlan.TableModel.Table);
            }
        }

        foreach (var tableName in allCollectionTableNames)
        {
            var candidates = candidatesByTable.TryGetValue(tableName, out var list) ? list : [];

            if (candidates.Count == 0 && isCreate)
            {
                // No candidates and no current state on create - nothing to do
                continue;
            }

            // Need the table write plan from the first candidate, or look it up from the builders
            var tablePlan =
                candidates.Count > 0
                    ? candidates[0].TableWritePlan
                    : tableStateBuilders[tableName].TableWritePlan;

            var scopeInstanceFailure = SynthesizeProfileCollectionScopeInstance(
                tablePlan,
                candidates,
                parentPhysicalRowIdentityValues,
                scopeLookup,
                profileCollectionLookup,
                currentStateProjection,
                tableStateBuilders,
                isCreate,
                docRefBindings,
                inlinedScopePathsByTable,
                writePlan,
                compiledScopeCatalog,
                useLegacyRequestOrderForVisibleRows,
                ancestorContextKey,
                visitedScopeKeys
            );

            if (scopeInstanceFailure is not null)
            {
                return scopeInstanceFailure;
            }
        }

        return null;
    }

    /// <summary>
    /// Implements the profile-aware collection merge for a single scope instance.
    /// Collects all merge actions, computes ordinals, then emits to the builder.
    /// </summary>
    private static RelationalWriteMergeSynthesisOutcome? SynthesizeProfileCollectionScopeInstance(
        TableWritePlan tablePlan,
        IReadOnlyList<CollectionWriteCandidate> requestCandidates,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues,
        MergeScopeLookup scopeLookup,
        MergeCollectionLookup profileCollectionLookup,
        MergeCurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders,
        bool isCreate,
        IReadOnlyList<DocumentReferenceBinding> docRefBindings,
        IReadOnlyDictionary<
            DbTableName,
            (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)
        > inlinedScopePathsByTable,
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        bool useLegacyRequestOrderForVisibleRows,
        string ancestorContextKey = "",
        HashSet<string>? visitedScopeKeys = null
    )
    {
        var mergePlan =
            tablePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tablePlan)}' does not have a compiled collection merge plan."
            );

        var jsonScope = tablePlan.TableModel.JsonScope.Canonical;

        // If the entire collection scope is hidden, preserve all current rows and skip merge
        var collectionStoredScopeState = scopeLookup.TryGetStoredScopeStateForInstance(
            jsonScope,
            ancestorContextKey
        );
        if (collectionStoredScopeState is { Visibility: ProfileVisibilityKind.Hidden })
        {
            var hiddenScopeCurrentRows = isCreate
                ? []
                : currentStateProjection.GetCurrentRowsForParent(tablePlan, parentPhysicalRowIdentityValues);

            var builder = tableStateBuilders[tablePlan.TableModel.Table];
            for (var i = 0; i < hiddenScopeCurrentRows.Count; i++)
            {
                var currentRow = hiddenScopeCurrentRows[i];
                var ordinal = ExtractOrdinalFromRow(currentRow, mergePlan);
                builder.AddPreservedRow(new MergePreservedRow(currentRow.Values, ordinal));
                var comparableValues = ProjectComparableValues(tablePlan, currentRow.Values);
                var hiddenRow = new MergeTableRow(currentRow.Values, comparableValues);
                builder.AddComparableMergedRow(hiddenRow);

                // Preserve descendant rows (aligned scopes, child collections) under each
                // hidden collection row — consistent with root extension and aligned scope
                // hidden handling.
                PreserveHiddenRowDescendants(
                    tablePlan,
                    hiddenRow,
                    currentStateProjection,
                    tableStateBuilders,
                    compiledScopeCatalog
                );
            }

            return null;
        }

        // Step 1: Gather inputs — current rows, visible stored rows, and visible request items
        var allCurrentRows = isCreate
            ? []
            : currentStateProjection.GetCurrentRowsForParent(tablePlan, parentPhysicalRowIdentityValues);

        var visibleStoredRows = profileCollectionLookup.GetVisibleStoredRows(jsonScope, ancestorContextKey);

        var visibleRequestItems = profileCollectionLookup.GetVisibleRequestItems(
            jsonScope,
            ancestorContextKey
        );

        if (visibleRequestItems.Count == 0 && requestCandidates.Count > 0)
        {
            return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                $"Profile merge for collection scope '{jsonScope}' has {requestCandidates.Count} "
                    + "request candidate(s) but zero VisibleRequestCollectionItems. "
                    + "Core must emit a VisibleRequestCollectionItem for each visible request collection row.",
            ]);
        }

        // Step 2: Partition current rows into visible vs hidden
        var visibleCurrentRows =
            new List<(MergeTableRow Row, int CurrentIndex, VisibleStoredCollectionRow StoredRow)>();
        var hiddenCurrentRows = new List<(MergeTableRow Row, int CurrentIndex)>();

        for (var i = 0; i < allCurrentRows.Count; i++)
        {
            var currentRow = allCurrentRows[i];
            var matchedStoredRow = TryMatchCurrentRowToVisibleStored(
                tablePlan,
                mergePlan,
                currentRow,
                visibleStoredRows
            );

            if (matchedStoredRow is not null)
            {
                visibleCurrentRows.Add((currentRow, i, matchedStoredRow));
            }
            else
            {
                hiddenCurrentRows.Add((currentRow, i));
            }
        }

        // Step 3: Match visible stored rows to request candidates by semantic identity
        var matchedPairs =
            new List<(
                MergeTableRow CurrentRow,
                int CurrentIndex,
                CollectionWriteCandidate Candidate,
                VisibleStoredCollectionRow StoredRow
            )>();
        var unmatchedVisibleCurrentRows = new List<(MergeTableRow Row, int CurrentIndex)>();

        // Build a lookup from candidate semantic identity to candidate (only visible request candidates)
        var candidatesByIdentity = new Dictionary<string, CollectionWriteCandidate>();

        foreach (var candidate in requestCandidates)
        {
            var isVisibleRequest = TryMatchCandidateToVisibleRequest(
                mergePlan,
                candidate,
                visibleRequestItems
            );

            if (isVisibleRequest)
            {
                var identityKey = BuildSemanticIdentityKeyString(
                    candidate.SemanticIdentityJsonValues,
                    candidate.SemanticIdentityPresenceFlags
                );
                if (!candidatesByIdentity.TryAdd(identityKey, candidate))
                {
                    return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                        $"Profile merge for collection scope '{jsonScope}' received duplicate visible "
                            + $"request candidates with semantic identity '{identityKey}'. "
                            + "Core must reject duplicate collection items before they reach the backend.",
                    ]);
                }
            }
        }

        // Validate 1:1 coverage in BOTH directions.
        //
        // Forward: every request candidate must match a VisibleRequestCollectionItem.
        // Missing forward coverage means Core emitted a candidate visible-row but no
        // corresponding visible item — the candidate would be silently ignored.
        if (candidatesByIdentity.Count != requestCandidates.Count)
        {
            var unmatchedCount = requestCandidates.Count - candidatesByIdentity.Count;
            return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                $"Profile merge for collection scope '{jsonScope}' has {requestCandidates.Count} "
                    + $"request candidate(s) but only {candidatesByIdentity.Count} matched "
                    + $"VisibleRequestCollectionItems ({unmatchedCount} unmatched). "
                    + "Core must emit a VisibleRequestCollectionItem for each visible request collection row.",
            ]);
        }

        // Reverse: every VisibleRequestCollectionItem must have a matching candidate by
        // semantic identity. Missing reverse coverage means the backend flattener dropped
        // a candidate or Core emitted an orphan visible item — without this check, the
        // current row whose stored identity matches the orphan would fall into
        // unmatchedVisibleCurrentRows and be silently queued for delete.
        //
        // Relies on upstream duplicate rejection: profiled writes go through
        // ProfileWriteContractValidator.ValidateDuplicateCollectionItems; no-profile
        // writes synthesize VisibleRequestCollectionItems 1:1 from flattenedWriteSet in
        // NoProfileSyntheticProfileAdapter.BuildVisibleRequestCollectionItems, and Core's
        // DocumentValidator rejects duplicate collection items in the request body before
        // flattening. Either upstream guarantee can be weakened only by tightening this
        // check to a multiset comparison.
        foreach (var visibleItem in visibleRequestItems)
        {
            var visibleItemKey = BuildSemanticIdentityKeyFromVisibleItem(visibleItem);
            if (!candidatesByIdentity.ContainsKey(visibleItemKey))
            {
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"Profile merge for collection scope '{jsonScope}' has a VisibleRequestCollectionItem "
                        + $"with semantic identity '{visibleItemKey}' that has no matching request candidate. "
                        + "This can originate either from the backend flattener dropping a candidate "
                        + "or from Core emitting an orphan VisibleRequestCollectionItem; without a "
                        + "matching candidate the merge cannot safely classify the current row, and "
                        + "proceeding would queue it for silent delete.",
                ]);
            }
        }

        foreach (var (currentRow, currentIndex, storedRow) in visibleCurrentRows)
        {
            var currentIdentityKey = BuildSemanticIdentityKeyFromRow(
                tablePlan,
                mergePlan,
                currentRow,
                storedRow
            );

            if (candidatesByIdentity.TryGetValue(currentIdentityKey, out var matchedCandidate))
            {
                matchedPairs.Add((currentRow, currentIndex, matchedCandidate, storedRow));
                candidatesByIdentity.Remove(currentIdentityKey);
            }
            else
            {
                unmatchedVisibleCurrentRows.Add((currentRow, currentIndex));
            }
        }

        // Remaining unmatched candidates are new inserts
        var unmatchedCandidates = candidatesByIdentity.Values.ToList();

        // Step 4: Build pre-ordinal merged rows (do NOT emit to builder yet — ordinals not computed)

        // Precompute table-backed scope set once for all per-row inlined scope path lookups
        var tableBackedScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in writePlan.TablePlansInDependencyOrder)
        {
            tableBackedScopes.Add(plan.TableModel.JsonScope.Canonical);
        }

        // Matched updates: overlay hidden values, carry stable row identity
        var mergedUpdates =
            new List<(
                ImmutableArray<FlattenedWriteValue> Values,
                long? StableRowIdentityValue,
                int OriginalCurrentIndex,
                CollectionWriteCandidate Candidate
            )>();

        foreach (var (currentRow, currentIndex, candidate, storedRow) in matchedPairs)
        {
            // Compute per-row inlined scope paths: different collection items can have
            // different inlined scope visibility (e.g. VisiblePresent for one, VisibleAbsent for another).
            var itemAncestorContextKey = ExtendAncestorContextKey(
                ancestorContextKey,
                jsonScope,
                candidate.SemanticIdentityJsonValues,
                candidate.SemanticIdentityPresenceFlags
            );

            var (collClearable, collHidden, collMismatch) = CollectInlinedScopePathsForCollectionInstance(
                tablePlan,
                tableBackedScopes,
                scopeLookup,
                compiledScopeCatalog,
                itemAncestorContextKey
            );

            if (collMismatch is not null)
            {
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([collMismatch]);
            }

            var hiddenMemberPaths = HiddenMemberPathVocabulary.ToJsonPathRelative(
                storedRow.HiddenMemberPaths
            );
            var augmentedCollHidden =
                collHidden.Length > 0 ? hiddenMemberPaths.AddRange(collHidden) : hiddenMemberPaths;
            var classifications = RelationalWriteBindingClassifier.Classify(
                tablePlan,
                augmentedCollHidden,
                collClearable,
                docRefBindings
            );
            RelationalWriteBindingClassifier.ValidateCollectionKeyBinding(tablePlan, classifications);

            var rewrittenValues = RewriteParentKeyPartValues(
                tablePlan,
                candidate.Values,
                parentPhysicalRowIdentityValues
            );

            var overlaidValues = OverlayValues(
                tablePlan,
                classifications,
                rewrittenValues,
                currentRow.Values,
                parentPhysicalRowIdentityValues
            );
            var (adjustedCollValues, collKeyUnificationFailures) =
                AdjustKeyUnificationOverlayForHiddenMembers(
                    tablePlan,
                    overlaidValues,
                    currentRow.Values,
                    augmentedCollHidden
                );

            if (collKeyUnificationFailures.Length > 0)
            {
                return new RelationalWriteMergeSynthesisOutcome.ValidationFailure(collKeyUnificationFailures);
            }

            overlaidValues = adjustedCollValues;

            overlaidValues = RewriteCollectionStableRowIdentity(tablePlan, overlaidValues, currentRow.Values);
            var stableRowIdentityValue = ExtractStableRowIdentityValue(tablePlan, overlaidValues);

            mergedUpdates.Add((overlaidValues, stableRowIdentityValue, currentIndex, candidate));
        }

        // Deletes
        foreach (var (currentRow, _) in unmatchedVisibleCurrentRows)
        {
            var stableRowIdentityValue = ExtractStableRowIdentityValue(tablePlan, currentRow.Values);
            tableStateBuilders[tablePlan.TableModel.Table]
                .AddDelete(new MergeRowDelete(stableRowIdentityValue));
        }

        // New inserts (pre-ordinal)
        var mergedInserts =
            new List<(ImmutableArray<FlattenedWriteValue> Values, CollectionWriteCandidate Candidate)>();

        foreach (var candidate in unmatchedCandidates)
        {
            // Gate 3: Reject non-creatable collection item insert
            var matchedRequestItem = TryFindMatchedVisibleRequestItem(
                mergePlan,
                candidate,
                visibleRequestItems
            );

            if (matchedRequestItem is { Creatable: false })
            {
                return new RelationalWriteMergeSynthesisOutcome.ValidationFailure([
                    new MergeValidationFailure(
                        $"Profile does not allow creating a new collection item at '{jsonScope}'."
                    ),
                ]);
            }

            var rewrittenValues = RewriteParentKeyPartValues(
                tablePlan,
                candidate.Values,
                parentPhysicalRowIdentityValues
            );

            // Compute per-row inlined scope paths for new inserts: clearable columns
            // from VisibleAbsent inlined scopes must be nulled out.
            var insertAncestorContextKey = ExtendAncestorContextKey(
                ancestorContextKey,
                jsonScope,
                candidate.SemanticIdentityJsonValues,
                candidate.SemanticIdentityPresenceFlags
            );

            // Hidden paths are irrelevant for inserts — there is no stored row to preserve from
            var (insertClearable, _, insertMismatch) = CollectInlinedScopePathsForCollectionInstance(
                tablePlan,
                tableBackedScopes,
                scopeLookup,
                compiledScopeCatalog,
                insertAncestorContextKey
            );

            if (insertMismatch is not null)
            {
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([insertMismatch]);
            }

            if (insertClearable.Length > 0)
            {
                var insertClassifications = RelationalWriteBindingClassifier.Classify(
                    tablePlan,
                    [],
                    insertClearable,
                    docRefBindings
                );

                rewrittenValues = ApplyClearableToInsert(insertClassifications, rewrittenValues);
            }

            mergedInserts.Add((rewrittenValues, candidate));
        }

        // Step 5: Ordinal recomputation — build interleaved sequence, then emit to builder
        List<CollectionInterleavedEntry> interleavedEntries;

        if (useLegacyRequestOrderForVisibleRows && hiddenCurrentRows.Count == 0)
        {
            interleavedEntries =
            [
                .. mergedUpdates
                    .Select(update =>
                        (CollectionInterleavedEntry)
                            new CollectionInterleavedEntry.Update(
                                update.Values,
                                update.StableRowIdentityValue,
                                update.Candidate
                            )
                    )
                    .Concat(
                        mergedInserts.Select(insert =>
                            (CollectionInterleavedEntry)
                                new CollectionInterleavedEntry.Insert(insert.Values, insert.Candidate)
                        )
                    )
                    .OrderBy(GetRequestOrder),
            ];
        }
        else
        {
            var visibleCurrentIndexSet = new HashSet<int>(visibleCurrentRows.Select(v => v.CurrentIndex));
            var deletedVisibleIndexSet = new HashSet<int>(
                unmatchedVisibleCurrentRows.Select(v => v.CurrentIndex)
            );
            var hiddenIndexSet = new HashSet<int>(hiddenCurrentRows.Select(h => h.CurrentIndex));

            // Order current rows by existing ordinal
            var orderedCurrentIndexes = Enumerable
                .Range(0, allCurrentRows.Count)
                .OrderBy(i => ExtractOrdinalFromRow(allCurrentRows[i], mergePlan))
                .ToList();

            // Matched updates in request order
            var orderedUpdates = mergedUpdates.OrderBy(u => u.Candidate.RequestOrder).ToList();
            var orderedInserts = mergedInserts.OrderBy(ins => ins.Candidate.RequestOrder).ToList();

            // Build the interleaved sequence
            interleavedEntries = [];
            var updateCursor = 0;

            // Tracks the index in the interleaved list where new inserts should be placed.
            // For surviving visible rows this is one past the just-added entry; for deleted
            // visible rows it is the current Count (where the entry would have been). This
            // ensures hidden rows that follow deleted visible rows keep their relative gaps.
            int visibleInsertPoint = -1;

            foreach (var currentIndex in orderedCurrentIndexes)
            {
                if (hiddenIndexSet.Contains(currentIndex))
                {
                    // Hidden row: preserve in place
                    interleavedEntries.Add(
                        new CollectionInterleavedEntry.Hidden(allCurrentRows[currentIndex].Values)
                    );
                }
                else if (visibleCurrentIndexSet.Contains(currentIndex))
                {
                    // Surviving visible position: substitute next matched update
                    if (!deletedVisibleIndexSet.Contains(currentIndex) && updateCursor < orderedUpdates.Count)
                    {
                        var update = orderedUpdates[updateCursor];
                        interleavedEntries.Add(
                            new CollectionInterleavedEntry.Update(
                                update.Values,
                                update.StableRowIdentityValue,
                                update.Candidate
                            )
                        );
                        updateCursor++;
                    }

                    // Track insert point for ALL originally-visible rows (surviving and deleted)
                    visibleInsertPoint = interleavedEntries.Count;
                }
            }

            // Append remaining updates beyond surviving visible positions
            while (updateCursor < orderedUpdates.Count)
            {
                var update = orderedUpdates[updateCursor];
                interleavedEntries.Add(
                    new CollectionInterleavedEntry.Update(
                        update.Values,
                        update.StableRowIdentityValue,
                        update.Candidate
                    )
                );
                updateCursor++;
                visibleInsertPoint = interleavedEntries.Count;
            }

            // Place new inserts after the last originally-visible row's position
            if (visibleInsertPoint >= 0)
            {
                var insertPosition = visibleInsertPoint;

                foreach (var insert in orderedInserts)
                {
                    interleavedEntries.Insert(
                        insertPosition,
                        new CollectionInterleavedEntry.Insert(insert.Values, insert.Candidate)
                    );
                    insertPosition++;
                }
            }
            else if (orderedInserts.Count > 0)
            {
                // No previously-visible rows: append at the end
                foreach (var insert in orderedInserts)
                {
                    interleavedEntries.Add(
                        new CollectionInterleavedEntry.Insert(insert.Values, insert.Candidate)
                    );
                }
            }
        }

        // Renumber ordinals contiguously and emit to builder
        var finalMergedRows =
            new List<(
                ImmutableArray<FlattenedWriteValue> Values,
                bool IsInsert,
                CollectionWriteCandidate? Candidate
            )>();

        for (var ordinal = 0; ordinal < interleavedEntries.Count; ordinal++)
        {
            var entry = interleavedEntries[ordinal];
            var reorderedValues = SetOrdinalValue(entry.Values, mergePlan.OrdinalBindingIndex, ordinal);
            var comparableValues = ProjectComparableValues(tablePlan, reorderedValues);
            var builder = tableStateBuilders[tablePlan.TableModel.Table];

            if (entry is CollectionInterleavedEntry.Hidden hidden)
            {
                var originalOrdinal = ExtractOrdinalFromRow(new MergeTableRow(hidden.Values, []), mergePlan);

                if (ordinal != originalOrdinal)
                {
                    // Hidden row's ordinal changed due to visible insert/delete — emit as an
                    // update so the DB stays contiguous and the ordinal uniqueness constraint
                    // is not violated.
                    var stableRowIdentityValue = ExtractStableRowIdentityValue(tablePlan, reorderedValues);
                    builder.AddUpdate(new MergeRowUpdate(reorderedValues, stableRowIdentityValue));
                }
                else
                {
                    builder.AddPreservedRow(new MergePreservedRow(reorderedValues, originalOrdinal));
                }

                builder.AddComparableMergedRow(new MergeTableRow(reorderedValues, comparableValues));
            }
            else if (entry is CollectionInterleavedEntry.Update update)
            {
                builder.AddUpdate(new MergeRowUpdate(reorderedValues, update.StableRowIdentityValue));
                builder.AddComparableMergedRow(new MergeTableRow(reorderedValues, comparableValues));
                finalMergedRows.Add((reorderedValues, false, update.Candidate));
            }
            else if (entry is CollectionInterleavedEntry.Insert insert)
            {
                builder.AddInsert(new MergeRowInsert(reorderedValues));
                builder.AddComparableMergedRow(new MergeTableRow(reorderedValues, comparableValues));
                finalMergedRows.Add((reorderedValues, true, insert.Candidate));
            }
        }

        // Step 6: Recursion — process children of matched updates and new inserts
        foreach (var (values, rowIsInsert, candidate) in finalMergedRows)
        {
            if (candidate is null)
            {
                continue;
            }

            var collectionPhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(tablePlan, values);

            // Build the ancestor context key for child scopes under this collection item
            var childAncestorContextKey = ExtendAncestorContextKey(
                ancestorContextKey,
                jsonScope,
                candidate.SemanticIdentityJsonValues,
                candidate.SemanticIdentityPresenceFlags
            );

            var alignedScopeFailure = SynthesizeProfileAttachedAlignedScopeRows(
                candidate.AttachedAlignedScopeData,
                collectionPhysicalRowIdentityValues,
                scopeLookup,
                profileCollectionLookup,
                currentStateProjection,
                tableStateBuilders,
                rowIsInsert,
                docRefBindings,
                inlinedScopePathsByTable,
                writePlan,
                compiledScopeCatalog,
                useLegacyRequestOrderForVisibleRows,
                childAncestorContextKey,
                visitedScopeKeys
            );

            if (alignedScopeFailure is not null)
            {
                return alignedScopeFailure;
            }

            var childCollectionFailure = SynthesizeProfileCollectionCandidates(
                candidate.CollectionCandidates,
                collectionPhysicalRowIdentityValues,
                scopeLookup,
                profileCollectionLookup,
                currentStateProjection,
                tableStateBuilders,
                rowIsInsert,
                docRefBindings,
                inlinedScopePathsByTable,
                writePlan,
                compiledScopeCatalog,
                useLegacyRequestOrderForVisibleRows,
                parentJsonScope: jsonScope,
                ancestorContextKey: childAncestorContextKey,
                visitedScopeKeys: visitedScopeKeys
            );

            if (childCollectionFailure is not null)
            {
                return childCollectionFailure;
            }
        }

        // Step 7: For hidden preserved rows, preserve all descendant table rows
        foreach (var (hiddenRow, _) in hiddenCurrentRows)
        {
            PreserveHiddenRowDescendants(
                tablePlan,
                hiddenRow,
                currentStateProjection,
                tableStateBuilders,
                compiledScopeCatalog
            );
        }

        return null;
    }

    /// <summary>
    /// Reverse contract validation for non-collection separate-table scopes. Every VisiblePresent
    /// RequestScopeState targeting a separate-table non-collection scope must have been visited
    /// by buffer iteration. If not, the flattener dropped the buffer row (e.g. empty scope object)
    /// and the scope would silently fall through — preserving stale data on update or skipping
    /// insert/creatability checks on create.
    /// </summary>
    /// <remarks>
    /// Mirrors the collection reverse check (VisibleRequestCollectionItems ↔ candidates).
    /// Inlined scopes are excluded because they share the parent table's buffer row.
    /// Collection scopes are excluded because they have their own reverse validation.
    /// The root scope ($) is excluded because it always has a buffer row.
    /// Scopes whose content in the request body is null or empty are excluded because the
    /// flattener correctly produces no buffer row when there is nothing to write (e.g. all
    /// visible fields are hidden by the profile, so the client sends <c>{}</c>).
    /// </remarks>
    private static RelationalWriteMergeSynthesisOutcome? ValidateNonCollectionScopeReverseCoverage(
        ImmutableArray<RequestScopeState> requestScopeStates,
        JsonNode writableRequestBody,
        ResourceWritePlan writePlan,
        HashSet<string> visitedScopeKeys
    )
    {
        var tablePlansByJsonScope = writePlan.TablePlansInDependencyOrder.ToDictionary(
            plan => plan.TableModel.JsonScope.Canonical,
            StringComparer.Ordinal
        );

        foreach (var requestState in requestScopeStates)
        {
            if (requestState.Visibility != ProfileVisibilityKind.VisiblePresent)
            {
                continue;
            }

            // Skip the root scope — always processed by the main synthesis path
            if (requestState.Address.JsonScope == "$")
            {
                continue;
            }

            // Skip scopes nested under collections (collection-aligned extensions, scopes
            // under collection items). Their buffer visitation is tracked per-collection-item
            // via attached scope data, not in the root-level visitedScopeKeys set.
            if (!requestState.Address.AncestorCollectionInstances.IsEmpty)
            {
                continue;
            }

            // Only validate separate-table scopes (those with their own table plan)
            if (!tablePlansByJsonScope.TryGetValue(requestState.Address.JsonScope, out var tablePlan))
            {
                continue;
            }

            // Skip collection scopes — they have their own reverse validation
            if (tablePlan.CollectionMergePlan is not null)
            {
                continue;
            }

            // Build the same scope key format used by buffer iteration tracking
            var ancestorKey = AncestorKeyHelpers.BuildAncestorKeyFromScopeInstanceAddress(
                requestState.Address
            );
            var scopeKey = $"{requestState.Address.JsonScope}|{ancestorKey}";

            if (!visitedScopeKeys.Contains(scopeKey))
            {
                // If the scope content in the request body is null or an empty object, the
                // flattener correctly produced no buffer row — there is nothing to write.
                // This happens when all visible fields are hidden by the profile (client
                // sends {} to acknowledge the scope without providing data).
                if (IsScopeContentEmptyInRequestBody(writableRequestBody, requestState.Address.JsonScope))
                {
                    continue;
                }

                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"Profile merge encountered VisiblePresent RequestScopeState for "
                        + $"separate-table scope '{requestState.Address.JsonScope}' "
                        + "but buffer iteration did not visit it. The flattener must produce "
                        + "a buffer row for every VisiblePresent separate-table scope; an empty "
                        + "scope object may have been incorrectly dropped.",
                ]);
            }
        }

        return null;
    }

    /// <summary>
    /// Navigates the request body to the given JSON scope path and returns true if the
    /// scope content is null or an empty object. Scope paths use dot-separated segments
    /// starting with "$" (e.g. "$._ext.sample").
    /// </summary>
    private static bool IsScopeContentEmptyInRequestBody(JsonNode requestBody, string jsonScope)
    {
        var segments = jsonScope.Split('.');
        JsonNode? current = requestBody;

        // Skip the first segment ("$" = root) and navigate to the scope
        for (int i = 1; i < segments.Length && current is not null; i++)
        {
            current = current[segments[i]];
        }

        return current is null || (current is JsonObject obj && obj.Count == 0);
    }

    /// <summary>
    /// Second pass over StoredScopeStates: handles separate-table scopes that were not visited
    /// by buffer iteration (because the flattener dropped them from the profiled selected body).
    /// VisiblePresent stored scopes not visited by buffer iteration are deleted (the request
    /// omitted a visible scope that has stored data). Hidden scopes preserve current rows and
    /// descendants.
    /// </summary>
    /// <remarks>
    /// This closes a profile-mode gap: when a scope is present in current state but omitted from
    /// the request body, the flattener produces no buffer entry for it, so the merge's normal
    /// buffer-iteration branches never trigger. The second pass walks StoredScopeStates and
    /// applies mode-dependent visibility logic:
    ///
    /// Real profile mode:
    ///   - VisiblePresent (not in visitedScopeKeys): emits deletes. Core classifies existing
    ///     stored scopes as VisiblePresent (ClassifyScope with non-null scopeData). When buffer
    ///     iteration did not visit the scope, the request omitted it — delete per the design's
    ///     "request VisibleAbsent + stored visible existence → Delete" rule.
    ///   - Hidden: preserves current rows and all descendants.
    ///   - VisibleAbsent: skipped (no data in database, nothing to delete or preserve).
    ///
    /// Null-profile mode:
    ///   - VisibleAbsent: emits deletes. The null-profile adapter uses VisibleAbsent to signal
    ///     "the request omitted this scope" (a request-side semantic, not a stored-data semantic).
    ///   - Hidden: preserves current rows and all descendants.
    ///   - VisiblePresent: skipped (handled by buffer iteration).
    ///
    /// Shortcut: only separate-table scopes are handled here. Inlined-scope VisibleAbsent
    /// handling remains in the buffer iteration path. Hidden collection scopes under visible
    /// parents are handled by SynthesizeProfileCollectionCandidates' current-state discovery.
    /// </remarks>
    private static RelationalWriteMergeSynthesisOutcome? ApplyStoredScopeStatesSecondPass(
        ImmutableArray<StoredScopeState> storedScopeStates,
        MergeCurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders,
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        HashSet<string> visitedScopeKeys,
        bool isNullProfile
    )
    {
        var tablePlansByJsonScope = writePlan.TablePlansInDependencyOrder.ToDictionary(
            plan => plan.TableModel.JsonScope.Canonical,
            StringComparer.Ordinal
        );

        foreach (var storedState in storedScopeStates)
        {
            // Mode-dependent visibility filter:
            // - Null-profile: the adapter uses VisibleAbsent to signal "request omitted this
            //   scope" (a request-side semantic). Process VisibleAbsent for delete, skip
            //   VisiblePresent (handled by buffer iteration).
            // - Real profile: Core classifies existing stored scopes as VisiblePresent (non-null
            //   scopeData) and absent scopes as VisibleAbsent (null scopeData). Skip VisibleAbsent
            //   (no data to delete/preserve). Process VisiblePresent not in buffer for delete.
            if (isNullProfile)
            {
                if (
                    storedState.Visibility != ProfileVisibilityKind.VisibleAbsent
                    && storedState.Visibility != ProfileVisibilityKind.Hidden
                )
                {
                    continue;
                }
            }
            else
            {
                if (storedState.Visibility == ProfileVisibilityKind.VisibleAbsent)
                {
                    continue;
                }
            }

            // Skip the root scope — always handled by the main synthesis path
            if (storedState.Address.JsonScope == "$")
            {
                continue;
            }

            // Compute the canonical scope key using the same format as buffer iteration tracking
            var ancestorKey = AncestorKeyHelpers.BuildAncestorKeyFromScopeInstanceAddress(
                storedState.Address
            );
            var scopeKey = $"{storedState.Address.JsonScope}|{ancestorKey}";

            // Skip if buffer iteration already handled this scope
            if (visitedScopeKeys.Contains(scopeKey))
            {
                continue;
            }

            // Find the matching table plan — separate-table scopes have their own TableWritePlan
            var matchedTablePlan = tablePlansByJsonScope.GetValueOrDefault(storedState.Address.JsonScope);

            if (matchedTablePlan is null)
            {
                // No table plan means this is an inlined scope — skip (shortcut: separate-table only).
                // Verify via the compiled scope catalog that this is a known scope.
                var isKnownScope = compiledScopeCatalog.Any(descriptor =>
                    string.Equals(
                        descriptor.JsonScope,
                        storedState.Address.JsonScope,
                        StringComparison.Ordinal
                    )
                );

                if (!isKnownScope && compiledScopeCatalog.Count > 0)
                {
                    return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                        $"StoredScopeStates second pass encountered scope "
                            + $"'{storedState.Address.JsonScope}' which is not present in the "
                            + "CompiledScopeCatalog. This indicates a Core-backend contract gap.",
                    ]);
                }

                // Known inlined scope — handled by buffer iteration only, not the second pass.
                continue;
            }

            // Collection scopes are not handled by the second pass — they have their own
            // deletion mechanism via ComparableCurrentRowset vs ComparableMergedRowset diffing.
            if (matchedTablePlan.CollectionMergePlan is not null)
            {
                continue;
            }

            var tableStateBuilder = tableStateBuilders[matchedTablePlan.TableModel.Table];
            IReadOnlyList<MergeTableRow> scopeCurrentRows;

            if (
                RelationalWriteMergeShared.IsCollectionAlignedExtensionScope(matchedTablePlan)
                && storedState.Address.AncestorCollectionInstances.IsEmpty
            )
            {
                return new RelationalWriteMergeSynthesisOutcome.ContractMismatch([
                    $"StoredScopeStates second pass received collection-aligned scope "
                        + $"'{storedState.Address.JsonScope}' without ancestor collection instances. "
                        + "Core must emit per-instance StoredScopeState addresses for collection-aligned scopes; "
                        + "backend will not apply a scope-wide fallback.",
                ]);
            }

            var currentRow = currentStateProjection.TryMatchScopeInstanceRow(
                matchedTablePlan,
                storedState.Address,
                tablePlansByJsonScope
            );

            if (currentRow is null)
            {
                continue;
            }

            scopeCurrentRows = [currentRow];

            if (RelationalWriteMergeShared.IsCollectionAlignedExtensionScope(matchedTablePlan))
            {
                scopeCurrentRows = scopeCurrentRows
                    .Where(row => !tableStateBuilder.HasComparableMergedRowWithPhysicalIdentity(row.Values))
                    .ToList();

                if (scopeCurrentRows.Count == 0)
                {
                    continue;
                }
            }

            if (
                storedState.Visibility == ProfileVisibilityKind.Hidden
                || storedState.HiddenMemberPaths.Length > 0
            )
            {
                // Hidden scope or scope with hidden members: preserve the current row and
                // all descendant rows so that hidden data is not lost and guarded no-op row
                // counts remain correct. A VisiblePresent scope with HiddenMemberPaths (e.g.
                // an extension where some fields are hidden by the profile) must be preserved
                // even when buffer iteration did not visit it — deleting would lose the
                // hidden member data.
                foreach (var values in scopeCurrentRows.Select(static currentRow => currentRow.Values))
                {
                    var comparableValues = ProjectComparableValues(matchedTablePlan, values);
                    tableStateBuilder.AddPreservedRow(new MergePreservedRow(values, OriginalOrdinal: 0));
                    var hiddenRow = new MergeTableRow(values, comparableValues);
                    tableStateBuilder.AddComparableMergedRow(hiddenRow);
                    PreserveHiddenRowDescendants(
                        matchedTablePlan,
                        hiddenRow,
                        currentStateProjection,
                        tableStateBuilders,
                        compiledScopeCatalog
                    );
                }
            }
            else
            {
                // Delete: either VisibleAbsent (null-profile adapter signals request omitted
                // the scope) or VisiblePresent not visited by buffer (real profile — the
                // request omitted a visible scope that has stored data and no hidden members).
                foreach (var values in scopeCurrentRows.Select(static currentRow => currentRow.Values))
                {
                    tableStateBuilder.AddDelete(
                        new MergeRowDelete(StableRowIdentityValue: null, Values: values)
                    );
                }
                // No comparable merged row when deleting — child collections are implicitly removed
            }
        }

        return null;
    }

    /// <summary>
    /// Discriminated union for interleaved collection row entries during ordinal recomputation.
    /// </summary>
    private abstract record CollectionInterleavedEntry(ImmutableArray<FlattenedWriteValue> Values)
    {
        public sealed record Hidden(ImmutableArray<FlattenedWriteValue> Values)
            : CollectionInterleavedEntry(Values);

        public sealed record Update(
            ImmutableArray<FlattenedWriteValue> Values,
            long? StableRowIdentityValue,
            CollectionWriteCandidate Candidate
        ) : CollectionInterleavedEntry(Values);

        public sealed record Insert(
            ImmutableArray<FlattenedWriteValue> Values,
            CollectionWriteCandidate Candidate
        ) : CollectionInterleavedEntry(Values);
    }

    private static int ExtractOrdinalFromRow(MergeTableRow row, CollectionMergePlan mergePlan)
    {
        var ordinalValue = row.Values[mergePlan.OrdinalBindingIndex];

        if (ordinalValue is FlattenedWriteValue.Literal { Value: int intValue })
        {
            return intValue;
        }

        if (ordinalValue is FlattenedWriteValue.Literal { Value: long longValue })
        {
            return (int)longValue;
        }

        throw new InvalidOperationException(
            $"Ordinal binding at index {mergePlan.OrdinalBindingIndex} has unexpected value type "
                + $"'{ordinalValue.GetType().Name}' (expected int or long literal)."
        );
    }

    private static int GetRequestOrder(CollectionInterleavedEntry entry) =>
        entry switch
        {
            CollectionInterleavedEntry.Update update => update.Candidate.RequestOrder,
            CollectionInterleavedEntry.Insert insert => insert.Candidate.RequestOrder,
            _ => int.MaxValue,
        };

    private static ImmutableArray<FlattenedWriteValue> SetOrdinalValue(
        ImmutableArray<FlattenedWriteValue> values,
        int ordinalBindingIndex,
        int ordinal
    )
    {
        FlattenedWriteValue[] result = [.. values];
        result[ordinalBindingIndex] = new FlattenedWriteValue.Literal(ordinal);
        return result.ToImmutableArray();
    }

    /// <summary>
    /// Tries to match a current row to a VisibleStoredCollectionRow by semantic identity.
    /// </summary>
    private static VisibleStoredCollectionRow? TryMatchCurrentRowToVisibleStored(
        TableWritePlan tableWritePlan,
        CollectionMergePlan mergePlan,
        MergeTableRow currentRow,
        IReadOnlyList<VisibleStoredCollectionRow> visibleStoredRows
    )
    {
        foreach (var storedRow in visibleStoredRows)
        {
            if (storedRow.Address.SemanticIdentityInOrder.Length != mergePlan.SemanticIdentityBindings.Length)
            {
                // Defensive: ValidateCollectionRowAddress pre-catches shape mismatches
                // before merge runs, so reaching here is an internal invariant break rather
                // than a Core contract gap. Mirror the throw at BuildSemanticIdentityKeyFromRow
                // instead of silently skipping (which would misclassify the current row as hidden).
                throw new InvalidOperationException(
                    $"VisibleStoredCollectionRow semantic identity length "
                        + $"({storedRow.Address.SemanticIdentityInOrder.Length}) does not match "
                        + $"merge plan binding count ({mergePlan.SemanticIdentityBindings.Length})."
                );
            }

            var match = true;

            for (var i = 0; i < mergePlan.SemanticIdentityBindings.Length; i++)
            {
                var binding = mergePlan.SemanticIdentityBindings[i];
                var currentValue = currentRow.Values[binding.BindingIndex];
                var storedPart = storedRow.Address.SemanticIdentityInOrder[i];
                var scalarType = tableWritePlan.ColumnBindings[binding.BindingIndex].Column.ScalarType;

                if (!CompareSemanticIdentityValue(currentValue, storedPart, scalarType))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return storedRow;
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to match a request candidate to a VisibleRequestCollectionItem by semantic identity.
    /// Returns true if the candidate is visible in the request.
    /// </summary>
    /// <remarks>
    /// Callers guarantee <paramref name="visibleRequestItems"/> is non-empty: the contract
    /// mismatch guard in <see cref="SynthesizeProfileCollectionScopeInstance"/> rejects requests
    /// with candidates but zero visible items before the matching loop runs.
    /// </remarks>
    private static bool TryMatchCandidateToVisibleRequest(
        CollectionMergePlan mergePlan,
        CollectionWriteCandidate candidate,
        IReadOnlyList<VisibleRequestCollectionItem> visibleRequestItems
    )
    {
        return visibleRequestItems.Any(requestItem =>
            MatchesSemanticIdentity(
                mergePlan,
                candidate.SemanticIdentityJsonValues,
                candidate.SemanticIdentityPresenceFlags,
                requestItem
            )
        );
    }

    /// <summary>
    /// Finds the VisibleRequestCollectionItem that matches a candidate by semantic identity.
    /// Returns null if no match is found.
    /// </summary>
    /// <remarks>
    /// Callers guarantee <paramref name="visibleRequestItems"/> is non-empty: only unmatched
    /// candidates reach this method, and candidates only exist when visible items exist (enforced
    /// by the contract mismatch guard in <see cref="SynthesizeProfileCollectionScopeInstance"/>).
    /// </remarks>
    private static VisibleRequestCollectionItem? TryFindMatchedVisibleRequestItem(
        CollectionMergePlan mergePlan,
        CollectionWriteCandidate candidate,
        IReadOnlyList<VisibleRequestCollectionItem> visibleRequestItems
    )
    {
        return visibleRequestItems.FirstOrDefault(requestItem =>
            MatchesSemanticIdentity(
                mergePlan,
                candidate.SemanticIdentityJsonValues,
                candidate.SemanticIdentityPresenceFlags,
                requestItem
            )
        );
    }

    private static bool MatchesSemanticIdentity(
        CollectionMergePlan mergePlan,
        IReadOnlyList<JsonNode?> candidateValues,
        IReadOnlyList<bool> candidatePresenceFlags,
        VisibleRequestCollectionItem requestItem
    )
    {
        if (requestItem.Address.SemanticIdentityInOrder.Length != mergePlan.SemanticIdentityBindings.Length)
        {
            // Defensive: ValidateCollectionRowAddress pre-catches shape mismatches before
            // merge runs. Silently returning false would misclassify a visible candidate as
            // hidden (and the merge would then drop it); throwing surfaces the internal bug.
            throw new InvalidOperationException(
                $"VisibleRequestCollectionItem semantic identity length "
                    + $"({requestItem.Address.SemanticIdentityInOrder.Length}) does not match "
                    + $"merge plan binding count ({mergePlan.SemanticIdentityBindings.Length})."
            );
        }

        for (var i = 0; i < mergePlan.SemanticIdentityBindings.Length; i++)
        {
            if (
                !CompareJsonNodeToSemanticIdentityPart(
                    candidateValues[i],
                    candidatePresenceFlags[i],
                    requestItem.Address.SemanticIdentityInOrder[i]
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a CLR value to a canonical string for semantic identity comparison.
    /// Handles culture-sensitive types (DateOnly, TimeOnly, DateTime) that would otherwise
    /// produce locale-dependent strings via ToString().
    /// </summary>
    /// <summary>
    /// Compares a FlattenedWriteValue from a current row to a SemanticIdentityPart from
    /// a VisibleStoredCollectionRow or VisibleRequestCollectionItem.
    /// </summary>
    /// <remarks>
    /// At the database/flatvalue level, absent and explicit-null are both represented as
    /// SQL NULL, so this comparison cannot distinguish them. The IsPresent distinction is
    /// preserved in ancestor key building (BuildAncestorKey / ExtendAncestorContextKey)
    /// where SemanticIdentityPart instances are available on both sides.
    ///
    /// Contract assumption: semantic identity members MUST NOT reach the merge as
    /// (IsPresent=true, Value=null). This helper is consumed from two call sites, each
    /// gated by a different upstream invariant today:
    /// <list type="bullet">
    ///   <item>
    ///     Request-to-DB collection row matching — called from
    ///     <see cref="TryMatchCurrentRowToVisibleStored"/> to pair
    ///     Core-projected <c>VisibleStoredCollectionRow</c> entries with current DB rows.
    ///     Gated by <c>DocumentReconstituter.EmitScalars</c>
    ///     (src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs:159),
    ///     which omits null scalars during stored-document reconstitution so stored-side
    ///     address derivation does not observe explicit JSON nulls. Request null pruning
    ///     (<c>DocumentValidator.PruneNullData</c> at
    ///     src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs:72)
    ///     is an adjacent invariant that keeps the request-side key built by
    ///     <see cref="BuildSemanticIdentityKeyFromRow"/> consistent with the stored side,
    ///     but the direct current-row comparison at this site is gated by reconstitution.
    ///   </item>
    ///   <item>
    ///     Stored-side ancestor/scope-instance resolution — called from
    ///     <see cref="MatchesAncestorCollectionInstance"/> during
    ///     <c>TryMatchScopeInstanceRow</c> and <c>ApplyStoredScopeStatesSecondPass</c>.
    ///     This path is stored-side only: <c>StoredScopeState</c> addresses and their
    ///     <c>AncestorCollectionInstance</c> chains come from stored-side address
    ///     derivation over reconstituted JSON. Only
    ///     <c>DocumentReconstituter.EmitScalars</c> gates it; request null pruning does
    ///     not apply.
    ///   </item>
    /// </list>
    /// If stored reconstitution ever emits explicit JSON null for an optional scalar (a
    /// future code path or an externally supplied <c>ProfileAppliedWriteContext</c>),
    /// two stored rows could become indistinguishable at this helper — bind-to-first-match
    /// would silently misroute the update/delete decision (site 1) or silently
    /// preserve/delete the wrong row under a presence-sensitive collection ancestor
    /// (site 2). A deterministic fail-fast for that case needs either DB-side presence
    /// fidelity (presence flag columns threaded into the current-state projection) or
    /// pre-merge detection of ambiguous stored tuples. Both are tracked as follow-up work
    /// rather than solved inside this comparator.
    /// </remarks>
    private static bool CompareSemanticIdentityValue(
        FlattenedWriteValue currentValue,
        SemanticIdentityPart storedPart,
        RelationalScalarType? scalarType
    )
    {
        if (currentValue is not FlattenedWriteValue.Literal { Value: var literal })
        {
            return false;
        }

        if (!storedPart.IsPresent)
        {
            return literal is null;
        }

        if (storedPart.Value is null)
        {
            return literal is null;
        }

        var storedString = AncestorKeyHelpers.ExtractJsonNodeStringValue(storedPart.Value);
        var currentString = AncestorKeyHelpers.ExtractJsonNodeStringValue(
            AncestorKeyHelpers.ConvertClrValueToSemanticIdentityJsonNode(literal, scalarType)
        );
        return string.Equals(storedString, currentString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares a JSON-domain candidate semantic identity value plus its candidate-side IsPresent
    /// flag to a SemanticIdentityPart.
    /// </summary>
    /// <remarks>
    /// Both sides participate in the IsPresent comparison: a candidate whose identity member
    /// was absent from the request JSON (candidatePresent=false) must not match a visible
    /// request item whose identity member was sent as explicit null (part.IsPresent=true,
    /// part.Value=null), even though both collapse to a literal null value. This preserves
    /// the absent-vs-present-null distinction that <see cref="CollectionWriteCandidate.SemanticIdentityPresenceFlags"/>
    /// threads through the contract.
    /// </remarks>
    private static bool CompareJsonNodeToSemanticIdentityPart(
        JsonNode? candidateValue,
        bool candidatePresent,
        SemanticIdentityPart part
    )
    {
        if (candidatePresent != part.IsPresent)
        {
            return false;
        }

        if (!part.IsPresent)
        {
            // Both sides absent; value must be null on both (flattener guarantees this).
            return candidateValue is null;
        }

        if (part.Value is null)
        {
            return candidateValue is null;
        }

        var partString = AncestorKeyHelpers.ExtractJsonNodeStringValue(part.Value);
        var candidateString = AncestorKeyHelpers.ExtractJsonNodeStringValue(candidateValue);
        return string.Equals(partString, candidateString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Formats a single identity key part, encoding whether the member was absent from the
    /// source JSON so that (absent, null) and (present, null) produce distinct keys. Keeps
    /// candidate-side dedup and stored-row lookup consistent with the IsPresent distinction
    /// preserved elsewhere in the contract.
    /// </summary>
    private static string FormatIdentityKeyPart(JsonNode? value, bool isPresent)
    {
        return isPresent ? "P:" + AncestorKeyHelpers.ExtractJsonNodeStringValue(value) : "A";
    }

    /// <summary>
    /// Builds a string key from semantic identity values and per-member presence flags
    /// for efficient lookup. The presence flag distinguishes absent members from
    /// present-with-null members so they don't collapse to the same key.
    /// </summary>
    private static string BuildSemanticIdentityKeyString(
        IReadOnlyList<JsonNode?> values,
        IReadOnlyList<bool> presenceFlags
    )
    {
        if (values.Count != presenceFlags.Count)
        {
            throw new InvalidOperationException(
                $"Semantic identity values ({values.Count}) and presence flags ({presenceFlags.Count}) "
                    + "must have equal length."
            );
        }

        if (values.Count == 1)
        {
            return FormatIdentityKeyPart(values[0], presenceFlags[0]);
        }

        return string.Join("\0", values.Select((v, i) => FormatIdentityKeyPart(v, presenceFlags[i])));
    }

    /// <summary>
    /// Builds a string key from a VisibleRequestCollectionItem's compiled semantic-identity
    /// parts using the same format as <see cref="BuildSemanticIdentityKeyString"/>. Used by
    /// the reverse-coverage check to prove every visible request item has a matching
    /// backend-flattened CollectionWriteCandidate.
    /// </summary>
    private static string BuildSemanticIdentityKeyFromVisibleItem(VisibleRequestCollectionItem visibleItem)
    {
        var parts = visibleItem.Address.SemanticIdentityInOrder;
        var values = new JsonNode?[parts.Length];
        var flags = new bool[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = parts[i].Value;
            flags[i] = parts[i].IsPresent;
        }

        return BuildSemanticIdentityKeyString(values, flags);
    }

    /// <summary>
    /// Builds a string key from a current row's semantic identity bindings, using the
    /// matched VisibleStoredCollectionRow's per-part IsPresent flags so the key format
    /// matches candidates keyed via <see cref="BuildSemanticIdentityKeyString"/>.
    /// </summary>
    private static string BuildSemanticIdentityKeyFromRow(
        TableWritePlan tableWritePlan,
        CollectionMergePlan mergePlan,
        MergeTableRow row,
        VisibleStoredCollectionRow storedRow
    )
    {
        if (storedRow.Address.SemanticIdentityInOrder.Length != mergePlan.SemanticIdentityBindings.Length)
        {
            throw new InvalidOperationException(
                $"VisibleStoredCollectionRow semantic identity length "
                    + $"({storedRow.Address.SemanticIdentityInOrder.Length}) does not match "
                    + $"merge plan binding count ({mergePlan.SemanticIdentityBindings.Length})."
            );
        }

        if (mergePlan.SemanticIdentityBindings.Length == 1)
        {
            var binding = mergePlan.SemanticIdentityBindings[0];
            var value = row.Values[binding.BindingIndex];
            var isPresent = storedRow.Address.SemanticIdentityInOrder[0].IsPresent;
            var scalarType = tableWritePlan.ColumnBindings[binding.BindingIndex].Column.ScalarType;

            return value is FlattenedWriteValue.Literal { Value: var literal }
                ? FormatIdentityKeyPart(
                    AncestorKeyHelpers.ConvertClrValueToSemanticIdentityJsonNode(literal, scalarType),
                    isPresent
                )
                : FormatIdentityKeyPart(null, isPresent);
        }

        return string.Join(
            "\0",
            mergePlan.SemanticIdentityBindings.Select(
                (b, i) =>
                {
                    var value = row.Values[b.BindingIndex];
                    var isPresent = storedRow.Address.SemanticIdentityInOrder[i].IsPresent;
                    var scalarType = tableWritePlan.ColumnBindings[b.BindingIndex].Column.ScalarType;
                    return value is FlattenedWriteValue.Literal { Value: var literal }
                        ? FormatIdentityKeyPart(
                            AncestorKeyHelpers.ConvertClrValueToSemanticIdentityJsonNode(literal, scalarType),
                            isPresent
                        )
                        : FormatIdentityKeyPart(null, isPresent);
                }
            )
        );
    }

    /// <summary>
    /// Preserves all descendant rows under a hidden parent row.
    /// Uses compiled immediate-child scope metadata where available, and falls back to
    /// collection-aligned extension scope alignment / physical parent-scope FK shape when
    /// the compiled JSON hierarchy does not expose the aligned base collection as the
    /// immediate parent.
    /// </summary>
    private static void PreserveHiddenRowDescendants(
        TableWritePlan parentTablePlan,
        MergeTableRow hiddenRow,
        MergeCurrentStateProjection currentStateProjection,
        IReadOnlyDictionary<DbTableName, MergeTableStateBuilder> tableStateBuilders,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog
    )
    {
        var physicalRowIdentityValues = ExtractPhysicalRowIdentityValues(parentTablePlan, hiddenRow.Values);

        // Only proceed if all parent identity values are literals (resolvable)
        if (physicalRowIdentityValues.Any(v => v is not FlattenedWriteValue.Literal))
        {
            return;
        }

        var parentJsonScope = parentTablePlan.TableModel.JsonScope.Canonical;

        // Build set of immediate child scopes from compiled metadata
        var childScopes = new HashSet<string>(
            compiledScopeCatalog
                .Where(s =>
                    string.Equals(s.ImmediateParentJsonScope, parentJsonScope, StringComparison.Ordinal)
                )
                .Select(s => s.JsonScope),
            StringComparer.Ordinal
        );

        foreach (var (_, builder) in tableStateBuilders)
        {
            var childPlan = builder.TableWritePlan;

            if (!IsDirectHiddenDescendant(parentTablePlan, childPlan, childScopes))
            {
                continue;
            }

            var childRows = currentStateProjection.GetCurrentRowsForParent(
                childPlan,
                physicalRowIdentityValues
            );

            foreach (var childRow in childRows)
            {
                var ordinal = childPlan.CollectionMergePlan is not null
                    ? ExtractOrdinalFromRow(childRow, childPlan.CollectionMergePlan)
                    : 0;
                builder.AddPreservedRow(new MergePreservedRow(childRow.Values, ordinal));
                var comparableValues = ProjectComparableValues(childPlan, childRow.Values);
                builder.AddComparableMergedRow(new MergeTableRow(childRow.Values, comparableValues));

                PreserveHiddenRowDescendants(
                    childPlan,
                    childRow,
                    currentStateProjection,
                    tableStateBuilders,
                    compiledScopeCatalog
                );
            }
        }
    }

    private static bool IsDirectHiddenDescendant(
        TableWritePlan parentTablePlan,
        TableWritePlan childPlan,
        HashSet<string> compiledChildScopes
    )
    {
        if (compiledChildScopes.Contains(childPlan.TableModel.JsonScope.Canonical))
        {
            return true;
        }

        if (
            RelationalWriteMergeShared.IsCollectionAlignedExtensionScope(childPlan)
            && IsAlignedToParentScope(parentTablePlan, childPlan)
        )
        {
            return true;
        }

        return HasParentScopeForeignKey(parentTablePlan, childPlan);
    }

    private static bool IsAlignedToParentScope(TableWritePlan parentTablePlan, TableWritePlan childPlan)
    {
        var alignedBaseJsonScope = TryGetAlignedBaseJsonScope(childPlan.TableModel.JsonScope.Canonical);

        return alignedBaseJsonScope is not null
            && string.Equals(
                alignedBaseJsonScope,
                parentTablePlan.TableModel.JsonScope.Canonical,
                StringComparison.Ordinal
            );
    }

    private static bool HasParentScopeForeignKey(TableWritePlan parentTablePlan, TableWritePlan childPlan)
    {
        var expectedColumns = BuildParentScopeForeignKeyColumns(
            childPlan.TableModel.IdentityMetadata,
            parentTablePlan.TableModel
        );
        var expectedTargetColumns = BuildParentScopeForeignKeyTargetColumns(parentTablePlan.TableModel);

        return childPlan
            .TableModel.Constraints.OfType<TableConstraint.ForeignKey>()
            .Any(fk =>
                fk.TargetTable.Equals(parentTablePlan.TableModel.Table)
                && fk.Columns.SequenceEqual(expectedColumns)
                && fk.TargetColumns.SequenceEqual(expectedTargetColumns)
            );
    }

    private static string? TryGetAlignedBaseJsonScope(string jsonScope)
    {
        if (!jsonScope.Contains("._ext.", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = jsonScope.Split('.');
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
            index += 1;
        }

        if (baseScopeSegments.Count == 0 || baseScopeSegments.Count == segments.Length)
        {
            return null;
        }

        return string.Join(".", baseScopeSegments);
    }

    private static IReadOnlyList<DbColumnName> BuildParentScopeForeignKeyColumns(
        DbTableIdentityMetadata childIdentityMetadata,
        DbTableModel parentTable
    )
    {
        if (UsesSingleColumnParentScopeForeignKey(parentTable.IdentityMetadata.TableKind))
        {
            return childIdentityMetadata.ImmediateParentScopeLocatorColumns;
        }

        return
        [
            .. childIdentityMetadata.ImmediateParentScopeLocatorColumns,
            .. childIdentityMetadata.RootScopeLocatorColumns,
        ];
    }

    private static IReadOnlyList<DbColumnName> BuildParentScopeForeignKeyTargetColumns(
        DbTableModel parentTable
    )
    {
        if (UsesSingleColumnParentScopeForeignKey(parentTable.IdentityMetadata.TableKind))
        {
            return parentTable.IdentityMetadata.PhysicalRowIdentityColumns;
        }

        return
        [
            .. parentTable.IdentityMetadata.PhysicalRowIdentityColumns,
            .. parentTable.IdentityMetadata.RootScopeLocatorColumns,
        ];
    }

    private static bool UsesSingleColumnParentScopeForeignKey(DbTableKind parentTableKind) =>
        parentTableKind is DbTableKind.Root or DbTableKind.RootExtension;

    // --- Value overlay ---

    private static ImmutableArray<FlattenedWriteValue> OverlayValues(
        TableWritePlan tableWritePlan,
        BindingClassification[] classifications,
        IReadOnlyList<FlattenedWriteValue> requestValues,
        ImmutableArray<FlattenedWriteValue>? currentValues,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
    )
    {
        FlattenedWriteValue[] overlaid = new FlattenedWriteValue[tableWritePlan.ColumnBindings.Length];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            var classification = classifications[bindingIndex];

            switch (classification)
            {
                case BindingClassification.StorageManaged:
                case BindingClassification.VisibleWritable:
                    // Rewrite ParentKeyPart from parent identity
                    if (
                        tableWritePlan.ColumnBindings[bindingIndex].Source
                            is WriteValueSource.ParentKeyPart parentKeyPart
                        && parentPhysicalRowIdentityValues.Count > 0
                    )
                    {
                        overlaid[bindingIndex] = parentPhysicalRowIdentityValues[parentKeyPart.Index];
                    }
                    else
                    {
                        overlaid[bindingIndex] = requestValues[bindingIndex];
                    }
                    break;

                case BindingClassification.HiddenPreserved:
                    if (currentValues is null)
                    {
                        throw new InvalidOperationException(
                            $"Table '{FormatTable(tableWritePlan)}' binding index {bindingIndex} is classified as "
                                + $"{nameof(BindingClassification.HiddenPreserved)} but no current state is available."
                        );
                    }
                    overlaid[bindingIndex] = currentValues.Value[bindingIndex];
                    break;

                case BindingClassification.ClearOnVisibleAbsent:
                    overlaid[bindingIndex] = new FlattenedWriteValue.Literal(null);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unrecognized binding classification '{classification}' at index {bindingIndex}."
                    );
            }
        }

        return overlaid.ToImmutableArray();
    }

    /// <summary>
    /// For new inserts, applies ClearOnVisibleAbsent to null out columns belonging to
    /// inlined VisibleAbsent scopes. Other columns are passed through unchanged.
    /// </summary>
    private static ImmutableArray<FlattenedWriteValue> ApplyClearableToInsert(
        BindingClassification[] classifications,
        ImmutableArray<FlattenedWriteValue> values
    )
    {
        FlattenedWriteValue[]? adjusted = null;

        for (var i = 0; i < classifications.Length; i++)
        {
            if (classifications[i] == BindingClassification.ClearOnVisibleAbsent)
            {
                adjusted ??= values.ToArray();
                adjusted[i] = new FlattenedWriteValue.Literal(null);
            }
        }

        return adjusted is not null ? adjusted.ToImmutableArray() : values;
    }

    /// <summary>
    /// Adjusts key-unification precomputed bindings after overlay to account for hidden
    /// members whose stored values are not reflected in request-side key-unification.
    /// <para>
    /// Implements the full-member-set canonical-source evaluation rule from
    /// <c>reference/design/backend-redesign/design-docs/profiles.md</c> ("Visible and
    /// writable" / "Hidden and preserved"): the canonical source is the first present
    /// member in <see cref="KeyUnificationWritePlan.MembersInOrder"/>, where "present"
    /// counts visible members from the request overlay and hidden members from
    /// preserved stored presence. When the first present member is hidden, the canonical
    /// binding is hidden-and-preserved; when the first present member is visible, it is
    /// visible-and-writable. Hidden members' synthetic presence columns always preserve
    /// stored state so that the class's effective presence profile remains intact.
    /// </para>
    /// <para>
    /// Conflict detection: when a preserved hidden member and a visible present member
    /// coexist in the same class, their canonical values MUST agree (per
    /// <c>key-unification.md</c> "Canonical value coalescing" conflict rule and the
    /// profiles.md "Hidden and preserved" fail-closed clause). Returned failures are
    /// mapped by callers into <see cref="RelationalWriteMergeSynthesisOutcome.ValidationFailure"/>.
    /// </para>
    /// </summary>
    private static (
        ImmutableArray<FlattenedWriteValue> Values,
        ImmutableArray<MergeValidationFailure> Failures
    ) AdjustKeyUnificationOverlayForHiddenMembers(
        TableWritePlan tableWritePlan,
        ImmutableArray<FlattenedWriteValue> overlaidValues,
        ImmutableArray<FlattenedWriteValue>? currentValues,
        ImmutableArray<string> hiddenMemberPaths
    )
    {
        if (
            currentValues is null
            || tableWritePlan.KeyUnificationPlans.Length == 0
            || hiddenMemberPaths.Length == 0
        )
        {
            return (overlaidValues, []);
        }

        var hiddenSet = new HashSet<string>(hiddenMemberPaths, StringComparer.Ordinal);
        FlattenedWriteValue[]? adjusted = null;
        List<MergeValidationFailure>? failures = null;

        foreach (var plan in tableWritePlan.KeyUnificationPlans)
        {
            // Pass 1: classify each member's effective presence and find the first-present
            // member in MembersInOrder — the canonical source per the design rule.
            int? firstPresentIndex = null;
            var firstPresentIsHidden = false;
            var anyVisiblePresent = false;
            var anyHiddenStoredPresent = false;

            for (var i = 0; i < plan.MembersInOrder.Length; i++)
            {
                var member = plan.MembersInOrder[i];
                var memberIsHidden = hiddenSet.Contains(member.RelativePath.Canonical);

                bool isPresent;
                if (member.PresenceBindingIndex is not int presenceIdx)
                {
                    // No presence column — member is unconditionally present (required field).
                    isPresent = true;
                }
                else if (memberIsHidden)
                {
                    // Hidden member: presence is governed by preserved stored state.
                    isPresent =
                        currentValues.Value[presenceIdx] is FlattenedWriteValue.Literal { Value: true };
                    if (isPresent)
                    {
                        anyHiddenStoredPresent = true;
                    }
                }
                else
                {
                    // Visible member: presence is governed by the request (via the overlay).
                    isPresent = overlaidValues[presenceIdx] is FlattenedWriteValue.Literal { Value: true };
                    if (isPresent)
                    {
                        anyVisiblePresent = true;
                    }
                }

                if (isPresent && firstPresentIndex is null)
                {
                    firstPresentIndex = i;
                    firstPresentIsHidden = memberIsHidden;
                }
            }

            // Pass 2: preserve hidden members' synthetic presence columns from stored state.
            // Must happen regardless of canonical-source choice so the stored presence profile
            // is never silently dropped.
            foreach (var member in plan.MembersInOrder)
            {
                if (
                    hiddenSet.Contains(member.RelativePath.Canonical)
                    && member.PresenceIsSynthetic
                    && member.PresenceBindingIndex is int presenceIdx
                )
                {
                    adjusted ??= overlaidValues.ToArray();
                    adjusted[presenceIdx] = currentValues.Value[presenceIdx];
                }
            }

            // Fail-closed conflict detection: preserved hidden member AND visible present member
            // must agree on the canonical value. Compare stored canonical vs. overlay canonical
            // (both already in canonical storage form).
            if (anyHiddenStoredPresent && anyVisiblePresent)
            {
                var storedCanonical = currentValues.Value[plan.CanonicalBindingIndex];
                var requestCanonical = overlaidValues[plan.CanonicalBindingIndex];

                if (!CanonicalValueEquals(storedCanonical, requestCanonical))
                {
                    var canonicalColumn = tableWritePlan
                        .ColumnBindings[plan.CanonicalBindingIndex]
                        .Column
                        .ColumnName
                        .Value;

                    failures ??= [];
                    failures.Add(
                        new MergeValidationFailure(
                            $"Key-unification conflict on canonical column '{canonicalColumn}' "
                                + $"in table '{FormatTable(tableWritePlan)}': preserved hidden "
                                + "member value disagrees with visible request member value."
                        )
                    );

                    // Do not write a canonical choice when a conflict was detected — the caller
                    // will surface the validation failure and the resulting values are unused.
                    continue;
                }
            }

            // Choose the canonical source per the design's first-present-in-MembersInOrder rule:
            //   - no first-present member → preserve stored canonical (preserves no-op equivalence
            //     and matches the key-unification.md "no members present => NULL" outcome, since
            //     a stored row with no present members already has a NULL canonical),
            //   - first-present member is hidden → canonical binding is hidden-and-preserved,
            //   - first-present member is visible → canonical binding is visible-and-writable
            //     and the overlay already carries the request-driven canonical (no change).
            var preserveCanonical = firstPresentIndex is null || firstPresentIsHidden;
            if (preserveCanonical)
            {
                adjusted ??= overlaidValues.ToArray();
                adjusted[plan.CanonicalBindingIndex] = currentValues.Value[plan.CanonicalBindingIndex];
            }
        }

        return (
            adjusted is not null ? adjusted.ToImmutableArray() : overlaidValues,
            failures is not null ? [.. failures] : []
        );
    }

    /// <summary>
    /// Equality comparison for canonical storage values. Both sides have already been
    /// canonicalized (request side by the flattener, stored side by hydration), so
    /// CLR-level equality on <see cref="FlattenedWriteValue.Literal.Value"/> is the
    /// correct comparison per <c>key-unification.md</c> §"Apply conflict detection".
    /// </summary>
    private static bool CanonicalValueEquals(FlattenedWriteValue left, FlattenedWriteValue right)
    {
        if (left is FlattenedWriteValue.Literal leftLit && right is FlattenedWriteValue.Literal rightLit)
        {
            return Equals(leftLit.Value, rightLit.Value);
        }
        return ReferenceEquals(left, right);
    }

    // --- Inlined visible-absent scope path precomputation ---

    private static Dictionary<
        DbTableName,
        (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)
    > PrecomputeInlinedScopePathsForAllTables(
        ResourceWritePlan writePlan,
        MergeScopeLookup scopeLookup,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog
    )
    {
        var result =
            new Dictionary<DbTableName, (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)>();

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            // Collection tables' inlined paths are computed per-row in the collection merge loop,
            // because different collection items can have different scope visibility.
            if (tablePlan.CollectionMergePlan is not null)
            {
                continue;
            }

            result[tablePlan.TableModel.Table] = CollectInlinedScopePaths(
                tablePlan,
                writePlan,
                scopeLookup,
                compiledScopeCatalog
            );
        }

        return result;
    }

    private static (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden) GetInlinedScopePaths(
        TableWritePlan tablePlan,
        IReadOnlyDictionary<
            DbTableName,
            (ImmutableArray<string> Clearable, ImmutableArray<string> Hidden)
        > inlinedScopePathsByTable
    ) => inlinedScopePathsByTable.TryGetValue(tablePlan.TableModel.Table, out var paths) ? paths : ([], []);

    // --- Inlined visible-absent scope path collection ---

    /// <summary>
    /// Identifies non-collection scopes that are visible-absent and inlined into a parent table
    /// (no separate backing table). Returns two arrays:
    /// - clearable paths: root-relative member paths that should be cleared (set to null)
    /// - hidden paths: root-relative member paths from inlined scopes that must be preserved
    /// </summary>
    private static (
        ImmutableArray<string> ClearablePaths,
        ImmutableArray<string> HiddenPaths
    ) CollectInlinedScopePaths(
        TableWritePlan parentTablePlan,
        ResourceWritePlan writePlan,
        MergeScopeLookup scopeLookup,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog
    )
    {
        var tableBackedScopes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            tableBackedScopes.Add(tablePlan.TableModel.JsonScope.Canonical);
        }

        var (clearable, hidden, _) = CollectInlinedScopePathsCore(
            parentTablePlan,
            tableBackedScopes,
            compiledScopeCatalog,
            scopeLookup.TryGetStoredScopeState,
            scopeLookup.TryGetRequestScopeState,
            failOnMissingRequestState: false
        );

        return (clearable, hidden);
    }

    /// <summary>
    /// Instance-specific variant of <see cref="CollectInlinedScopePaths"/> for collection tables.
    /// Uses instance-specific scope lookup (disambiguated by ancestor context key) so that
    /// different collection items can have different inlined scope visibility.
    /// </summary>
    /// <remarks>
    /// When a per-instance RequestScopeState is missing for a non-Hidden inlined scope,
    /// returns a non-null <c>ContractMismatchMessage</c>. Callers must propagate this as a
    /// <see cref="RelationalWriteMergeSynthesisOutcome.ContractMismatch"/> instead of using the
    /// returned path tuples.
    /// </remarks>
    private static (
        ImmutableArray<string> ClearablePaths,
        ImmutableArray<string> HiddenPaths,
        string? ContractMismatchMessage
    ) CollectInlinedScopePathsForCollectionInstance(
        TableWritePlan parentTablePlan,
        HashSet<string> tableBackedScopes,
        MergeScopeLookup scopeLookup,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        string childAncestorContextKey
    ) =>
        CollectInlinedScopePathsCore(
            parentTablePlan,
            tableBackedScopes,
            compiledScopeCatalog,
            jsonScope => scopeLookup.TryGetStoredScopeStateForInstance(jsonScope, childAncestorContextKey),
            jsonScope => scopeLookup.TryGetRequestScopeStateForInstance(jsonScope, childAncestorContextKey),
            failOnMissingRequestState: true
        );

    /// <summary>
    /// Shared implementation for inlined scope path collection. Walks the compiled scope catalog
    /// to find non-collection scopes inlined into <paramref name="parentTablePlan"/>, classifies
    /// their members as clearable or hidden, and optionally fails on missing request state.
    /// </summary>
    private static (
        ImmutableArray<string> ClearablePaths,
        ImmutableArray<string> HiddenPaths,
        string? ContractMismatchMessage
    ) CollectInlinedScopePathsCore(
        TableWritePlan parentTablePlan,
        HashSet<string> tableBackedScopes,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopeCatalog,
        Func<string, StoredScopeState?> getStoredScopeState,
        Func<string, RequestScopeState?> getRequestScopeState,
        bool failOnMissingRequestState
    )
    {
        var parentJsonScope = parentTablePlan.TableModel.JsonScope.Canonical;
        List<string>? clearablePaths = null;
        List<string>? hiddenPaths = null;

        foreach (var scopeDescriptor in compiledScopeCatalog)
        {
            if (scopeDescriptor.ScopeKind != ScopeKind.NonCollection)
            {
                continue;
            }

            if (tableBackedScopes.Contains(scopeDescriptor.JsonScope))
            {
                continue;
            }

            // Inlined scope — check if it belongs to this parent table.
            // Walk the scope hierarchy: the inlined scope belongs here when it is
            // a path-descendant of the table scope and no intermediate table-backed
            // scope sits between them. This catches nested inlined common-type
            // scopes (e.g. $.parentObject.nestedCommonType under root table $)
            // whose ImmediateParentJsonScope is another inlined scope, not the table.
            if (!IsInlinedIntoTable(scopeDescriptor.JsonScope, parentJsonScope, tableBackedScopes))
            {
                continue;
            }

            var storedScopeState = getStoredScopeState(scopeDescriptor.JsonScope);

            // Hidden stored scope: ALL canonical members must be preserved (hidden).
            // VisibleAbsent request scope: classify per hidden/visible stored members.
            // All other states: not applicable for inlined path collection.
            var isScopeHidden = storedScopeState is { Visibility: ProfileVisibilityKind.Hidden };
            if (!isScopeHidden)
            {
                var requestScopeState = getRequestScopeState(scopeDescriptor.JsonScope);

                if (requestScopeState is null && failOnMissingRequestState)
                {
                    return (
                        [],
                        [],
                        $"Inlined scope path collection for collection instance encountered scope "
                            + $"'{scopeDescriptor.JsonScope}' with no per-instance RequestScopeState. "
                            + "This indicates a Core-backend contract gap."
                    );
                }

                if (requestScopeState is not { Visibility: ProfileVisibilityKind.VisibleAbsent })
                {
                    continue;
                }
            }

            // Derive the scope prefix for converting scope-relative to parent-relative paths.
            var scopePrefix = DeriveScopePrefix(scopeDescriptor.JsonScope, parentJsonScope);

            if (scopePrefix is null)
            {
                continue;
            }

            if (isScopeHidden)
            {
                // Entire inlined scope is hidden — all members are preserved from stored state
                foreach (var memberPath in scopeDescriptor.CanonicalScopeRelativeMemberPaths)
                {
                    hiddenPaths ??= [];
                    hiddenPaths.Add($"$.{scopePrefix}.{memberPath}");
                }
            }
            else
            {
                // VisibleAbsent: split members into clearable (visible) vs hidden (preserved)
                var scopeHiddenPaths = storedScopeState?.HiddenMemberPaths ?? [];
                var scopeHiddenSet = new HashSet<string>(scopeHiddenPaths, StringComparer.Ordinal);

                foreach (var memberPath in scopeDescriptor.CanonicalScopeRelativeMemberPaths)
                {
                    var parentRelativePath = $"$.{scopePrefix}.{memberPath}";

                    if (scopeHiddenSet.Contains(memberPath))
                    {
                        hiddenPaths ??= [];
                        hiddenPaths.Add(parentRelativePath);
                    }
                    else
                    {
                        clearablePaths ??= [];
                        clearablePaths.Add(parentRelativePath);
                    }
                }
            }
        }

        return (
            clearablePaths is not null ? [.. clearablePaths] : [],
            hiddenPaths is not null ? [.. hiddenPaths] : [],
            null
        );
    }

    /// <summary>
    /// Determines whether an inlined (non-table-backed) scope's columns are stored in the
    /// given table. True when the inlined scope is a path-descendant of the table scope and
    /// no intermediate table-backed scope exists between them in the hierarchy. This correctly
    /// handles nested inlined common-type scopes (e.g. <c>$.address.nestedCommonType</c> under
    /// root table <c>$</c>) whose <c>ImmediateParentJsonScope</c> is another inlined scope.
    /// </summary>
    private static bool IsInlinedIntoTable(
        string inlinedJsonScope,
        string tableJsonScope,
        HashSet<string> tableBackedScopes
    )
    {
        var tablePrefix = tableJsonScope == "$" ? "$." : tableJsonScope + ".";

        if (!inlinedJsonScope.StartsWith(tablePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Verify no intermediate table-backed scope sits between the table and this scope
        foreach (var tbScope in tableBackedScopes)
        {
            if (string.Equals(tbScope, tableJsonScope, StringComparison.Ordinal))
            {
                continue;
            }

            if (
                tbScope.StartsWith(tablePrefix, StringComparison.Ordinal)
                && inlinedJsonScope.StartsWith(tbScope + ".", StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Derives the scope prefix for converting scope-relative paths to parent-relative paths.
    /// For scope "$.calendarReference" with parent "$", returns "calendarReference".
    /// Returns null if the scope path cannot be converted.
    /// </summary>
    private static string? DeriveScopePrefix(string scopeJsonScope, string parentJsonScope)
    {
        if (parentJsonScope == "$")
        {
            // Strip "$." prefix
            return scopeJsonScope.Length > 2 && scopeJsonScope.StartsWith("$.", StringComparison.Ordinal)
                ? scopeJsonScope[2..]
                : null;
        }

        // For non-root parents, strip the parent scope prefix plus "."
        var expectedPrefix = parentJsonScope + ".";

        return scopeJsonScope.StartsWith(expectedPrefix, StringComparison.Ordinal)
            ? scopeJsonScope[expectedPrefix.Length..]
            : null;
    }

    // --- Shared helpers (delegated to RelationalWriteMergeShared) ---

    private static ImmutableArray<FlattenedWriteValue> RewriteParentKeyPartValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
    ) =>
        RelationalWriteMergeShared.RewriteParentKeyPartValues(
            tableWritePlan,
            values,
            parentPhysicalRowIdentityValues
        );

    private static ImmutableArray<FlattenedWriteValue> RewriteCollectionStableRowIdentity(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<FlattenedWriteValue> currentValues
    ) => RelationalWriteMergeShared.RewriteCollectionStableRowIdentity(tableWritePlan, values, currentValues);

    private static ImmutableArray<FlattenedWriteValue> ExtractPhysicalRowIdentityValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    ) => RelationalWriteMergeShared.ExtractPhysicalRowIdentityValues(tableWritePlan, values);

    private static ImmutableArray<FlattenedWriteValue> ProjectComparableValues(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    ) => RelationalWriteMergeShared.ProjectComparableValues(tableWritePlan, values);

    private static long? ExtractStableRowIdentityValue(
        TableWritePlan tableWritePlan,
        IReadOnlyList<FlattenedWriteValue> values
    )
    {
        var mergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new InvalidOperationException(
                $"Collection table '{FormatTable(tableWritePlan)}' does not have a compiled collection merge plan."
            );

        var value = values[mergePlan.StableRowIdentityBindingIndex];

        return value is FlattenedWriteValue.Literal { Value: long longValue } ? longValue : null;
    }

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        RelationalWriteMergeShared.FormatTable(tableWritePlan);

    // --- Scope lookup helper ---

    private sealed class MergeScopeLookup
    {
        private readonly IReadOnlyDictionary<string, List<RequestScopeState>> _requestScopesByJsonScope;
        private readonly IReadOnlyDictionary<string, List<StoredScopeState>> _storedScopesByJsonScope;

        private MergeScopeLookup(
            IReadOnlyDictionary<string, List<RequestScopeState>> requestScopesByJsonScope,
            IReadOnlyDictionary<string, List<StoredScopeState>> storedScopesByJsonScope
        )
        {
            _requestScopesByJsonScope = requestScopesByJsonScope;
            _storedScopesByJsonScope = storedScopesByJsonScope;
        }

        public static MergeScopeLookup Create(RelationalWriteMergeRequest request)
        {
            Dictionary<string, List<RequestScopeState>> requestScopes = [];

            foreach (var scopeState in request.ProfileRequest!.RequestScopeStates)
            {
                if (!requestScopes.TryGetValue(scopeState.Address.JsonScope, out var list))
                {
                    list = [];
                    requestScopes[scopeState.Address.JsonScope] = list;
                }

                list.Add(scopeState);
            }

            Dictionary<string, List<StoredScopeState>> storedScopes = [];

            if (request.ProfileContext is not null)
            {
                foreach (var scopeState in request.ProfileContext.StoredScopeStates)
                {
                    if (!storedScopes.TryGetValue(scopeState.Address.JsonScope, out var list))
                    {
                        list = [];
                        storedScopes[scopeState.Address.JsonScope] = list;
                    }

                    list.Add(scopeState);
                }
            }

            return new MergeScopeLookup(requestScopes, storedScopes);
        }

        /// <summary>
        /// Returns the first scope state for the given JsonScope. Correct for root-level
        /// and collection-level scopes where all instances share the same visibility.
        /// </summary>
        public RequestScopeState? TryGetRequestScopeState(string jsonScope) =>
            _requestScopesByJsonScope.TryGetValue(jsonScope, out var list) && list.Count > 0 ? list[0] : null;

        /// <inheritdoc cref="TryGetRequestScopeState"/>
        public StoredScopeState? TryGetStoredScopeState(string jsonScope) =>
            _storedScopesByJsonScope.TryGetValue(jsonScope, out var list) && list.Count > 0 ? list[0] : null;

        /// <summary>
        /// Returns the scope state for a specific collection-aligned scope instance,
        /// disambiguating by the ancestor collection context key.
        /// </summary>
        public RequestScopeState? TryGetRequestScopeStateForInstance(
            string jsonScope,
            string ancestorContextKey
        )
        {
            if (!_requestScopesByJsonScope.TryGetValue(jsonScope, out var list) || list.Count == 0)
            {
                return null;
            }

            foreach (var state in list)
            {
                if (BuildAncestorKey(state.Address.AncestorCollectionInstances) == ancestorContextKey)
                {
                    return state;
                }
            }

            return null;
        }

        /// <inheritdoc cref="TryGetRequestScopeStateForInstance"/>
        public StoredScopeState? TryGetStoredScopeStateForInstance(
            string jsonScope,
            string ancestorContextKey
        )
        {
            if (!_storedScopesByJsonScope.TryGetValue(jsonScope, out var list) || list.Count == 0)
            {
                return null;
            }

            foreach (var state in list)
            {
                if (BuildAncestorKey(state.Address.AncestorCollectionInstances) == ancestorContextKey)
                {
                    return state;
                }
            }

            return null;
        }

        /// <summary>
        /// Delegates to <see cref="AncestorKeyHelpers.BuildAncestorKeyFromInstances"/>
        /// so that all ancestor-key construction in the JSON domain shares a single implementation.
        /// </summary>
        private static string BuildAncestorKey(ImmutableArray<AncestorCollectionInstance> ancestors) =>
            AncestorKeyHelpers.BuildAncestorKeyFromInstances(ancestors);
    }

    /// <summary>
    /// Extends an ancestor context key with a new collection item's identity,
    /// producing the ancestor key for scopes nested under that collection item.
    ///
    /// Uses the same JSON-domain serialization as <see cref="AncestorKeyHelpers.BuildAncestorKeyFromInstances"/>
    /// so request-side ancestor keys stay aligned with stored-side keys under the unified no-profile path.
    /// </summary>
    private static string ExtendAncestorContextKey(
        string currentKey,
        string collectionJsonScope,
        IReadOnlyList<JsonNode?> semanticIdentityValues,
        ImmutableArray<bool> semanticIdentityPresenceFlags
    ) =>
        AncestorKeyHelpers.ExtendAncestorKey(
            currentKey,
            collectionJsonScope,
            semanticIdentityValues,
            semanticIdentityPresenceFlags
        );

    // --- Profile collection lookup ---

    /// <summary>
    /// Groups visible stored rows and request items by (JsonScope, ancestorContextKey)
    /// so that multi-instance scopes under different parents are correctly isolated.
    /// </summary>
    private sealed class MergeCollectionLookup
    {
        private readonly IReadOnlyDictionary<
            (string JsonScope, string AncestorContextKey),
            ImmutableArray<VisibleStoredCollectionRow>
        > _storedRowsByKey;

        private readonly IReadOnlyDictionary<
            (string JsonScope, string AncestorContextKey),
            ImmutableArray<VisibleRequestCollectionItem>
        > _requestItemsByKey;

        private MergeCollectionLookup(
            IReadOnlyDictionary<(string, string), ImmutableArray<VisibleStoredCollectionRow>> storedRowsByKey,
            IReadOnlyDictionary<
                (string, string),
                ImmutableArray<VisibleRequestCollectionItem>
            > requestItemsByKey
        )
        {
            _storedRowsByKey = storedRowsByKey;
            _requestItemsByKey = requestItemsByKey;
        }

        public static MergeCollectionLookup Create(RelationalWriteMergeRequest request)
        {
            Dictionary<(string, string), List<VisibleStoredCollectionRow>> storedByKey = [];

            if (request.ProfileContext is not null)
            {
                foreach (var storedRow in request.ProfileContext.VisibleStoredCollectionRows)
                {
                    var key = (
                        storedRow.Address.JsonScope,
                        BuildAncestorKeyFromAddress(storedRow.Address.ParentAddress)
                    );

                    if (!storedByKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        storedByKey[key] = list;
                    }

                    list.Add(storedRow);
                }
            }

            Dictionary<(string, string), List<VisibleRequestCollectionItem>> requestByKey = [];

            foreach (var requestItem in request.ProfileRequest!.VisibleRequestCollectionItems)
            {
                var key = (
                    requestItem.Address.JsonScope,
                    BuildAncestorKeyFromAddress(requestItem.Address.ParentAddress)
                );

                if (!requestByKey.TryGetValue(key, out var list))
                {
                    list = [];
                    requestByKey[key] = list;
                }

                list.Add(requestItem);
            }

            return new MergeCollectionLookup(
                storedByKey.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray()),
                requestByKey.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray())
            );
        }

        public IReadOnlyList<VisibleStoredCollectionRow> GetVisibleStoredRows(
            string jsonScope,
            string ancestorContextKey
        ) => _storedRowsByKey.TryGetValue((jsonScope, ancestorContextKey), out var rows) ? rows : [];

        public IReadOnlyList<VisibleRequestCollectionItem> GetVisibleRequestItems(
            string jsonScope,
            string ancestorContextKey
        ) => _requestItemsByKey.TryGetValue((jsonScope, ancestorContextKey), out var items) ? items : [];

        /// <summary>
        /// Builds an ancestor context key from a CollectionRowAddress.ParentAddress.
        /// Delegates to <see cref="AncestorKeyHelpers.BuildAncestorKeyFromInstances"/>
        /// so all JSON-domain key construction shares a single implementation.
        /// </summary>
        private static string BuildAncestorKeyFromAddress(ScopeInstanceAddress parentAddress) =>
            AncestorKeyHelpers.BuildAncestorKeyFromInstances(parentAddress.AncestorCollectionInstances);
    }

    // --- Current state projection ---

    private sealed class MergeCurrentStateProjection
    {
        private readonly IReadOnlyDictionary<DbTableName, ImmutableArray<MergeTableRow>> _currentRowsByTable;

        private MergeCurrentStateProjection(
            IReadOnlyDictionary<DbTableName, ImmutableArray<MergeTableRow>> currentRowsByTable
        )
        {
            _currentRowsByTable = currentRowsByTable;
        }

        public static MergeCurrentStateProjection Create(RelationalWriteMergeRequest request)
        {
            Dictionary<DbTableName, ImmutableArray<MergeTableRow>> currentRowsByTable = [];

            var hydratedRowsByTable = request.CurrentState is null
                ? new Dictionary<DbTableName, HydratedTableRows>()
                : request.CurrentState.TableRowsInDependencyOrder.ToDictionary(hydratedTableRows =>
                    hydratedTableRows.TableModel.Table
                );

            foreach (var tableWritePlan in request.WritePlan.TablePlansInDependencyOrder)
            {
                var projectedRows = hydratedRowsByTable.TryGetValue(
                    tableWritePlan.TableModel.Table,
                    out var hydratedTableRows
                )
                    ? ProjectCurrentRows(tableWritePlan, hydratedTableRows.Rows)
                    : ImmutableArray<MergeTableRow>.Empty;

                currentRowsByTable.Add(tableWritePlan.TableModel.Table, projectedRows);
            }

            return new MergeCurrentStateProjection(currentRowsByTable);
        }

        public ImmutableArray<MergeTableRow> GetCurrentRows(TableWritePlan tableWritePlan) =>
            _currentRowsByTable.TryGetValue(tableWritePlan.TableModel.Table, out var currentRows)
                ? currentRows
                : ImmutableArray<MergeTableRow>.Empty;

        /// <summary>
        /// Gets the first current row's values for a non-collection table (root, root-extension).
        /// Returns null if no current rows exist.
        /// </summary>
        public ImmutableArray<FlattenedWriteValue>? GetCurrentRowValues(TableWritePlan tableWritePlan)
        {
            var rows = GetCurrentRows(tableWritePlan);
            return rows.IsEmpty ? null : rows[0].Values;
        }

        /// <summary>
        /// Resolves the concrete current row for a non-collection scope instance using the
        /// scope address's ancestor collection identities. This is required for profiled
        /// second-pass deletes, where multiple rows can exist in the same table under
        /// different collection parents.
        /// </summary>
        public MergeTableRow? TryMatchScopeInstanceRow(
            TableWritePlan tableWritePlan,
            ScopeInstanceAddress address,
            IReadOnlyDictionary<string, TableWritePlan> tablePlansByJsonScope
        )
        {
            var rows = GetCurrentRows(tableWritePlan);

            if (rows.IsEmpty)
            {
                return null;
            }

            if (address.AncestorCollectionInstances.IsEmpty)
            {
                return rows[0];
            }

            var parentPhysicalRowIdentityValues = TryResolveAncestorPhysicalRowIdentityValues(
                address.AncestorCollectionInstances,
                tablePlansByJsonScope
            );

            if (parentPhysicalRowIdentityValues is null)
            {
                return null;
            }

            var parentLocatorColumns = tableWritePlan
                .TableModel
                .IdentityMetadata
                .ImmediateParentScopeLocatorColumns;

            return rows.FirstOrDefault(row =>
                MatchesParentLocator(
                    tableWritePlan,
                    row,
                    parentLocatorColumns,
                    parentPhysicalRowIdentityValues
                )
            );
        }

        /// <summary>
        /// Gets current rows for a collection table filtered by parent scope locator columns
        /// matching the given parent physical row identity values.
        /// </summary>
        public IReadOnlyList<MergeTableRow> GetCurrentRowsForParent(
            TableWritePlan tableWritePlan,
            IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
        )
        {
            var allRows = GetCurrentRows(tableWritePlan);

            if (allRows.IsEmpty)
            {
                return [];
            }

            var parentLocatorColumns = tableWritePlan
                .TableModel
                .IdentityMetadata
                .ImmediateParentScopeLocatorColumns;

            if (parentLocatorColumns.Count == 0)
            {
                return [.. allRows];
            }

            // If any parent value is non-literal (e.g. UnresolvedRootDocumentId),
            // we cannot filter by parent key. Return all rows since this is a
            // single-document context.
            for (var i = 0; i < parentLocatorColumns.Count; i++)
            {
                if (
                    i < parentPhysicalRowIdentityValues.Count
                    && parentPhysicalRowIdentityValues[i] is not FlattenedWriteValue.Literal
                )
                {
                    return [.. allRows];
                }
            }

            return allRows
                .Where(row =>
                    MatchesParentLocator(
                        tableWritePlan,
                        row,
                        parentLocatorColumns,
                        parentPhysicalRowIdentityValues
                    )
                )
                .ToList();
        }

        /// <summary>
        /// Tries to find a current row for a collection-aligned extension scope table by parent key.
        /// </summary>
        public MergeTableRow? TryMatchAlignedScopeRow(
            TableWritePlan tableWritePlan,
            IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
        )
        {
            var rows = GetCurrentRows(tableWritePlan);

            if (rows.IsEmpty)
            {
                return null;
            }

            var parentLocatorColumns = tableWritePlan
                .TableModel
                .IdentityMetadata
                .ImmediateParentScopeLocatorColumns;

            return rows.FirstOrDefault(row =>
                MatchesParentLocator(
                    tableWritePlan,
                    row,
                    parentLocatorColumns,
                    parentPhysicalRowIdentityValues
                )
            );
        }

        private ImmutableArray<FlattenedWriteValue>? TryResolveAncestorPhysicalRowIdentityValues(
            ImmutableArray<AncestorCollectionInstance> ancestors,
            IReadOnlyDictionary<string, TableWritePlan> tablePlansByJsonScope
        )
        {
            var rootPlan = tablePlansByJsonScope.GetValueOrDefault("$");

            if (rootPlan is null)
            {
                return null;
            }

            var rootRows = GetCurrentRows(rootPlan);

            if (rootRows.IsEmpty)
            {
                return null;
            }

            IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues =
                ExtractPhysicalRowIdentityValues(rootPlan, rootRows[0].Values);

            foreach (var ancestor in ancestors)
            {
                var collectionPlan = tablePlansByJsonScope.GetValueOrDefault(ancestor.JsonScope);
                var mergePlan = collectionPlan?.CollectionMergePlan;

                if (collectionPlan is null || mergePlan is null)
                {
                    return null;
                }

                var matchedRow = GetCurrentRowsForParent(collectionPlan, parentPhysicalRowIdentityValues)
                    .FirstOrDefault(row =>
                        MatchesAncestorCollectionInstance(collectionPlan, mergePlan, row, ancestor)
                    );

                if (matchedRow is null)
                {
                    return null;
                }

                parentPhysicalRowIdentityValues = ExtractPhysicalRowIdentityValues(
                    collectionPlan,
                    matchedRow.Values
                );
            }

            return [.. parentPhysicalRowIdentityValues];
        }

        private static bool MatchesAncestorCollectionInstance(
            TableWritePlan tableWritePlan,
            CollectionMergePlan mergePlan,
            MergeTableRow row,
            AncestorCollectionInstance ancestorInstance
        )
        {
            if (ancestorInstance.SemanticIdentityInOrder.Length != mergePlan.SemanticIdentityBindings.Length)
            {
                throw new InvalidOperationException(
                    $"AncestorCollectionInstance semantic identity length "
                        + $"({ancestorInstance.SemanticIdentityInOrder.Length}) does not match "
                        + $"merge plan binding count ({mergePlan.SemanticIdentityBindings.Length})."
                );
            }

            for (var i = 0; i < mergePlan.SemanticIdentityBindings.Length; i++)
            {
                var binding = mergePlan.SemanticIdentityBindings[i];
                var rowValue = row.Values[binding.BindingIndex];
                var ancestorPart = ancestorInstance.SemanticIdentityInOrder[i];
                var scalarType = tableWritePlan.ColumnBindings[binding.BindingIndex].Column.ScalarType;

                if (!CompareSemanticIdentityValue(rowValue, ancestorPart, scalarType))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesParentLocator(
            TableWritePlan tableWritePlan,
            MergeTableRow row,
            IReadOnlyList<DbColumnName> parentLocatorColumns,
            IReadOnlyList<FlattenedWriteValue> parentPhysicalRowIdentityValues
        )
        {
            for (var i = 0; i < parentLocatorColumns.Count; i++)
            {
                var bindingIndex = RelationalWriteMergeShared.FindBindingIndex(
                    tableWritePlan,
                    parentLocatorColumns[i]
                );
                var rowValue = row.Values[bindingIndex];
                var parentValue = parentPhysicalRowIdentityValues[i];

                if (
                    rowValue is FlattenedWriteValue.Literal { Value: var rowLiteral }
                    && parentValue is FlattenedWriteValue.Literal { Value: var parentLiteral }
                )
                {
                    if (!Equals(rowLiteral, parentLiteral))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private static ImmutableArray<MergeTableRow> ProjectCurrentRows(
            TableWritePlan tableWritePlan,
            IReadOnlyList<object?[]> hydratedRows
        ) => RelationalWriteMergeShared.ProjectCurrentRows(tableWritePlan, hydratedRows);
    }

    // --- Table state builder ---

    private sealed class MergeTableStateBuilder(
        TableWritePlan tableWritePlan,
        ImmutableArray<MergeTableRow> currentRows
    )
    {
        public TableWritePlan TableWritePlan { get; } = tableWritePlan;

        private readonly List<MergeRowInsert> _inserts = [];
        private readonly List<MergeRowUpdate> _updates = [];
        private readonly List<MergeRowDelete> _deletes = [];
        private readonly List<MergePreservedRow> _preservedRows = [];
        private readonly List<MergeTableRow> _comparableMergedRows = [];

        public void AddInsert(MergeRowInsert insert)
        {
            ArgumentNullException.ThrowIfNull(insert);
            _inserts.Add(insert);
        }

        public void AddUpdate(MergeRowUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);
            _updates.Add(update);
        }

        public void AddDelete(MergeRowDelete delete)
        {
            ArgumentNullException.ThrowIfNull(delete);
            _deletes.Add(delete);
        }

        public void AddPreservedRow(MergePreservedRow preservedRow)
        {
            ArgumentNullException.ThrowIfNull(preservedRow);
            _preservedRows.Add(preservedRow);
        }

        public void AddComparableMergedRow(MergeTableRow row)
        {
            ArgumentNullException.ThrowIfNull(row);
            _comparableMergedRows.Add(row);
        }

        public bool HasComparableMergedRowWithPhysicalIdentity(ImmutableArray<FlattenedWriteValue> values)
        {
            var physicalIdentityValues = ExtractPhysicalRowIdentityValues(TableWritePlan, values);

            return _comparableMergedRows.Exists(row =>
                HaveSamePhysicalIdentityValues(
                    physicalIdentityValues,
                    ExtractPhysicalRowIdentityValues(TableWritePlan, row.Values)
                )
            );
        }

        private static bool HaveSamePhysicalIdentityValues(
            IReadOnlyList<FlattenedWriteValue> left,
            IReadOnlyList<FlattenedWriteValue> right
        )
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (
                    left[i] is not FlattenedWriteValue.Literal { Value: var leftValue }
                    || right[i] is not FlattenedWriteValue.Literal { Value: var rightValue }
                    || !Equals(leftValue, rightValue)
                )
                {
                    return false;
                }
            }

            return true;
        }

        public RelationalWriteMergeTableState Build()
        {
            IReadOnlyList<MergeTableRow> currentRowsForComparison;

            if (TableWritePlan.CollectionMergePlan is not null)
            {
                currentRowsForComparison = RelationalWriteMergeShared.OrderCollectionRowsIfFullyBound(
                    TableWritePlan,
                    currentRows
                );
            }
            else if (RelationalWriteMergeShared.IsCollectionAlignedExtensionScope(TableWritePlan))
            {
                currentRowsForComparison =
                    RelationalWriteMergeShared.OrderCollectionAlignedExtensionScopeRowsIfFullyBound(
                        TableWritePlan,
                        currentRows
                    );
            }
            else
            {
                currentRowsForComparison = currentRows;
            }
            IReadOnlyList<MergeTableRow> mergedRows;

            if (TableWritePlan.CollectionMergePlan is not null)
            {
                mergedRows = RelationalWriteMergeShared.OrderCollectionRowsIfFullyBound(
                    TableWritePlan,
                    _comparableMergedRows
                );
            }
            else if (RelationalWriteMergeShared.IsCollectionAlignedExtensionScope(TableWritePlan))
            {
                mergedRows = RelationalWriteMergeShared.OrderCollectionAlignedExtensionScopeRowsIfFullyBound(
                    TableWritePlan,
                    _comparableMergedRows
                );
            }
            else
            {
                mergedRows = _comparableMergedRows;
            }

            return new RelationalWriteMergeTableState(
                TableWritePlan,
                _inserts,
                _updates,
                _deletes,
                _preservedRows,
                currentRowsForComparison,
                mergedRows
            );
        }
    }
}
