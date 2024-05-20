// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class DiscoveryEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", GetApiDetails);
    }

    private async Task GetApiDetails(
        HttpContext httpContext,
        IVersionProvider versionProvider,
        IApiService apiService,
        IOptions<AppSettings> appSettings
    )
    {
        IList<IDataModelInfo> dataModelInfos = apiService.GetDataModelInfo();

        var result = new DiscoveryApiDetails(
            version: versionProvider.Version,
            applicationName: versionProvider.ApplicationName,
            dataModelInfos
                .Select(x => new DataModel(x.ProjectName, x.ProjectVersion, x.Description))
                .ToArray(),
            GetUrlsByName()
        );

        await httpContext.Response.WriteAsSerializedJsonAsync(result);

        Dictionary<string, string> GetUrlsByName()
        {
            var urlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootUrl = httpContext.Request.RootUrl();
            urlsByName["dependencies"] = $"{rootUrl}/metadata/dependencies";
            urlsByName["openApiMetadata"] = $"{rootUrl}/metadata/specifications";
            urlsByName["oauth"] = appSettings.Value.AuthenticationService;
            urlsByName["dataManagementApi"] = $"{rootUrl}/data";
            urlsByName["xsdMetadata"] = $"{rootUrl}/metadata/xsd";
            return urlsByName;
        }
    }
}

public record DataModel(string name, string version, string informationalVersion);

public record DiscoveryApiDetails(
    string version,
    string applicationName,
    DataModel[] dataModels,
    Dictionary<string, string> urls
);
