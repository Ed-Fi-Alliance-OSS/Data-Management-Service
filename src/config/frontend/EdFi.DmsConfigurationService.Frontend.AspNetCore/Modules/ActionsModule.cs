// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ActionsModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredGet("/v3/actions", GetUserActions);
    }

    private static async Task<IResult> GetUserActions(
        IClaimSetRepository repository,
        [AsParameters] FrontendActionQuery query,
        ActionPagingQueryValidator validator
    )
    {
        // Validated before the list is read, so an invalid query never reaches the repository.
        await validator.GuardAsync(query);

        var actionList = repository.GetActions();

        return Results.Ok(ApplyQuery(actionList, query.ToQuery()));
    }

    /// <summary>
    /// Filters, sorts and pages the fixed action list in memory. Actions are a small reference set,
    /// so this reuses the shared <see cref="PagingQuery"/> members (including
    /// <see cref="PagingQuery.IsDescending"/>) instead of changing the repository contract, in the
    /// same way ProfileRepository and ResourceClaimRepository order their small result sets.
    /// </summary>
    private static IReadOnlyList<Action> ApplyQuery(IEnumerable<Action> actions, ActionQuery query)
    {
        // With no query parameters supplied the repository list is returned unchanged, so existing
        // callers see exactly the response they see today.
        if (
            query.Offset is null
            && query.Limit is null
            && query.OrderBy is null
            && query.Direction is null
            && query.Id is null
            && query.Name is null
        )
        {
            return actions.ToList();
        }

        IEnumerable<Action> results = actions;

        if (query.Id is not null)
        {
            results = results.Where(action => action.Id == query.Id.Value);
        }

        if (query.Name is not null)
        {
            results = results.Where(action =>
                string.Equals(action.Name, query.Name, StringComparison.OrdinalIgnoreCase)
            );
        }

        // Any unsupported orderBy is rejected by ActionPagingQueryValidator before this runs; the
        // default arm keeps the fallback to Id safe for any future caller added without it.
        results = (query.OrderBy?.ToLowerInvariant() ?? "id") switch
        {
            "name" => query.IsDescending
                ? results
                    .OrderByDescending(action => action.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(action => action.Id)
                : results
                    .OrderBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(action => action.Id),
            _ => query.IsDescending
                ? results.OrderByDescending(action => action.Id)
                : results.OrderBy(action => action.Id),
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
