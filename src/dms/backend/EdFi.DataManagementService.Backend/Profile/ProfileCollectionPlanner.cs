// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Pure-function planner for a single collection scope instance under a specific parent
/// instance. Consumes the Core-emitted request / stored metadata for the scope, validates
/// fail-closed invariants, and produces a sequenced <see cref="ProfileCollectionPlan"/>
/// describing the final sibling sequence for the merge. No IO, no builders, no table-state
/// access — the synthesizer owns all runtime plumbing.
/// </summary>
internal static class ProfileCollectionPlanner
{
    public static ProfileCollectionPlanResult Plan(ProfileCollectionScopeInput input)
    {
        ValidateInvariants(input);
        return BuildMergedVisibleSequence(input);
    }

    /// <summary>
    /// Builds the final sequenced plan via two logical phases described in spec Section 5.2.
    ///
    /// <para><b>Phase 1 — Build <c>mergedVisibleSequence</c> in request order.</b>
    /// Walks <see cref="ProfileCollectionScopeInput.VisibleRequestItems"/> in request array order.
    /// For each item:
    /// <list type="bullet">
    ///   <item>Emits a <see cref="ProfileCollectionPlanEntry.MatchedUpdateEntry"/> when the item's
    ///   semantic identity matches a <see cref="CurrentCollectionRowSnapshot"/> (via the visible-stored index).</item>
    ///   <item>Emits a <see cref="ProfileCollectionPlanEntry.VisibleInsertEntry"/> when unmatched and
    ///   <see cref="VisibleRequestCollectionItem.Creatable"/> is <c>true</c>.</item>
    ///   <item>Short-circuits with <see cref="ProfileCollectionPlanResult.CreatabilityRejection"/> when
    ///   unmatched and <see cref="VisibleRequestCollectionItem.Creatable"/> is <c>false</c>.</item>
    /// </list></para>
    ///
    /// <para><b>Phase 2 — Walk <see cref="ProfileCollectionScopeInput.CurrentRows"/> in stored-ordinal
    /// order, interleaving hidden-preserves and consuming <c>mergedVisibleSequence</c> at visible slots.</b>
    /// Hidden rows (not in the visible-stored index) are emitted as
    /// <see cref="ProfileCollectionPlanEntry.HiddenPreserveEntry"/>; visible slots consume the next
    /// entry from <c>mergedVisibleSequence</c> in first-come-first-served order (matching the request's
    /// reordering of visibles). Visible slots reached after the merged cursor is exhausted are omitted;
    /// the persister's delete-by-absence mechanism handles their removal. Leftover merged entries
    /// (new inserts beyond the previous visible count) are appended at the end after all current rows.</para>
    /// </summary>
    private static ProfileCollectionPlanResult BuildMergedVisibleSequence(ProfileCollectionScopeInput input)
    {
        // Pre-compute the semantic identity key for every visible-stored row and current row
        // exactly once, then drive every downstream lookup off the cached pair. The previous
        // implementation built each row's key 2-4x across the four indexes plus the Phase 2
        // loop; the cached form is behavior-preserving and keeps the same invariant-ordering
        // contract (ValidateInvariants is untouched and still runs first via Plan).
        var visibleStoredEntries = input
            .VisibleStoredRows.Select(row =>
                (Row: row, Key: SemanticIdentityKeys.BuildKey(row.Address.SemanticIdentityInOrder))
            )
            .ToArray();
        var visibleStoredByIdentity = visibleStoredEntries.ToDictionary(p => p.Key, p => p.Row);

        var currentRowEntries = input
            .CurrentRows.Select(row =>
                (Row: row, Key: SemanticIdentityKeys.BuildKey(row.SemanticIdentityInOrder))
            )
            .ToArray();

        // Matched current rows are those whose semantic identity also appears in visible-stored.
        var matchedCurrentByIdentity = currentRowEntries
            .Where(p => visibleStoredByIdentity.ContainsKey(p.Key))
            .ToDictionary(p => p.Key, p => p.Row);

        // Build candidate lookup for retrieving the CollectionWriteCandidate per request item.
        var candidateByIdentityKey = input.RequestCandidates.ToDictionary(SemanticIdentityKeys.BuildKey);

        // Phase 1: build mergedVisibleSequence in request order.
        var mergedVisibleSequence = new List<ProfileCollectionPlanEntry>();

        foreach (var visibleRequestItem in input.VisibleRequestItems)
        {
            var key = SemanticIdentityKeys.BuildKey(visibleRequestItem.Address.SemanticIdentityInOrder);

            if (matchedCurrentByIdentity.TryGetValue(key, out var currentRow))
            {
                // Matched: visible request item maps to a visible stored row with a current snapshot.
                var hiddenPaths = visibleStoredByIdentity[key].HiddenMemberPaths;
                var candidate = candidateByIdentityKey[key];
                mergedVisibleSequence.Add(
                    new ProfileCollectionPlanEntry.MatchedUpdateEntry(currentRow, candidate, hiddenPaths)
                );
            }
            else if (visibleRequestItem.Creatable)
            {
                // Unmatched but creatable: new row to insert.
                var candidate = candidateByIdentityKey[key];
                mergedVisibleSequence.Add(new ProfileCollectionPlanEntry.VisibleInsertEntry(candidate));
            }
            else
            {
                // Unmatched and not creatable: reject immediately before Phase 2.
                return new ProfileCollectionPlanResult.CreatabilityRejection(
                    visibleRequestItem.Address,
                    $"Profile does not allow creating new collection items in scope '{input.JsonScope}'."
                );
            }
        }

        // Phase 2: walk current rows in stored-ordinal order, interleaving hidden-preserves
        // and consuming mergedVisibleSequence at visible slots. See spec Section 5.2.
        var output = new List<ProfileCollectionPlanEntry>(
            capacity: input.CurrentRows.Length + mergedVisibleSequence.Count
        );
        var mergedCursor = 0;
        foreach (var (currentRow, currentKey) in currentRowEntries)
        {
            if (!visibleStoredByIdentity.ContainsKey(currentKey))
            {
                // Hidden slot: preserve verbatim.
                output.Add(new ProfileCollectionPlanEntry.HiddenPreserveEntry(currentRow));
            }
            else if (mergedCursor < mergedVisibleSequence.Count)
            {
                // Visible slot: consume next merged-visible entry in request order.
                output.Add(mergedVisibleSequence[mergedCursor]);
                mergedCursor++;
            }
            // else: visible slot with no merged entry to consume → omitted, persister deletes by absence.
        }

        // Append any leftover merged entries (new inserts beyond previous visible count).
        while (mergedCursor < mergedVisibleSequence.Count)
        {
            output.Add(mergedVisibleSequence[mergedCursor]);
            mergedCursor++;
        }

        return new ProfileCollectionPlanResult.Success(new ProfileCollectionPlan(output.ToImmutableArray()));
    }

