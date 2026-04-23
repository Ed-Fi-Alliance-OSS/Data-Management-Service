// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Pure-function planner for a single top-level collection scope instance. Consumes the
/// Core-emitted request / stored metadata for the scope, validates fail-closed invariants,
/// and produces a sequenced <see cref="ProfileTopLevelCollectionPlan"/> describing the final
/// sibling sequence for the merge. No IO, no builders, no table-state access — the synthesizer
/// owns all runtime plumbing.
/// </summary>
internal static class ProfileTopLevelCollectionPlanner
{
    public static ProfileTopLevelCollectionPlanResult Plan(ProfileTopLevelCollectionScopeInput input)
    {
        ValidateInvariants(input);
        // Task 1.2 stub: return empty success until matching/sequencing land in Task 2.2 / Task 2.3.
        return new ProfileTopLevelCollectionPlanResult.Success(
            new ProfileTopLevelCollectionPlan(ImmutableArray<ProfileTopLevelCollectionPlanEntry>.Empty)
        );
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
    private static void ValidateInvariants(ProfileTopLevelCollectionScopeInput input)
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

    private static void ValidateCandidateScopes(ProfileTopLevelCollectionScopeInput input)
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

    private static void ValidateVisibleRequestItemScopes(ProfileTopLevelCollectionScopeInput input)
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

    private static void ValidateVisibleStoredRowScopes(ProfileTopLevelCollectionScopeInput input)
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
        ProfileTopLevelCollectionScopeInput input
    )
    {
        var currentByIdentity = new Dictionary<string, CurrentCollectionRowSnapshot>();
        foreach (var row in input.CurrentRows)
        {
            var key = BuildSemanticIdentityKey(row.SemanticIdentityInOrder);
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

    private static void ValidateUniqueVisibleStoredRows(ProfileTopLevelCollectionScopeInput input)
    {
        var seen = new HashSet<string>();
        foreach (var identity in input.VisibleStoredRows.Select(r => r.Address.SemanticIdentityInOrder))
        {
            var key = BuildSemanticIdentityKey(identity);
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
        ProfileTopLevelCollectionScopeInput input,
        Dictionary<string, CurrentCollectionRowSnapshot> currentByIdentity
    )
    {
        foreach (var identity in input.VisibleStoredRows.Select(r => r.Address.SemanticIdentityInOrder))
        {
            var key = BuildSemanticIdentityKey(identity);
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
        ProfileTopLevelCollectionScopeInput input,
        Dictionary<string, CurrentCollectionRowSnapshot> currentByIdentity
    )
    {
        // Precondition: ValidateReverseStoredCoverage must have run, so every stored row's
        // identity resolves to a current row via currentByIdentity.
        var lastStoredOrdinal = int.MinValue;
        foreach (var address in input.VisibleStoredRows.Select(r => r.Address))
        {
            var key = BuildSemanticIdentityKey(address.SemanticIdentityInOrder);
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
        ProfileTopLevelCollectionScopeInput input
    )
    {
        var candidatesByIdentityKey = new Dictionary<string, CollectionWriteCandidate>();
        foreach (var candidate in input.RequestCandidates)
        {
            var key = BuildCandidateIdentityKey(candidate);
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

    private static void ValidateUniqueVisibleRequestItems(ProfileTopLevelCollectionScopeInput input)
    {
        var seen = new HashSet<string>();
        foreach (var identity in input.VisibleRequestItems.Select(i => i.Address.SemanticIdentityInOrder))
        {
            var key = BuildSemanticIdentityKey(identity);
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
        ProfileTopLevelCollectionScopeInput input,
        Dictionary<string, CollectionWriteCandidate> candidatesByIdentityKey
    )
    {
        foreach (var identity in input.VisibleRequestItems.Select(i => i.Address.SemanticIdentityInOrder))
        {
            var candidateKey = BuildAddressAsCandidateKey(identity);
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

    private static void ValidateReverseRequestSideCoverage(ProfileTopLevelCollectionScopeInput input)
    {
        // Build a set of address-side keys from VisibleRequestItems for O(1) lookup.
        var visibleRequestKeys = input
            .VisibleRequestItems.Select(i => BuildAddressAsCandidateKey(i.Address.SemanticIdentityInOrder))
            .ToHashSet();

        foreach (var candidate in input.RequestCandidates)
        {
            var key = BuildCandidateIdentityKey(candidate);
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
        ProfileTopLevelCollectionScopeInput input,
        Dictionary<string, CollectionWriteCandidate> candidatesByIdentityKey
    )
    {
        // Precondition: ValidateRequestSideCoverage must have run, so every visible request
        // item's identity resolves to a candidate via candidatesByIdentityKey.
        var lastRequestOrder = int.MinValue;
        foreach (var address in input.VisibleRequestItems.Select(i => i.Address))
        {
            var candidateKey = BuildAddressAsCandidateKey(address.SemanticIdentityInOrder);
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
    /// Builds a string key from a <see cref="SemanticIdentityPart"/> array by serializing each
    /// part's value to its JSON string form and joining with a pipe delimiter. Used for
    /// current-row, visible-stored, and visible-request-item identity lookups.
    /// </summary>
    private static string BuildSemanticIdentityKey(ImmutableArray<SemanticIdentityPart> identity) =>
        string.Join("|", identity.Select(p => p.Value?.ToJsonString() ?? "null"));

    /// <summary>
    /// Builds a string key from a <see cref="CollectionWriteCandidate"/> by wrapping each CLR
    /// value in a <see cref="JsonValue"/> and serializing to its JSON string form, then joining
    /// with a pipe delimiter. This normalizes CLR values (e.g. <c>"A1"</c>) to the same JSON
    /// representation produced by <see cref="BuildAddressAsCandidateKey"/> (e.g. <c>"\"A1\""</c>)
    /// so that candidate keys and address keys are always comparable.
    /// </summary>
    private static string BuildCandidateIdentityKey(CollectionWriteCandidate candidate) =>
        string.Join(
            "|",
            candidate.SemanticIdentityValues.Select(v =>
                v is null ? "null" : JsonValue.Create(v)?.ToJsonString() ?? "null"
            )
        );

    /// <summary>
    /// Formats a candidate's semantic identity as a pipe-joined diagnostics string using the
    /// same key form as <see cref="BuildCandidateIdentityKey"/>. Used in exception messages
    /// where <see cref="SemanticIdentityPart"/> context is unavailable.
    /// </summary>
    private static string FormatCandidateIdentity(CollectionWriteCandidate candidate) =>
        BuildCandidateIdentityKey(candidate);

    /// <summary>
    /// Builds a candidate-compatible key from a visible-request-item's
    /// <see cref="SemanticIdentityPart"/> array by serializing each part's value the same way
    /// <see cref="BuildCandidateIdentityKey"/> serializes candidate values.
    /// </summary>
    private static string BuildAddressAsCandidateKey(ImmutableArray<SemanticIdentityPart> identity) =>
        string.Join("|", identity.Select(p => p.Value?.ToJsonString() ?? "null"));

    /// <summary>
    /// Formats a semantic identity into a human-readable diagnostics string
    /// (e.g. <c>"$.addressId=\"A1\""</c>). The output embeds schema-derived
    /// <see cref="SemanticIdentityPart.RelativePath"/> values verbatim; callers that
    /// include the result in log or exception messages MUST wrap the output in
    /// <c>LogSanitizer.SanitizeForLog</c> to prevent log-forging via schema-sourced
    /// control characters.
    /// </summary>
    private static string FormatIdentity(ImmutableArray<SemanticIdentityPart> identity) =>
        string.Join(",", identity.Select(p => $"{p.RelativePath}={p.Value?.ToJsonString() ?? "null"}"));
}

/// <summary>
/// Scope-local inputs to the planner. <see cref="RequestCandidates"/> carries the flattened
/// write values; <see cref="VisibleRequestItems"/> flags which request items are profile-visible
/// with per-item creatability; <see cref="VisibleStoredRows"/> flags which stored rows are visible
/// and names their hidden-member paths; <see cref="CurrentRows"/> is the ordered current DB state
/// for this scope instance.
/// </summary>
internal sealed record ProfileTopLevelCollectionScopeInput(
    string JsonScope,
    ScopeInstanceAddress ParentScopeAddress,
    ImmutableArray<CollectionWriteCandidate> RequestCandidates,
    ImmutableArray<VisibleRequestCollectionItem> VisibleRequestItems,
    ImmutableArray<VisibleStoredCollectionRow> VisibleStoredRows,
    ImmutableArray<CurrentCollectionRowSnapshot> CurrentRows
);

internal sealed record CurrentCollectionRowSnapshot(
    long StableRowIdentity,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder,
    int StoredOrdinal,
    RelationalWriteMergedTableRow ProjectedCurrentRow
);

internal sealed record ProfileTopLevelCollectionPlan(
    ImmutableArray<ProfileTopLevelCollectionPlanEntry> Sequence
);

internal abstract record ProfileTopLevelCollectionPlanResult
{
    public sealed record Success(ProfileTopLevelCollectionPlan Plan) : ProfileTopLevelCollectionPlanResult;

    public sealed record CreatabilityRejection(CollectionRowAddress OffendingAddress, string Reason)
        : ProfileTopLevelCollectionPlanResult;
}

internal abstract record ProfileTopLevelCollectionPlanEntry
{
    public sealed record MatchedUpdateEntry(
        CurrentCollectionRowSnapshot StoredRow,
        CollectionWriteCandidate RequestCandidate,
        ImmutableArray<string> HiddenMemberPaths
    ) : ProfileTopLevelCollectionPlanEntry;

    public sealed record HiddenPreserveEntry(CurrentCollectionRowSnapshot StoredRow)
        : ProfileTopLevelCollectionPlanEntry;

    public sealed record VisibleInsertEntry(CollectionWriteCandidate RequestCandidate)
        : ProfileTopLevelCollectionPlanEntry;
}
