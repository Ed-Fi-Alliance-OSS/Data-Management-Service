// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from the ResourceSchema portion of an ApiSchema.json document
/// </summary>
internal class ResourceSchema(JsonNode _resourceSchemaNode)
{
    private readonly Lazy<ResourceName> _resourceName =
        new(() =>
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

    private readonly Lazy<bool> _isSchoolYearEnumeration =
        new(() =>
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

    private readonly Lazy<bool> _isDescriptor =
        new(() =>
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

    private readonly Lazy<bool> _allowIdentityUpdates =
        new(() =>
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

    private readonly Lazy<JsonNode> _jsonSchemaForInsert =
        new(() =>
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
                { "description", "The item id" }
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

    private readonly Lazy<IEnumerable<EqualityConstraint>> _equalityConstraints =
        new(() =>
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

    private readonly Lazy<IEnumerable<JsonPath>> _identityJsonPaths =
        new(() =>
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

    private readonly Lazy<IEnumerable<JsonPath>> _booleanJsonPaths =
        new(() =>
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

    private readonly Lazy<IEnumerable<JsonPath>> _numericJsonPaths =
        new(() =>
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

    private readonly Lazy<IEnumerable<DocumentPath>> _documentPaths =
        new(() =>
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

    public readonly Lazy<IEnumerable<QueryField>> _queryFields =
        new(() =>
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
                    queryField.Value?.AsArray().GetValues<string>().Select(x => new JsonPath(x)).ToArray()
                        ?? throw new InvalidOperationException(
                            "Expected queryField to be on queryFieldMapping, invalid ApiSchema"
                        )
                ));
        });

    /// <summary>
    /// The list of QueryFields for this resource. A QueryField is a mapping of a valid query field
    /// along with a list of document paths that query field should be applied to by a query handler.
    /// </summary>
    public IEnumerable<QueryField> QueryFields => _queryFields.Value;

    private readonly Lazy<bool> _isSubclass =
        new(() =>
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
}