    /// <summary>
    /// Runs all ten fail-closed invariants on the scoped planner input. Throws
    /// <see cref="InvalidOperationException"/> on the first violation, with the offending
    /// <c>JsonScope</c> and semantic identity embedded in the message.
    /// </summary>
    /// <remarks>
    /// Invariant ordering is load-bearing. Coverage invariants (reverse-stored, request-side)
    /// must run before their ordering invariants (stored ordinal, request order). Reordering
    /// without also adding fallback paths in <see cref="ValidateStoredOrdinalOrder"/> or
    /// <see cref="ValidateRequestOrder"/> would surface a <c>KeyNotFoundException</c>
    /// instead of the intended fail-closed <c>InvalidOperationException</c>.
    /// </remarks>
    private static void ValidateInvariants(ProfileCollectionScopeInput input)
    {
        // Invariant 6a: pre-scoped input — JsonScope must match for all candidates.
        ValidateCandidateScopes(input);

        // Invariant 6b: pre-scoped input — JsonScope and ParentScopeAddress must match for
        // all VisibleRequestItems and VisibleStoredRows.
        ValidateVisibleRequestItemScopes(input);
        ValidateVisibleStoredRowScopes(input);

        // Invariant 5: current-row identity uniqueness — checked before visible/hidden
        // partitioning so duplicate ambiguity fails immediately.
        var currentByIdentity = BuildCurrentRowIndex(input);

        // Invariant 4: duplicate VisibleStoredCollectionRow — defense-in-depth.
        ValidateUniqueVisibleStoredRows(input);

        // Invariant 1: reverse stored coverage — every VisibleStoredCollectionRow must map
        // to exactly one CurrentCollectionRowSnapshot by semantic identity.
        ValidateReverseStoredCoverage(input, currentByIdentity);

        // Invariant 7: order consistency — stored rows map to strictly increasing StoredOrdinals.
        ValidateStoredOrdinalOrder(input, currentByIdentity);

        // New invariant: duplicate visible request candidates — must fire before invariant 2 so
        // the candidate stream is deduplicated before coverage checks.
        var candidatesByIdentityKey = ValidateUniqueRequestCandidates(input);

        // Invariant 3: duplicate VisibleRequestCollectionItem — defense-in-depth.
        ValidateUniqueVisibleRequestItems(input);

        // Invariant 2: request-side coverage — every VisibleRequestCollectionItem must map
        // to exactly one CollectionWriteCandidate.
        ValidateRequestSideCoverage(input, candidatesByIdentityKey);

        // New invariant: reverse request-side coverage — every CollectionWriteCandidate must
        // have a matching VisibleRequestCollectionItem in the scope.
        ValidateReverseRequestSideCoverage(input);

        // Invariant 8: order consistency — walking VisibleRequestItems in array order, the
        // paired candidate's RequestOrder must be strictly increasing.
        ValidateRequestOrder(input, candidatesByIdentityKey);
    }

