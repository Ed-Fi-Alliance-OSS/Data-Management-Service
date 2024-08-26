// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public partial class MetadataEndpointModule : IEndpointModule
{
    private sealed record SpecificationSection(string name, string prefix);

    [GeneratedRegex(@"specifications\/(?<section>[^-]+)-spec.json?")]
    private static partial Regex PathExpression();

    private readonly SpecificationSection[] Sections =
    [
        new SpecificationSection("Resources", string.Empty),
        new SpecificationSection("Descriptors", string.Empty),
        new SpecificationSection("Discovery", "Other")
    ];

    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata", GetMetadata);
        endpoints.MapGet("/metadata/dependencies", GetDependencies);
        endpoints.MapGet("/metadata/specifications", GetSections);
        endpoints.MapGet("/metadata/specifications/{section}-spec.json", GetSectionMetadata);
    }

    internal async Task GetMetadata(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        var content = new
        {
            dependencies = $"{baseUrl}/dependencies",
            specifications = $"{baseUrl}/specifications",
            xsdMetadata = $"{baseUrl}/xsd"
        };

        await httpContext.Response.WriteAsJsonAsync(content);
    }

    internal static async Task GetDependencies(HttpContext httpContext, IApiService apiService)
    {
        var content = apiService.GetDependencies();
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal async Task GetSections(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<RouteInformation> sections = [];
        foreach (var section in Sections)
        {
            sections.Add(
                new RouteInformation(
                    section.name,
                    $"{baseUrl}/{section.name.ToLower()}-spec.json",
                    section.prefix
                )
            );
        }

        await httpContext.Response.WriteAsSerializedJsonAsync(sections);
    }

    internal async Task GetSectionMetadata(
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
                x => x.name.ToLowerInvariant().Equals(section, StringComparison.InvariantCultureIgnoreCase)
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
