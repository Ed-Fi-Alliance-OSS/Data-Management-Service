// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Core and extension ApiSchemas as JsonNodes
/// </summary>
public record ApiSchemaDocumentNodes(
    /// <summary>
    /// Core ApiSchema as parsed JSON.
    /// </summary>
    JsonNode CoreApiSchemaRootNode,
    /// <summary>
    /// Extension ApiSchemas as parsed JSON. Empty if there are none.
    /// </summary>
    JsonNode[] ExtensionApiSchemaRootNodes
);
