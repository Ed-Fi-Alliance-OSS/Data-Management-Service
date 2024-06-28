// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// An update request to a document repository
/// </summary>
internal record UpdateRequest(
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
    /// The request TraceId
    /// </summary>
    TraceId TraceId,
    /// <summary>
    /// The DocumentUuid of the document to update
    /// </summary>
    DocumentUuid DocumentUuid
) : IUpdateRequest;
