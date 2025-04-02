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
    // A list of the StudentIds extracted from the document
    StudentId[] StudentId
);

/// <summary>
/// The ResourceName and Id of an EducationOrganization type referenced by a document
/// </summary>
public record EducationOrganizationSecurityElement(ResourceName ResourceName, EducationOrganizationId Id);

/// <summary>
/// The ResourceName and JsonPath of an EducationOrganization type referenced by a document
/// </summary>
public record EducationOrganizationSecurityElementPath(ResourceName ResourceName, JsonPath Path);
