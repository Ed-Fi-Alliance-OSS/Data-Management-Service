// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public partial class MetadataEndpointModule : IEndpointModule
{
    private static JsonArray GetServers(HttpContext httpContext)
    {
        return new JsonArray { new JsonObject { ["url"] = $"{httpContext.Request.RootUrl()}/data" } };
    }

    private sealed record SpecificationSection(string name, string prefix);

    [GeneratedRegex(@"specifications\/(?<section>[^-]+)-spec.json?")]
    private static partial Regex PathExpression();

    private readonly SpecificationSection[] Sections =
    [
        new SpecificationSection("Resources", string.Empty),
        new SpecificationSection("Descriptors", string.Empty),
        new SpecificationSection("Discovery", "Other"),
    ];

    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata", GetMetadata);
        // Combine the conflicting routes into a single MapGet
        endpoints.MapGet(
            "/metadata/dependencies",
            async (HttpContext httpContext, IApiService apiService) =>
            {
                var acceptHeader = httpContext.Request.Headers["Accept"].ToString();

                if (acceptHeader.Contains("application/graphml", StringComparison.OrdinalIgnoreCase))
                {
                    // Respond using GraphML-specific logic
                    await GetDependenciesGraphML(httpContext, apiService);
                }
                else
                {
                    // Default behavior
                    await GetDependencies(httpContext, apiService);
                }
            }
        );
        endpoints.MapGet("/metadata/specifications", GetSections);
        endpoints.MapGet("/metadata/specifications/resources-spec.json", GetResourceOpenApiSpec);
        endpoints.MapGet("/metadata/specifications/descriptors-spec.json", GetDescriptorOpenApiSpec);
        endpoints.MapGet("/metadata/specifications/{section}-spec.json", GetSectionMetadata);
    }

    internal async Task GetMetadata(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        var content = new
        {
            dependencies = $"{baseUrl}/dependencies",
            specifications = $"{baseUrl}/specifications",
            xsdMetadata = $"{baseUrl}/xsd",
        };

        await httpContext.Response.WriteAsJsonAsync(content);
    }

    internal static async Task GetDependencies(HttpContext httpContext, IApiService apiService)
    {
        JsonArray content = apiService.GetDependencies();
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal static async Task GetDependenciesGraphML(HttpContext httpContext, IApiService apiService)
    {
        var graphML = apiService.GetDependenciesAsGraphML();

        // Set GraphML content type
        httpContext.Response.ContentType = "application/graphml";

        // Serialize the GraphML content to XML
        await httpContext.Response.WriteAsync(CreateXml());

        string CreateXml()
        {
            var sb = new StringBuilder();

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");

            sb.AppendLine(
                "<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd\">"
            );

            sb.AppendLine($"<graph id=\"{graphML.Id}\" edgedefault=\"directed\">");

            foreach (var node in graphML.Nodes)
            {
                sb.AppendLine($"<node id=\"{node.Id}\"/>");
            }

            foreach (var edge in graphML.Edges)
            {
                sb.AppendLine($"<edge source=\"{edge.Source}\" target=\"{edge.Target}\"/>");
            }

            sb.AppendLine("</graph>");
            sb.AppendLine("</graphml>");

            return sb.ToString();
        }
    }

    internal static async Task GetResourceOpenApiSpec(HttpContext httpContext, IApiService apiService)
    {
        JsonArray servers = GetServers(httpContext);
        JsonNode content = apiService.GetResourceOpenApiSpecification(servers);
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal static async Task GetDescriptorOpenApiSpec(HttpContext httpContext, IApiService apiService)
    {
        JsonArray servers = GetServers(httpContext);
        JsonNode content = apiService.GetDescriptorOpenApiSpecification(servers);
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
            content["servers"] = GetServers(httpContext);
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
