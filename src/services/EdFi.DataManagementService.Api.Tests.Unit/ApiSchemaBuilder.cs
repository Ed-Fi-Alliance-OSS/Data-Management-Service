// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Api.Tests.Unit;

/// <summary>
/// This class provides a fluent interface for building an ApiSchema suitable for unit testing,
/// allowing tests to focus on scenarios without getting bogged down in JSON authoring
/// </summary>
public class ApiSchemaBuilder
{
    private readonly JsonNode _apiSchemaRootNode;

    public JsonNode RootNode => _apiSchemaRootNode;

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
    /// A naive decapitalizer and pluralizer, which should be adequate for tests
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

    /// <summary>
    /// Returns an ApiSchemaDocument for the current api schema state
    /// </summary>
    public ApiSchemaDocument ToApiSchemaDocument()
    {
        return new ApiSchemaDocument(RootNode, NullLogger.Instance);
    }

    /// <summary>
    /// Start a project definition. This is the starting point for any api schema,
    /// as projects are at the top level and contain all resources.
    /// Always end a project definition when finished.
    ///
    /// projectName should be the MetaEdProjectName for a project, e.g. Ed-Fi, TPDM, Michigan
    /// </summary>
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

    /// <summary>
    /// End a project definition.
    /// </summary>
    public ApiSchemaBuilder WithProjectEnd()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode = null;
        return this;
    }

    /// <summary>
    /// Start a resource definition. Can only be done inside a project definition.
    /// Always end a resource definition when finished.
    ///
    /// resourceName should be the MetaEdName for a resource, e.g. School, Student, Course
    /// </summary>
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
        _currentProjectNode["caseInsensitiveEndpointNameMapping"]![endpointName.ToLower()] = endpointName;
        return this;
    }

    /// <summary>
    /// End a resource definition.
    /// </summary>
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
