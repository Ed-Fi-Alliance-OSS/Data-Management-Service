// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Api.Modules;

public partial class MetaDataModule : IModule
{
    [GeneratedRegex(@"specifications\/(?<section>[^-]+)-spec.json?")]
    private static partial Regex PathExpression();

    private readonly string[] Sections = ["resources", "descriptors", "discovery"];
    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata", GetMetadata);
        endpoints.MapGet("/metadata/dependencies", GetDependencies);
        endpoints.MapGet("/metadata/specifications", GetSections);
        endpoints.MapGet("/metadata/specifications/{section}-spec.json", GetSectionMetaData);
    }

    internal async Task GetMetadata(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        var content = new
        {
            dependencies = $"{baseUrl}/dependencies",
            specifications = $"{baseUrl}/specifications",
            xsdFiles = $"{baseUrl}/xsdMetadata"
        };

        await httpContext.Response.WriteAsJsonAsync(content);
    }

    internal async Task GetDependencies(HttpContext httpContext, IContentProvider contentProvider)
    {
        var content = contentProvider.LoadJsonContent("dependencies").Value;
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal async Task GetSections(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<RouteInformation> sections = [];
        foreach (var section in Sections)
        {
            sections.Add(
                new RouteInformation(section, $"{baseUrl}/{section.ToLower()}-spec.json", string.Empty)
            );
        }

        await httpContext.Response.WriteAsSerializedJsonAsync(sections);
    }

    internal async Task GetSectionMetaData(
        HttpContext httpContext,
        IContentProvider contentProvider,
        IOptions<AppSettings> options
    )
    {
        var request = httpContext.Request;
        Match match = PathExpression().Match(request.Path);
        if (!match.Success)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
            return;
        }

        string section = match.Groups["section"].Value.ToLower();
        string? rootUrl = request.RootUrl();
        string oAuthUrl = options.Value.AuthenticationService;
        if (
            Array.Exists(
                Sections,
                x => x.ToLowerInvariant().Equals(section, StringComparison.InvariantCultureIgnoreCase)
            )
        )
        {
            var content = contentProvider.LoadJsonContent(section, rootUrl, oAuthUrl);
            await httpContext.Response.WriteAsSerializedJsonAsync(content);
        }
        else
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
        }
    }
}

public record RouteInformation(string name, string endpointUri, string prefix);
