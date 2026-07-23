// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ResourceClaimModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredGet("/v3/resourceClaims", GetAll)
            .WithQueryParameterValidation<FrontendResourceClaimQuery>();
        endpoints.MapSecuredGet("/v3/resourceClaims/{id}", GetById);
        endpoints
            .MapSecuredGet("/v3/resourceClaimActions", GetActions)
            .WithQueryParameterValidation<FrontendResourceClaimActionQuery>();
        endpoints
            .MapSecuredGet("/v3/resourceClaimActionAuthStrategies", GetActionAuthStrategies)
            .WithQueryParameterValidation<FrontendResourceClaimActionAuthStrategyQuery>();
    }

    private static async Task<IResult> GetAll(
        IResourceClaimRepository repository,
        [AsParameters] FrontendResourceClaimQuery query,
        ResourceClaimPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardQueryAsync(query);
        var result = await repository.GetResourceClaims(query.ToQuery());

        return result switch
        {
            ResourceClaimListResult.Success success => Results.Ok(success.ResourceClaims),
            ResourceClaimListResult.FailureHierarchyNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    "The claims hierarchy was not found.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ResourceClaimListResult.FailureProjectionIntegrity => FailureResults.Unknown(
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        IResourceClaimRepository repository,
        HttpContext httpContext
    )
    {
        var result = await repository.GetResourceClaim(id);

        return result switch
        {
            ResourceClaimGetResult.Success success => Results.Ok(success.ResourceClaim),
            ResourceClaimGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound($"ResourceClaim {id} not found.", httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ResourceClaimGetResult.FailureHierarchyNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    "The claims hierarchy was not found.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ResourceClaimGetResult.FailureProjectionIntegrity => FailureResults.Unknown(
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetActions(
        IResourceClaimRepository repository,
        [AsParameters] FrontendResourceClaimActionQuery query,
        ResourceClaimActionPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardQueryAsync(query);
        var result = await repository.GetResourceClaimActions(query.ToQuery());

        return result switch
        {
            ResourceClaimActionListResult.Success success => Results.Ok(success.ResourceClaimActions),
            ResourceClaimActionListResult.FailureHierarchyNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    "The claims hierarchy was not found.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ResourceClaimActionListResult.FailureProjectionIntegrity => FailureResults.Unknown(
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetActionAuthStrategies(
        IResourceClaimRepository repository,
        [AsParameters] FrontendResourceClaimActionAuthStrategyQuery query,
        ResourceClaimActionAuthStrategyPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardQueryAsync(query);
        var result = await repository.GetResourceClaimActionAuthStrategies(query.ToQuery());

        return result switch
        {
            ResourceClaimActionAuthStrategyListResult.Success success => Results.Ok(
                success.ResourceClaimActionAuthStrategies
            ),
            ResourceClaimActionAuthStrategyListResult.FailureHierarchyNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    "The claims hierarchy was not found.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity => FailureResults.Unknown(
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
