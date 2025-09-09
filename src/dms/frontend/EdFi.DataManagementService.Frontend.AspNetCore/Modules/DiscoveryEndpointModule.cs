// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Provides the Discovery API endpoint that returns metadata about the DMS instance,
/// including version information, data models, and available API URLs.
/// </summary>
public class DiscoveryEndpointModule : IEndpointModule
{
    /// <summary>
    /// Maps the discovery endpoint to the root path.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", BuildDiscoveryResponse);
    }

    /// <summary>
    /// Handles the discovery API request and returns metadata about the DMS instance.
    /// </summary>
    private static async Task BuildDiscoveryResponse(
        HttpContext httpContext,
        IVersionProvider versionProvider,
        IApiService apiService,
        IOptions<AppSettings> appSettings
    )
    {
        // Get available data models from the API service
        IList<IDataModelInfo> dataModelInfos = apiService.GetDataModelInfo();

        // Extract base URL for constructing metadata URLs
        string rootUrl = httpContext.Request.RootUrl();

        // Build the discovery response with version info, data models, and API URLs
        JsonObject response = new()
        {
            ["version"] = versionProvider.Version,
            ["applicationName"] = versionProvider.ApplicationName,
            ["informationalVersion"] = versionProvider.InformationalVersion,
            ["dataModels"] = new JsonArray(
                dataModelInfos
                    .OrderBy(x => x.ProjectName)
                    .Select(x => new JsonObject
                    {
                        ["name"] = x.ProjectName,
                        ["version"] = x.ProjectVersion,
                        ["informationalVersion"] = x.Description,
                    })
                    .ToArray()
            ),
            ["urls"] = new JsonObject
            {
                ["dependencies"] = $"{rootUrl}/metadata/dependencies",
                ["openApiMetadata"] = $"{rootUrl}/metadata/specifications",
                ["oauth"] = appSettings.Value.AuthenticationService,
                ["dataManagementApi"] = $"{rootUrl}/data",
                ["xsdMetadata"] = $"{rootUrl}/metadata/xsd",
            },
        };

        await httpContext.Response.WriteAsSerializedJsonAsync(response);
    }
}
