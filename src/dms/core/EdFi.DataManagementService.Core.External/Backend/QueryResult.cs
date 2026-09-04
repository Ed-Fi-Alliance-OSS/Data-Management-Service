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
    /// <param name="HighestSelectedAnchor">
    /// The maximum continuation-anchor value in the selected page keyset, or null when page selection
    /// was skipped or selected no keys, including early-empty paths and zero-size pages. Its units
    /// follow the anchor the request resolved: ContentVersion for a max-bearing change-version
    /// window against any data source, and for any windowed shape served from a frozen snapshot;
    /// DocumentId otherwise, and DocumentId throughout while the
    /// UseLegacyDocumentIdOrderingForChangeQueries switch is set. Independent of
    /// <paramref name="EdfiDocs"/>: it can be non-null while the body is empty, because every selected
    /// row may be deleted before hydration completes.
    /// </param>
    /// <remarks>
    /// There is no companion flag saying whether the maximum may anchor a continuation. Every page that
    /// selects keys now reports the maximum of the key it was actually ordered by, so a non-null maximum
    /// always describes where that page ended; the state a flag once distinguished — a page that
    /// selected keys but was ordered by something the maximum could not express — no longer exists.
    /// </remarks>
    public record QuerySuccess(JsonArray EdfiDocs, int? TotalCount, long? HighestSelectedAnchor = null)
        : QueryResult()
    {
        /// <summary>
        /// No candidate selection command was issued; this empty success is a short-circuit.
        /// </summary>
        /// <remarks>
        /// Defaults to false, so every site that actually selected is already correct — including the
        /// executed pages that legitimately return nothing. Only the deliberate short-circuits set it
        /// true. Without it, an empty success from a real selection is indistinguishable from one where
        /// no command ran at all, and the two are different facts about the request.
        /// </remarks>
        public bool SelectionSkipped { get; init; }
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
