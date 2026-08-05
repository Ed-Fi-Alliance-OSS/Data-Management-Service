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
    /// row may be deleted before hydration completes. Populated by cursor execution in a later story.
    /// </param>
    public record QuerySuccess(JsonArray EdfiDocs, int? TotalCount, long? HighestSelectedDocumentId = null)
        : QueryResult();

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
