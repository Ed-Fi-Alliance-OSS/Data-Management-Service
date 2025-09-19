// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from loaded ApiSchema.json documents
/// </summary>
internal class ApiSchemaDocuments(ApiSchemaDocumentNodes _apiSchemaNodes, ILogger _logger)
{
    /// <summary>
    /// Gets the core ProjectSchema node in the document.
    /// </summary>
    public ProjectSchema GetCoreProjectSchema()
    {
        JsonNode projectSchemaNode =
            _apiSchemaNodes.CoreApiSchemaRootNode["projectSchema"]
            ?? throw new InvalidOperationException("Expected projectSchema node to exist.");
        return new ProjectSchema(projectSchemaNode, _logger);
    }

    /// <summary>
    /// Gets the extension ProjectSchemas.
    /// </summary>
    public ProjectSchema[] GetExtensionProjectSchemas()
    {
        return _apiSchemaNodes
            .ExtensionApiSchemaRootNodes.Select(node =>
                node["projectSchema"]
                ?? throw new InvalidOperationException("Expected projectSchema node to exist.")
            )
            .Select(node => new ProjectSchema(node, _logger))
            .ToArray();
    }

    /// <summary>
    /// Finds the ProjectSchema that represents the given ProjectNamespace e.g. "ed-fi" for the Data Standard.
    /// Returns null if not found.
    /// </summary>
    public ProjectSchema? FindProjectSchemaForProjectNamespace(ProjectEndpointName projectEndpointName)
    {
        ProjectSchema coreProjectSchema = GetCoreProjectSchema();
        if (projectEndpointName.Value == coreProjectSchema.ProjectEndpointName.Value)
        {
            return coreProjectSchema;
        }

        ProjectSchema[] extensionProjectSchemas = GetExtensionProjectSchemas();
        return Array.Find(
            extensionProjectSchemas,
            projectSchema => projectSchema.ProjectEndpointName.Value == projectEndpointName.Value
        );
    }

    /// <summary>
    /// Finds the ProjectSchema that represents the given ProjectName e.g. "Ed-Fi" for the Data Standard.
    /// Returns null if not found.
    /// </summary>
    public ProjectSchema? FindProjectSchemaForProjectName(ProjectName projectName)
    {
        ProjectSchema coreProjectSchema = GetCoreProjectSchema();
        if (projectName.Value == coreProjectSchema.ProjectName.Value)
        {
            return coreProjectSchema;
        }

        ProjectSchema[] extensionProjectSchemas = GetExtensionProjectSchemas();
        return Array.Find(
            extensionProjectSchemas,
            projectSchema => projectSchema.ProjectName.Value == projectName.Value
        );
    }

    /// <summary>
    /// Gets all of the ProjectSchemas, both core and extensions.
    /// </summary>
    public ProjectSchema[] GetAllProjectSchemas()
    {
        return [GetCoreProjectSchema(), .. GetExtensionProjectSchemas()];
    }
}
