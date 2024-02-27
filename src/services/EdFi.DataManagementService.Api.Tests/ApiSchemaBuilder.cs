// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.Tests;

public class ApiSchemaBuilder
{
    private readonly JsonNode _apiSchemaRootNode;

    public JsonNode ApiSchemaRootNode => _apiSchemaRootNode;

    private JsonNode? _currentProjectNode = null;
    private JsonNode? _currentResourceNode = null;

    public ApiSchemaBuilder()
    {
        _apiSchemaRootNode = new JsonObject
        {
            ["projectNameMapping"] = new JsonObject(),
            ["projectSchemas"] = new JsonObject(),
        };
    }

    /// <summary>
    /// A naive decapitalizer and pluralizer
    /// </summary>
    private static string ToEndpointName(string resourceName)
    {
        string decapitalized = resourceName.Length switch
        {
            0 => resourceName,
            1 => resourceName.ToLower(),
            _ => char.ToLower(resourceName[0]) + resourceName[1..]
        };
        return decapitalized + "s";
    }

    public ApiSchemaBuilder WithProjectStart(string projectName, string projectVersion = "1.0.0")
    {
        if (_currentProjectNode != null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode = new JsonObject
        {
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["description"] = $"{projectName} description",
            ["projectName"] = projectName,
            ["projectVersion"] = projectVersion,
            ["resourceNameMapping"] = new JsonObject(),
            ["resourceSchemas"] = new JsonObject(),
        };

        _apiSchemaRootNode["projectNameMapping"]![projectName] = projectName.ToLower();
        _apiSchemaRootNode["projectSchemas"]![projectName.ToLower()] = _currentProjectNode;
        return this;
    }

    public ApiSchemaBuilder WithProjectEnd()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode = null;
        return this;
    }

    public ApiSchemaBuilder WithResourceStart(string resourceName)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode != null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode = new JsonObject
        {
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityFullnames"] = new JsonArray(),
            ["identityPathOrder"] = new JsonArray(),
            ["isDescriptor"] = false,
            ["isSchoolYearEnumeration"] = false,
            ["isSubclass"] = false,
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["resourceName"] = resourceName
        };

        string endpointName = ToEndpointName(resourceName);
        _currentProjectNode["resourceNameMapping"]![resourceName] = endpointName;
        _currentProjectNode["resourceSchemas"]![endpointName] = _currentResourceNode;
        return this;
    }

    public ApiSchemaBuilder WithResourceEnd()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode = null;
        return this;
    }
}
