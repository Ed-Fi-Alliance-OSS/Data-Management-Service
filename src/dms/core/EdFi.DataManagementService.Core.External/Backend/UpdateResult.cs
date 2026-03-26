// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// An update result from a document repository
/// </summary>
public record UpdateResult
{
    /// <summary>
    /// A successful update request
    /// </summary>
    /// <param name="ExistingDocumentUuid">The DocumentUuid of the existing document</param>
    public record UpdateSuccess(DocumentUuid ExistingDocumentUuid) : UpdateResult();

    /// <summary>
    /// A failure because the document does not exist
    /// </summary>
    public record UpdateFailureNotExists() : UpdateResult();

    /// <summary>
    /// A failure because the Etag mismatch
    /// </summary>
    public record UpdateFailureETagMisMatch() : UpdateResult();

    /// <summary>
    /// A failure because referenced documents and/or descriptors in the updated document are invalid
    /// </summary>
    /// <param name="InvalidDocumentReferences">
    /// The invalid document references keyed by concrete path instance
    /// </param>
    /// <param name="InvalidDescriptorReferences">
    /// The invalid descriptor references keyed by concrete path instance
    /// </param>
    public record UpdateFailureReference(
        DocumentReferenceFailure[] InvalidDocumentReferences,
        DescriptorReferenceFailure[] InvalidDescriptorReferences
    ) : UpdateResult()
    {
        public bool HasDocumentReferenceFailures => InvalidDocumentReferences.Length != 0;

        public bool HasDescriptorReferenceFailures => InvalidDescriptorReferences.Length != 0;

        public ResourceName[] GetResourceNames() =>
            InvalidDocumentReferences
                .Select(failure => failure.TargetResource.ResourceName)
                .Concat(InvalidDescriptorReferences.Select(failure => failure.TargetResource.ResourceName))
                .Distinct()
                .ToArray();
    }

    /// <summary>
    /// A failure because there is a different document with the same identity
    /// </summary>
    /// <param name="ResourceName">The name of the resource that failed to update</param>
    /// <param name="DuplicateIdentityValues">The identity names and values on the attempted update</param>
    public record UpdateFailureIdentityConflict(
        ResourceName ResourceName,
        IEnumerable<KeyValuePair<string, string>> DuplicateIdentityValues
    ) : UpdateResult();

    /// <summary>
    /// A transient failure due to a retryable transaction write conflict, for example a serialization issue
    /// </summary>
    public record UpdateFailureWriteConflict() : UpdateResult();

    /// <summary>
    /// A failure because the identity of this type of resource cannot be changed via update
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UpdateFailureImmutableIdentity(string FailureMessage) : UpdateResult();

    /// <summary>
    /// A failure because the client is not authorized to update the document
    /// </summary>
    public record UpdateFailureNotAuthorized(string[] ErrorMessages) : UpdateResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : UpdateResult();

    private UpdateResult() { }
}
