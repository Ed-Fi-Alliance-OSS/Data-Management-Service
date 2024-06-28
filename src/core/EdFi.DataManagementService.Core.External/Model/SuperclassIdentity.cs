// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The optional superclass information for a DocumentInfo. Applies only to documents that are subclasses,
/// providing superclass identity information. (Note that only a single level of subclassing is permitted.)
/// </summary>
public record SuperclassIdentity(
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
    BaseResourceInfo ResourceInfo,
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
    DocumentIdentity DocumentIdentity,
    /// <summary>
    /// The referentialId derived from the DocumentIdentity
    /// </summary>
    ReferentialId ReferentialId
) : DocumentReference(ResourceInfo, DocumentIdentity, ReferentialId);
