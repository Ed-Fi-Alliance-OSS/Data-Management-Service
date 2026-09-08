// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;

namespace EdFi.DataManagementService.Core.Utilities;

internal static class ProjectEndpointNameExtensions
{
    /// <summary>
    /// Generates a resource claim URI for the specified project and resource.
    /// </summary>
    public static string GetResourceClaimUri(this ProjectEndpointName projectName, ResourceName resourceName)
    {
        string resourceClaimUri =
            $"{Conventions.EdFiOdsResourceClaimBaseUri}/{projectName.Value}/{resourceName.Value}";

        return resourceClaimUri;
    }

    /// <summary>
    /// Generates an endpoint URI path for the specified project and endpoint.
    /// </summary>
    internal static string GetEndpointUri(
        this ProjectEndpointName projectEndpointName,
        EndpointName endpointName
    )
    {
        return $"/{projectEndpointName.Value}/{endpointName.Value}";
    }
}
