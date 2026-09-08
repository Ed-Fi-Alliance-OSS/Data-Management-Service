// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Identifies the authorization value family evaluated by a namespace authorization check.
/// </summary>
public enum NamespaceAuthorizationFailureValueSource
{
    Stored,
    Proposed,
}

/// <summary>
/// Identifies the failed namespace authorization condition.
/// </summary>
public enum NamespaceAuthorizationFailureKind
{
    /// <summary>The API client has no namespace prefixes configured. Emitted at planner/preflight time.</summary>
    NoPrefixesConfigured,

    /// <summary>The stored namespace value is null or empty.</summary>
    StoredNamespaceUninitialized,

    /// <summary>The proposed namespace value is null or empty.</summary>
    ProposedNamespaceMissing,

    /// <summary>The namespace value does not start with any configured prefix.</summary>
    NamespaceMismatch,
}

/// <summary>
/// Cross-boundary metadata for a failed namespace authorization check.
/// </summary>
/// <remarks>
/// <see cref="ValueSource"/> is <see langword="null"/> for <see cref="NamespaceAuthorizationFailureKind.NoPrefixesConfigured"/>
/// because that case is emitted at planner/preflight time without evaluating stored or proposed data.
/// <see cref="EmittedAuth1Index"/> is <see langword="null"/> for the same reason — the no-prefixes path never reaches AUTH1.
/// </remarks>
public sealed record NamespaceAuthorizationFailure(
    NamespaceAuthorizationFailureKind FailureKind,
    NamespaceAuthorizationFailureValueSource? ValueSource,
    int? EmittedAuth1Index,
    string StrategyName,
    string[] ConfiguredNamespacePrefixes
);
