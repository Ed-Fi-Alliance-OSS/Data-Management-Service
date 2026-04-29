// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Domain outcome for a single separate-table non-collection scope,
/// as produced by <see cref="IProfileSeparateTableMergeDecider"/>. The decider
/// returns only this enum; typed-failure wrapping
/// (e.g. converting <see cref="RejectCreateDenied"/> into a creatability rejection)
/// happens at the synthesizer/executor seam.
/// </summary>
internal enum ProfileSeparateTableMergeOutcome
{
    /// <summary>
    /// Request-side visible-present with no matched stored row and the scope is
    /// creatable under the writable profile. Synthesizer should emit a merged row
    /// without a corresponding current row (insert semantics in the persister).
    /// </summary>
    Insert,

    /// <summary>
    /// Request-side visible-present with a matched stored visible row. Synthesizer
    /// should emit both a current row and a merged row (update semantics in the
    /// persister).
    /// </summary>
    Update,

    /// <summary>
    /// Request-side omits the scope (visible-absent/hidden) while the stored side
    /// has a matched visible row. Synthesizer should emit the current row without
    /// a merged row (delete semantics in the persister: current + no merged = delete).
    /// </summary>
    Delete,

    /// <summary>
    /// Stored side classifies the scope as Hidden relative to the writable profile
    /// and a stored row exists. Synthesizer MUST emit the current row as the merged
    /// row unchanged, because omitting the merged row would be interpreted by the
    /// persister as a delete (current + no merged = delete). Preserve keeps stored
    /// state intact across a profile-constrained write.
    /// </summary>
    Preserve,

    /// <summary>
    /// Request-side visible-present with no matched stored row but the scope is
    /// NOT creatable under the writable profile. The synthesizer/executor seam
    /// maps this to a <c>ProfileCreatabilityRejection</c> typed failure.
    /// </summary>
    RejectCreateDenied,
}

/// <summary>
/// Pure-function decider for a single separate-table non-collection scope. Returns the
/// domain outcome (see <see cref="ProfileSeparateTableMergeOutcome"/>);
/// does not perform any IO or typed-failure wrapping.
/// </summary>
internal interface IProfileSeparateTableMergeDecider
{
    /// <summary>
    /// Decides the merge outcome for a single scope given request-side state,
    /// stored-side state, and whether the separate table has a matching row.
    /// The caller is expected to skip scopes with no presence on either side;
    /// if the decider is invoked for a truly empty scope (no request state, no
    /// stored state, and no stored row), it throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    ProfileSeparateTableMergeOutcome Decide(
        string scopeJsonScope,
        RequestScopeState? requestScopeState,
        StoredScopeState? storedScopeState,
        bool storedRowExists
    );
}

/// <summary>
/// Stateless implementation of <see cref="IProfileSeparateTableMergeDecider"/>.
/// Implements the Slice 3 decision matrix as a clear if-chain with no dead
/// branches. Hidden-stored-with-row dominates all non-VisiblePresent request
/// shapes; a VisiblePresent request paired with a Hidden stored scope is an
/// inconsistent tuple under a consistent writable profile and falls through
/// to the throw so it fails closed rather than silently discarding the
/// request's visible values.
/// </summary>
internal sealed class ProfileSeparateTableMergeDecider : IProfileSeparateTableMergeDecider
{
    public ProfileSeparateTableMergeOutcome Decide(
        string scopeJsonScope,
        RequestScopeState? requestScopeState,
        StoredScopeState? storedScopeState,
        bool storedRowExists
    )
    {
        bool requestVisiblePresent =
            requestScopeState is { Visibility: ProfileVisibilityKind.VisiblePresent };
        bool storedHiddenWithRow =
            storedScopeState is { Visibility: ProfileVisibilityKind.Hidden } && storedRowExists;

        // Preserve dominates for any non-VisiblePresent request against a Hidden stored
        // scope with a row: the persister must emit the current row unchanged so the row
        // is not deleted. A VisiblePresent request against a Hidden stored scope cannot
        // occur under a consistent writable profile (Hidden is profile-level and applied
        // uniformly to both sides), so it falls through to the throw below rather than
        // silently preserving (which would discard the request's visible values) or
        // routing to Insert (which would collide with the existing row).
        if (storedHiddenWithRow && !requestVisiblePresent)
        {
            return ProfileSeparateTableMergeOutcome.Preserve;
        }

        bool storedVisibleMatched =
            storedScopeState is { Visibility: ProfileVisibilityKind.VisiblePresent } && storedRowExists;

        if (requestVisiblePresent && !storedHiddenWithRow)
        {
            if (storedVisibleMatched)
            {
                return ProfileSeparateTableMergeOutcome.Update;
            }
            return requestScopeState!.Creatable
                ? ProfileSeparateTableMergeOutcome.Insert
                : ProfileSeparateTableMergeOutcome.RejectCreateDenied;
        }

        // Only an explicit request-side VisibleAbsent + matched stored visible row drives
        // Delete. Other non-VisiblePresent request shapes (Hidden, or null) paired with a
        // matched stored visible row are inconsistent tuples / caller-contract violations
        // and must fall through to the throw below (fail closed).
        if (requestScopeState is { Visibility: ProfileVisibilityKind.VisibleAbsent } && storedVisibleMatched)
        {
            return ProfileSeparateTableMergeOutcome.Delete;
        }

        throw new InvalidOperationException(
            $"ProfileSeparateTableMergeDecider invoked for scope '{scopeJsonScope}' with no actionable state; "
                + "callers must skip scopes with neither request-side visible-present nor matched/hidden stored state."
        );
    }
}
