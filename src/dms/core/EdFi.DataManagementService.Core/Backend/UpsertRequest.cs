// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// An upsert request to a document repository. This extends UpdateRequest because
/// sometimes upserts are actually updates.
/// </summary>
internal record UpsertRequest(
    /// <summary>
    /// The ResourceInfo of the document to upsert
    /// </summary>
    ResourceInfo ResourceInfo,
    /// <summary>
    /// The DocumentInfo of the document to upsert
    /// </summary>
    DocumentInfo DocumentInfo,
    /// <summary>
    /// The EdfiDoc of the document to upsert, as a JsonNode
    /// </summary>
    JsonNode EdfiDoc,
    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId,
    /// <summary>
    /// A candidate DocumentUuid of the document to upsert, used only
    /// if the upsert happens as an insert
    /// </summary>
    DocumentUuid DocumentUuid,
    /// <summary>
    /// This class will modify the EdFiDoc of a referencing
    /// resource when the referenced resource's identifying
    /// values are modified
    /// </summary>
    IUpdateCascadeHandler UpdateCascadeHandler
)
    : UpdateRequest(ResourceInfo, DocumentInfo, EdfiDoc, TraceId, DocumentUuid, UpdateCascadeHandler),
        IUpsertRequest;
