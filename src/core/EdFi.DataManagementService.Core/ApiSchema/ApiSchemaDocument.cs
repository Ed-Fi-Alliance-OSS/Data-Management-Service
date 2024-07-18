// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.ApiSchema.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from a loaded ApiSchema.json document
/// </summary>
internal class ApiSchemaDocument(JsonNode _apiSchemaRootNode, ILogger _logger)
{
    /// <summary>
    /// Finds the ProjectSchema that represents the given ProjectNamespace. Returns null if not found.
    /// </summary>
    public JsonNode? FindProjectSchemaNode(ProjectNamespace projectNamespace)
    {
        return _apiSchemaRootNode.SelectNodeFromPath(
            $"$.projectSchemas[\"{projectNamespace.Value}\"]",
            _logger
        );
    }

    /// <summary>
    /// Gets all ProjectSchema nodes in the document. 
    /// </summary>
    public List<JsonNode> GetAllProjectSchemaNodes()
    {
        JsonNode schema = _apiSchemaRootNode;

        var projectSchemasNode = schema["projectSchemas"];

        if (projectSchemasNode == null)
        {
            throw new InvalidOperationException("Expected ProjectSchmas node to exist.");
        }

        return projectSchemasNode.SelectNodesFromPropertyValues();
    }
}
