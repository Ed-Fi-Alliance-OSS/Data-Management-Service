// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from the ResourceSchema portion of an ApiSchema.json document
/// </summary>
internal class ResourceSchema(JsonNode _resourceSchemaNode, ILogger _logger)
{
    private readonly Lazy<MetaEdResourceName> _resourceName =
        new(() =>
        {
            return new MetaEdResourceName(
                _resourceSchemaNode["resourceName"]?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        "Expected resourceName to be on ResourceSchema, invalid ApiSchema"
                    )
            );
        });

    /// <summary>
    /// The ResourceName of this resource, taken from the resourceName
    /// </summary>
    public MetaEdResourceName ResourceName => _resourceName.Value;

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
                var sourceJsonPath = new JsonPath(x!["sourceJsonPath"]!.GetValue<string>());
                var targetJsonPath = new JsonPath(x!["targetJsonPath"]!.GetValue<string>());

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

    /// <summary>
    /// Takes an API JSON body for the resource and extracts the document identity information from the JSON body.
    /// </summary>
    public DocumentIdentity ExtractDocumentIdentity(JsonNode documentBody)
    {
        _logger.LogDebug("ExtractDocumentIdentity");

        if (IsDescriptor)
        {
            return new DescriptorDocument(documentBody).ToDocumentIdentity();
        }

        if (IsSchoolYearEnumeration)
        {
            return new SchoolYearEnumerationDocument(documentBody).ToDocumentIdentity();
        }

        // Build up documentIdentity in order
        IEnumerable<DocumentIdentityElement> documentIdentityElements = IdentityJsonPaths.Select(
            identityJsonPath => new DocumentIdentityElement(
                identityJsonPath,
                documentBody.SelectRequiredNodeFromPathCoerceToString(identityJsonPath.Value, _logger)
            )
        );

        return new DocumentIdentity(documentIdentityElements.ToList());
    }

    /// <summary>
    /// Create a SuperclassIdentity from an already constructed DocumentIdentity, if the entity should have one.
    /// If the entity is a subclass with an identity rename, replace the renamed identity property with the
    /// original superclass identity property name, thereby putting it in superclass form.
    ///
    /// For example, School is a subclass of EducationOrganization which renames educationOrganizationId
    /// to schoolId. An example document identity for a School is { name: schoolId, value: 123 }. The
    /// equivalent superclass identity for this School would be { name: educationOrganizationId, value: 123 }.
    /// </summary>
    public SuperclassIdentity? DeriveSuperclassIdentityFrom(DocumentIdentity documentIdentity)
    {
        bool isSubclass = _resourceSchemaNode.SelectNodeValue<bool>("isSubclass");

        // Only applies to subclasses
        if (!isSubclass)
        {
            return null;
        }

        string subclassType = _resourceSchemaNode.SelectNodeValue<string>("subclassType");

        string superclassResourceName = _resourceSchemaNode.SelectNodeValue<string>("superclassResourceName");

        string superclassProjectName = _resourceSchemaNode.SelectNodeValue<string>("superclassProjectName");

        // Associations do not rename the identity fields in MetaEd, so the DocumentIdentity portion is the same
        if (subclassType == "association")
        {
            return new(
                ResourceInfo: new(
                    ResourceName: new(superclassResourceName),
                    ProjectName: new(superclassProjectName),
                    IsDescriptor: false
                ),
                DocumentIdentity: documentIdentity
            );
        }

        string superclassIdentityJsonPath = _resourceSchemaNode.SelectNodeValue<string>(
            "superclassIdentityJsonPath"
        );

        DocumentIdentity superclassIdentity = documentIdentity.IdentityRename(
            new(superclassIdentityJsonPath)
        );

        return new(
            ResourceInfo: new(
                ResourceName: new(superclassResourceName),
                ProjectName: new(superclassProjectName),
                IsDescriptor: false
            ),
            DocumentIdentity: superclassIdentity
        );
    }

    /// <summary>
    /// Return both a DocumentIdentity extracted from the document body as well
    /// as a SuperclassIdentity derived from the DocumentIdentity, if the resource
    /// is a subclass.
    /// </summary>
    public (DocumentIdentity, SuperclassIdentity?) ExtractIdentities(JsonNode documentBody)
    {
        DocumentIdentity documentIdentity = ExtractDocumentIdentity(documentBody);
        return (documentIdentity, DeriveSuperclassIdentityFrom(documentIdentity));
    }

    /// <summary>
    /// Takes an API JSON body for the resource and extracts the document reference information from the JSON body.
    /// </summary>
    public DocumentReference[] ExtractDocumentReferences(JsonNode documentBody)
    {
        _logger.LogDebug("ExtractDocumentReferences");

        List<DocumentReference> result = [];

        foreach (DocumentPath documentPath in DocumentPaths)
        {
            if (!documentPath.IsReference)
                continue;
            if (documentPath.IsDescriptor)
                continue;

            // Extract the reference values from the document
            IntermediateReferenceElement[] intermediateReferenceElements = documentPath
                .ReferenceJsonPathsElements.Select(
                    referenceJsonPathsElement => new IntermediateReferenceElement(
                        referenceJsonPathsElement.IdentityJsonPath,
                        documentBody
                            .SelectNodesFromArrayPathCoerceToStrings(
                                referenceJsonPathsElement.ReferenceJsonPath.Value,
                                _logger
                            )
                            .ToArray()
                    )
                )
                .ToArray();

            // If a JsonPath selection had no results, we can assume an optional reference wasn't there
            if (Array.Exists(intermediateReferenceElements, x => x.ValueSlice.Length == 0))
                continue;

            int valueSliceLength = intermediateReferenceElements[0].ValueSlice.Length;

            // Number of document values from resolved JsonPaths should all be the same, otherwise something is very wrong
            Trace.Assert(
                Array.TrueForAll(intermediateReferenceElements, x => x.ValueSlice.Length == valueSliceLength),
                "Length of document value slices are not equal"
            );

            BaseResourceInfo resourceInfo =
                new(documentPath.ProjectName, documentPath.ResourceName, documentPath.IsDescriptor);

            // Reorient intermediateReferenceElements into actual DocumentReferences
            for (var index = 0; index < valueSliceLength; index += 1)
            {
                List<DocumentIdentityElement> documentIdentityElements = [];

                foreach (
                    IntermediateReferenceElement intermediateReferenceElement in intermediateReferenceElements
                )
                {
                    documentIdentityElements.Add(
                        new(
                            intermediateReferenceElement.IdentityJsonPath,
                            intermediateReferenceElement.ValueSlice[index]
                        )
                    );
                }

                result.Add(new(resourceInfo, new(documentIdentityElements)));
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Takes an API JSON body for the resource and extracts the descriptor URI reference information from the JSON body.
    /// </summary>
    public DocumentReference[] ExtractDescriptorValues(JsonNode documentBody)
    {
        // DMS-37

        return [];
    }
}
