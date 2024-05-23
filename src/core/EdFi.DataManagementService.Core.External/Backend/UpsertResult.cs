// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// An upsert result from a document repository
/// </summary>
public record UpsertResult
{
    /// <summary>
    /// A successful upsert request that took the form of an insert
    /// </summary>
    /// <param name="NewDocumentUuid">The DocumentUuid of the new document</param>
    public record InsertSuccess(DocumentUuid NewDocumentUuid) : UpsertResult();

    /// <summary>
    /// A successful upsert request that took the form of an update
    /// </summary>
    /// <param name="ExistingDocumentUuid">The DocumentUuid of the existing document</param>
    public record UpdateSuccess(DocumentUuid ExistingDocumentUuid) : UpsertResult();

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
