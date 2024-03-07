// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using static EdFi.DataManagementService.Api.Core.Model.HashUtility;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// A DocumentIdentity is an array of key-value pairs that represents the complete identity of an Ed-Fi document.
/// In API documents, these are always a list of document elements from the top level of the document
/// (never nested in sub-objects, and never collections). The keys are DocumentObjectKeys. A DocumentIdentity
/// must maintain a strict ordering.
///
/// This can be an array of key-value pairs because many documents have multiple values as part of their identity.
/// </summary>
public record DocumentIdentity(IList<DocumentIdentityElement> _documentIdentityElements)
{
    /// <summary>
    /// An immutable version of the underlying identity elements, mostly for testability
    /// </summary>
    public IList<DocumentIdentityElement> DocumentIdentityElements =>
        _documentIdentityElements.ToImmutableList();

    /// <summary>
    /// For a DocumentIdentity with a single element, returns a new DocumentIdentity with the
    /// element DocumentObjectKey replaced with a new DocumentObjectKey.
    /// </summary>
    public DocumentIdentity IdentityRename(DocumentObjectKey originalKey, DocumentObjectKey replacementKey)
    {
        if (_documentIdentityElements.Count != 1)
        {
            throw new InvalidOperationException(
                "DocumentIdentity rename attempt with more than one element, invalid ApiSchema"
            );
        }

        if (_documentIdentityElements[0].DocumentObjectKey != originalKey)
        {
            throw new InvalidOperationException(
                "DocumentIdentity rename attempt with wrong original key name, invalid ApiSchema"
            );
        }

        DocumentIdentityElement[] newElementList =
        [
            _documentIdentityElements[0] with
            {
                DocumentObjectKey = replacementKey
            }
        ];
        return new(newElementList.ToList());
    }

    /// <summary>
    /// Returns the 12 byte SHAKE256 Base64Url encoded hash form of a ResourceInfo.
    /// </summary>
    public static string ResourceInfoHashFrom(BaseResourceInfo resourceInfo)
    {
        return ToHash($"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}", 12);
    }

    /// <summary>
    /// Returns the 16 byte SHAKE256 Base64Url encoded hash form of a DocumentIdentity.
    /// </summary>
    private string DocumentIdentityHashFrom()
    {
        string documentIdentityString = string.Join(
            "#",
            _documentIdentityElements.Select(
                (DocumentIdentityElement element) =>
                    $"${element.DocumentObjectKey.Value}=${element.DocumentValue}"
            )
        );

        return ToHash(documentIdentityString, 16);
    }

    /// <summary>
    /// Returns a 224-bit ReferentialId for the document identity and given BaseResourceInfo, as a concatenation
    /// of two Base64Url hashes.
    ///
    /// The first 96 bits (12 bytes) are a SHAKE256 Base64Url encoded hash of the resource info.
    /// The remaining 128 bits (16 bytes) are a SHAKE256 Base64Url encoded hash of the document identity.
    ///
    /// The resulting Base64Url string is 38 characters long. The first 16 characters are the resource info hash
    /// and the remaining 22 characters are the identity hash.
    /// </summary>
    public ReferentialId ToReferentialId(BaseResourceInfo resourceInfo)
    {
        return new($"{ResourceInfoHashFrom(resourceInfo)}{DocumentIdentityHashFrom()}");
    }
}
