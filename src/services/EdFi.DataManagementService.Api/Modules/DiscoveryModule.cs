// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Api.Modules;

public class DiscoveryModule : IModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", GetApiDetails);
    }

    internal async Task GetApiDetails(
        HttpContext httpContext,
        IVersionProvider versionProvider,
        IDataModelProvider dataModelProvider,
        IOptions<AppSettings> appSettings
    )
    {
        var dataModels = dataModelProvider.GetDataModels().ToArray();

        var result = new DiscoveryApiDetails(
            version: versionProvider.Version,
            applicationName: versionProvider.ApplicationName,
            dataModels,
            GetUrlsByName()
        );

        await httpContext.Response.WriteAsSerializedJsonAsync(result);

        Dictionary<string, string> GetUrlsByName()
        {
            var urlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootUrl = httpContext.Request.RootUrl();
            urlsByName["dependencies"] = $"{rootUrl}/metadata/dependencies";
            urlsByName["openApiMetadata"] = $"{rootUrl}/metadata/specifications";
            urlsByName["oauth"] = appSettings.Value.AuthenticationService ?? string.Empty;
            urlsByName["dataManagementApi"] = $"{rootUrl}/data";
            urlsByName["xsdMetadata"] = $"{rootUrl}/metadata/xsd";
            return urlsByName;
        }
    }
}

public record DiscoveryApiDetails(
    string version,
    string applicationName,
    DataModel[] dataModels,
    Dictionary<string, string> urls
);
