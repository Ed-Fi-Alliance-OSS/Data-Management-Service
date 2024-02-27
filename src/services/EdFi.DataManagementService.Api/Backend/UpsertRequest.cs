// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Backend;

/// <summary>
/// An upsert request to a document repository
/// </summary>
/// <param name="ReferentialId">The ReferentialId of the document to upsert</param>
/// <param name="ResourceInfo">The ResourceInfo for the resource being upserted</param>
/// <param name="DocumentInfo">The DocumentInfo for the document being upserted</param>
/// <param name="EdfiDoc">The document to upsert</param>
/// <param name="validateDocumentReferencesExist">If true, validates that all references in the document exist</param>
/// <param name="TraceId">The request TraceId</param>
public record UpsertRequest(
    ReferentialId ReferentialId,
    ResourceInfo ResourceInfo,
    DocumentInfo DocumentInfo,
    JsonNode EdfiDoc,
    bool validateDocumentReferencesExist,
    TraceId TraceId
);
