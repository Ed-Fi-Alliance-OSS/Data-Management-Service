// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// The form of the school year enumeration document
/// </summary>
public record SchoolYearEnumerationDocument(JsonNode _document)
{
    private static readonly DocumentObjectKey _schoolYearKey = new("schoolYear");

    /// <summary>
    /// Creates a new DocumentIdentity from the SchoolYearEnumerationDocument
    /// </summary>
    public DocumentIdentity ToDocumentIdentity()
    {
        JsonValue? schoolYearNode = _document["schoolYear"]!.AsValue();

        Debug.Assert(
            schoolYearNode != null,
            "Failed getting schoolYear field, JSON schema validation not in pipeline?"
        );

        DocumentIdentityElement[] schoolYearEnumerationElement =
        [
            new(DocumentObjectKey: _schoolYearKey, DocumentValue: schoolYearNode.ToString())
        ];
        return new DocumentIdentity(schoolYearEnumerationElement);
    }

    /// <summary>
    /// Creates a new DocumentInfo from the SchoolYearEnumerationDocument
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
