// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from a loaded ApiSchema.json document
/// </summary>
internal class ApiSchemaDocument(JsonNode _apiSchemaRootNode, ILogger _logger)
{
    /// <summary>
    /// Returns the mapped projectNamespace from the given project name
    /// </summary>
    public ProjectNamespace GetMappedProjectName(string projectName)
    {
        return new ProjectNamespace(
            _apiSchemaRootNode.SelectNodeFromPathAs<string>(
                $"$.projectNameMapping[\"{projectName}\"]",
                _logger
            ) ?? string.Empty
        );
    }

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
    /// Finds the resourceSchema for a given projectName and resourceName looking up their mapped value.
    /// Returns null if not found.
    /// </summary>
    public JsonNode? FindResourceNode(string projectName, string resourceName)
    {
        var projectSchemaNode = FindProjectSchemaNode(GetMappedProjectName(projectName));

        if (projectSchemaNode != null)
        {
            var mappedResourceName = projectSchemaNode.SelectNodeFromPathAs<string>(
                $"$.resourceNameMapping[\"{resourceName}\"]",
                _logger
            );

            if (mappedResourceName != null)
            {
                var resourceNode = projectSchemaNode.SelectNodeFromPath(
                    $"$.resourceSchemas[\"{mappedResourceName}\"]",
                    _logger
                );

                return resourceNode;
            }
        }

        return null;
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
