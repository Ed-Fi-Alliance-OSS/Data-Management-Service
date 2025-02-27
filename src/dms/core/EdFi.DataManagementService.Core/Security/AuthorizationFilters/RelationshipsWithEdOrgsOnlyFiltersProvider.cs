// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for RelationshipsWithEdOrgsOnly authorization strategy
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class RelationshipsWithEdOrgsOnlyFiltersProvider : IAuthorizationFiltersProvider
{
    private const string AuthorizationStrategyName = "RelationshipsWithEdOrgsOnly";

    public AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations)
    {
        var filters = new List<AuthorizationFilter>();
        foreach (var edOrgId in authorizations.EducationOrganizationIds)
        {
            filters.Add(
                new AuthorizationFilter(
                    "EducationOrganization",
                    edOrgId.Value.ToString(),
                    "Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: {claims}.",
                    FilterComparison.Equals
                )
            );
        }

        return new AuthorizationStrategyEvaluator([.. filters], FilterOperator.Or);
    }
}
