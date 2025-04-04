// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Represents the path the authorization logic should take to calculate the EducationOrganizations that can reach the related Securable Document(s).
/// </summary>
public record AuthorizationPathway
{
    /// <summary>
    /// Authorization Pathway that uses StudentSchoolAssociation to calculate the EducationOrganizations that can reach the related Securable Document(s).
    /// </summary>
    /// <param name="StudentId">Extracted from the request body.</param>
    /// <param name="SchoolId">Extracted from the request body.</param>
    public record StudentSchoolAssociation(StudentId StudentId, EducationOrganizationId SchoolId)
        : AuthorizationPathway;

    private AuthorizationPathway() { }
}
