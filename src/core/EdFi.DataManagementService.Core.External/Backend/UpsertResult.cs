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
    /// <param name="ResourceNames">The unique ResourceNames of the invalid references</param>
    public record UpsertFailureReference(ResourceName[] ResourceNames) : UpsertResult();

    /// <summary>
    /// A failure because referenced descriptors in the upserted document do not exist
    /// </summary>
    /// <param name="InvalidDescriptorReferences">The invalid descriptor references</param>
    public record UpsertFailureDescriptorReference(List<DescriptorReference> InvalidDescriptorReferences) : UpsertResult();

    /// <summary>
    /// A failure because there is a different document with the same identity
    /// </summary>
    /// <param name="ResourceName">The name of the resource that failed to upsert</param>
    /// <param name="DuplicateIdentityValues">The identity names and values on the attempted upsert</param>
    public record UpsertFailureIdentityConflict(ResourceName ResourceName, IEnumerable<KeyValuePair<string, string>> DuplicateIdentityValues) : UpsertResult();

    /// <summary>
    /// A transient failure due to a retryable transaction write conflict, for example a serialization issue
    /// </summary>
    public record UpsertFailureWriteConflict() : UpsertResult();

    /// <summary>
    /// A failure of unknown category
    /// </summary>
    /// <param name="FailureMessage">A message providing failure information</param>
    public record UnknownFailure(string FailureMessage) : UpsertResult();

    private UpsertResult() { }
}
