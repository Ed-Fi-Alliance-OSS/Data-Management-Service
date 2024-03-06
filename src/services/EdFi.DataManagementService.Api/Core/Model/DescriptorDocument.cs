// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// The form of all descriptor documents
/// </summary>
public class DescriptorDocument(JsonNode _document)
{
    private static readonly DocumentObjectKey _descriptorKey = new("descriptor");

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
            new(DocumentObjectKey: _descriptorKey, DocumentValue: $"{namespaceName}#{codeValue}")
        ];
        return new DocumentIdentity(descriptorElement);
    }

    /// <summary>
    /// Creates a new DocumentInfo from the DescriptorDocument
    /// </summary>
    public DocumentInfo ToDocumentInfo()
    {
        return new(
            DocumentIdentity: ToDocumentIdentity(),
            DocumentReferences: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }
}
