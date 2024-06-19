// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// The optional superclass information for a DocumentInfo. Applies only to documents that are subclasses,
/// providing superclass identity information. (Note that only a single level of subclassing is permitted.)
/// </summary>
internal record SuperclassIdentity(
    /// <summary>
    /// The base API resource information for the superclass of the document.
    ///
    /// Example for ResourceInfo.ProjectName:
    /// XYZStudentProgramAssociation is created in an extension project named 'ABC',
    /// and it subclasses GeneralStudentProgramAssociation from the data standard.
    /// The project name would be 'Ed-Fi'.
    ///
    /// Example for ResourceInfo.ResourceName:
    /// If the entity for this mapping is School (subclass of EducationOrganization),
    /// then the resourceName would be EducationOrganization.
    ///
    /// Example for ResourceInfo.IsDescriptor:
    /// It should always be false as descriptors cannot be subclassed.
    /// </summary>
    IBaseResourceInfo ResourceInfo,
    /// <summary>
    /// This is the identity of the document, but in the form of the superclass
    /// identity. This differs from the regular identity because the subclass will have an
    /// identity element renamed.
    ///
    /// Example: EducationOrganization has educationOrganizationId as its identity.
    ///          School is a subclass of EducationOrganization and has identity renamed
    ///          from educationOrganizationId to schoolId.
    ///          This documentIdentity will use educationOrganizationId instead of schoolId.
    /// </summary>
    IDocumentIdentity DocumentIdentity
) : DocumentReference(ResourceInfo, DocumentIdentity), ISuperclassIdentity
{
    /// <summary>
    /// Returns the string form of a ResourceInfo for identity hashing.
    /// </summary>
    private static string ResourceInfoString(IBaseResourceInfo resourceInfo)
    {
        return $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}";
    }

    /// <summary>
    /// Returns the string form of a DocumentIdentity.
    /// </summary>
    private string DocumentIdentityString()
    {
        return string.Join(
            "#",
            DocumentIdentity.DocumentIdentityElements.Select(
                (IDocumentIdentityElement element) =>
                    $"${element.IdentityJsonPath.Value}=${element.IdentityValue}"
            )
        );
    }

    /// <summary>
    /// Returns a ReferentialId as a UUIDv5-compliant deterministic UUID per RFC 4122.
    /// </summary>
    public ReferentialId ToReferentialId(IBaseResourceInfo resourceInfo)
    {
        return new(
            Deterministic.Create(
                Model.DocumentIdentity.EdFiUuidv5Namespace,
                $"{ResourceInfoString(resourceInfo)}{DocumentIdentityString()}"
            )
        );
    }
};
