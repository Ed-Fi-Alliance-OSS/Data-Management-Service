// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema.Extensions;
using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.ApiSchema;

/// <summary>
/// Provides information from a loaded ApiSchema.json document
/// </summary>
public class ApiSchemaDocument(JsonNode _apiSchemaRootNode)
{
    /// <summary>
    /// Finds the ProjectSchema that represents the given ProjectNamespace. Returns null if not found.
    /// </summary>
    public JsonNode? FindProjectSchemaNode(ProjectNamespace projectNamespace)
    {
        return _apiSchemaRootNode.SelectNodeFromPath($"$.projectSchemas[\"{projectNamespace.Value}\"]");
    }
}
