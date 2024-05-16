// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// An update request to a document repository
/// </summary>
public record UpdateRequest(
    /// <summary>
    /// The ReferentialId of the document to update
    /// </summary>
    ReferentialId ReferentialId,
    /// <summary>
    /// The ResourceInfo of the document to update
    /// </summary>
    ResourceInfo ResourceInfo,
    /// <summary>
    /// The DocumentInfo of the document to update
    /// </summary>
    DocumentInfo DocumentInfo,
    /// <summary>
    /// The EdfiDoc of the document to update, as a JsonNode
    /// </summary>
    JsonNode EdfiDoc,
    /// <summary>
    /// If true, validates that all references in the document exist
    /// </summary>
    bool validateDocumentReferencesExist,
    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId,
    /// <summary>
    /// The DocumentUuid of the document to update
    /// </summary>
    DocumentUuid DocumentUuid
);
