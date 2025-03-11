// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A handler to determine if the client is authorized for the resource.
/// </summary>
public interface IResourceAuthorizationHandler
{
    /// <summary>
    /// Determine whether the authorization conditions are met for the given
    /// namespaces and educationOrganizationIds
    /// </summary>
    /// <returns></returns>
    ResourceAuthorizationResult Authorize(string[] namespaces, long[] educationOrganizationIds);

    /// <summary>
    /// Returns whether this handler expects to evaluate education
    /// organizations, based on the authorization strategy used to create it. We
    /// won't bother to hydrate the education organization hierarchy data if
    /// this is false.
    /// </summary>
    bool IsRelationshipWithEdOrg { get; }
}
