// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// The form of the school year enumeration document
/// </summary>
internal record SchoolYearEnumerationDocument(JsonNode _document)
{
    private static readonly JsonPath _identityJsonPath = new("$.schoolYear");

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
            new(IdentityJsonPath: _identityJsonPath, IdentityValue: schoolYearNode.ToString())
        ];
        return new DocumentIdentity(schoolYearEnumerationElement);
    }

    /// <summary>
    /// Creates a new DocumentInfo from the SchoolYearEnumerationDocument
    /// </summary>
    public DocumentInfo ToDocumentInfo(BaseResourceInfo resourceInfo)
    {
        DocumentIdentity documentIdentity = ToDocumentIdentity();
        return new(
            DocumentIdentity: documentIdentity,
            ReferentialId: ReferentialIdFrom(resourceInfo, documentIdentity),
            DocumentReferences: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }
}
