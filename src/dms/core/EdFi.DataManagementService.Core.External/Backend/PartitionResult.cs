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
/// Provider-neutral by construction: success carries typed inclusive anchor ranges only, never token
/// text and no provider syntax. The values are <c>DocumentId</c> or <c>ContentVersion</c> depending
/// on how the request resolved its anchor, and one range type carries either because both name a
/// <c>bigint</c> column. Neither is a dense sequence, and nothing here needs one to be: the ranges
/// are assembled from anchor values read at boundary row numbers rather than computed from a range
/// width. Core encodes each range as a page token at the HTTP contract boundary.
///
/// The failure alternatives mirror the query failure set the shared capability lookup, authorization
/// resolution, and command execution report, so the two operations cannot answer the same backend
/// condition differently. Mirroring the set rather than only the conditions a partition request reaches
/// means an alternative may have no producer: nothing classifies a provider fault as retryable on
/// either operation today, so <see cref="PartitionFailureRetryable"/> is carried for the query
/// alternative it mirrors rather than because a partition path constructs it. They are named for
/// partitions rather than reused from <see cref="QueryResult"/> so a partition outcome cannot be
/// mistaken for a GET-many one at the seam. There is deliberately no known-error alternative: that
/// query failure reports invalid query terms that evaded validation against a hydrating page, and no
/// partition path produces it, so the contract does not advertise an outcome nothing can return.
/// </remarks>
public abstract record PartitionResult
{
    /// <summary>
    /// Successful partition boundaries, ascending. An empty list means no accessible candidates.
    /// </summary>
    /// <param name="Ranges">
    /// The inclusive ranges a client can walk independently, in the units of the anchor the request
    /// resolved. Every range but the last is bounded above, so a later write cannot move into a
    /// completed partition: under a <c>DocumentId</c> anchor the mover would be an insert, and under a
    /// <c>ContentVersion</c> anchor an update, which re-stamps the row above every closed range.
    /// </param>
    public sealed record PartitionSuccess(IReadOnlyList<CursorRange> Ranges) : PartitionResult
    {
        /// <summary>
        /// No candidate selection command was issued; this empty success is a short-circuit.
        /// </summary>
        /// <remarks>
        /// Defaults to false, so a boundary command that executed and found no starts stays correct
        /// without edits. Only the deliberate short-circuits set it true, which is what separates
        /// "no database work was done" from "the command ran and matched nothing".
        /// </remarks>
        public bool SelectionSkipped { get; init; }
    }

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