    private static void ValidateCandidateScopes(ProfileCollectionScopeInput input)
    {
        foreach (
            var jsonScope in input.RequestCandidates.Select(c =>
                c.TableWritePlan.TableModel.JsonScope.Canonical
            )
        )
        {
            if (jsonScope != input.JsonScope)
            {
                throw new InvalidOperationException(
                    $"RequestCandidate belongs to scope '{LogSanitizer.SanitizeForLog(jsonScope)}' "
                        + $"but planner input scope is '{input.JsonScope}'. "
                        + "Planner invariant violated: pre-scoped input: JsonScope mismatch."
                );
            }
        }
    }

    private static void ValidateVisibleRequestItemScopes(ProfileCollectionScopeInput input)
    {
        foreach (var address in input.VisibleRequestItems.Select(i => i.Address))
        {
            if (address.JsonScope != input.JsonScope)
            {
                throw new InvalidOperationException(
                    $"VisibleRequestCollectionItem belongs to scope '{LogSanitizer.SanitizeForLog(address.JsonScope)}' "
                        + $"but planner input scope is '{input.JsonScope}'. "
                        + "Planner invariant violated: pre-scoped input: JsonScope mismatch."
                );
            }

            if (
                !ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(
                    address.ParentAddress,
                    input.ParentScopeAddress
                )
            )
            {
                throw new InvalidOperationException(
                    $"VisibleRequestCollectionItem in scope '{input.JsonScope}' has parent address "
                        + $"'{LogSanitizer.SanitizeForLog(address.ParentAddress.JsonScope)}' "
                        + $"but expected '{input.ParentScopeAddress.JsonScope}'. "
                        + "Planner invariant violated: pre-scoped input: parent scope mismatch."
                );
            }
        }
    }

