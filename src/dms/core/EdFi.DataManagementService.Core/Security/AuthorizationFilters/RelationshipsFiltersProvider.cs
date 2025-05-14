// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for various authorization strategies
/// </summary>
public static class RelationshipsFiltersProvider
{
    public static AuthorizationStrategyEvaluator GetFilters(
        ClientAuthorizations authorizations,
        string authorizationStrategyName
    )
    {
        var filters = new List<AuthorizationFilter>();
        var edOrgIdsFromClaim = authorizations
            .EducationOrganizationIds.Select(e => e.Value.ToString())
            .ToList();
        if (edOrgIdsFromClaim.Count == 0)
        {
            string noRequiredClaimError =
                $"The API client has been given permissions on a resource that uses the '{authorizationStrategyName}' authorization strategy but the client doesn't have any education organizations assigned.";
            throw new AuthorizationException(noRequiredClaimError);
        }
        foreach (var edOrgId in edOrgIdsFromClaim)
        {
            filters.Add(new AuthorizationFilter(SecurityElementNameConstants.EducationOrganization, edOrgId));
        }

        return new AuthorizationStrategyEvaluator(authorizationStrategyName, [.. filters], FilterOperator.Or);
    }
}
