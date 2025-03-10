// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class AuthorizationMetadataModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet("/authorizationMetadata", GetAuthorizationMetadata)
            .RequireAuthorizationWithPolicy();
    }

    private async Task<IResult> GetAuthorizationMetadata(
        [FromQuery] string claimSetName,
        IClaimsHierarchyRepository repository,
        IAuthorizationMetadataResponseFactory responseFactory,
        HttpContext httpContext
    )
    {
        var claimsHierarchyResult = await repository.GetClaimsHierarchy();

        if (claimsHierarchyResult is ClaimsHierarchyResult.Success success)
        {
            var authorizationMetadataResponse = responseFactory.Create(claimSetName, success.Claims);

            return Results.Ok(authorizationMetadataResponse);
        }

        return claimsHierarchyResult switch
        {
            ClaimsHierarchyResult.FailureHierarchyNotFound => Results.Json(
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
