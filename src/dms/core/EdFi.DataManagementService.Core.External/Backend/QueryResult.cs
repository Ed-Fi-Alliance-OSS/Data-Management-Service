// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A query result from a query handler
/// </summary>
public record QueryResult
{
    /// <summary>
    /// A successful query request
    /// </summary>
    /// <param name="EdfiDocs">The documents returned from the query</param>
    /// <param name="TotalCount">The total number of documents returned</param>
    /// <param name="HighestSelectedDocumentId">
    /// The maximum DocumentId in the selected page keyset, or null when page selection was skipped or
    /// selected no keys, including early-empty paths and zero-size pages. Independent of
    /// <paramref name="EdfiDocs"/>: it can be non-null while the body is empty, because every selected
    /// row may be deleted before hydration completes.
    /// </param>
    public record QuerySuccess(JsonArray EdfiDocs, int? TotalCount, long? HighestSelectedDocumentId = null)
        : QueryResult()
    {
        /// <summary>
        /// Whether a DocumentId-anchored continuation may be created from
        /// <see cref="HighestSelectedDocumentId"/>. False when this page was ordered by something else,
        /// so its highest selected DocumentId does not describe where the page ended.
        /// </summary>
        /// <remarks>
        /// Independent of <see cref="HighestSelectedDocumentId"/>, and deliberately not folded into it:
        /// a page that selected keys but cannot anchor a DocumentId continuation is a different state
        /// from a page that selected none, and collapsing the two would make the maximum report a
        /// selection that happened as one that did not. Defaults to true because only page selection
        /// ordered by something other than DocumentId can invalidate the anchor, and only the two
        /// backend sites that produce a real maximum can observe that ordering; every other result
        /// carries no maximum for this to qualify. Temporary: the only ordering that sets it false is
        /// traditional paging over a max-bearing change-version window, which acquires its own
        /// ContentVersion anchor in later work.
        /// </remarks>
        public bool AllowsDocumentIdContinuation { get; init; } = true;
    }

    /// <summary>
    /// A known failure from the query handler, likely invalid query terms that
    /// evaded validation.
    /// </summary>
    public record QueryFailureKnownError(string ErrorMessage) : QueryResult();

    /// <summary>
    /// A transient failure due to a retryable condition, for example a serialization issue
    /// </summary>
    public record QueryFailureRetryable() : QueryResult();

    /// <summary>
    /// A failure because the requested read operation is intentionally not implemented.
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record QueryFailureNotImplemented(string FailureMessage) : QueryResult();

    /// <summary>
    /// A failure because security configuration metadata for the query is invalid.
    /// </summary>
    /// <param name="Errors">Actionable diagnostics describing the invalid metadata</param>
    public record QueryFailureSecurityConfiguration(
        string[] Errors,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : QueryResult();

    /// <summary>
    /// A failure because namespace authorization denied the query (the §2.9 no-prefixes preflight).
    /// Carries the namespace failure metadata so Core can build the ProblemDetails response.
    /// </summary>
    public record QueryFailureNamespaceNotAuthorized(NamespaceAuthorizationFailure NamespaceFailure)
        : QueryResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : QueryResult();

    private QueryResult() { }
}
