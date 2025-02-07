// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for namespace based authorization strategy
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NamespaceBasedFilters : IAuthorizationFilters
{
    private const string AuthorizationStrategyName = "NamespaceBased";

    public AuthorizationStrategyFilter Create(ApiClientDetails details)
    {
        var filters = new List<AuthorizationFilter>();
        foreach (var namespacePrefix in details.NamespacePrefixes)
        {
            filters.Add(new AuthorizationFilter("Namespace", namespacePrefix.Value));
        }

        return new AuthorizationStrategyFilter([.. filters], FilterOperator.Or);
    }
}
