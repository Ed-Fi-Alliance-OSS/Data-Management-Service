// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ApiClientModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Limited access endpoints - accessible by service accounts for internal DMS operations
        endpoints.MapLimitedAccess("/v2/apiClients/", GetAll);
        endpoints.MapLimitedAccess("/v2/apiClients/{clientId}", GetByClientId);
    }

    private static async Task<IResult> GetAll(
        IApiClientRepository apiClientRepository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        ApiClientQueryResult getResult = await apiClientRepository.QueryApiClient(query);
        return getResult switch
        {
            ApiClientQueryResult.Success success => Results.Ok(success.ApiClientResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetByClientId(
        string clientId,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository
    )
    {
        ApiClientGetResult getResult = await apiClientRepository.GetApiClientByClientId(clientId);
        return getResult switch
        {
            ApiClientGetResult.Success success => Results.Ok(success.ApiClientResponse),
            ApiClientGetResult.FailureNotFound => FailureResults.NotFound(
                "ApiClient not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
