// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// A DocumentIdentity is an array of key-value pairs that represents the complete identity of an Ed-Fi document.
/// In API documents, these are always a list of document elements from the top level of the document
/// (never nested in sub-objects, and never collections). The keys are DocumentObjectKeys. A DocumentIdentity
/// must maintain a strict ordering.
///
/// This can be an array of key-value pairs because many documents have multiple values as part of their identity.
/// </summary>
internal record DocumentIdentity(IList<IDocumentIdentityElement> _documentIdentityElements) : IDocumentIdentity
{
    /// <summary>
    /// A UUID namespace for generating UUIDv5-compliant deterministic UUIDs per RFC 4122.
    /// </summary>
    public static readonly Guid EdFiUuidv5Namespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    // Use this synthetic, hardcoded identity JsonPath for all descriptors
    public static readonly JsonPath DescriptorIdentityJsonPath = new("$.descriptor");

    /// <summary>
    /// An immutable version of the underlying identity elements, mostly for testability
    /// </summary>
    public IList<IDocumentIdentityElement> DocumentIdentityElements =>
        _documentIdentityElements.ToImmutableList();

    /// <summary>
    /// For a DocumentIdentity with a single element, returns a new DocumentIdentity with the
    /// element DocumentObjectKey replaced with a new DocumentObjectKey.
    /// </summary>
    public DocumentIdentity IdentityRename(JsonPath superclassIdentityJsonPath)
    {
        if (_documentIdentityElements.Count != 1)
        {
            throw new InvalidOperationException(
                "DocumentIdentity rename attempt with more than one element, invalid ApiSchema"
            );
        }

        IDocumentIdentityElement[] newElementList =
        [
            new DocumentIdentityElement(superclassIdentityJsonPath, _documentIdentityElements[0].IdentityValue)
        ];
        return new(newElementList.ToList());
    }

    /// <summary>
    /// Returns the string form of a ResourceInfo for identity hashing.
    /// </summary>
    private static string ResourceInfoString(BaseResourceInfo resourceInfo)
    {
        return $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}";
    }

    /// <summary>
    /// Returns the string form of a DocumentIdentity.
    /// </summary>
    private string DocumentIdentityString()
    {
        return string.Join(
            "#",
            _documentIdentityElements.Select(
                (IDocumentIdentityElement element) =>
                    $"${element.IdentityJsonPath.Value}=${element.IdentityValue}"
            )
        );
    }

    /// <summary>
    /// Returns a ReferentialId as a UUIDv5-compliant deterministic UUID per RFC 4122.
    /// </summary>
    public ReferentialId ToReferentialId(BaseResourceInfo resourceInfo)
    {
        return new(
            Deterministic.Create(
                EdFiUuidv5Namespace,
                $"{ResourceInfoString(resourceInfo)}{DocumentIdentityString()}"
            )
        );
    }
}
