// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Why a decoded ownership AUTH1 payload could not be attributed to a planned check.
/// </summary>
/// <remarks>
/// Both reasons are security-configuration problems, not client errors: the batch aborted with an ownership
/// payload that the request's own plan cannot account for, so the only safe response is the
/// <c>urn:ed-fi:api:system</c> 500. Reporting a 403 instead would attribute a denial to a strategy that was
/// never planned.
/// </remarks>
public enum OwnershipAuthorizationAuth1UnmappableReason
{
    /// <summary>
    /// The command carried no ownership check, so no ownership payload could legitimately have been raised.
    /// </summary>
    NoOwnershipCheckPlanned,

    /// <summary>
    /// The payload's configured strategy index is not the index the planned check was stamped with, so the
    /// payload cannot be the one this request's check emitted.
    /// </summary>
    ConfiguredStrategyIndexMismatch,
}

/// <summary>
/// The outcome of attributing a decoded ownership AUTH1 payload to the request's planned ownership check.
/// </summary>
/// <remarks>
/// A single tri-state result rather than the sibling families' <c>TryMap</c> plus separate stale-target
/// predicate. The three outcomes carry three different response obligations — a 403, a target re-resolution
/// retry, and a 500 — and a caller that had to remember to consult a second predicate could silently turn a
/// stale target into a 500. Returning one closed result makes every outcome a case the caller must handle.
/// </remarks>
public abstract record OwnershipAuthorizationAuth1MapResult
{
    private OwnershipAuthorizationAuth1MapResult() { }

    /// <summary>The check denied the request: §2.13 or §2.14, a 403.</summary>
    public sealed record Denied(OwnershipAuthorizationFailure Failure) : OwnershipAuthorizationAuth1MapResult;

    /// <summary>
    /// The stored target vanished between the unlocked lookup and the check. A retry signal that resolves to
    /// a 404 on re-resolution, never a response of its own.
    /// </summary>
    public sealed record StaleStoredTarget : OwnershipAuthorizationAuth1MapResult;

    /// <summary>The payload cannot be attributed to a planned check: a security-configuration 500.</summary>
    public sealed record Unmappable(OwnershipAuthorizationAuth1UnmappableReason Reason)
        : OwnershipAuthorizationAuth1MapResult;
}

/// <summary>
/// Maps a decoded ownership AUTH1 payload back to a cross-boundary
/// <see cref="OwnershipAuthorizationFailure"/>, or to the retry / security-configuration outcomes.
/// </summary>
/// <remarks>
/// Attribution is an equality check, not a lookup. Ownership plans exactly one check per operation, so the
/// payload's configured strategy index must equal the index that check was stamped with; anything else is a
/// payload this request's plan cannot account for. The sibling families index into a list of emitted checks
/// instead, because they emit several.
/// </remarks>
public static class OwnershipAuthorizationFailureMapper
{
    /// <param name="payload">The decoded <c>own1</c> payload.</param>
    /// <param name="plannedConfiguredStrategyIndex">
    /// The configured strategy index the request's single planned ownership check was stamped with, or
    /// <see langword="null"/> when the request planned no ownership check at all.
    /// </param>
    public static OwnershipAuthorizationAuth1MapResult Map(
        OwnershipAuthorizationAuth1FailurePayload payload,
        int? plannedConfiguredStrategyIndex
    )
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (plannedConfiguredStrategyIndex is not { } plannedIndex)
        {
            return new OwnershipAuthorizationAuth1MapResult.Unmappable(
                OwnershipAuthorizationAuth1UnmappableReason.NoOwnershipCheckPlanned
            );
        }

        if (payload.ConfiguredStrategyIndex != plannedIndex)
        {
            return new OwnershipAuthorizationAuth1MapResult.Unmappable(
                OwnershipAuthorizationAuth1UnmappableReason.ConfiguredStrategyIndexMismatch
            );
        }

        // Checked after attribution: a stale payload is only a trustworthy retry signal once it is known to
        // have come from this request's own check.
        if (payload.FailureKind is OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing)
        {
            return new OwnershipAuthorizationAuth1MapResult.StaleStoredTarget();
        }

        return new OwnershipAuthorizationAuth1MapResult.Denied(
            new OwnershipAuthorizationFailure(
                MapFailureKind(payload.FailureKind),
                payload.ConfiguredStrategyIndex,
                AuthorizationStrategyNameConstants.OwnershipBased
            )
        );
    }

    private static OwnershipAuthorizationFailureKind MapFailureKind(
        OwnershipAuthorizationAuth1FailureKind failureKind
    ) =>
        failureKind switch
        {
            OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch =>
                OwnershipAuthorizationFailureKind.OwnershipTokenMismatch,
            OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized =>
                OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized,
            // StoredTargetMissing is answered above and has no cross-boundary failure representation.
            _ => throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported AUTH1 ownership failure kind."
            ),
        };
}
