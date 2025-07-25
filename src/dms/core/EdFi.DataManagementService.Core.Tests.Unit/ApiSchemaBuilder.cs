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
            new(ToApiSchemaRootNode(_coreProjectNode!), [.. extensionApiSchemaRootNodes]),
            NullLogger.Instance
        );
    }

    /// <summary>
    /// Returns all the projects as <see cref="ApiSchemaDocumentNodes"/>.
    /// </summary>
    internal ApiSchemaDocumentNodes AsApiSchemaNodes()
    {
        return new ApiSchemaDocumentNodes(
            ToApiSchemaRootNode(_coreProjectNode!),
            _extensionProjectNodes.Select(ToApiSchemaRootNode).ToArray()
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
    public ApiSchemaBuilder WithStartProject(
        string projectName = "Ed-Fi",
        string projectVersion = "5.0.0",
        JsonObject? abstractResources = null
    )
    {
        if (_currentProjectNode != null)
        {
            throw new InvalidOperationException();
        }

        _isCoreProject = projectName.Equals("Ed-Fi", StringComparison.OrdinalIgnoreCase);

        _currentProjectNode = new JsonObject
        {
            ["abstractResources"] = abstractResources ?? new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["description"] = $"{projectName} description",
            ["educationOrganizationHierarchy"] = new JsonObject(),
            ["educationOrganizationTypes"] = new JsonArray(),
            ["domains"] = new JsonArray(),
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
        bool isSchoolYearEnumeration = false,
        bool isResourceExtension = false
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
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["authorizationPathways"] = new JsonArray(),
            ["booleanJsonPaths"] = new JsonArray(),
            ["dateTimeJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["domains"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["isDescriptor"] = isDescriptor,
            ["isResourceExtension"] = isResourceExtension,
            ["isSchoolYearEnumeration"] = isSchoolYearEnumeration,
            ["isSubclass"] = isSubclass,
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["numericJsonPaths"] = new JsonArray(),
            ["resourceName"] = resourceName,
            ["queryFieldMapping"] = new JsonObject(),
            ["securableElements"] = new JsonObject { ["Namespace"] = new JsonArray() },
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
    /// Adds a dateTimeJsonPaths section to a resource
    /// </summary>
    public ApiSchemaBuilder WithDateTimeJsonPaths(string[] dateTimeJsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["dateTimeJsonPaths"] = new JsonArray(
            dateTimeJsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a dateJsonPaths section to a resource
    /// </summary>
    public ApiSchemaBuilder WithDateJsonPaths(string[] dateJsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["dateJsonPaths"] = new JsonArray(
            dateJsonPaths.Select(x => JsonValue.Create(x)).ToArray()
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

        _currentResourceNode["securableElements"]!["Namespace"] = new JsonArray(
            jsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a EducationOrganizationSecurityElements section to a resource
    /// </summary>
    public ApiSchemaBuilder WithEducationOrganizationSecurityElements((string, string)[] jsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["securableElements"]!["EducationOrganization"] = new JsonArray(
            jsonPaths
                .Select(x => new JsonObject { ["metaEdName"] = x.Item1, ["jsonPath"] = x.Item2 })
                .ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a StudentSecurityElements section to a resource
    /// </summary>
    public ApiSchemaBuilder WithStudentSecurityElements(string[] jsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["securableElements"]!["Student"] = new JsonArray(
            jsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a ContactSecurityElements section to a resource
    /// </summary>
    public ApiSchemaBuilder WithContactSecurityElements(string[] jsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["securableElements"]!["Contact"] = new JsonArray(
            jsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds a StaffSecurityElements section to a resource
    /// </summary>
    public ApiSchemaBuilder WithStaffSecurityElements(string[] jsonPaths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["securableElements"]!["Staff"] = new JsonArray(
            jsonPaths.Select(x => JsonValue.Create(x)).ToArray()
        );

        return this;
    }

    /// <summary>
    /// Adds an AuthorizationPathways section to a resource
    /// </summary>
    public ApiSchemaBuilder WithAuthorizationPathways(string[] authorizationPathways)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["authorizationPathways"] = new JsonArray(
            authorizationPathways.Select(x => JsonValue.Create(x)).ToArray()
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
        string referenceProjectName = "Ed-Fi",
        bool isRequired = false
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
            ["isRequired"] = isRequired,
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
    /// Creates a minimal OpenAPI doc structure.
    /// </summary>
    private static JsonObject CreateMinimalOpenApiDoc(string title)
    {
        return new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = "1.0.0" },
            ["servers"] = new JsonArray(),
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject
            {
                ["schemas"] = new JsonObject(),
                ["parameters"] = new JsonObject(),
            },
            ["tags"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Adds OpenAPI base documents to the current project.
    /// If null is passed for either parameter, creates a minimal valid OpenAPI document.
    ///
    /// Example JSON for resourcesDoc/descriptorsDoc parameter:
    /// {
    ///   "openapi": "3.0.1",
    ///   "info": {
    ///     "title": "Ed-Fi Resources API",
    ///     "version": "5.0.0"
    ///   },
    ///   "servers": [],
    ///   "paths": {},
    ///   "components": {
    ///     "parameters": {
    ///       "If-None-Match": {
    ///         "description": "The previously returned ETag header value...",
    ///         "in": "header",
    ///         "name": "If-None-Match",
    ///         "schema": { "type": "string" }
    ///       }
    ///     },
    ///     "responses": {
    ///       "Updated": {
    ///         "description": "The resource was updated."
    ///       }
    ///     },
    ///     "schemas": {}
    ///   },
    ///   "tags": []
    /// }
    /// </summary>
    public ApiSchemaBuilder WithOpenApiBaseDocuments(
        JsonNode? resourcesDoc = null,
        JsonNode? descriptorsDoc = null
    )
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException("Must be within a project context");
        }

        _currentProjectNode["openApiBaseDocuments"] = new JsonObject
        {
            ["resources"] = resourcesDoc ?? CreateMinimalOpenApiDoc("Ed-Fi Resources API"),
            ["descriptors"] = descriptorsDoc ?? CreateMinimalOpenApiDoc("Ed-Fi Descriptors API"),
        };

        return this;
    }

    /// <summary>
    /// Adds an abstract resource to the current project with optional OpenAPI fragment.
    ///
    /// Example JSON for openApiFragment parameter:
    /// {
    ///   "components": {
    ///     "schemas": {
    ///       "EdFi_EducationOrganization": {
    ///         "description": "This entity represents any public or private institution...",
    ///         "properties": {
    ///           "addresses": {
    ///             "items": {
    ///               "$ref": "#/components/schemas/EdFi_EducationOrganization_Address"
    ///             },
    ///             "minItems": 0,
    ///             "type": "array"
    ///           },
    ///           "categories": {
    ///             "items": {
    ///               "$ref": "#/components/schemas/EdFi_EducationOrganizationCategory"
    ///             },
    ///             "type": "array"
    ///           },
    ///           "educationOrganizationId": {
    ///             "description": "The identifier assigned to an education organization.",
    ///             "type": "integer",
    ///             "x-Ed-Fi-isIdentity": true
    ///           }
    ///         },
    ///         "required": ["educationOrganizationId"],
    ///         "type": "object"
    ///       }
    ///     }
    ///   }
    /// }
    /// </summary>
    public ApiSchemaBuilder WithAbstractResource(
        string resourceName,
        string[] identityJsonPaths,
        JsonNode? openApiFragment = null
    )
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException("Must be within a project context");
        }

        // Create abstractResources if it doesn't exist
        if (_currentProjectNode["abstractResources"] == null)
        {
            _currentProjectNode["abstractResources"] = new JsonObject();
        }

        var abstractResourceNode = new JsonObject
        {
            ["identityJsonPaths"] = new JsonArray(
                identityJsonPaths.Select(p => JsonValue.Create(p)).ToArray()
            ),
        };

        if (openApiFragment != null)
        {
            abstractResourceNode["openApiFragment"] = openApiFragment.DeepClone();
        }

        _currentProjectNode["abstractResources"]![resourceName] = abstractResourceNode;

        return this;
    }

    /// <summary>
    /// Creates a minimal OpenAPI fragment structure.
    /// </summary>
    private static JsonObject CreateMinimalOpenApiFragment()
    {
        return new JsonObject
        {
            ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
            ["paths"] = new JsonObject(),
            ["tags"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Adds OpenAPI fragments to the current resource by assembling individual components.
    /// If all components are null, creates a minimal fragment structure.
    ///
    /// Parameters:
    /// - documentType: "resources" or "descriptors" to specify which fragment to update
    /// - schemas: OpenAPI schema definitions (for new resources/descriptors)
    /// - paths: OpenAPI path definitions (for new resources/descriptors)
    /// - tags: OpenAPI tag definitions (for new resources/descriptors)
    /// - exts: Extension schema definitions (for extension resources only)
    ///
    /// Example schemas parameter:
    /// {
    ///   "EdFi_AcademicWeek": {
    ///     "description": "This entity represents the academic weeks...",
    ///     "properties": { ... },
    ///     "type": "object"
    ///   }
    /// }
    ///
    /// Example paths parameter:
    /// {
    ///   "/ed-fi/academicWeeks": {
    ///     "get": { "description": "Retrieves specific resources..." },
    ///     "post": { "description": "Creates or updates resources..." }
    ///   }
    /// }
    ///
    /// Example tags parameter:
    /// [
    ///   {
    ///     "name": "academicWeeks",
    ///     "description": "This entity represents the academic weeks..."
    ///   }
    /// ]
    ///
    /// Example exts parameter (for extension resources):
    /// {
    ///   "EdFi_Credential": {
    ///     "description": "",
    ///     "properties": {
    ///       "boardCertificationIndicator": {
    ///         "description": "Indicator that the credential was granted...",
    ///         "type": "boolean"
    ///       }
    ///     }
    ///   }
    /// }
    /// </summary>
    public ApiSchemaBuilder WithResourceOpenApiFragments(
        string documentType,
        JsonNode? schemas = null,
        JsonNode? paths = null,
        JsonNode? tags = null,
        JsonNode? exts = null
    )
    {
        if (_currentProjectNode == null || _currentResourceNode == null)
        {
            throw new InvalidOperationException("Must be within a resource context");
        }

        if (documentType != "resources" && documentType != "descriptors")
        {
            throw new ArgumentException("documentType must be 'resources' or 'descriptors'");
        }

        // Create openApiFragments if it doesn't exist
        if (_currentResourceNode["openApiFragments"] == null)
        {
            _currentResourceNode["openApiFragments"] = new JsonObject();
        }

        // Build the fragment based on provided components
        JsonObject fragment = new();

        if (exts != null)
        {
            fragment["exts"] = exts.DeepClone();
        }

        if (schemas != null || paths != null || tags != null)
        {
            // Create components with schemas (either provided or empty)
            fragment["components"] = new JsonObject
            {
                ["schemas"] = schemas != null ? schemas.DeepClone() : new JsonObject(),
            };

            if (paths != null)
            {
                fragment["paths"] = paths.DeepClone();
            }

            if (tags != null)
            {
                fragment["tags"] = tags.DeepClone();
            }
        }

        // If no components provided, create minimal structure
        if (fragment.Count == 0)
        {
            fragment = CreateMinimalOpenApiFragment();
        }

        _currentResourceNode["openApiFragments"]![documentType] = fragment;

        return this;
    }

    /// <summary>
    /// Adds OpenAPI fragments to the current resource for both resources and descriptors.
    /// Creates minimal fragment structures if null is passed.
    /// </summary>
    public ApiSchemaBuilder WithResourceOpenApiFragments(
        JsonNode? resourcesFragment = null,
        JsonNode? descriptorsFragment = null
    )
    {
        if (_currentProjectNode == null || _currentResourceNode == null)
        {
            throw new InvalidOperationException("Must be within a resource context");
        }

        // Create openApiFragments if it doesn't exist
        if (_currentResourceNode["openApiFragments"] == null)
        {
            _currentResourceNode["openApiFragments"] = new JsonObject();
        }

        _currentResourceNode["openApiFragments"]!["resources"] =
            resourcesFragment != null ? resourcesFragment.DeepClone() : CreateMinimalOpenApiFragment();

        _currentResourceNode["openApiFragments"]!["descriptors"] =
            descriptorsFragment != null ? descriptorsFragment.DeepClone() : CreateMinimalOpenApiFragment();

        return this;
    }

    /// <summary>
    /// Adds extension fragments for resources marked as isResourceExtension.
    /// This is a convenience method for resources that extend existing Ed-Fi resources.
    /// </summary>
    public ApiSchemaBuilder WithResourceExtensionFragments(string documentType, JsonObject exts)
    {
        return WithResourceOpenApiFragments(documentType, schemas: null, paths: null, tags: null, exts: exts);
    }

    /// <summary>
    /// Adds fragments for new extension resources/descriptors.
    /// This is a convenience method for completely new resources introduced by an extension.
    /// </summary>
    public ApiSchemaBuilder WithNewExtensionResourceFragments(
        string documentType,
        JsonObject? schemas = null,
        JsonObject? paths = null,
        JsonArray? tags = null
    )
    {
        return WithResourceOpenApiFragments(documentType, schemas, paths, tags, exts: null);
    }

    /// <summary>
    /// Convenience method to create a simple resource with optional OpenAPI schema.
    /// </summary>
    public ApiSchemaBuilder WithSimpleResource(
        string resourceName,
        bool isDescriptor,
        JsonNode? schema = null
    )
    {
        WithStartResource(resourceName, isDescriptor: isDescriptor);

        if (schema != null)
        {
            var schemaName =
                $"{(_isCoreProject ? "EdFi" : _currentProjectNode!["projectName"]?.GetValue<string>() ?? "Extension")}_{resourceName}";
            var schemas = new JsonObject { [schemaName] = schema.DeepClone() };

            WithResourceOpenApiFragments(
                documentType: isDescriptor ? "descriptors" : "resources",
                schemas: schemas,
                paths: null,
                tags: null,
                exts: null
            );
        }

        WithEndResource();
        return this;
    }

    /// <summary>
    /// Convenience method to create a simple descriptor with optional OpenAPI schema.
    /// </summary>
    public ApiSchemaBuilder WithSimpleDescriptor(string descriptorName, JsonNode? schema = null)
    {
        return WithSimpleResource(descriptorName, true, schema);
    }

    public ApiSchemaBuilder WithDecimalPropertyValidationInfos(DecimalValidationInfo[] decimalValidationInfos)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentResourceNode["decimalPropertyValidationInfos"] = new JsonArray(
            decimalValidationInfos
                .Select(x => new JsonObject
                {
                    ["path"] = x.Path.Value,
                    ["decimalPlaces"] = x.DecimalPlaces,
                    ["totalDigits"] = x.TotalDigits,
                })
                .ToArray<JsonNode?>()
        );

        return this;
    }

    /// <summary>
    /// Add array uniqueness constraint with simple paths to a resource.
    /// Must be of form "$.someArray[*].scalarPathFromHere"
    /// </summary>
    /// <param name="paths">List of JSON paths for the constraint</param>
    ///
    /// An example parameter:
    /// [
    ///     "$.performanceLevels[*].assessmentReportingMethodDescriptor",
    ///     "$.performanceLevels[*].performanceLevelDescriptor"
    /// ]
    public ApiSchemaBuilder WithArrayUniquenessConstraintSimple(List<string> paths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        JsonObject constraintObject = new()
        {
            ["paths"] = new JsonArray(paths.Select(p => JsonValue.Create(p)!).ToArray()),
        };

        if (_currentResourceNode["arrayUniquenessConstraints"] is not JsonArray constraintsArray)
        {
            constraintsArray = [];
            _currentResourceNode["arrayUniquenessConstraints"] = constraintsArray;
        }

        constraintsArray.Add(constraintObject);

        return this;
    }

    /// <summary>
    /// Add array uniqueness constraints for a resource.
    /// An example nested parameter:
    //    [
    ///       new {
    ///           paths = "$.schools[*].schoolId",
    ///           nestedConstraints = new[] {
    ///               new {
    ///                   basePath = "$.schools[*]",
    ///                   paths = new[] { "$.sections[*].sectionIdentifier", "$.sections[*].sessionName" }
    ///               }
    ///           }
    ///       }
    ///   ]
    public ApiSchemaBuilder WithArrayUniquenessConstraint(List<object> constraints)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }
        if (_currentResourceNode == null)
        {
            throw new InvalidOperationException();
        }

        if (_currentResourceNode["arrayUniquenessConstraints"] is not JsonArray constraintsArray)
        {
            constraintsArray = [];
            _currentResourceNode["arrayUniquenessConstraints"] = constraintsArray;
        }

        foreach (var constraint in constraints)
        {
            JsonNode constraintJson = JsonSerializer.SerializeToNode(constraint)!;
            constraintsArray.Add(constraintJson);
        }

        return this;
    }
}