    private static void ValidateVisibleStoredRowScopes(ProfileCollectionScopeInput input)
    {
        foreach (var address in input.VisibleStoredRows.Select(r => r.Address))
        {
            if (address.JsonScope != input.JsonScope)
            {
                throw new InvalidOperationException(
                    $"VisibleStoredCollectionRow belongs to scope '{LogSanitizer.SanitizeForLog(address.JsonScope)}' "
                        + $"but planner input scope is '{input.JsonScope}'. "
                        + "Planner invariant violated: pre-scoped input: JsonScope mismatch."
                );
            }

            if (
                !ScopeInstanceAddressComparer.ScopeInstanceAddressEquals(
                    address.ParentAddress,
                    input.ParentScopeAddress
                )
            )
            {
                throw new InvalidOperationException(
                    $"VisibleStoredCollectionRow in scope '{input.JsonScope}' has parent address "
                        + $"'{LogSanitizer.SanitizeForLog(address.ParentAddress.JsonScope)}' "
                        + $"but expected '{input.ParentScopeAddress.JsonScope}'. "
                        + "Planner invariant violated: pre-scoped input: parent scope mismatch."
                );
            }
        }
    }

    private static Dictionary<string, CurrentCollectionRowSnapshot> BuildCurrentRowIndex(
        ProfileCollectionScopeInput input
    )
    {
        var currentByIdentity = new Dictionary<string, CurrentCollectionRowSnapshot>();
        foreach (var row in input.CurrentRows)
        {
            var key = SemanticIdentityKeys.BuildKey(row.SemanticIdentityInOrder);
            if (!currentByIdentity.TryAdd(key, row))
            {
                throw new InvalidOperationException(
                    $"Current rows contain duplicate semantic identity in scope '{input.JsonScope}': "
                        + $"{LogSanitizer.SanitizeForLog(FormatIdentity(row.SemanticIdentityInOrder))}. "
                        + "Planner invariant violated: current row identity uniqueness."
                );
            }
        }

        return currentByIdentity;
    }

    private static void ValidateUniqueVisibleStoredRows(ProfileCollectionScopeInput input)
    {
        var seen = new HashSet<string>();
        foreach (var identity in input.VisibleStoredRows.Select(r => r.Address.SemanticIdentityInOrder))
        {
            var key = SemanticIdentityKeys.BuildKey(identity);
            if (!seen.Add(key))
            {
                throw new InvalidOperationException(
                    $"Duplicate visible stored row in scope '{input.JsonScope}': "
                        + $"{LogSanitizer.SanitizeForLog(FormatIdentity(identity))}. "
                        + "Planner invariant violated: duplicate visible stored row."
                );
            }
        }
    }

