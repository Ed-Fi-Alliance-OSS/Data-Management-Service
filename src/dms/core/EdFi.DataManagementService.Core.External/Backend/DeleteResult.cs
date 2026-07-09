// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A delete result from a document repository
/// </summary>
public abstract record DeleteResult
{
    /// <summary>
    /// A successful delete request
    /// </summary>
    public record DeleteSuccess() : DeleteResult();

    /// <summary>
    /// A failure because the document does not exist
    /// </summary>
    public record DeleteFailureNotExists() : DeleteResult();

    /// <summary>
    /// A failure because of the existence of referencing documents
    /// </summary>
    /// <param name="ReferencingDocumentResourceNames">Resource names of referencing documents</param>
    public record DeleteFailureReference(string[] ReferencingDocumentResourceNames) : DeleteResult();

    /// <summary>
    /// A transient failure due to a retryable transaction write conflict, for example a serialization issue
    /// </summary>
    public record DeleteFailureWriteConflict() : DeleteResult();

    /// <summary>
    /// A failure because the client is not authorized to delete the document
    /// </summary>
    public record DeleteFailureNotAuthorized(string[] ErrorMessages, string[]? Hints = null) : DeleteResult();

    /// <summary>
    /// A failure because stored relationship authorization denied deletion of the document.
    /// </summary>
    public record DeleteFailureRelationshipNotAuthorized(RelationshipAuthorizationFailure RelationshipFailure)
        : DeleteResult();

    /// <summary>
    /// A failure because stored namespace authorization denied the delete. Carries the namespace
    /// failure metadata so Core can build the §2.9-§2.12 ProblemDetails response.
    /// </summary>
    public record DeleteFailureNamespaceNotAuthorized(NamespaceAuthorizationFailure NamespaceFailure)
        : DeleteResult();

    /// <summary>
    /// A failure because the requested delete operation is intentionally not implemented.
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record DeleteFailureNotImplemented(string FailureMessage) : DeleteResult();

    /// <summary>
    /// A failure because security configuration metadata for the delete operation is invalid.
    /// </summary>
    /// <param name="Errors">Actionable diagnostics describing the invalid metadata</param>
    public record DeleteFailureSecurityConfiguration(
        string[] Errors,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : DeleteResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : DeleteResult();

    /// <summary>
    /// A failure because the Etag mismatch
    /// </summary>
    /// <param name="Reason">Machine-readable reason for the precondition failure</param>
    public record DeleteFailureETagMisMatch(
        ETagPreconditionFailureReason Reason = ETagPreconditionFailureReason.Concurrency
    ) : DeleteResult();

    private DeleteResult() { }
}
