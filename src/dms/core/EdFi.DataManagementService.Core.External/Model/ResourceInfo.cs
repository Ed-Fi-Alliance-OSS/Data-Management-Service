// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// API resource information including version
/// </summary>
public record ResourceInfo(
    ProjectName ProjectName,
    ResourceName ResourceName,
    bool IsDescriptor,
    /// <summary>
    /// The project version the resource belongs to.
    /// </summary>
    SemVer ResourceVersion,
    /// <summary>
    /// Whether the resource allows the identity fields of a document to be updated (changed)
    /// </summary>
    bool AllowIdentityUpdates,
    /// <summary>
    /// If a resource is part of the EducationOrganization hierarchy, information about its
    /// place in that hierarchy for security.
    /// </summary>
    EducationOrganizationHierarchyInfo EducationOrganizationHierarchyInfo,
    /// <summary>
    /// How this resource is securable relative to other resources
    /// </summary>
    AuthorizationSecurableInfo[] AuthorizationSecurableInfo
) : BaseResourceInfo(ProjectName, ResourceName, IsDescriptor);
