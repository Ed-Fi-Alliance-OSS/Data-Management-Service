// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Represents an authorization filter with a specified
/// JSON element name for extracting data, and the expected value
/// </summary>
public abstract record AuthorizationFilter(
    /// <summary>
    /// The expected value used
    /// </summary>
    string Value
)
{
    /// <summary>
    /// Education Organization based authorization filter
    /// <param name="EducationOrganizationId"></param>
    /// </summary>
    public record EducationOrganization(string EducationOrganizationId)
        : AuthorizationFilter(EducationOrganizationId);

    /// <summary>
    /// Education Organization based authorization filter
    /// <param name="NamespacePrefix"></param>
    /// </summary>
    public record Namespace(string NamespacePrefix) : AuthorizationFilter(NamespacePrefix);

    /// <summary>
    /// Education Organization based authorization filter
    /// <param name="UniqueId"></param>
    /// </summary>
    public record StudentUniqueId(string UniqueId) : AuthorizationFilter(UniqueId);

    /// <summary>
    /// Education Organization based authorization filter
    /// <param name="UniqueId"></param>
    /// </summary>
    public record StaffUniqueId(string UniqueId) : AuthorizationFilter(UniqueId);

    /// <summary>
    /// Parent based authorization filter
    /// <param name="UniqueId"></param>
    /// </summary>
    public record ParentUniqueId(string UniqueId) : AuthorizationFilter(UniqueId);

    /// <summary>
    /// Contact based authorization filter
    /// <param name="UniqueId"></param>
    /// </summary>
    public record ContactUniqueId(string UniqueId) : AuthorizationFilter(UniqueId);
}
