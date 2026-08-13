// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Identifies the authorization value family evaluated by a custom view-based authorization check.
/// </summary>
public enum CustomViewAuthorizationFailureValueSource
{
    Stored,
    Proposed,
}

/// <summary>
/// Identifies the failed custom view-based authorization condition.
/// </summary>
/// <remarks>
/// The stale-stored-target condition the SQL batch can also raise is deliberately absent: it is a retry
/// signal that resolves to a 404, never a response of its own. A missing or non-conforming
/// <c>auth.{StrategyName}</c> view is likewise absent — that is a security-configuration problem carrying the
/// <c>urn:ed-fi:api:system</c> 500 rather than an authorization denial.
/// </remarks>
public enum CustomViewAuthorizationFailureKind
{
    /// <summary>
    /// The basis resource's DocumentId resolved but is not present in the custom authorization view.
    /// auth.md §2.4 — authorization denied, without the EdOrg-claims relationship wording.
    /// </summary>
    NoMatchingRow,

    /// <summary>
    /// The stored basis value is null, so the existing item can never be authorized by this strategy.
    /// auth.md §2.7 — custom view, invalid data, element uninitialized.
    /// </summary>
    StoredValueUninitialized,

    /// <summary>
    /// The proposed basis value is absent, so the request body supplies nothing to authorize.
    /// auth.md §2.8 — custom view, access denied, element required.
    /// </summary>
    ProposedValueMissing,
}

/// <summary>
/// Cross-boundary metadata for a failed custom view-based authorization check.
/// </summary>
/// <param name="FailureKind">Which of §2.4 / §2.7 / §2.8 applies.</param>
/// <param name="ValueSource">Whether the stored row or the proposed request body was evaluated.</param>
/// <param name="EmittedAuth1Index">The failing check's index within the request's custom-view check list.</param>
/// <param name="StrategyName">The configured custom view-based strategy name.</param>
/// <param name="ReadableSecurableElements">
/// User-facing names of the securable element the check decided on. More than one only for a
/// composite-identity basis resource, which is what §2.4's multiple-element phrasing covers.
/// </param>
/// <param name="Hint">
/// The §"Authorization Failure Hints" sentence for this strategy, with no <c>Hint:</c> prefix; the response
/// formatter supplies that prefix.
/// </param>
public sealed record CustomViewAuthorizationFailure(
    CustomViewAuthorizationFailureKind FailureKind,
    CustomViewAuthorizationFailureValueSource ValueSource,
    int EmittedAuth1Index,
    string StrategyName,
    string[] ReadableSecurableElements,
    string? Hint
);
