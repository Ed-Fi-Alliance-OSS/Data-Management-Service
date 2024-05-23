// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// An upsert request to a document repository. This extends UpdateRequest because
/// sometimes upserts are actually updates.
/// </summary>
public interface IUpsertRequest
{
    /// <summary>
    /// The ReferentialId of the document to upsert
    /// </summary>
    ReferentialId ReferentialId { get; }

    /// <summary>
    /// The ResourceInfo of the document to upsert
    /// </summary>
    IResourceInfo ResourceInfo { get; }

    /// <summary>
    /// The DocumentInfo of the document to upsert
    /// </summary>
    IDocumentInfo DocumentInfo { get; }

    /// <summary>
    /// The EdfiDoc of the document to upsert, as a JsonNode
    /// </summary>
    JsonNode EdfiDoc { get; }

    /// <summary>
    /// If true, validates that all references in the document exist
    /// </summary>
    bool validateDocumentReferencesExist { get; }

    /// <summary>
    /// The request TraceId
    /// </summary>
    ITraceId TraceId { get; }

    /// <summary>
    /// A candidate DocumentUuid of the document to upsert, used only
    /// if the upsert happens as an insert
    /// </summary>
    DocumentUuid DocumentUuid { get; }
}
