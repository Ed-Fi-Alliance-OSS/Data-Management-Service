// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Model;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// This class provides a fluent interface for building an ApiSchema suitable for unit testing,
/// allowing tests to focus on scenarios without getting bogged down in JSON authoring
/// </summary>
public class ApiSchemaBuilder
{
    private JsonNode? _currentProjectNode = null;
    private bool _isCoreProject = false;

    private JsonNode? _coreProjectNode = null;
    private readonly List<JsonNode> _extensionProjectNodes = [];

    private JsonNode? _currentResourceNode = null;

    private JsonNode? _currentDocumentPathsMappingNode = null;

    private JsonNode? _currentQueryFieldMappingNode = null;

    /// <summary>
    /// A naive decapitalizer and pluralizer, which should be adequate for tests
    /// </summary>
    private static string ToEndpointName(string resourceName)
    {
        string decapitalized = resourceName.Length switch
        {
            0 => resourceName,
            1 => resourceName.ToLower(),
            _ => char.ToLower(resourceName[0]) + resourceName[1..],
        };
        return decapitalized + "s";
    }

    /// <summary>
    /// Returns a project JsonNode wrapped with an ApiSchema root JsonNode.
    /// </summary>
    internal static JsonNode ToApiSchemaRootNode(JsonNode projectNode)
    {
        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectNode };
    }

    /// <summary>
    /// Returns an ApiSchemaDocuments for the current api schema state
    /// </summary>
    internal ApiSchemaDocuments ToApiSchemaDocuments()
    {
        IEnumerable<JsonNode> extensionApiSchemaRootNodes = _extensionProjectNodes.Select(
            ToApiSchemaRootNode
        );
        return new ApiSchemaDocuments(
            ToApiSchemaRootNode(_coreProjectNode!),
            [.. extensionApiSchemaRootNodes],
            NullLogger.Instance
        );
    }

    /// <summary>
    /// Returns the first project as an ApiSchema root node
    /// </summary>
    internal JsonNode AsSingleApiSchemaRootNode()
    {
        if (_coreProjectNode != null)
        {
            return ToApiSchemaRootNode(_coreProjectNode);
        }

        return ToApiSchemaRootNode(_extensionProjectNodes.ToArray()[0]);
    }

    /// <summary>
    /// Start a project definition. This is the starting point for any api schema,
    /// as projects are at the top level and contain all resources.
    /// Always end a project definition when finished.
    ///
    /// projectName should be the ProjectName for a project, e.g. Ed-Fi, TPDM, Michigan
    /// </summary>
    public ApiSchemaBuilder WithStartProject(string projectName = "ed-fi", string projectVersion = "5.0.0")
    {
        if (_currentProjectNode != null)
        {
            throw new InvalidOperationException();
        }

        _isCoreProject = projectName.ToLower() == "ed-fi";

        _currentProjectNode = new JsonObject
        {
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["description"] = $"{projectName} description",
            ["isExtensionProject"] = !_isCoreProject,
            ["projectName"] = projectName,
            ["projectVersion"] = projectVersion,
            ["projectEndpointName"] = projectName.ToLower(),
            ["resourceNameMapping"] = new JsonObject(),
            ["resourceSchemas"] = new JsonObject(),
        };

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
            ["identityJsonPaths"] = new JsonArray(),
            ["isDescriptor"] = isDescriptor,
            ["isSchoolYearEnumeration"] = isSchoolYearEnumeration,
            ["isSubclass"] = isSubclass,
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["resourceName"] = resourceName,
            ["queryFieldMapping"] = new JsonObject(),
            ["securityElements"] = new JsonObject { ["Namespace"] = new JsonArray() },
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
        string superclassIdentityJsonPath,
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
        _currentResourceNode["superclassIdentityJsonPath"] = superclassIdentityJsonPath;
        _currentResourceNode["subclassType"] = subclassType;
        _currentResourceNode["superclassProjectName"] = superclassProjectName;
        _currentResourceNode["superclassResourceName"] = superclassResourceName;

        return this;
    }

    /// <summary>
    /// Adds an identityJsonPaths section to a resource
    /// </summary>
    public ApiSchemaBuilder WithIdentityJsonPaths(string[] identityJsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["identityJsonPaths"] = new JsonArray(
            identityJsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a booleanJsonPaths section to a resource
    /// </summary>
    public ApiSchemaBuilder WithBooleanJsonPaths(string[] booleanJsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["booleanJsonPaths"] = new JsonArray(
            booleanJsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a numericJsonPaths section to a resource
    /// </summary>
    public ApiSchemaBuilder WithNumericJsonPaths(string[] numericJsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["numericJsonPaths"] = new JsonArray(
            numericJsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a NamespaceSecurityElements section to a resource
    /// </summary>
    public ApiSchemaBuilder WithNamespaceSecurityElements(string[] jsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["securityElements"]!["Namespace"] = new JsonArray(
            jsonPaths.Select(x => JsonValue.Create(x)).ToArray()
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
    /// Start a query field mapping definition. Can only be done inside a resource definition.
    /// Always end when finished.
    /// </summary>
    public ApiSchemaBuilder WithStartQueryFieldMapping()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentQueryFieldMappingNode = _currentResourceNode["queryFieldMapping"];
        return this;
    }

    /// <summary>
    /// Adds a DocumentPath to a DocumentPathsMapping for a scalar path
    ///
    /// Example for parameters ("OfficialAttendancePeriod", "$.officialAttendancePeriod")
    ///
    /// "OfficialAttendancePeriod": {
    ///   "isReference": false,
    ///   "path": "$.officialAttendancePeriod"
    /// },
    /// </summary>
    public ApiSchemaBuilder WithDocumentPathScalar(string pathFullName, string jsonPath)
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
            ["path"] = jsonPath,
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
    ///       new ("$.localCourseCode", "$.courseOfferingReference.localCourseCode"),
    ///       new ("$.schoolReference.schoolId", "$.courseOfferingReference.schoolId"),
    ///       new ("$.sessionReference.schoolYear", "$.courseOfferingReference.schoolYear"),
    ///       new ("$.sessionReference.sessionName", "$.courseOfferingReference.sessionName")
    ///   ]
    /// )
    ///
    ///  Results in document:
    ///
    ///  "CourseOffering": {
    ///    "isDescriptor": false,
    ///    "isReference": true,
    ///    "projectName": "Ed-Fi",
    ///      "referenceJsonPaths": [
    ///        {
    ///          "identityJsonPath": "$.localCourseCode",
    ///          "referenceJsonPath": "$.courseOfferingReference.localCourseCode"
    ///        },
    ///        {
    ///          "identityJsonPath": "$.schoolReference.schoolId",
    ///          "referenceJsonPath": "$.courseOfferingReference.schoolId"
    ///        },
    ///        {
    ///          "identityJsonPath": "$.sessionReference.schoolYear",
    ///          "referenceJsonPath": "$.courseOfferingReference.schoolYear"
    ///        },
    ///        {
    ///          "identityJsonPath": "$.sessionReference.sessionName",
    ///          "referenceJsonPath": "$.courseOfferingReference.sessionName"
    ///        }
    ///      ],
    ///      "resourceName": "CourseOffering"
    ///    },
    /// </summary>
    public ApiSchemaBuilder WithDocumentPathReference(
        string pathFullName,
        KeyValuePair<string, string>[] referenceJsonPaths,
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
            ["isDescriptor"] = false,
            ["projectName"] = referenceProjectName,
            ["resourceName"] = pathFullName,
            ["referenceJsonPaths"] = new JsonArray(
                referenceJsonPaths
                    .Select(x => new JsonObject
                    {
                        ["identityJsonPath"] = x.Key,
                        ["referenceJsonPath"] = x.Value,
                    })
                    .ToArray()
            ),
        };

        return this;
    }

    /// <summary>
    /// Adds a DocumentPath to a DocumentPathsMapping for a descriptor path
    ///
    /// Example for parameters ("GradingPeriodDescriptor", "$.gradingPeriodDescriptor")
    ///
    /// "GradingPeriodDescriptor": {
    ///   "isReference": false,
    ///   "isDescriptor": true,
    ///   "path": "$.officialAttendancePeriod",
    ///   "projectName": "Ed-Fi",
    ///   "resourceName": "CourseOffering"
    /// },
    /// </summary>
    public ApiSchemaBuilder WithDocumentPathDescriptor(
        string pathFullName,
        string jsonPath,
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
            ["isDescriptor"] = true,
            ["projectName"] = referenceProjectName,
            ["resourceName"] = pathFullName,
            ["path"] = jsonPath,
        };

        return this;
    }

    internal ApiSchemaBuilder WithEqualityConstraints(EqualityConstraint[] equalityConstraints)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["equalityConstraints"] = new JsonArray(
            equalityConstraints
                .Select(x => new JsonObject
                {
                    ["sourceJsonPath"] = x.SourceJsonPath.Value,
                    ["targetJsonPath"] = x.TargetJsonPath.Value,
                })
                .ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a query field to a query field mapping with the given query field name
    /// and array of JsonPaths and types
    ///
    /// Example for parameters "studentUniqueId", [(JsonPathString: "$.studentReference.studentUniqueId", Type: "string")]
    ///
    /// "studentUniqueId": [
    ///   "path": "$.studentReference.studentUniqueId",
    ///   "type": "string"
    /// ],
    ///
    /// </summary>
    public ApiSchemaBuilder WithQueryField(string fieldName, JsonPathAndType[] jsonPathAndTypes)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentQueryFieldMappingNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentQueryFieldMappingNode[fieldName] = new JsonArray(
            jsonPathAndTypes
                .Select(x => new JsonObject { ["path"] = x.JsonPathString, ["type"] = x.Type })
                .ToArray()
        );

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
    /// End a query field mapping definition.
    /// </summary>
    public ApiSchemaBuilder WithEndQueryFieldMapping()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentQueryFieldMappingNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentQueryFieldMappingNode = null;
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

        if (_isCoreProject)
        {
            _coreProjectNode = _currentProjectNode;
        }
        else
        {
            _extensionProjectNodes.Add(_currentProjectNode);
        }

        _currentProjectNode = null;
        return this;
    }

    /// <summary>
    /// Adds a core OpenAPI specification to a project definition.
    /// </summary>
    public ApiSchemaBuilder WithCoreOpenApiSpecification(JsonNode schemas, JsonNode paths, JsonArray tags)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode["coreOpenApiSpecification"] = new JsonObject
        {
            ["components"] = new JsonObject { ["schemas"] = schemas },
            ["paths"] = paths,
            ["tags"] = tags,
        };
        return this;
    }

    /// <summary>
    /// Adds OpenAPI extension fragments to a project definition.
    /// </summary>
    public ApiSchemaBuilder WithOpenApiExtensionFragments(
        JsonNode exts,
        JsonNode newPaths,
        JsonNode newSchemas,
        JsonNode newTags
    )
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode["openApiExtensionFragments"] = new JsonObject
        {
            ["exts"] = exts,
            ["newPaths"] = newPaths,
            ["newSchemas"] = newSchemas,
            ["newTags"] = newTags,
        };
        return this;
    }
}
