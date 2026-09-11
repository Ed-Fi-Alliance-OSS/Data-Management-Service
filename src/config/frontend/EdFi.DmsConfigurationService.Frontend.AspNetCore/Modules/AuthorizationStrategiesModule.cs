// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using AuthorizationStrategy = EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class AuthorizationStrategiesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredGet("/v3/authorizationStrategies", GetAuthorizationStrategies);
    }

    public static async Task<IResult> GetAuthorizationStrategies(
        IClaimSetRepository claimSetRepository,
        [AsParameters] FrontendAuthorizationStrategyQuery query,
        AuthorizationStrategyPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        // Validated before the strategies are read, so an invalid query never reaches the datastore.
        await validator.GuardAsync(query);

        AuthorizationStrategyGetResult result = await claimSetRepository.GetAuthorizationStrategies();
        return result switch
        {
            AuthorizationStrategyGetResult.Success success => Results.Json(
                ApplyQuery(success.AuthorizationStrategy, query.ToQuery())
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    /// <summary>
    /// Sorts and pages the authorization strategy list in memory. The list is a small reference set,
    /// so this reuses the shared <see cref="PagingQuery"/> members (including
    /// <see cref="PagingQuery.IsDescending"/>) instead of changing the repository contract, in the
    /// same way ProfileRepository and ResourceClaimRepository order their small result sets.
    /// </summary>
    private static IReadOnlyList<AuthorizationStrategy> ApplyQuery(
        IEnumerable<AuthorizationStrategy> authorizationStrategies,
        AuthorizationStrategyQuery query
    )
    {
        // With no query parameters supplied the repository list is returned unchanged, so existing
        // callers see exactly the response they see today. The backing query has no ORDER BY, so
        // sorting here unconditionally would change the order those callers already receive.
        if (query.Offset is null && query.Limit is null && query.OrderBy is null && query.Direction is null)
        {
            return authorizationStrategies.ToList();
        }

        // Any unsupported orderBy is rejected by AuthorizationStrategyPagingQueryValidator before this
        // runs; the default arm keeps the fallback to Id safe for any future caller added without it.
        // DisplayName is nullable, so the comparer handles nulls and Id breaks ties deterministically.
        IEnumerable<AuthorizationStrategy> results = (query.OrderBy?.ToLowerInvariant() ?? "id") switch
        {
            "name" => query.IsDescending
                ? authorizationStrategies
                    .OrderByDescending(
                        strategy => strategy.AuthorizationStrategyName,
                        StringComparer.OrdinalIgnoreCase
                    )
                    .ThenBy(strategy => strategy.Id)
                : authorizationStrategies
                    .OrderBy(strategy => strategy.AuthorizationStrategyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(strategy => strategy.Id),
            "displayname" => query.IsDescending
                ? authorizationStrategies
                    .OrderByDescending(strategy => strategy.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(strategy => strategy.Id)
                : authorizationStrategies
                    .OrderBy(strategy => strategy.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(strategy => strategy.Id),
            _ => query.IsDescending
                ? authorizationStrategies.OrderByDescending(strategy => strategy.Id)
                : authorizationStrategies.OrderBy(strategy => strategy.Id),
        };

        // No implicit row cap: Skip and Take apply only when the caller supplies the value.
        if (query.Offset is not null)
        {
            results = results.Skip(query.Offset.Value);
        }

        if (query.Limit is not null)
        {
            results = results.Take(query.Limit.Value);
        }

        return results.ToList();
    }
}
