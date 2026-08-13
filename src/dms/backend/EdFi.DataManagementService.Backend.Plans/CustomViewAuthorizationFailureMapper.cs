// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Maps a decoded custom view-based AUTH1 payload back to a cross-boundary
/// <see cref="CustomViewAuthorizationFailure"/>.
/// </summary>
/// <remarks>
/// The payload carries only an index and a failure kind, so the planned check list is what supplies the
/// strategy name, readable securable elements, and hint. The index is therefore validated against that list
/// and the kind against the indexed check's value source: a payload the planner could not have produced maps
/// to nothing and falls through to the invalid-payload security-configuration path rather than becoming a
/// misleading 403.
/// </remarks>
public static class CustomViewAuthorizationFailureMapper
{
    public static bool TryMapAuth1Failure(
        CustomViewAuthorizationAuth1FailurePayload payload,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks,
        out CustomViewAuthorizationFailure? failure
    )
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(plannedChecks);

        failure = null;

        if (payload.EmittedAuth1Index >= plannedChecks.Count)
        {
            return false;
        }

        var check = plannedChecks[payload.EmittedAuth1Index];

        if (!IsFailureKindCompatibleWithValueSource(payload.FailureKind, check.ValueSource))
        {
            return false;
        }

        if (!TryMapFailureKind(payload.FailureKind, out var failureKind))
        {
            return false;
        }

        failure = new CustomViewAuthorizationFailure(
            failureKind,
            MapValueSource(check.ValueSource),
            payload.EmittedAuth1Index,
            check.ConfiguredStrategy.StrategyName,
            [.. check.ReadableSecurableElements],
            check.FailureHint
        );
        return true;
    }

    /// <summary>
    /// Whether <paramref name="payload"/> reports that the stored target row no longer exists. Read paths
    /// map this to a retry so the target is re-resolved and a still-missing row surfaces as a 404 rather
    /// than an authorization denial; locked write and delete paths never observe it.
    /// </summary>
    /// <remarks>
    /// Only honored when the indexed planned check is a stored-value check, which is the only shape the SQL
    /// compiler emits the stale kind from. A payload pairing the stale kind with a proposed check, or with an
    /// out-of-range index, is malformed and returns <see langword="false"/> so it reaches the
    /// invalid-payload security-configuration path instead of silently becoming a retry.
    /// </remarks>
    public static bool IsStaleStoredTargetFailure(
        CustomViewAuthorizationAuth1FailurePayload payload,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks
    )
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(plannedChecks);

        return payload.FailureKind is CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing
            && payload.EmittedAuth1Index < plannedChecks.Count
            && plannedChecks[payload.EmittedAuth1Index].ValueSource
                is CustomViewAuthorizationCheckValueSource.Stored;
    }

    private static bool IsFailureKindCompatibleWithValueSource(
        CustomViewAuthorizationAuth1FailureKind failureKind,
        CustomViewAuthorizationCheckValueSource valueSource
    ) =>
        (failureKind, valueSource) switch
        {
            (CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow, _) => true,
            (
                CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull,
                CustomViewAuthorizationCheckValueSource.Stored
            ) => true,
            (
                CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing,
                CustomViewAuthorizationCheckValueSource.Proposed
            ) => true,
            _ => false,
        };

    /// <summary>
    /// Maps the payload kind to its cross-boundary counterpart. The stale-target kind has none — it is a
    /// retry signal, not a response — so it maps to nothing rather than throwing, and
    /// <see cref="IsStaleStoredTargetFailure"/> is the seam that recognizes it.
    /// </summary>
    private static bool TryMapFailureKind(
        CustomViewAuthorizationAuth1FailureKind failureKind,
        out CustomViewAuthorizationFailureKind mappedFailureKind
    )
    {
        switch (failureKind)
        {
            case CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow:
                mappedFailureKind = CustomViewAuthorizationFailureKind.NoMatchingRow;
                return true;
            case CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull:
                mappedFailureKind = CustomViewAuthorizationFailureKind.StoredValueUninitialized;
                return true;
            case CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing:
                mappedFailureKind = CustomViewAuthorizationFailureKind.ProposedValueMissing;
                return true;
            default:
                mappedFailureKind = CustomViewAuthorizationFailureKind.NoMatchingRow;
                return false;
        }
    }

    private static CustomViewAuthorizationFailureValueSource MapValueSource(
        CustomViewAuthorizationCheckValueSource valueSource
    ) =>
        valueSource switch
        {
            CustomViewAuthorizationCheckValueSource.Stored =>
                CustomViewAuthorizationFailureValueSource.Stored,
            CustomViewAuthorizationCheckValueSource.Proposed =>
                CustomViewAuthorizationFailureValueSource.Proposed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(valueSource),
                valueSource,
                "Unsupported custom view authorization value source."
            ),
        };
}
