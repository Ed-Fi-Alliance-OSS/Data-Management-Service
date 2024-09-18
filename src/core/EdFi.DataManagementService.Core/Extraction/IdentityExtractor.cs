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
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts the document identity for a resource
/// </summary>
internal static class IdentityExtractor
{
    /// <summary>
    /// Takes an API JSON body for the resource and extracts the document identity information from the JSON body.
    /// </summary>
    public static DocumentIdentity ExtractDocumentIdentity(
        ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        logger.LogDebug("IdentityExtractor.ExtractDocumentIdentity");

        if (resourceSchema.IsDescriptor)
        {
            return new DescriptorDocument(documentBody).ToDocumentIdentity();
        }

        if (resourceSchema.IsSchoolYearEnumeration)
        {
            return new SchoolYearEnumerationDocument(documentBody).ToDocumentIdentity();
        }

        // Build up documentIdentity in order
        IEnumerable<DocumentIdentityElement> documentIdentityElements =
            resourceSchema.IdentityJsonPaths.Select(identityJsonPath => new DocumentIdentityElement(
                identityJsonPath,
                documentBody.SelectRequiredNodeFromPathCoerceToString(identityJsonPath.Value, logger)
            ));

        return new DocumentIdentity(documentIdentityElements.ToArray());
    }

    /// <summary>
    /// For a DocumentIdentity with a single element, returns a new DocumentIdentity with the
    /// element DocumentObjectKey replaced with a new DocumentObjectKey.
    /// </summary>
    public static DocumentIdentity IdentityRename(
        JsonPath superclassIdentityJsonPath,
        DocumentIdentityElement identityElement
    )
    {
        DocumentIdentityElement[] newElementArray =
        [
            new DocumentIdentityElement(superclassIdentityJsonPath, identityElement.IdentityValue)
        ];
        return new(newElementArray);
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
    public static SuperclassIdentity? DeriveSuperclassIdentityFrom(
        ResourceSchema resourceSchema,
        DocumentIdentity documentIdentity,
        ILogger logger
    )
    {
        logger.LogDebug("IdentityExtractor.DeriveSuperclassIdentityFrom");

        // Only applies to subclasses
        if (!resourceSchema.IsSubclass)
        {
            return null;
        }

        BaseResourceInfo superclassResourceInfo =
            new(
                ResourceName: resourceSchema.SuperclassResourceName,
                ProjectName: resourceSchema.SuperclassProjectName,
                IsDescriptor: false
            );

        // Associations do not rename the identity fields in MetaEd, so the DocumentIdentity portion is the same
        if (resourceSchema.SubclassType == "association")
        {
            return new(
                superclassResourceInfo,
                documentIdentity,
                ReferentialIdFrom(superclassResourceInfo, documentIdentity)
            );
        }

        DocumentIdentity superclassIdentity = IdentityRename(
            resourceSchema.SuperclassIdentityJsonPath,
            documentIdentity.DocumentIdentityElements[0]
        );

        return new(
            superclassResourceInfo,
            superclassIdentity,
            ReferentialIdFrom(superclassResourceInfo, superclassIdentity)
        );
    }

    /// <summary>
    /// Return both a DocumentIdentity extracted from the document body as well
    /// as a SuperclassIdentity derived from the DocumentIdentity, if the resource
    /// is a subclass.
    /// </summary>
    public static (DocumentIdentity, SuperclassIdentity?) ExtractIdentities(
        this ResourceSchema resourceSchema,
        JsonNode documentBody,
        ILogger logger
    )
    {
        DocumentIdentity documentIdentity = ExtractDocumentIdentity(resourceSchema, documentBody, logger);
        return (documentIdentity, DeriveSuperclassIdentityFrom(resourceSchema, documentIdentity, logger));
    }
}
