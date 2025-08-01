// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from the ResourceSchema portion of an ApiSchema.json document
/// </summary>
internal class ResourceSchema(JsonNode _resourceSchemaNode)
{
    private readonly Lazy<ResourceName> _resourceName = new(() =>
    {
        return new ResourceName(
            _resourceSchemaNode["resourceName"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    "Expected resourceName to be on ResourceSchema, invalid ApiSchema"
                )
        );
    });

    /// <summary>
    /// The ResourceName of this resource, taken from the resourceName
    /// </summary>
    public ResourceName ResourceName => _resourceName.Value;

    private readonly Lazy<bool> _isSchoolYearEnumeration = new(() =>
    {
        return _resourceSchemaNode["isSchoolYearEnumeration"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isSchoolYearEnumeration to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// Whether the resource is a school year enumeration, taken from isSchoolYearEnumeration
    /// </summary>
    public bool IsSchoolYearEnumeration => _isSchoolYearEnumeration.Value;

    private readonly Lazy<bool> _isDescriptor = new(() =>
    {
        return _resourceSchemaNode["isDescriptor"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isDescriptor to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// Whether the resource is a descriptor, taken from isDescriptor
    /// </summary>
    public bool IsDescriptor => _isDescriptor.Value;

    private readonly Lazy<bool> _isResourceExtension = new(() =>
    {
        return _resourceSchemaNode["isResourceExtension"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isResourceExtension to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// Whether the resource is extending another Resource, taken from isResourceExtension
    /// </summary>
    public bool IsResourceExtension => _isResourceExtension.Value;

    private readonly Lazy<bool> _allowIdentityUpdates = new(() =>
    {
        return _resourceSchemaNode["allowIdentityUpdates"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected allowIdentityUpdates to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// Whether the resource allows identity updates, taken from allowIdentityUpdates
    /// </summary>
    public bool AllowIdentityUpdates => _allowIdentityUpdates.Value;

    private readonly Lazy<JsonNode> _jsonSchemaForInsert = new(() =>
    {
        return _resourceSchemaNode["jsonSchemaForInsert"]
            ?? throw new InvalidOperationException(
                "Expected jsonSchemaForInsert to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// The JSONSchema for the body of this resource on insert
    /// </summary>
    public JsonNode JsonSchemaForInsert => _jsonSchemaForInsert.Value;

    private Lazy<JsonNode> _jsonSchemaForUpdate =>
        new(() =>
        {
            JsonNode jsonSchemaForUpdate = JsonSchemaForInsert.DeepClone();
            jsonSchemaForUpdate["properties"]!["id"] = new JsonObject
            {
                { "type", "string" },
                { "description", "The item id" },
            };
            jsonSchemaForUpdate["required"]!.AsArray().Add("id");
            return jsonSchemaForUpdate;
        });

    /// <summary>
    /// The JSONSchema for the body of this resource on update
    /// </summary>
    public JsonNode JsonSchemaForUpdate => _jsonSchemaForUpdate.Value;

    /// <summary>
    /// Returns request method specific JSONSchema
    /// </summary>
    /// <param name="requestMethod"></param>
    /// <returns></returns>
    public JsonNode JsonSchemaForRequestMethod(RequestMethod requestMethod)
    {
        if (requestMethod == RequestMethod.POST)
        {
            return JsonSchemaForInsert;
        }
        return JsonSchemaForUpdate;
    }

    private readonly Lazy<IEnumerable<EqualityConstraint>> _equalityConstraints = new(() =>
    {
        var equalityConstraintsJsonArray =
            _resourceSchemaNode["equalityConstraints"]?.AsArray()
            ?? throw new InvalidOperationException(
                "Expected equalityConstraints to be on ResourceSchema, invalid ApiSchema"
            );

        return equalityConstraintsJsonArray.Select(x =>
        {
            JsonPath sourceJsonPath = new(x!["sourceJsonPath"]!.GetValue<string>());
            JsonPath targetJsonPath = new(x!["targetJsonPath"]!.GetValue<string>());

            return new EqualityConstraint(sourceJsonPath, targetJsonPath);
        });
    });

    /// <summary>
    /// A list of EqualityConstraints to be applied to a resource document. An EqualityConstraint is a source/target JsonPath pair.
    /// </summary>
    public IEnumerable<EqualityConstraint> EqualityConstraints => _equalityConstraints.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _identityJsonPaths = new(() =>
    {
        return _resourceSchemaNode["identityJsonPaths"]
                ?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected identityJsonPaths to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// An ordered list of the JsonPaths that are part of the identity for this resource,
    /// </summary>
    public IEnumerable<JsonPath> IdentityJsonPaths => _identityJsonPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _booleanJsonPaths = new(() =>
    {
        return _resourceSchemaNode["booleanJsonPaths"]
                ?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected booleanJsonPaths to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// An ordered list of the JsonPaths that are of type boolean, for use in type coercion
    /// </summary>
    public IEnumerable<JsonPath> BooleanJsonPaths => _booleanJsonPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _numericJsonPaths = new(() =>
    {
        return _resourceSchemaNode["numericJsonPaths"]
                ?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected numericJsonPaths to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// An ordered list of the JsonPaths that are of type boolean, for use in type coercion
    /// </summary>
    public IEnumerable<JsonPath> NumericJsonPaths => _numericJsonPaths.Value;

    /// <summary>
    /// An ordered list of the JsonPaths that are of type date, for use in format coercion
    /// </summary>
    public IEnumerable<JsonPath> DateJsonPaths => _dateJsonPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _dateJsonPaths = new(() =>
    {
        return _resourceSchemaNode["dateJsonPaths"]
                ?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected dateJsonPaths to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// An ordered list of the JsonPaths that are of type dateTime, for use in type coercion
    /// </summary>
    public IEnumerable<JsonPath> DateTimeJsonPaths => _dateTimeJsonPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _dateTimeJsonPaths = new(() =>
    {
        return _resourceSchemaNode["dateTimeJsonPaths"]
                ?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected dateTimeJsonPaths to be on ResourceSchema, invalid ApiSchema"
            );
    });

    private readonly Lazy<IEnumerable<DocumentPath>> _documentPaths = new(() =>
    {
        JsonNode documentPathsMapping =
            _resourceSchemaNode["documentPathsMapping"]
            ?? throw new InvalidOperationException(
                "Expected documentPathsMapping to be on ResourceSchema, invalid ApiSchema"
            );
        return documentPathsMapping
            .AsObject()
            .AsEnumerable()
            .Select(documentPathsMappingElement => new DocumentPath(
                documentPathsMappingElement.Value ?? throw new InvalidOperationException()
            ));
    });

    /// <summary>
    /// The list of DocumentPaths for this resource.
    ///
    /// For example, a partial documentPathsMapping for a BellSchedule document looks like:
    ///
    /// "documentPathsMapping": {
    ///   "EndTime": {
    ///     "isReference": false,
    ///     "path": "$.endTime"
    ///   },
    ///   "GradeLevelDescriptor": {
    ///     "isDescriptor": true,
    ///     "isReference": true,
    ///     "path": "$.gradeLevels[*].gradeLevelDescriptor",
    ///     "projectName": "Ed-Fi",
    ///     "resourceName": "GradeLevelDescriptor"
    ///   },
    ///   "School": {
    ///     "isDescriptor": false,
    ///     "isReference": true,
    ///     "projectName": "Ed-Fi",
    ///     "referenceJsonPaths": [
    ///       {
    ///         "identityJsonPath": "$.schoolId",
    ///         "referenceJsonPath": "$.schoolReference.schoolId"
    ///       }
    ///     ],
    ///     "resourceName": "School"
    ///   }
    /// }
    ///
    /// The list of DocumentPaths would be the object values of the keys "EndTime", "GradeLevelDescriptor"
    /// and "School".
    /// </summary>
    public IEnumerable<DocumentPath> DocumentPaths => _documentPaths.Value;

    public readonly Lazy<IEnumerable<QueryField>> _queryFields = new(() =>
    {
        JsonNode queryFieldMapping =
            _resourceSchemaNode["queryFieldMapping"]
            ?? throw new InvalidOperationException(
                "Expected queryFieldMapping to be on ResourceSchema, invalid ApiSchema"
            );
        return queryFieldMapping
            .AsObject()
            .AsEnumerable()
            .Select(queryField => new QueryField(
                queryField.Key,
                queryField
                    .Value?.AsArray()
                    .Select(x => new JsonPathAndType(
                        x!["path"]!.GetValue<string>(),
                        x["type"]!.GetValue<string>()
                    ))
                    .ToArray()
                    ?? throw new InvalidOperationException(
                        "Expected queryField to be on queryFieldMapping, invalid ApiSchema"
                    )
            ));
    });

    /// <summary>
    /// The list of QueryFields for this resource. A QueryField is a mapping of a valid query field
    /// along with a list of document paths that query field should be applied to by a query handler.
    /// </summary>
    public IEnumerable<QueryField> QueryFields
    {
        get => _queryFields.Value;
        set => throw new NotImplementedException();
    }

    private readonly Lazy<bool> _isSubclass = new(() =>
    {
        return _resourceSchemaNode["isSubclass"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isSubclass to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// Whether the resource is a subclass, taken from isSubclass
    /// </summary>
    public bool IsSubclass => _isSubclass.Value;

    /// <summary>
    /// The subclass type of this resource, such as "association", taken from subclassType
    /// </summary>
    public string SubclassType => _resourceSchemaNode.SelectNodeValue<string>("subclassType");

    /// <summary>
    /// The superclass resource name, such as "EducationOrganization", of this resource,
    /// taken from superclassResourceName
    /// </summary>
    public ResourceName SuperclassResourceName =>
        new(_resourceSchemaNode.SelectNodeValue<string>("superclassResourceName"));

    /// <summary>
    /// The superclass project name, such as "EdFi", of this resource,
    /// taken from superclassProjectName
    /// </summary>
    public ProjectName SuperclassProjectName =>
        new(_resourceSchemaNode.SelectNodeValue<string>("superclassProjectName"));

    /// <summary>
    /// The superclass version of the identity JsonPath, such as "$.educationOrganizationId", of this resource,
    /// taken from superclassIdentityJsonPath
    /// </summary>
    public JsonPath SuperclassIdentityJsonPath =>
        new(_resourceSchemaNode.SelectNodeValue<string>("superclassIdentityJsonPath"));

    private readonly Lazy<IEnumerable<JsonPath>> _namespaceSecurityElementPaths = new(() =>
    {
        return _resourceSchemaNode["securableElements"]
                ?["Namespace"]?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected securableElements.Namespace to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// A list of the JsonPaths that are namespace security elements, for authorization.
    /// Note these can be array paths.
    /// </summary>
    public IEnumerable<JsonPath> NamespaceSecurityElementPaths => _namespaceSecurityElementPaths.Value;

    private readonly Lazy<
        IEnumerable<EducationOrganizationSecurityElementPath>
    > _educationOrganizationSecurityElementPaths = new(() =>
    {
        return _resourceSchemaNode["securableElements"]
                ?["EducationOrganization"]?.AsArray()
                .Select(x => new EducationOrganizationSecurityElementPath(
                    new MetaEdPropertyFullName(x!["metaEdName"]!.GetValue<string>()),
                    new JsonPath(x!["jsonPath"]!.GetValue<string>())
                ))
            ?? throw new InvalidOperationException(
                "Expected securableElements.EducationOrganization to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// A list of the ResourceNames and JsonPaths that are education organization security elements, for authorization.
    /// Note these can be array paths.
    /// </summary>
    public IEnumerable<EducationOrganizationSecurityElementPath> EducationOrganizationSecurityElementPaths =>
        _educationOrganizationSecurityElementPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _studentSecurityElementPaths = new(() =>
    {
        return _resourceSchemaNode["securableElements"]
                ?["Student"]?.AsArray()
                .GetValues<string>()
                .Select(path => new JsonPath(path))
            ?? throw new InvalidOperationException(
                "Expected securableElements.Student to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// A list of the JsonPaths that are student security elements, for authorization.
    /// </summary>
    public IEnumerable<JsonPath> StudentSecurityElementPaths => _studentSecurityElementPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _contactSecurityElementPaths = new(() =>
    {
        return _resourceSchemaNode["securableElements"]
                ?["Contact"]?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected securableElements.Contact to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// A list of the JsonPaths that are authorizationSecurable for type Contact
    /// </summary>
    public IEnumerable<JsonPath> ContactSecurityElementPaths => _contactSecurityElementPaths.Value;

    private readonly Lazy<IEnumerable<JsonPath>> _staffSecurityElementPaths = new(() =>
    {
        return _resourceSchemaNode["securableElements"]
                ?["Staff"]?.AsArray()
                .GetValues<string>()
                .Select(x => new JsonPath(x))
            ?? throw new InvalidOperationException(
                "Expected securableElements.Staff to be on ResourceSchema, invalid ApiSchema"
            );
    });

    public IEnumerable<JsonPath> StaffSecurityElementPaths => _staffSecurityElementPaths.Value;

    private readonly Lazy<IEnumerable<string>> _authorizationPathways = new(() =>
    {
        return _resourceSchemaNode["authorizationPathways"]?.AsArray().GetValues<string>()
            ?? throw new InvalidOperationException(
                "Expected authorizationPathways to be on ResourceSchema, invalid ApiSchema"
            );
    });

    /// <summary>
    /// The AuthorizationPathways the resource is part of.
    /// </summary>
    public IEnumerable<string> AuthorizationPathways => _authorizationPathways.Value;

    private readonly Lazy<IEnumerable<DecimalValidationInfo>> _decimalPropertyValidationInfos = new(() =>
    {
        JsonNode decimalPropertyValidationInfos =
            _resourceSchemaNode["decimalPropertyValidationInfos"]
            ?? throw new InvalidOperationException(
                "Expected decimalPropertyValidationInfos to be on ResourceSchema, invalid ApiSchema"
            );

        return decimalPropertyValidationInfos
            .AsArray()
            .Select(x =>
            {
                JsonPath path = new JsonPath(x!["path"]!.GetValue<string>());
                short? totalDigits = x!["totalDigits"]!.GetValue<short?>();
                short? decimalPlaces = x!["decimalPlaces"]!.GetValue<short?>();

                return new DecimalValidationInfo(path, totalDigits, decimalPlaces);
            });
    });

    public IEnumerable<DecimalValidationInfo> DecimalPropertyValidationInfos =>
        _decimalPropertyValidationInfos.Value;

    private readonly Lazy<IReadOnlyList<ArrayUniquenessConstraint>> _arrayUniquenessConstraints = new(() =>
    {
        var constraintsNode = _resourceSchemaNode["arrayUniquenessConstraints"];
        if (constraintsNode == null)
        {
            return Array.Empty<ArrayUniquenessConstraint>().ToList().AsReadOnly();
        }

        var outerArray =
            constraintsNode.AsArray()
            ?? throw new InvalidOperationException(
                "Expected arrayUniquenessConstraints to be an array on ResourceSchema, invalid ApiSchema"
            );

        return outerArray.Select(ParseArrayUniquenessConstraint).ToList().AsReadOnly();
    });

    /// <summary>
    /// The ArrayUniquenessConstraints for this resource using the new object-based format.
    /// </summary>
    public IReadOnlyList<ArrayUniquenessConstraint> ArrayUniquenessConstraints =>
        _arrayUniquenessConstraints.Value;

    /// <summary>
    /// Parses an array uniqueness constraint from JSON using the new object-based format
    /// </summary>
    private static ArrayUniquenessConstraint ParseArrayUniquenessConstraint(JsonNode? constraintNode)
    {
        if (constraintNode == null)
        {
            throw new InvalidOperationException("Array uniqueness constraint node cannot be null");
        }

        if (constraintNode is not JsonObject constraintObject)
        {
            throw new InvalidOperationException(
                $"Invalid array uniqueness constraint format. Expected object, got {constraintNode.GetType()}"
            );
        }

        JsonPath? basePath = null;
        IReadOnlyList<JsonPath>? paths = null;
        IReadOnlyList<ArrayUniquenessConstraint>? nestedConstraints = null;

        // Parse basePath if present
        if (constraintObject["basePath"] != null)
        {
            basePath = new JsonPath(constraintObject["basePath"]!.GetValue<string>());
        }

        // Parse paths if present
        if (constraintObject["paths"] is JsonArray pathsArray)
        {
            paths = pathsArray
                .Select(pathElement => new JsonPath(pathElement!.GetValue<string>()!))
                .ToList()
                .AsReadOnly();
        }

        // Parse nestedConstraints if present
        if (constraintObject["nestedConstraints"] is JsonArray nestedArray)
        {
            nestedConstraints = nestedArray.Select(ParseArrayUniquenessConstraint).ToList().AsReadOnly();
        }

        return new ArrayUniquenessConstraint(basePath, paths, nestedConstraints);
    }

    private readonly Lazy<JsonNode?> _openApiFragments = new(() =>
    {
        return _resourceSchemaNode["openApiFragments"];
    });

    /// <summary>
    /// The OpenAPI fragments for this resource, containing resources and/or descriptors fragments
    /// </summary>
    public JsonNode? OpenApiFragments => _openApiFragments.Value;
}
