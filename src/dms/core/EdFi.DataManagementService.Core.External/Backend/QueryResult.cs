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
    public record QuerySuccess(JsonArray EdfiDocs, int? TotalCount)
        : QueryResult();

    /// <summary>
    /// A failure because the query was invalid
    /// </summary>
    public record QueryFailureInvalidQuery(string FailureMessage) : QueryResult();

    /// <summary>
    /// A transient failure due to a retryable condition, for example a serialization issue
    /// </summary>
    public record QueryFailureRetryable() : QueryResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : QueryResult();

    private QueryResult() { }
}
