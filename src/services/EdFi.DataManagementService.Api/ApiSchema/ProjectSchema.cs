// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema.Extensions;
using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.ApiSchema;

/// <summary>
/// Provides information from the ProjectSchema portion of an ApiSchema.json document
/// </summary>
public class ProjectSchema(JsonNode _projectSchemaNode)
{
    private readonly Lazy<MetaEdProjectName> _projectName = new(() =>
    {
        return new MetaEdProjectName(_projectSchemaNode.SelectRequiredNodeFromPathAs<string>("$.projectName"));
    });

    /// <summary>
    /// The ProjectName for this ProjectSchema, taken from the projectName
    /// </summary>
    public MetaEdProjectName ProjectName => _projectName.Value;

    private readonly Lazy<SemVer> _resourceVersion = new(() =>
    {
        return new SemVer(_projectSchemaNode.SelectRequiredNodeFromPathAs<string>("$.projectVersion"));
    });

    /// <summary>
    /// The ResourceVersion for this ProjectSchema, taken from the projectVersion
    /// </summary>
    public SemVer ResourceVersion => _resourceVersion.Value;

    /// <summary>
    /// Finds the ResourceSchemaNode that represents the given REST resource path. Returns null if not found.
    /// </summary>
    public JsonNode? FindResourceSchemaNode(EndpointName endpointName)
    {
        return _projectSchemaNode.SelectNodeFromPath($"$.resourceSchemas[\"{endpointName.Value}\"]");
    }
}
