// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A partition-boundary result from a partition handler.
/// </summary>
/// <remarks>
/// Provider-neutral by construction: success carries typed inclusive DocumentId ranges only, never
/// token text and no provider syntax. Core encodes each range as a page token at the HTTP contract
/// boundary.
///
/// The failure alternatives mirror the query failures reachable from the shared capability lookup,
/// authorization resolution, and command execution the partition operation runs, so the two operations
/// cannot answer the same backend condition differently. They are named for partitions rather than
/// reused from <see cref="QueryResult"/> so a partition outcome cannot be mistaken for a GET-many one
/// at the seam. There is deliberately no known-error alternative: that query failure reports invalid
/// query terms that evaded validation against a hydrating page, and no partition path produces it, so
/// the contract does not advertise an outcome nothing can return.
/// </remarks>
public abstract record PartitionResult
{
    /// <summary>
    /// Successful partition boundaries, ascending. An empty list means no accessible candidates.
    /// </summary>
    /// <param name="Ranges">
    /// The inclusive DocumentId ranges a client can walk independently. Every range but the last is
    /// bounded above, so a later insert cannot move into a completed partition.
    /// </param>
    public sealed record PartitionSuccess(IReadOnlyList<CursorRange> Ranges) : PartitionResult;

    /// <summary>
    /// A failure because the requested partition operation is intentionally not implemented, for
    /// example a resource with no relational query capability.
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public sealed record PartitionFailureNotImplemented(string FailureMessage) : PartitionResult;

    /// <summary>
    /// A failure because security configuration metadata for the partition query is invalid.
    /// </summary>
    /// <param name="Errors">Actionable diagnostics describing the invalid metadata</param>
    public sealed record PartitionFailureSecurityConfiguration(
        string[] Errors,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : PartitionResult;

    /// <summary>
    /// A failure because namespace authorization denied the request. Carries the namespace failure
    /// metadata so Core can build the ProblemDetails response.
    /// </summary>
    public sealed record PartitionFailureNamespaceNotAuthorized(
        NamespaceAuthorizationFailure NamespaceFailure
    ) : PartitionResult;

    /// <summary>
    /// A transient failure due to a retryable condition, for example a serialization issue.
    /// </summary>
    public sealed record PartitionFailureRetryable : PartitionResult;

    /// <summary>
    /// A failure of unknown category.
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public sealed record UnknownPartitionFailure(string FailureMessage) : PartitionResult;

    private PartitionResult() { }
}
