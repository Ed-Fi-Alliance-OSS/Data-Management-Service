// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts the document identity for a resource
/// </summary>
///
/// <param name="ResourceSchema">The ResourceSchema for the resource</params>
internal class IdentityExtractor(ResourceSchema ResourceSchema)
{
    /// <summary>
    /// Takes an API JSON body for the resource and extracts the document identity information from the JSON body.
    /// </summary>
    public DocumentIdentity ExtractDocumentIdentity(JsonNode documentBody, ILogger _logger)
    {
        _logger.LogDebug("IdentityExtractor.ExtractDocumentIdentity");

        if (ResourceSchema.IsDescriptor)
        {
            return new DescriptorDocument(documentBody).ToDocumentIdentity();
        }

        if (ResourceSchema.IsSchoolYearEnumeration)
        {
            return new SchoolYearEnumerationDocument(documentBody).ToDocumentIdentity();
        }

        // Build up documentIdentity in order
        IEnumerable<IDocumentIdentityElement> documentIdentityElements =
            ResourceSchema.IdentityJsonPaths.Select(identityJsonPath => new DocumentIdentityElement(
                identityJsonPath,
                documentBody.SelectRequiredNodeFromPathCoerceToString(identityJsonPath.Value, _logger)
            ));

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
    public SuperclassIdentity? DeriveSuperclassIdentityFrom(
        DocumentIdentity documentIdentity,
        ILogger _logger
    )
    {
        _logger.LogDebug("IdentityExtractor.DeriveSuperclassIdentityFrom");

        // Only applies to subclasses
        if (!ResourceSchema.IsSubclass)
        {
            return null;
        }

        // Associations do not rename the identity fields in MetaEd, so the DocumentIdentity portion is the same
        if (ResourceSchema.SubclassType == "association")
        {
            return new(
                ResourceInfo: new BaseResourceInfo(
                    ResourceName: ResourceSchema.SuperclassResourceName,
                    ProjectName: ResourceSchema.SuperclassProjectName,
                    IsDescriptor: false
                ),
                DocumentIdentity: documentIdentity
            );
        }

        DocumentIdentity superclassIdentity = documentIdentity.IdentityRename(
            ResourceSchema.SuperclassIdentityJsonPath
        );

        return new(
            ResourceInfo: new BaseResourceInfo(
                ResourceName: ResourceSchema.SuperclassResourceName,
                ProjectName: ResourceSchema.SuperclassProjectName,
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
    public (DocumentIdentity, SuperclassIdentity?) Extract(JsonNode documentBody, ILogger _logger)
    {
        DocumentIdentity documentIdentity = ExtractDocumentIdentity(documentBody, _logger);
        return (documentIdentity, DeriveSuperclassIdentityFrom(documentIdentity, _logger));
    }
}
