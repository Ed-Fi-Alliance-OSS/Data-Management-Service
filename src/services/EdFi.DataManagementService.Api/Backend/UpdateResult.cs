// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
namespace EdFi.DataManagementService.Api.Backend;

/// <summary>
/// An update result from a document repository
/// </summary>
public record UpdateResult
{
    /// <summary>
    /// A successful update request
    /// </summary>
    public record UpdateSuccess() : UpdateResult();

    /// <summary>
    /// A failure because the document does not exist
    /// </summary>
    public record UpdateFailureNotExists() : UpdateResult();

    /// <summary>
    /// A failure because referenced documents in the updated document do not exist
    /// </summary>
    /// <param name="ReferencingDocumentInfo">Information about the referencing documents</param>
    public record UpdateFailureReference(string ReferencingDocumentInfo) : UpdateResult();

    /// <summary>
    /// A failure because there is a different document with the same identity
    /// </summary>
    /// <param name="ReferencingDocumentInfo">Information about the existing document</param>
    public record UpdateFailureIdentityConflict(string ReferencingDocumentInfo) : UpdateResult();

    /// <summary>
    /// A transient failure due to a transaction write conflict
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UpdateFailureWriteConflict(string FailureMessage) : UpdateResult();

    /// <summary>
    /// A failure because the identity of this type of resource cannot be changed via update
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UpdateFailureImmutableIdentity(string FailureMessage) : UpdateResult();

    /// <summary>
    /// A failure because an update cascade is required
    /// </summary>
    public record UpdateFailureCascadeRequired() : UpdateResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : UpdateResult();

    private UpdateResult() { }
}
