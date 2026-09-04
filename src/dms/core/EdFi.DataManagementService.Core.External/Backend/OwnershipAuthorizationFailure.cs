// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Identifies the failed ownership-based authorization condition.
/// </summary>
/// <remarks>
/// <para>
/// Both kinds describe an <em>existing</em> stored document, because ownership authorizes only stored values:
/// the API client's <c>OwnershipTokenIds</c> authorize reads and mutations, while the single
/// <c>CreatorOwnershipTokenId</c> is used solely to stamp <c>dms.Document.CreatedByOwnershipTokenId</c> on
/// creation and is never an authorization input. A create therefore has nothing for this strategy to deny.
/// </para>
/// <para>
/// Two conditions the SQL check can also raise are deliberately absent. A stale stored target is a retry
/// signal that resolves to a 404, never a response of its own. An <c>OwnershipTokenIds</c> count at or above
/// the defensive limit is a security-configuration problem carrying the <c>urn:ed-fi:api:system</c> 500,
/// reported as a planner terminal rather than as an authorization denial.
/// </para>
/// </remarks>
public enum OwnershipAuthorizationFailureKind
{
    /// <summary>
    /// The stored <c>CreatedByOwnershipTokenId</c> is non-null but matches none of the caller's ownership
    /// tokens. auth.md §2.13 — ownership, access denied, ownership mismatch.
    /// </summary>
    OwnershipTokenMismatch,

    /// <summary>
    /// The stored <c>CreatedByOwnershipTokenId</c> is null, so the existing item can never be reached through
    /// ownership-based authorization. auth.md §2.14 — ownership, invalid data, ownership uninitialized.
    /// </summary>
    StoredOwnershipTokenUninitialized,
}

/// <summary>
/// Cross-boundary metadata for a failed ownership-based authorization check.
/// </summary>
/// <param name="FailureKind">Which of §2.13 / §2.14 applies.</param>
/// <param name="ConfiguredStrategyIndex">
/// Zero-based position of <c>OwnershipBased</c> in the CMS-configured strategy list for this request — the
/// configured index, not an emitted statement ordinal.
/// <para>
/// This differs from the namespace and custom view-based failure records, which carry an emitted AUTH1
/// ordinal, and the difference is deliberate. Those strategies emit several checks per request that share one
/// provider exception, so they need an emitted ordinal to resolve a payload to a specific check within a
/// batch. Ownership emits exactly one check per operation, so an emitted ordinal would be a constant zero
/// carrying no information, while the configured index identifies the strategy that denied the request — which
/// is what the AUTH1 design calls for.
/// </para>
/// <para>
/// Non-nullable because every ownership denial arrives through AUTH1. Ownership has no planner/preflight 403
/// analogous to the namespace no-prefixes-configured case: a caller with an empty ownership-token list still
/// executes the stored-row check, so that the response can distinguish a stored null (§2.14) from a
/// non-matching stored value (§2.13) rather than guessing.
/// </para>
/// </param>
/// <param name="StrategyName">The configured strategy name — always <c>OwnershipBased</c>.</param>
/// <remarks>
/// No value-source discriminator: unlike namespace and custom view-based authorization, ownership evaluates
/// only stored values, so there is no proposed-value counterpart to distinguish. §2.13's and §2.14's
/// <c>detail</c> text carries no existing/proposed variation either, so nothing downstream needs one.
/// </remarks>
public sealed record OwnershipAuthorizationFailure(
    OwnershipAuthorizationFailureKind FailureKind,
    int ConfiguredStrategyIndex,
    string StrategyName
);
