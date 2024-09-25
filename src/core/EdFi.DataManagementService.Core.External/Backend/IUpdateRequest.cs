// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using DocumentInfo = EdFi.DataManagementService.Core.External.Model.DocumentInfo;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// An update request to a document repository
/// </summary>
public interface IUpdateRequest
{
    /// <summary>
    /// The ResourceInfo of the document to update
    /// </summary>
    ResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The DocumentInfo of the document to update
    /// </summary>
    DocumentInfo DocumentInfo { get; }

    /// <summary>
    /// The EdfiDoc of the document to update, as a JsonNode
    /// </summary>
    JsonNode EdfiDoc { get; }

    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId { get; }

    /// <summary>
    /// The DocumentUuid of the document to update
    /// </summary>
    DocumentUuid DocumentUuid { get; }

    IUpdateCascadeHandler UpdateCascadeHandler { get; }
}

public interface IUpdateCascadeHandler
{
    UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
        string originalDocumentProjectName,
        string originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        ReferencingDocument referencingDocument
    );
}

public record UpdateCascadeResult(ReferencingDocument referencingDocument, bool isIdentityUpdate);

public record ReferencingDocument(
    JsonNode EdFiDoc,
    long Id,
    short DocumentPartitionKey,
    Guid DocumentUuid,
    string ProjectName,
    string ResourceName
);
