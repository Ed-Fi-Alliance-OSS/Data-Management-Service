// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
    /// Builds servers array for the OpenAPI spec using the configured multi-tenancy and route qualifier settings.
    /// </summary>
    private static JsonArray GetServers(
        HttpContext httpContext,
        IDmsInstanceProvider dmsInstanceProvider,
        IOptions<Configuration.AppSettings> appSettings
    )
    {
        string scheme = httpContext.Request.Scheme;
        string host = httpContext.Request.Host.ToString();
        string baseUrl = $"{scheme}://{host}";

        bool multiTenancyEnabled = appSettings.Value.MultiTenancy;
        string[] routeQualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();

        var variables = new JsonObject();
        var urlSegments = new List<string> { baseUrl };

        if (multiTenancyEnabled)
        {
            List<string> tenantValues = dmsInstanceProvider
                .GetLoadedTenantKeys()
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            variables["tenant"] = CreateServerVariable("tenant", "Tenant", tenantValues);
            urlSegments.Add("{tenant}");
        }

        if (routeQualifierSegments.Length > 0)
        {
            var qualifierValues = CollectRouteQualifierValues(dmsInstanceProvider, routeQualifierSegments);

            foreach (string segmentName in routeQualifierSegments)
            {
                List<string> values = qualifierValues.TryGetValue(segmentName, out var collected)
                    ? collected
                    : [];

                variables[segmentName] = CreateServerVariable(
                    segmentName,
                    BuildDisplayLabel(segmentName),
                    values
                );
                urlSegments.Add($"{{{segmentName}}}");
            }
        }

        urlSegments.Add("data");

        var serverObject = new JsonObject { ["url"] = string.Join("/", urlSegments) };

        if (variables.Count > 0)
        {
            serverObject["variables"] = variables;
        }

        return [serverObject];
    }

    private static JsonObject CreateServerVariable(string key, string description, List<string> values)
    {
        var variable = new JsonObject
        {
            ["default"] = values.Count > 0 ? values[0] : string.Empty,
            ["description"] = string.IsNullOrWhiteSpace(description) ? key : description,
        };

        if (values.Count > 0)
        {
            variable["enum"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray());
        }

        return variable;
    }

    private static Dictionary<string, List<string>> CollectRouteQualifierValues(
        IDmsInstanceProvider dmsInstanceProvider,
        string[] routeQualifierSegments
    )
    {
        var collectedValues = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string segment in routeQualifierSegments)
        {
            collectedValues[segment] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (collectedValues.Count == 0)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<string> tenantKeys = dmsInstanceProvider.GetLoadedTenantKeys();
        if (tenantKeys.Count == 0)
        {
            AppendValues(null);
        }
        else
        {
            foreach (string tenantKey in tenantKeys)
            {
                AppendValues(string.IsNullOrEmpty(tenantKey) ? null : tenantKey);
            }
        }

        return collectedValues.ToDictionary(
            pair => pair.Key,
            pair => SortRouteContextValues(pair.Value),
            StringComparer.OrdinalIgnoreCase
        );

        void AppendValues(string? tenantKey)
        {
            IReadOnlyList<DmsInstance> instances = dmsInstanceProvider.GetAll(tenantKey);
            foreach (var instance in instances)
            {
                foreach (var routeContext in instance.RouteContext)
                {
                    if (
                        collectedValues.TryGetValue(routeContext.Key.Value, out var values)
                        && !string.IsNullOrWhiteSpace(routeContext.Value.Value)
                    )
                    {
                        values.Add(routeContext.Value.Value);
                    }
                }
            }
        }
    }

    private static List<string> SortRouteContextValues(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        if (
            list.TrueForAll(value =>
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            )
        )
        {
            return list.Select(value => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))
                .OrderByDescending(number => number)
                .Select(number => number.ToString(CultureInfo.InvariantCulture))
                .ToList();
        }

        return list.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildDisplayLabel(string segmentName)
    {
        if (string.IsNullOrWhiteSpace(segmentName))
        {
            return "Value";
        }

        string withSpacing = Regex
            .Replace(segmentName, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace("-", " ")
            .Replace("_", " ")
            .Trim();

        if (withSpacing.Length == 0)
        {
            return segmentName;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(withSpacing);
    }

    private sealed record SpecificationSection(string name, string prefix);

    [GeneratedRegex(@"specifications\/(?<section>[^-]+)-spec.json?")]
    private static partial Regex PathExpression();

    private static readonly SpecificationSection[] _sections =
    [
        new SpecificationSection("Resources", string.Empty),
        new SpecificationSection("Descriptors", string.Empty),
        new SpecificationSection("Discovery", "Other"),
    ];

    private static readonly string _errorResourcePath = "Invalid resource path";

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
        IDmsInstanceProvider dmsInstanceProvider,
        IOptions<Configuration.AppSettings> appSettings
    )
    {
        JsonArray servers = GetServers(httpContext, dmsInstanceProvider, appSettings);
        JsonNode content = apiService.GetResourceOpenApiSpecification(servers);
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal static async Task GetDescriptorOpenApiSpec(
        HttpContext httpContext,
        IApiService apiService,
        IDmsInstanceProvider dmsInstanceProvider,
        IOptions<Configuration.AppSettings> appSettings
    )
    {
        JsonArray servers = GetServers(httpContext, dmsInstanceProvider, appSettings);
        JsonNode content = apiService.GetDescriptorOpenApiSpecification(servers);
        await httpContext.Response.WriteAsSerializedJsonAsync(content);
    }

    internal static async Task GetSections(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<RouteInformation> sections = [];
        foreach (var section in _sections)
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
            await httpContext.Response.WriteAsync(_errorResourcePath);
            return;
        }

        string section = match.Groups["section"].Value.ToLower();
        string? rootUrl = request.RootUrl();
        string oAuthUrl = options.Value.AuthenticationService;
        if (
            Array.Exists(
                _sections,
                x => x.name.ToLowerInvariant().Equals(section, StringComparison.InvariantCultureIgnoreCase)
            )
        )
        {
            var content = contentProvider.LoadJsonContent(section, rootUrl, oAuthUrl);
            content["servers"] = GetServers(httpContext, dmsInstanceProvider, options);
            await httpContext.Response.WriteAsSerializedJsonAsync(content);
        }
        else
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(_errorResourcePath);
        }
    }
}

public record RouteInformation(string name, string endpointUri, string prefix);
