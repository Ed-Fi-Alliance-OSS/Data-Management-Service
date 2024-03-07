// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using Json.Schema;
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

    private JsonNode? _currentDocumentPathsMappingNode = null;

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
    public ApiSchemaBuilder WithStartProject(string projectName = "Ed-Fi", string projectVersion = "5.0.0")
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
    /// Start a resource definition. Can only be done inside a project definition.
    /// Always end a resource definition when finished.
    ///
    /// resourceName should be the MetaEdName for a resource, e.g. School, Student, Course
    /// </summary>
    public ApiSchemaBuilder WithStartResource(
        string resourceName,
        bool isSubclass = false,
        bool allowIdentityUpdates = false,
        bool isDescriptor = false,
        bool isSchoolYearEnumeration = false
    )
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
            ["allowIdentityUpdates"] = allowIdentityUpdates,
            ["documentPathsMapping"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityFullnames"] = new JsonArray(),
            ["identityPathOrder"] = new JsonArray(),
            ["isDescriptor"] = isDescriptor,
            ["isSchoolYearEnumeration"] = isSchoolYearEnumeration,
            ["isSubclass"] = isSubclass,
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
    /// Adds superclass information to a resource
    /// </summary>
    public ApiSchemaBuilder WithSuperclassInformation(
        string subclassType,
        string subclassIdentityDocumentKey,
        string superclassIdentityDocumentKey,
        string superclassResourceName,
        string superclassProjectName = "Ed-Fi"
    )
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["isSubclass"] = true;
        _currentResourceNode["subclassIdentityDocumentKey"] = subclassIdentityDocumentKey;
        _currentResourceNode["subclassType"] = subclassType;
        _currentResourceNode["superclassIdentityDocumentKey"] = superclassIdentityDocumentKey;
        _currentResourceNode["superclassProjectName"] = superclassProjectName;
        _currentResourceNode["superclassResourceName"] = superclassResourceName;

        return this;
    }

    /// <summary>
    /// Adds an identityFullnames section to a resource
    /// </summary>
    public ApiSchemaBuilder WithIdentityFullnames(string[] identityFullnames)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["identityFullnames"] = new JsonArray(
            identityFullnames.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds an identityPathOrder section to a resource
    /// </summary>
    public ApiSchemaBuilder WithIdentityPathOrder(string[] identityPathOrder)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["identityPathOrder"] = new JsonArray(
            identityPathOrder.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Define resource schema. Can only be done inside a project definition.
    /// Always end a resource definition when finished.
    ///
    /// resourceSchema should contain schema definition for insert.
    /// </summary>
    public ApiSchemaBuilder WithJsonSchemaForInsert(JsonSchema jsonSchema)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }
        var serializedJson = JsonSerializer.Serialize(jsonSchema);
        _currentResourceNode["jsonSchemaForInsert"] = JsonNode.Parse(serializedJson);

        return this;
    }

    /// <summary>
    /// Start a document paths mapping definition. Can only be done inside a resource definition.
    /// Always end when finished.
    /// </summary>
    public ApiSchemaBuilder WithStartDocumentPathsMapping()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentDocumentPathsMappingNode = _currentResourceNode["documentPathsMapping"];
        return this;
    }

    /// <summary>
    /// Adds a DocumentPath to a DocumentPathsMapping for a scalar path
    ///
    /// Example for parameters ("OfficialAttendancePeriod", "officialAttendancePeriod", "$.officialAttendancePeriod")
    ///
    /// "OfficialAttendancePeriod": {
    ///   "isReference": false,
    ///   "pathOrder": [
    ///     "officialAttendancePeriod"
    ///   ],
    ///   "paths": {
    ///     "officialAttendancePeriod": "$.officialAttendancePeriod"
    ///   }
    /// },
    /// </summary>
    public ApiSchemaBuilder WithDocumentPathScalar(string pathFullName, string documentKey, string jsonPath)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentDocumentPathsMappingNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentDocumentPathsMappingNode[pathFullName] = new JsonObject
        {
            ["isReference"] = false,
            ["pathOrder"] = new JsonArray(documentKey),
            ["paths"] = new JsonObject { [documentKey] = jsonPath }
        };

        return this;
    }

    /// <summary>
    /// Adds a DocumentPath to a DocumentPathsMapping for a reference path. Makes some
    /// simplifying assumptions.
    ///
    /// Example for parameters: (
    ///   "CourseOffering",
    ///   [
    ///       new ("localCourseCode", "$.courseOfferingReference.localCourseCode"),
    ///       new ("schoolId", "$.courseOfferingReference.schoolId"),
    ///       new ("schoolYear", "$.courseOfferingReference.schoolYear"),
    ///       new ("sessionName", "$.courseOfferingReference.sessionName")
    ///   ]
    /// )
    ///
    ///  Results in document:
    ///
    ///  "CourseOffering": {
    ///    "isDescriptor": false,
    ///    "isReference": true,
    ///    "pathOrder": [
    ///      "localCourseCode",
    ///      "schoolId",
    ///      "schoolYear",
    ///      "sessionName"
    ///    ],
    ///    "paths": {
    ///      "localCourseCode": "$.courseOfferingReference.localCourseCode",
    ///      "schoolId": "$.courseOfferingReference.schoolId",
    ///      "schoolYear": "$.courseOfferingReference.schoolYear",
    ///      "sessionName": "$.courseOfferingReference.sessionName"
    ///    },
    ///    "projectName": "Ed-Fi",
    ///    "resourceName": "CourseOffering"
    ///  },
    /// </summary>
    public ApiSchemaBuilder WithDocumentPathReference(
        string pathFullName,
        KeyValuePair<string, JsonNode?>[] paths,
        bool isDescriptor = false,
        string referenceProjectName = "Ed-Fi"
    )
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentDocumentPathsMappingNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentDocumentPathsMappingNode[pathFullName] = new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = isDescriptor,
            ["projectName"] = referenceProjectName,
            ["resourceName"] = pathFullName,
            ["pathOrder"] = new JsonArray(paths.Select(x => JsonValue.Create(x.Key)).ToArray()),
            ["paths"] = new JsonObject(paths)
        };

        return this;
    }

    /// <summary>
    /// End a document paths mapping definition. 
    /// </summary>
    public ApiSchemaBuilder WithEndDocumentPathsMapping()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentDocumentPathsMappingNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentDocumentPathsMappingNode = null;
        return this;
    }

    /// <summary>
    /// End a resource definition.
    /// </summary>
    public ApiSchemaBuilder WithEndResource()
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

    /// <summary>
    /// End a project definition.
    /// </summary>
    public ApiSchemaBuilder WithEndProject()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode = null;
        return this;
    }
}
