// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Api.Modules;

public class DiscoveryModule : IModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", GetApiDetails);
    }

    internal async Task GetApiDetails(HttpContext httpContext)
    {
        var result = new DiscoveryApiDetails(
            version: "1.0.0",
            informationalVersion: "1.0.0",
            build: "1",
            [new DataModel("Ed-Fi", "1.0.0", "1.0.0")],
            GetUrlsByName()
        );

        await httpContext.Response.WriteAsSerializedJsonAsync(result);

        Dictionary<string, string> GetUrlsByName()
        {
            var urlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootUrl = httpContext.Request.RootUrl();
            urlsByName["dependencies"] = $"{rootUrl}/metadata/data/dependencies";
            urlsByName["openApiMetadata"] = $"{rootUrl}/metadata/";
            urlsByName["oauth"] = $"{rootUrl}/oauth/token";
            urlsByName["dataManagementApi"] = $"{rootUrl}/data/";
            urlsByName["xsdMetadata"] = $"{rootUrl}/metadata/xsd";
            return urlsByName;
        }
    }
}

public record DiscoveryApiDetails(
    string version,
    string informationalVersion,
    string build,
    DataModel[] DataModels,
    Dictionary<string, string> Urls
);

public record DataModel(string name, string version, string informationalVersion);
