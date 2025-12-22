// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Information;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class InformationModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("", GetInformation);
    }

    private IResult GetInformation(HttpContext httpContext)
    {
        var baseUrl =
            $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}";
        var urls = new ApiUrls($"{baseUrl}/metadata/specifications");
        var response = new ApiInformation(
            ApiVersionDetails.Version,
            ApiVersionDetails.ApplicationName,
            ApiVersionDetails.InformationalVersion,
            urls
        );
        return Results.Ok(response);
    }
}
