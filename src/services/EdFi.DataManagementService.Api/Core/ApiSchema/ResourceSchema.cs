// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Api.Core.ApiSchema.Model;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Core.ApiSchema;

/// <summary>
/// Provides information from the ResourceSchema portion of an ApiSchema.json document
/// </summary>
public class ResourceSchema(JsonNode _resourceSchemaNode, ILogger _logger)
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
        // DMS-35

        return [];
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
