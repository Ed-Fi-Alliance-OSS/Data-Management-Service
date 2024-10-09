// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A get result from a document repository
/// </summary>
public record GetResult
{
    /// <summary>
    /// A successful get request
    /// </summary>
    /// <param name="DocumentUuid">The DocumentUuid of the document</param>
    /// <param name="EdfiDoc">The document itself</param>
    /// <param name="LastModifiedDate">The date the document was last modified</param>
    public record GetSuccess(DocumentUuid DocumentUuid, JsonNode EdfiDoc, DateTime LastModifiedDate, string LastModifiedTraceId)
        : GetResult();

    /// <summary>
    /// A failure because the document does not exist
    /// </summary>
    public record GetFailureNotExists() : GetResult();

    /// <summary>
    /// A transient failure due to a retryable condition, for example a serialization issue
    /// </summary>
    public record GetFailureRetryable() : GetResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : GetResult();

    private GetResult() { }
}
