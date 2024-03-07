// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Api.Modules;

public class DiscoveryModule : IModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", GetApiDetails);
    }

    internal async Task GetApiDetails(HttpContext httpContext)
    {
        var request = httpContext.Request;
        var basePath = $"{request.Scheme}://{request.Host}{request.PathBase}";
        var result = new DiscoveryApiDetails(
            "1.0.0",
            [new DataModel("Ed-Fi", "1.0.0")],
            [
                $"dependencies : {basePath}/metadata/data/v3/dependencies",
                $"openApiMetadata: {basePath}/metadata/",
                $"oauth :{basePath}/oauth",
                $"dataManagementApi : {basePath}/v3.3b/",
                $"xsdMetadata : {basePath}/metadata/xsd"
            ]
        );
        await httpContext.Response.WriteAsJsonAsync(result);
    }
}

public record DiscoveryApiDetails(string ApiVersion, DataModel[] DataModels, string[] Urls);

public record DataModel(string Name, string Version);
