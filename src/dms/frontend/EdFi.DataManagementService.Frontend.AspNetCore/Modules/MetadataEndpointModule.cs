// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public partial class MetadataEndpointModule : IEndpointModule
{
    /// <summary>
    /// Builds servers array for OpenAPI spec with tenant and/or school year variables as applicable.
    /// Handles single-tenant, multi-tenant, and school-year configurations uniformly.
    /// </summary>
    private static JsonArray GetServers(HttpContext httpContext, IDmsInstanceProvider dmsInstanceProvider)
    {
        string scheme = httpContext.Request.Scheme;
        string host = httpContext.Request.Host.ToString();
        string baseUrl = $"{scheme}://{host}";

        // Get all loaded tenant keys (excludes empty string for single-tenant mode)
        var tenants = dmsInstanceProvider
            .GetLoadedTenantKeys()
            .Where(t => !string.IsNullOrEmpty(t))
            .OrderBy(t => t)
            .ToList();

        // For single-tenant mode, get school years from default tenant
        // For multi-tenant mode, aggregate school years from all tenants
        var allSchoolYears =
            tenants.Count > 0
                ? tenants
                    .SelectMany(tenant => dmsInstanceProvider.GetAll(tenant))
                    .SelectMany(instance => instance.RouteContext)
                    .Where(kvp => kvp.Key.Value.Equals("schoolYear", StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Value.Value)
                    .Distinct()
                    .OrderByDescending(year => year)
                    .ToList()
                : dmsInstanceProvider
                    .GetAll(null)
                    .SelectMany(instance => instance.RouteContext)
                    .Where(kvp => kvp.Key.Value.Equals("schoolYear", StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Value.Value)
                    .Distinct()
                    .OrderByDescending(year => year)
                    .ToList();

        var variables = new JsonObject();
        var urlSegments = new List<string> { baseUrl };

        // Add Tenant Selection variable if tenants exist
        if (tenants.Count > 0)
        {
            variables["Tenant Selection"] = new JsonObject
            {
                ["default"] = tenants[0],
                ["description"] = "Tenant Selection",
                ["enum"] = new JsonArray(tenants.Select(t => JsonValue.Create(t)).ToArray()),
            };
            urlSegments.Add("{Tenant Selection}");
        }

        // Add School Year Selection variable if school years exist
        if (allSchoolYears.Count > 0)
        {
            variables["School Year Selection"] = new JsonObject
            {
                ["default"] = allSchoolYears[0],
                ["description"] = "School Year Selection",
                ["enum"] = new JsonArray(allSchoolYears.Select(y => JsonValue.Create(y)).ToArray()),
            };
            urlSegments.Add("{School Year Selection}");
        }

        urlSegments.Add("data");

        // Build URL template (e.g., "http://localhost:8080/{Tenant Selection}/{School Year Selection}/data")
        string urlTemplate = string.Join("/", urlSegments);

        var serverObject = new JsonObject { ["url"] = urlTemplate };

        // Only add variables if we have any
        if (variables.Count > 0)
        {
            serverObject["variables"] = variables;
        }

        return [serverObject];
    }

    private sealed record SpecificationSection(string name, string prefix);

    [GeneratedRegex(@"specifications\/(?<section>[^-]+)-spec.json?")]
    private static partial Regex PathExpression();

    private static readonly SpecificationSection[] Sections =
    [
        new SpecificationSection("Resources", string.Empty),
        new SpecificationSection("Descriptors", string.Empty),
        new SpecificationSection("Discovery", "Other"),
    ];

    private static readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata", GetMetadata);
        endpoints.MapGet(
            "/metadata/dependencies",
            async (HttpContext httpContext, IApiService apiService) =>
            {
                var acceptHeader = httpContext.Request.Headers["Accept"].ToString();

                if (acceptHeader.Contains("application/graphml", StringComparison.OrdinalIgnoreCase))
                {
                    await GetDependenciesGraphML(httpContext, apiService);
                }
                else
                {
                    await GetDependencies(httpContext, apiService);
                }
            }
        );
        endpoints.MapGet("/metadata/specifications", GetSections);
        endpoints.MapGet("/metadata/specifications/resources-spec.json", GetResourceOpenApiSpec);
        endpoints.MapGet("/metadata/specifications/descriptors-spec.json", GetDescriptorOpenApiSpec);
        endpoints.MapGet("/metadata/specifications/{section}-spec.json", GetSectionMetadata);
    }

    internal static async Task GetMetadata(HttpContext httpContext)
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

    internal static async Task GetResourceOpenApiSpec(
        HttpContext httpContext,
        IApiService apiService,
        IDmsInstanceProvider dmsInstanceProvider
    )
    {
        JsonArray servers = GetServers(httpContext, dmsInstanceProvider);
        JsonNode content = apiService.GetResourceOpenApiSpecification(servers);
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal static async Task GetDescriptorOpenApiSpec(
        HttpContext httpContext,
        IApiService apiService,
        IDmsInstanceProvider dmsInstanceProvider
    )
    {
        JsonArray servers = GetServers(httpContext, dmsInstanceProvider);
        JsonNode content = apiService.GetDescriptorOpenApiSpecification(servers);
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal static async Task GetSections(HttpContext httpContext)
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

    internal static async Task GetSectionMetadata(
        HttpContext httpContext,
        IContentProvider contentProvider,
        IOptions<Configuration.AppSettings> options,
        IDmsInstanceProvider dmsInstanceProvider
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
            content["servers"] = GetServers(httpContext, dmsInstanceProvider);
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