    private static void ValidateReverseStoredCoverage(
        ProfileCollectionScopeInput input,
        Dictionary<string, CurrentCollectionRowSnapshot> currentByIdentity
    )
    {
        foreach (var identity in input.VisibleStoredRows.Select(r => r.Address.SemanticIdentityInOrder))
        {
            var key = SemanticIdentityKeys.BuildKey(identity);
            if (!currentByIdentity.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"VisibleStoredCollectionRow for scope '{input.JsonScope}' with identity "
                        + $"{LogSanitizer.SanitizeForLog(FormatIdentity(identity))} "
                        + "has no matching current row. "
                        + "Planner invariant violated: reverse stored coverage."
                );
            }
        }
    }

    private static void ValidateStoredOrdinalOrder(
        ProfileCollectionScopeInput input,
        Dictionary<string, CurrentCollectionRowSnapshot> currentByIdentity
    )
    {
        // Precondition: ValidateReverseStoredCoverage must have run, so every stored row's
        // identity resolves to a current row via currentByIdentity.
        var lastStoredOrdinal = int.MinValue;
        foreach (var address in input.VisibleStoredRows.Select(r => r.Address))
        {
            var key = SemanticIdentityKeys.BuildKey(address.SemanticIdentityInOrder);
            var currentRow = currentByIdentity[key];
            if (currentRow.StoredOrdinal <= lastStoredOrdinal)
            {
                throw new InvalidOperationException(
                    $"VisibleStoredCollectionRows for scope '{input.JsonScope}' do not map to "
                        + $"strictly increasing StoredOrdinals (got {currentRow.StoredOrdinal} after {lastStoredOrdinal}). "
                        + "Planner invariant violated: order consistency: stored."
                );
            }

            lastStoredOrdinal = currentRow.StoredOrdinal;
        }
    }

    /// <summary>
    /// Builds the candidate index while enforcing that no two flattened request candidates share
    /// the same normalized semantic identity. Throws <see cref="InvalidOperationException"/> with
    /// phrase "duplicate visible request candidate" on the first collision.
    /// </summary>
    private static Dictionary<string, CollectionWriteCandidate> ValidateUniqueRequestCandidates(
        ProfileCollectionScopeInput input
    )
    {
        var candidatesByIdentityKey = new Dictionary<string, CollectionWriteCandidate>();
        foreach (var candidate in input.RequestCandidates)
        {
            var key = SemanticIdentityKeys.BuildKey(candidate);
            if (!candidatesByIdentityKey.TryAdd(key, candidate))
            {
                throw new InvalidOperationException(
                    $"Duplicate semantic identity among flattened request candidates in scope '{input.JsonScope}': "
                        + $"{LogSanitizer.SanitizeForLog(FormatCandidateIdentity(candidate))}. "
                        + "Planner invariant violated: duplicate visible request candidate."
                );
            }
        }

        return candidatesByIdentityKey;
    }

    private static void ValidateUniqueVisibleRequestItems(ProfileCollectionScopeInput input)
    {
        var seen = new HashSet<string>();
        foreach (var identity in input.VisibleRequestItems.Select(i => i.Address.SemanticIdentityInOrder))
        {
            var key = SemanticIdentityKeys.BuildKey(identity);
            if (!seen.Add(key))
            {
                throw new InvalidOperationException(
                    $"Duplicate visible request item in scope '{input.JsonScope}': "
                        + $"{LogSanitizer.SanitizeForLog(FormatIdentity(identity))}. "
                        + "Planner invariant violated: duplicate visible request item."
                );
            }
        }
    }

    private static void ValidateRequestSideCoverage(
        ProfileCollectionScopeInput input,
        Dictionary<string, CollectionWriteCandidate> candidatesByIdentityKey
    )
    {
        foreach (var identity in input.VisibleRequestItems.Select(i => i.Address.SemanticIdentityInOrder))
        {
            var candidateKey = SemanticIdentityKeys.BuildKey(identity);
            if (!candidatesByIdentityKey.ContainsKey(candidateKey))
            {
                throw new InvalidOperationException(
                    $"VisibleRequestCollectionItem for scope '{input.JsonScope}' with identity "
                        + $"{LogSanitizer.SanitizeForLog(FormatIdentity(identity))} "
                        + "has no matching request candidate. "
                        + "Planner invariant violated: request-side coverage."
                );
            }
        }
    }

    private static void ValidateReverseRequestSideCoverage(ProfileCollectionScopeInput input)
    {
        // Build a set of address-side keys from VisibleRequestItems for O(1) lookup.
        var visibleRequestKeys = input
            .VisibleRequestItems.Select(i => SemanticIdentityKeys.BuildKey(i.Address.SemanticIdentityInOrder))
            .ToHashSet();

        foreach (var candidate in input.RequestCandidates)
        {
            var key = SemanticIdentityKeys.BuildKey(candidate);
            if (!visibleRequestKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"Request candidate for scope '{input.JsonScope}' with identity "
                        + $"{LogSanitizer.SanitizeForLog(FormatCandidateIdentity(candidate))} "
                        + "has no matching VisibleRequestCollectionItem. "
                        + "Planner invariant violated: request-side coverage: orphan candidate."
                );
            }
        }
    }

    private static void ValidateRequestOrder(
        ProfileCollectionScopeInput input,
        Dictionary<string, CollectionWriteCandidate> candidatesByIdentityKey
    )
    {
        // Precondition: ValidateRequestSideCoverage must have run, so every visible request
        // item's identity resolves to a candidate via candidatesByIdentityKey.
        var lastRequestOrder = int.MinValue;
        foreach (var address in input.VisibleRequestItems.Select(i => i.Address))
        {
            var candidateKey = SemanticIdentityKeys.BuildKey(address.SemanticIdentityInOrder);
            var candidate = candidatesByIdentityKey[candidateKey];
            if (candidate.RequestOrder <= lastRequestOrder)
            {
                throw new InvalidOperationException(
                    $"VisibleRequestCollectionItems for scope '{input.JsonScope}' do not map to "
                        + $"strictly increasing RequestOrder values (got {candidate.RequestOrder} after {lastRequestOrder}). "
                        + "Planner invariant violated: order consistency: request."
                );
            }

            lastRequestOrder = candidate.RequestOrder;
        }
    }

    /// <summary>
    /// Formats a candidate's semantic identity as a human-readable diagnostics string. Used in
    /// exception messages alongside <see cref="LogSanitizer.SanitizeForLog"/>.
    /// </summary>
    private static string FormatCandidateIdentity(CollectionWriteCandidate candidate) =>
        SemanticIdentityKeys.FormatForDiagnostics(candidate.SemanticIdentityInOrder);

    /// <summary>
    /// Formats a semantic identity into a human-readable diagnostics string
    /// (e.g. <c>"$.addressId=\"A1\""</c>). The output embeds schema-derived
    /// <see cref="SemanticIdentityPart.RelativePath"/> values verbatim; callers that
    /// include the result in log or exception messages MUST wrap the output in
    /// <c>LogSanitizer.SanitizeForLog</c> to prevent log-forging via schema-sourced
    /// control characters.
    /// </summary>
    private static string FormatIdentity(ImmutableArray<SemanticIdentityPart> identity) =>
        SemanticIdentityKeys.FormatForDiagnostics(identity);
}

