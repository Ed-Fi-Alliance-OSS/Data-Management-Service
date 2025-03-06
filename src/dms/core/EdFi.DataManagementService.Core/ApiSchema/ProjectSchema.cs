// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from the ProjectSchema portion of an ApiSchema.json document
/// </summary>
internal class ProjectSchema(JsonNode _projectSchemaNode, ILogger _logger)
{
    private readonly Lazy<ProjectName> _projectName = new(() =>
    {
        return new ProjectName(
            _projectSchemaNode.SelectRequiredNodeFromPathAs<string>("$.projectName", _logger)
        );
    });

    /// <summary>
    /// The ProjectName for this ProjectSchema, taken from the projectName
    /// </summary>
    public ProjectName ProjectName => _projectName.Value;

    private readonly Lazy<ProjectEndpointName> _projectEndpointName = new(() =>
    {
        return new ProjectEndpointName(
            _projectSchemaNode.SelectRequiredNodeFromPathAs<string>("$.projectEndpointName", _logger)
        );
    });

    /// <summary>
    /// The ProjectEndpointName for this ProjectSchema, taken from the projectEndpointName
    /// </summary>
    public ProjectEndpointName ProjectEndpointName => _projectEndpointName.Value;

    private readonly Lazy<SemVer> _resourceVersion = new(() =>
    {
        return new SemVer(
            _projectSchemaNode.SelectRequiredNodeFromPathAs<string>("$.projectVersion", _logger)
        );
    });

    /// <summary>
    /// The ResourceVersion for this ProjectSchema, taken from the projectVersion
    /// </summary>
    public SemVer ResourceVersion => _resourceVersion.Value;

    private readonly Lazy<string> _description = new(() =>
    {
        return _projectSchemaNode.SelectRequiredNodeFromPathAs<string>("$.description", _logger);
    });

    /// <summary>
    /// The description for this ProjectSchema
    /// </summary>
    public string Description => _description.Value;

    private readonly Lazy<IEnumerable<AbstractResource>> _abstractResources = new(() =>
    {
        return _projectSchemaNode.SelectRequiredNodeFromPath("$.abstractResources", _logger)
            .AsObject()
            .Select(ar => new AbstractResource(new ResourceName(ar.Key),
                ar.Value!.SelectNodesFromArrayPathCoerceToStrings("$.identityJsonPaths", _logger)
                    .Select(ijp => new JsonPath(ijp))));
    });

    /// <summary>
    /// The AbstractResources for this ProjectSchema, taken from abstractResources
    /// </summary>
    public IEnumerable<AbstractResource> AbstractResources => _abstractResources.Value;

    /// <summary>
    /// Returns the EndpointName that represents the given ResourceName.
    /// </summary>
    public EndpointName GetEndpointNameFromResourceName(ResourceName resourceName)
    {
        string endpointName = _projectSchemaNode.SelectRequiredNodeFromPathAs<string>(
            $"$.resourceNameMapping[\"{resourceName.Value}\"]",
            _logger
        );

        return new EndpointName(endpointName);
    }

    /// <summary>
    /// Finds the ResourceSchemaNode that represents the given REST resource endpoint. Returns null if not found.
    /// </summary>
    public JsonNode? FindResourceSchemaNodeByEndpointName(EndpointName endpointName)
    {
        string? caseCorrectedEndpointName = _projectSchemaNode.SelectNodeFromPathAs<string>(
            $"$.caseInsensitiveEndpointNameMapping[\"{endpointName.Value.ToLower()}\"]",
            _logger
        );

        if (caseCorrectedEndpointName == null)
        {
            return null;
        }

        return _projectSchemaNode.SelectNodeFromPath(
            $"$.resourceSchemas[\"{caseCorrectedEndpointName}\"]",
            _logger
        );
    }

    /// <summary>
    /// Finds the ResourceSchemaNode that represents the given ResourceName.
    /// Returns null if not found.
    /// </summary>
    public JsonNode? FindResourceSchemaNodeByResourceName(ResourceName resourceName)
    {
        string? endpointName = _projectSchemaNode.SelectNodeFromPathAs<string>(
            $"$.resourceNameMapping[\"{resourceName.Value}\"]",
            _logger
        );

        if (endpointName == null)
        {
            return null;
        }

        return FindResourceSchemaNodeByEndpointName(new EndpointName(endpointName));
    }

    /// <summary>
    /// Returns all the resource schema nodes for this projectSchema
    /// </summary>
    public List<JsonNode> GetAllResourceSchemaNodes()
    {
        JsonNode resourceSchemasNode = _projectSchemaNode.SelectRequiredNodeFromPath(
            "$.resourceSchemas",
            _logger
        );
        return resourceSchemasNode.SelectNodesFromPropertyValues();
    }

    /// <summary>
    /// Returns a dictionary where they Key is an EducationOrganization and the value is
    /// a list of the parent EducationOrganization ResourceNames.
    /// </summary>
    public Dictionary<ResourceName, ResourceName[]> EducationOrganizationHierarchy =>
        _educationOrganizationHierarchy.Value;
    private readonly Lazy<Dictionary<ResourceName, ResourceName[]>> _educationOrganizationHierarchy = new(
        () =>
        {
            JsonNode edOrgHierarchyNode = _projectSchemaNode.SelectRequiredNodeFromPath(
                "$.educationOrganizationHierarchy",
                _logger
            );

            return edOrgHierarchyNode
                .AsObject()
                .ToDictionary(
                    kvp => new ResourceName(kvp.Key),
                    kvp =>
                        kvp.Value?.AsArray()
                            .Select(v => new ResourceName(v?.ToString() ?? string.Empty))
                            .ToArray() ?? []
                );
        }
    );

    /// <summary>
    /// Returns the list of EducationOrganization resource names
    /// </summary>
    public ResourceName[] EducationOrganizationTypes => _educationOrganizationTypes.Value;
    private readonly Lazy<ResourceName[]> _educationOrganizationTypes = new(() =>
    {
        JsonNode edOrgTypesNode = _projectSchemaNode.SelectRequiredNodeFromPath(
            "$.educationOrganizationTypes",
            _logger
        );

        return edOrgTypesNode
            .AsArray()
            .Select(v => new ResourceName(v?.ToString() ?? string.Empty))
            .ToArray();
    });
}
