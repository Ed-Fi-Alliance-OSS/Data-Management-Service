// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Extracts the AuthorizationSecurableInfo from a resource
/// </summary>
internal static class AuthorizationSecurableExtractor
{
    public static AuthorizationSecurableInfo[] ExtractAuthorizationSecurableInfo(
        this ResourceSchema resourceSchema
    )
    {
        // The securable flag is currently determined based on the presence of the specific path element
        // in the securable elements list.

        // Add student securable
        List<AuthorizationSecurableInfo> authorizationSecurable = [];
        var studentSecurablePaths = resourceSchema.StudentSecurityElementPaths;
        if (studentSecurablePaths.Any())
        {
            authorizationSecurable.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.StudentUniqueId)
            );
        }

        // Add contact securable
        var contactSecurablePaths = resourceSchema.ContactSecurityElementPaths;
        if (contactSecurablePaths.Any())
        {
            authorizationSecurable.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.ContactUniqueId)
            );
        }

        // add namespace securable
        var namespaceSecurablePaths = resourceSchema.NamespaceSecurityElementPaths;
        if (namespaceSecurablePaths.Any())
        {
            authorizationSecurable.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.Namespace)
            );
        }

        //add education organization securable
        var educationOrganizationSecurablePaths = resourceSchema.EducationOrganizationSecurityElementPaths;
        if (educationOrganizationSecurablePaths.Any())
        {
            authorizationSecurable.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.EducationOrganization)
            );
        }

        var staffSecurablePaths = resourceSchema.StaffSecurityElementPaths;
        if (staffSecurablePaths.Any())
        {
            authorizationSecurable.Add(
                new AuthorizationSecurableInfo(SecurityElementNameConstants.StaffUniqueId)
            );
        }
        return [.. authorizationSecurable];
    }
}
