// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
namespace EdFi.DataManagementService.Api.Core.Backend;

/// <summary>
/// An upsert result from a document repository
/// </summary>
public record UpsertResult
{
    /// <summary>
    /// A successful upsert request that took the form of an insert
    /// </summary>
    public record InsertSuccess() : UpsertResult();

    /// <summary>
    /// A successful upsert request that took the form of an update
    /// </summary>
    public record UpdateSuccess() : UpsertResult();

    /// <summary>
    /// A failure because referenced documents in the upserted document do not exist
    /// </summary>
    /// <param name="ReferencingDocumentInfo">Information about the referencing documents</param>
    public record UpsertFailureReference(string ReferencingDocumentInfo) : UpsertResult();

    /// <summary>
    /// A failure because there is a different document with the same identity
    /// </summary>
    /// <param name="ReferencingDocumentInfo">Information about the existing document</param>
    public record UpsertFailureIdentityConflict(string ReferencingDocumentInfo) : UpsertResult();

    /// <summary>
    /// A transient failure due to a transaction write conflict
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UpsertFailureWriteConflict(string FailureMessage) : UpsertResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : UpsertResult();

    private UpsertResult() { }
}
