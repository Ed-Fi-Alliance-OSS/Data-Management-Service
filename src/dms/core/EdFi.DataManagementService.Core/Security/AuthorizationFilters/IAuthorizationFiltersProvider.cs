// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides the authorization strategy filter for particular authorization strategy
/// </summary>
public interface IAuthorizationFiltersProvider
{
    AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations);
}

public abstract class AuthorizationFiltersProviderBase(string AuthorizationStrategyName)
    : IAuthorizationFiltersProvider
{
    protected readonly string _authorizationStrategyName = AuthorizationStrategyName;

    public virtual AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations)
    {
        return new AuthorizationStrategyEvaluator(_authorizationStrategyName, [], FilterOperator.Or);
    }

    protected AuthorizationStrategyEvaluator GetRelationshipFilters(ClientAuthorizations authorizations)
    {
        var filters = new List<AuthorizationFilter>();
        var edOrgIdsFromClaim = authorizations
            .EducationOrganizationIds.Select(e => e.Value.ToString())
            .ToList();
        if (edOrgIdsFromClaim.Count == 0)
        {
            string noRequiredClaimError =
                $"The API client has been given permissions on a resource that uses the '{_authorizationStrategyName}' authorization strategy but the client doesn't have any education organizations assigned.";
            throw new AuthorizationException(noRequiredClaimError);
        }
        foreach (var edOrgId in edOrgIdsFromClaim)
        {
            filters.Add(new AuthorizationFilter.EducationOrganization(edOrgId));
        }

        return new AuthorizationStrategyEvaluator(
            _authorizationStrategyName,
            [.. filters],
            FilterOperator.Or
        );
    }

    protected AuthorizationStrategyEvaluator GetNamespaceFilters(ClientAuthorizations authorizations)
    {
        var filters = new List<AuthorizationFilter>();
        var namespacePrefixesFromClaim = authorizations.NamespacePrefixes;
        if (namespacePrefixesFromClaim.Count == 0)
        {
            string noRequiredClaimError =
                $"The API client has been given permissions on a resource that uses the '{_authorizationStrategyName}' authorization strategy but the client doesn't have any namespace prefixes assigned.";
            throw new AuthorizationException(noRequiredClaimError);
        }
        foreach (var namespacePrefix in namespacePrefixesFromClaim)
        {
            filters.Add(new AuthorizationFilter.Namespace(namespacePrefix.Value));
        }

        return new AuthorizationStrategyEvaluator(
            _authorizationStrategyName,
            [.. filters],
            FilterOperator.Or
        );
    }
}
