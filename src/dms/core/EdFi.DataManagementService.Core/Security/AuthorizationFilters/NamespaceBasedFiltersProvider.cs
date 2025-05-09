// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for namespace based authorization strategy
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NamespaceBasedFiltersProvider : IAuthorizationFiltersProvider
{
    private const string AuthorizationStrategyName = "NamespaceBased";

    public AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations)
    {
        var filters = new List<AuthorizationFilter>();
        var namespacePrefixesFromClaim = authorizations.NamespacePrefixes;
        if (namespacePrefixesFromClaim.Count == 0)
        {
            string noRequiredClaimError =
                $"The API client has been given permissions on a resource that uses the '{AuthorizationStrategyName}' authorization strategy but the client doesn't have any namespace prefixes assigned.";
            throw new AuthorizationException(noRequiredClaimError);
        }
        foreach (var namespacePrefix in namespacePrefixesFromClaim)
        {
            filters.Add(
                new AuthorizationFilter.Namespace(
                    SecurityElementNameConstants.Namespace,
                    namespacePrefix.Value
                )
            );
        }

        return new AuthorizationStrategyEvaluator(AuthorizationStrategyName, [.. filters], FilterOperator.Or);
    }
}
