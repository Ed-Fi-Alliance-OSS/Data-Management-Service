// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Api.Modules;

public class ApiMetaDataModule : IModule
{
    private readonly Regex PathExpressionRegex = new(@"\/(?<section>[^/]+)\/swagger.json?");

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata", GetSections);
        endpoints.MapGet("/metadata/{section}/swagger.json", GetSectionMetaData);
    }

    internal async Task GetSections(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment;
        List<RouteInformation> sections = [];
        sections.Add(new RouteInformation("Resources", $"{baseUrl}resources/swagger.json"));
        sections.Add(new RouteInformation("Descriptors", $"{baseUrl}descriptors/swagger.json"));

        await httpContext.Response.WriteAsJsonAsync(sections);
    }

    internal async Task GetSectionMetaData(HttpContext httpContext)
    {
        var request = httpContext.Request;
        Match match = PathExpressionRegex.Match(request.Path);
        if (!match.Success)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }

        string section = match.Groups["section"].Value;
        if (section.ToLower().Equals("resources"))
        {
            await httpContext.Response.WriteAsJsonAsync("Resources");
        }
        else
        {
            await httpContext.Response.WriteAsJsonAsync("Descriptors");
        }
    }
}
