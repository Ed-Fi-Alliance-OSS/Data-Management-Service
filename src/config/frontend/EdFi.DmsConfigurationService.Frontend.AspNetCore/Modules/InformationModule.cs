// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Information;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class InformationModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("", GetInformation);
        endpoints.MapPublic("/tenancy", GetTenancy).Produces<ApiTenancy>(200);
    }

    /// <summary>
    /// Lists every configured tenant name so a client can choose one before it has credentials or a
    /// Tenant header. Mapped in every configuration; with multi-tenancy off the list is empty. The Tenant
    /// header is never read, and TenantResolutionMiddleware exempts this path from tenant resolution.
    /// </summary>
    private async Task<IResult> GetTenancy(ITenantRepository tenantRepository, HttpContext httpContext)
    {
        if (!appSettings.Value.MultiTenancy)
        {
            return Results.Ok(new ApiTenancy([]));
        }

        // No Limit or Offset, so every tenant comes back.
        TenantQueryResult queryResult = await tenantRepository.QueryTenant(new PagingQuery());
        return queryResult switch
        {
            TenantQueryResult.Success success => Results.Ok(
                new ApiTenancy([.. success.TenantResponses.Select(tenant => tenant.Name)])
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private IResult GetInformation(HttpContext httpContext)
    {
        var baseUrl =
            $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}";
        var urls = new ApiUrls($"{baseUrl}/metadata/specifications");
        var specificationVersion = appSettings.Value.SpecificationVersion.ToLowerInvariant();
        var response = new ApiInformation(
            ApiVersionDetails.Version,
            ApiVersionDetails.ApplicationName,
            ApiVersionDetails.InformationalVersion,
            ApiVersionDetails.Build,
            urls,
            specificationVersion
        );
        return Results.Ok(response);
    }
}
