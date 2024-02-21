// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema.Extensions;
using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.ApiSchema;

/// <summary>
/// Provides information from the ResourceSchema portion of an ApiSchema.json document
/// </summary>
public class ResourceSchema(JsonNode _resourceSchemaNode, ILogger _logger)
{
    private readonly Lazy<MetaEdResourceName> _resourceName = new(() =>
    {
        return new MetaEdResourceName(_resourceSchemaNode.SelectRequiredNodeFromPathAs<string>("$.resourceName", _logger));
    });

    /// <summary>
    /// The ResourceName of this resource, taken from the resourceName
    /// </summary>
    public MetaEdResourceName ResourceName => _resourceName.Value;

    private readonly Lazy<bool> _isDescriptor = new(() =>
    {
        return _resourceSchemaNode.SelectRequiredNodeFromPathAs<bool>("$.isDescriptor", _logger);
    });

    /// <summary>
    /// Whether the resource is a descriptor, taken from isDescriptor
    /// </summary>
    public bool IsDescriptor => _isDescriptor.Value;

    private readonly Lazy<bool> _allowIdentityUpdates = new(() =>
    {
        return _resourceSchemaNode.SelectRequiredNodeFromPathAs<bool>("$.allowIdentityUpdates", _logger);
    });

    /// <summary>
    /// Whether the resource allows identity updates, taken from allowIdentityUpdates
    /// </summary>
    public bool AllowIdentityUpdates => _allowIdentityUpdates.Value;

    private readonly Lazy<JsonNode> _jsonSchemaForInsert = new(() =>
    {
        return _resourceSchemaNode.SelectRequiredNodeFromPath("$.jsonSchemaForInsert", _logger);
    });

    /// <summary>
    /// The JSONSchema for the body of this resource on insert
    /// </summary>
    public JsonNode JsonSchemaForInsert => _jsonSchemaForInsert.Value;
}
