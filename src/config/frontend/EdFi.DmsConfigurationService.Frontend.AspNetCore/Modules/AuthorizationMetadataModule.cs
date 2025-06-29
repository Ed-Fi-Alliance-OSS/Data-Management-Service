// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class AuthorizationMetadataModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapLimitedAccess("/authorizationMetadata", GetAuthorizationMetadata);
    }

    private async Task<IResult> GetAuthorizationMetadata(
        [FromQuery] string? claimSetName,
        IClaimsHierarchyRepository repository,
        IAuthorizationMetadataResponseFactory responseFactory,
        HttpContext httpContext
    )
    {
        var claimsHierarchyResult = await repository.GetClaimsHierarchy();

        if (claimsHierarchyResult is ClaimsHierarchyGetResult.Success success)
        {
            var authorizationMetadataResponse = await responseFactory.Create(claimSetName, success.Claims);

            return Results.Ok(authorizationMetadataResponse.ClaimSets);
        }

        return claimsHierarchyResult switch
        {
            ClaimsHierarchyGetResult.FailureHierarchyNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"Authorization metadata for claim set '{claimSetName}' not found.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
