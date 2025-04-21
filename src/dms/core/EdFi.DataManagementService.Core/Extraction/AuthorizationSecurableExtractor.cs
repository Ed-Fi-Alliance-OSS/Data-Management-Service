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
        var securablePaths = resourceSchema.StudentSecurityElementPaths;
        if (!securablePaths.Any())
        {
            return [];
        }

        // The IsStudentSecurable flag is currently determined based on the presence of the path element
        // in the authorization securable element.
        return [new AuthorizationSecurableInfo(SecurityElementNameConstants.StudentUniqueId)];
    }
}
