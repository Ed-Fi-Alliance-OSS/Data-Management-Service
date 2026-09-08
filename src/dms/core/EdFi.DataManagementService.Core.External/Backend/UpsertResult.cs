// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Identifies why a POST/upsert operation intentionally failed closed with a temporary not-implemented
/// response.
/// </summary>
public enum UpsertFailureNotImplementedReason
{
    StrategyNotEnabled,
}

/// <summary>
/// An upsert result from a document repository
/// </summary>
public record UpsertResult
{
    /// <summary>
    /// A successful upsert request that took the form of an insert
    /// </summary>
    /// <param name="NewDocumentUuid">The DocumentUuid of the new document</param>
    /// <param name="ETag">The required response ETag to emit for the committed representation.</param>
    public record InsertSuccess(DocumentUuid NewDocumentUuid, string ETag) : UpsertResult()
    {
        private string _etag = RequireEtag(ETag);

        public string ETag
        {
            get => _etag;
            init => _etag = RequireEtag(value);
        }
    }

    /// <summary>
    /// A successful upsert request that took the form of an update
    /// </summary>
    /// <param name="ExistingDocumentUuid">The DocumentUuid of the existing document</param>
    /// <param name="ETag">The required response ETag to emit for the committed representation.</param>
    public record UpdateSuccess(DocumentUuid ExistingDocumentUuid, string ETag) : UpsertResult()
    {
        private string _etag = RequireEtag(ETag);

        public string ETag
        {
            get => _etag;
            init => _etag = RequireEtag(value);
        }
    }

    /// <summary>
    /// A failure because referenced documents and/or descriptors in the upserted document are invalid
    /// </summary>
    /// <param name="InvalidDocumentReferences">
    /// The invalid document references keyed by concrete path instance
    /// </param>
    /// <param name="InvalidDescriptorReferences">
    /// The invalid descriptor references keyed by concrete path instance
    /// </param>
    public record UpsertFailureReference(
        DocumentReferenceFailure[] InvalidDocumentReferences,
        DescriptorReferenceFailure[] InvalidDescriptorReferences
    ) : UpsertResult()
    {
        public bool HasDocumentReferenceFailures => InvalidDocumentReferences.Length != 0;

        public bool HasDescriptorReferenceFailures => InvalidDescriptorReferences.Length != 0;
    }

    /// <summary>
    /// A failure because there is a different document with the same identity
    /// </summary>
    /// <param name="ResourceName">The name of the resource that failed to upsert</param>
    /// <param name="DuplicateIdentityValues">The identity names and values on the attempted upsert</param>
    public record UpsertFailureIdentityConflict(
        ResourceName ResourceName,
        IEnumerable<KeyValuePair<string, string>> DuplicateIdentityValues
    ) : UpsertResult();

    /// <summary>
    /// A transient failure due to a retryable transaction write conflict, for example a serialization issue
    /// </summary>
    public record UpsertFailureWriteConflict() : UpsertResult();

    /// <summary>
    /// A failure because the request's If-Match or If-None-Match ETag precondition was not satisfied
    /// </summary>
    /// <param name="Reason">Machine-readable reason for the precondition failure</param>
    public record UpsertFailureETagMisMatch(
        ETagPreconditionFailureReason Reason = ETagPreconditionFailureReason.Concurrency
    ) : UpsertResult();

    /// <summary>
    /// A failure because the client is not authorized to upsert the document
    /// </summary>
    public record UpsertFailureNotAuthorized(string[] ErrorMessages, string[]? Hints = null) : UpsertResult();

    /// <summary>
    /// A failure because proposed-value or existing-target relationship authorization denied the upsert.
    /// </summary>
    public record UpsertFailureRelationshipNotAuthorized(RelationshipAuthorizationFailure RelationshipFailure)
        : UpsertResult();

    /// <summary>
    /// A failure because proposed-value namespace authorization denied the POST/upsert. Carries the
    /// namespace failure metadata so Core can build the §2.9-§2.12 ProblemDetails response.
    /// </summary>
    public record UpsertFailureNamespaceNotAuthorized(NamespaceAuthorizationFailure NamespaceFailure)
        : UpsertResult();

    /// <summary>
    /// A failure because the requested POST/upsert operation is intentionally not implemented.
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    /// <param name="Reason">Machine-readable reason for the temporary fail-closed outcome</param>
    public record UpsertFailureNotImplemented(string FailureMessage, UpsertFailureNotImplementedReason Reason)
        : UpsertResult();

    /// <summary>
    /// A failure because security configuration metadata for the POST/upsert operation is invalid.
    /// </summary>
    /// <param name="Errors">Actionable diagnostics describing the invalid metadata</param>
    public record UpsertFailureSecurityConfiguration(
        string[] Errors,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : UpsertResult();

    /// <summary>
    /// A failure because the request body violated a write-path validation guard rail
    /// </summary>
    /// <param name="ValidationFailures">Concrete validation failures keyed by JSON path</param>
    public record UpsertFailureValidation(WriteValidationFailure[] ValidationFailures) : UpsertResult();

    /// <summary>
    /// A failure because the request attempted to change immutable identifying values
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UpsertFailureImmutableIdentity(string FailureMessage) : UpsertResult();

    /// <summary>
    /// A failure because a writable profile's data policy rejected the request
    /// </summary>
    /// <param name="ProfileName">The profile enforcing the rejection</param>
    public record UpsertFailureProfileDataPolicy(string ProfileName) : UpsertResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : UpsertResult();

    private static string RequireEtag(string etag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        return etag;
    }

    private UpsertResult() { }
}
