// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The elements extracted from a document that can be secured on
/// </summary>
public record DocumentSecurityElements(
    // A list of the Namespaces extracted from the document. Note these are the full
    // Namespace values and not simply prefixes
    string[] Namespace,
    // A list of the EducationOrganizations extracted from the document
    EducationOrganizationSecurityElement[] EducationOrganization,
    // A list of the Student unique ids extracted from the document
    StudentUniqueId[] Student,
    // A list of the Contact unique ids extracted from the document
    ContactUniqueId[] Contact,
    // A list of the Staff unique ids extracted from the document
    StaffUniqueId[] Staff
);

/// <summary>
/// The PropertyName and Id of an EducationOrganization type referenced by a document
/// </summary>
public record EducationOrganizationSecurityElement(
    MetaEdPropertyFullName PropertyName,
    EducationOrganizationId Id
);

/// <summary>
/// The ResourceName and JsonPath of an EducationOrganization type referenced by a document
/// </summary>
public record EducationOrganizationSecurityElementPath(MetaEdPropertyFullName PropertyName, JsonPath Path);
