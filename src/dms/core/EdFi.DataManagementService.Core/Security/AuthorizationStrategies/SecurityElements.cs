// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security.AuthorizationStrategies;

/// <summary>
/// Information representing the authorization values retrieved from the request payload
/// </summary>

public record SecurityElements(
    string[] EducationOrganization,
    string[] School,
    string[] EducationOrganizationNetwork,
    string[] CommunityOrganization,
    string[] Namespace
);
