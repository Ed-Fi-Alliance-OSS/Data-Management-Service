// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using Json.Schema;

namespace EdFi.DataManagementService.Tests.E2E.Builders;

/// <summary>
/// This class provides a fluent interface for building an ApiSchema suitable for E2E testing,
/// allowing tests to create synthetic schemas focused on specific scenarios
/// </summary>
public class ApiSchemaBuilder
{
    private JsonNode? _currentProjectNode = null;
    private bool _isCoreProject = false;

    private JsonNode? _coreProjectNode = null;
    private readonly List<JsonNode> _extensionProjectNodes = [];

    private JsonNode? _currentResourceNode = null;
    private JsonNode? _currentDocumentPathsMappingNode = null;

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
    /// Writes the built API schemas to the specified directory
    /// </summary>
    public async Task WriteApiSchemasToDirectoryAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        // Clear existing ApiSchema files
        foreach (var file in Directory.GetFiles(directoryPath, "ApiSchema*.json"))
        {
            File.Delete(file);
        }

        // Write core schema
        if (_coreProjectNode != null)
        {
            var coreSchemaPath = Path.Combine(directoryPath, "ApiSchema.json");
            var coreSchemaJson = JsonSerializer.Serialize(
                ToApiSchemaRootNode(_coreProjectNode),
                new JsonSerializerOptions { WriteIndented = true }
            );
            await File.WriteAllTextAsync(coreSchemaPath, coreSchemaJson);
        }

        // Write extension schemas
        int extensionIndex = 1;
        foreach (var extensionNode in _extensionProjectNodes)
        {
            var extensionPath = Path.Combine(directoryPath, $"ApiSchema.Extension{extensionIndex}.json");
            var extensionJson = JsonSerializer.Serialize(
                ToApiSchemaRootNode(extensionNode),
                new JsonSerializerOptions { WriteIndented = true }
            );
            await File.WriteAllTextAsync(extensionPath, extensionJson);
            extensionIndex++;
        }
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
            ["booleanJsonPaths"] = new JsonArray(),
            ["dateTimeJsonPaths"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
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
            ["securableElements"] = new JsonObject
            {
                ["Namespace"] = new JsonArray(),
                ["EducationOrganization"] = new JsonArray(),
                ["Student"] = new JsonArray(),
                ["Contact"] = new JsonArray(),
                ["Staff"] = new JsonArray(),
            },
            ["authorizationPathways"] = new JsonArray(),
            ["arrayUniquenessConstraints"] = new JsonArray(),
        };

        string endpointName = ToEndpointName(resourceName);
        _currentProjectNode["resourceNameMapping"]![resourceName] = endpointName;
        _currentProjectNode["resourceSchemas"]![endpointName] = _currentResourceNode;
        _currentProjectNode["caseInsensitiveEndpointNameMapping"]![endpointName.ToLower()] = endpointName;
        return this;
    }

    /// <summary>
    /// Adds an identityJsonPaths section to a resource
    /// </summary>
    public ApiSchemaBuilder WithIdentityJsonPaths(params string[] identityJsonPaths)
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
    /// Define resource schema using a simple object structure
    /// </summary>
    public ApiSchemaBuilder WithSimpleJsonSchema(params (string propertyName, string type)[] properties)
    {
        var schemaBuilder = new JsonSchemaBuilder().Type(SchemaValueType.Object).AdditionalProperties(false);

        var props = new Dictionary<string, JsonSchema>();

        foreach (var (propertyName, type) in properties)
        {
            var propertySchema = type.ToLower() switch
            {
                "string" => new JsonSchemaBuilder().Type(SchemaValueType.String).Build(),
                "number" => new JsonSchemaBuilder().Type(SchemaValueType.Number).Build(),
                "integer" => new JsonSchemaBuilder().Type(SchemaValueType.Integer).Build(),
                "boolean" => new JsonSchemaBuilder().Type(SchemaValueType.Boolean).Build(),
                "array" => new JsonSchemaBuilder().Type(SchemaValueType.Array).Build(),
                "object" => new JsonSchemaBuilder().Type(SchemaValueType.Object).Build(),
                _ => throw new ArgumentException($"Unknown type: {type}"),
            };

            props[propertyName] = propertySchema;
        }

        schemaBuilder.Properties(props);
        return WithJsonSchemaForInsert(schemaBuilder.Build());
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
    /// Adds a DocumentPath to a DocumentPathsMapping for a reference path. Makes some
    /// simplifying assumptions.
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
    /// Adds a simple reference where the reference property name is based on the resource name
    /// </summary>
    public ApiSchemaBuilder WithSimpleReference(
        string referencedResourceName,
        string identityProperty,
        string referenceProjectName = "Ed-Fi"
    )
    {
        var referenceName =
            char.ToLower(referencedResourceName[0]) + referencedResourceName[1..] + "Reference";
        return WithDocumentPathReference(
            referencedResourceName,
            [
                new KeyValuePair<string, string>(
                    $"$.{identityProperty}",
                    $"$.{referenceName}.{identityProperty}"
                ),
            ],
            referenceProjectName
        );
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
    /// Add array uniqueness constraint with simple paths to a resource.
    /// Must be of form "$.someArray[*].scalarPathFromHere"
    /// </summary>
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
    /// Generates an UploadSchemaRequest with all built schemas
    /// </summary>
    public UploadSchemaRequest GenerateUploadRequest()
    {
        if (_coreProjectNode == null)
        {
            throw new InvalidOperationException("No core project has been built");
        }

        var coreSchema = JsonSerializer.Serialize(ToApiSchemaRootNode(_coreProjectNode));

        var extensionSchemas = _extensionProjectNodes
            .Select(node => JsonSerializer.Serialize(ToApiSchemaRootNode(node)))
            .ToArray();

        return new UploadSchemaRequest(
            CoreSchema: coreSchema,
            ExtensionSchemas: extensionSchemas.Length > 0 ? extensionSchemas : null
        );
    }
}
