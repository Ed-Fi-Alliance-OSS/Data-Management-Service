// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// The form of all descriptor documents
/// </summary>
internal class DescriptorDocument(JsonNode _document)
{
    // A hardcoded identity path for all descriptor document identities
    public static readonly JsonPath DescriptorIdentityPath = new("$");

    /// <summary>
    /// Creates a new DocumentIdentity from the DescriptorDocument
    /// </summary>
    public DocumentIdentity ToDocumentIdentity()
    {
        string? namespaceName = _document["namespace"]?.GetValue<string>();
        Debug.Assert(
            namespaceName != null,
            "Failed getting namespace field, JSON schema validation not in pipeline?"
        );

        string? codeValue = _document["codeValue"]?.GetValue<string>();
        Debug.Assert(
            namespaceName != null,
            "Failed getting codeValue field, JSON schema validation not in pipeline?"
        );

        DocumentIdentityElement[] descriptorElement =
        [
            new(IdentityJsonPath: DescriptorIdentityPath, IdentityValue: $"{namespaceName}#{codeValue}")
        ];
        return new DocumentIdentity(descriptorElement);
    }

    /// <summary>
    /// Creates a new DocumentInfo from the DescriptorDocument
    /// </summary>
    public DocumentInfo ToDocumentInfo(BaseResourceInfo resourceInfo)
    {
        DocumentIdentity documentIdentity = ToDocumentIdentity();

        return new(
            DocumentIdentity: documentIdentity,
            ReferentialId: documentIdentity.ToReferentialId(resourceInfo),
            DocumentReferences: [],
            DescriptorReferences: [],
            SuperclassIdentity: null,
            SuperclassReferentialId: null
        );
    }
}