/// <summary>
/// Scope-local inputs to the planner. <see cref="RequestCandidates"/> carries the flattened
/// write values; <see cref="VisibleRequestItems"/> flags which request items are profile-visible
/// with per-item creatability; <see cref="VisibleStoredRows"/> flags which stored rows are visible
/// and names their hidden-member paths; <see cref="CurrentRows"/> is the ordered current DB state
/// for this scope instance.
/// </summary>
internal sealed record ProfileCollectionScopeInput(
    string JsonScope,
    ScopeInstanceAddress ParentScopeAddress,
    ImmutableArray<CollectionWriteCandidate> RequestCandidates,
    ImmutableArray<VisibleRequestCollectionItem> VisibleRequestItems,
    ImmutableArray<VisibleStoredCollectionRow> VisibleStoredRows,
    ImmutableArray<CurrentCollectionRowSnapshot> CurrentRows
);

/// <summary>
/// Snapshot of a single current-state collection row, paired with both its binding-indexed
/// projection (used for overlay/comparison) and its column-name-keyed projection covering
/// every column on the table model (used by hidden key-unification preservation, which must
/// read alias-only columns that are absent from <see cref="TableWritePlan.ColumnBindings"/>).
/// </summary>
internal sealed record CurrentCollectionRowSnapshot(
    long StableRowIdentity,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder,
    int StoredOrdinal,
    RelationalWriteMergedTableRow ProjectedCurrentRow,
    IReadOnlyDictionary<DbColumnName, object?> CurrentRowByColumnName
);

internal sealed record ProfileCollectionPlan(ImmutableArray<ProfileCollectionPlanEntry> Sequence);

internal abstract record ProfileCollectionPlanResult
{
    public sealed record Success(ProfileCollectionPlan Plan) : ProfileCollectionPlanResult;

    public sealed record CreatabilityRejection(CollectionRowAddress OffendingAddress, string Reason)
        : ProfileCollectionPlanResult;
}

internal abstract record ProfileCollectionPlanEntry
{
    public sealed record MatchedUpdateEntry(
        CurrentCollectionRowSnapshot StoredRow,
        CollectionWriteCandidate RequestCandidate,
        ImmutableArray<string> HiddenMemberPaths
    ) : ProfileCollectionPlanEntry;

    public sealed record HiddenPreserveEntry(CurrentCollectionRowSnapshot StoredRow)
        : ProfileCollectionPlanEntry;

    public sealed record VisibleInsertEntry(CollectionWriteCandidate RequestCandidate)
        : ProfileCollectionPlanEntry;
}
