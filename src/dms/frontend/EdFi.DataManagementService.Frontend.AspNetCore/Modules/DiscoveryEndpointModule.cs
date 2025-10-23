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
public class DiscoveryEndpointModule(IOptions<AppSettings> options) : IEndpointModule
{
    /// <summary>
    /// Maps the discovery endpoint to the root path and optionally with route qualifiers.
    /// Supports partial route segments - e.g., if configured with "districtId,schoolYear",
    /// maps both "/{districtId}" and "/{districtId}/{schoolYear}" to allow flexible discovery.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Map the root discovery endpoint
        endpoints.MapGet("/", BuildDiscoveryResponse);

        // If route qualifiers are configured, map all partial route patterns
        // This allows discovery at any level: /, /{segment1}, /{segment1}/{segment2}, etc.
        string[] routeQualifierSegments = options.Value.GetRouteQualifierSegmentsArray();
        if (routeQualifierSegments.Length > 0)
        {
            // Map each progressive pattern: /{seg0}, /{seg0}/{seg1}, /{seg0}/{seg1}/{seg2}, etc.
            for (int i = 1; i <= routeQualifierSegments.Length; i++)
            {
                string routePattern = BuildDiscoveryRoutePattern(routeQualifierSegments[..i]);
                endpoints.MapGet(routePattern, BuildDiscoveryResponse);
            }
        }
    }

    /// <summary>
    /// Builds the route pattern for discovery endpoint with the specified segments.
    /// For example, with segments ["districtId", "schoolYear"], returns "/{districtId}/{schoolYear}".
    /// </summary>
    private static string BuildDiscoveryRoutePattern(string[] segments)
    {
        var segmentPlaceholders = string.Join("/", segments.Select(s => $"{{{s}}}"));
        return $"/{segmentPlaceholders}";
    }

    /// <summary>
    /// Handles the discovery API request and returns metadata about the DMS instance.
    /// </summary>
    private static async Task BuildDiscoveryResponse(
        HttpContext httpContext,
        IVersionProvider versionProvider,
        IDataModelInfoProvider dataModelInfoProvider,
        IOptions<AppSettings> appSettings
    )
    {
        // Get available data models without requiring database access
        IList<IDataModelInfo> dataModelInfos = dataModelInfoProvider.GetDataModelInfo();

        // Extract base URL and build route qualifier prefix
        string rootUrl = httpContext.Request.RootUrl();
        string routeQualifierPrefix = BuildRouteQualifierPrefix(httpContext, appSettings);

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
                ["dependencies"] = $"{rootUrl}{routeQualifierPrefix}/metadata/dependencies",
                ["openApiMetadata"] = $"{rootUrl}{routeQualifierPrefix}/metadata/specifications",
                ["oauth"] = BuildOAuthUrl(appSettings.Value.AuthenticationService, routeQualifierPrefix),
                ["dataManagementApi"] = $"{rootUrl}{routeQualifierPrefix}/data",
                ["xsdMetadata"] = $"{rootUrl}{routeQualifierPrefix}/metadata/xsd",
            },
        };

        await httpContext.Response.WriteAsSerializedJsonAsync(response);
    }

    /// <summary>
    /// Builds the route qualifier prefix for URLs.
    /// If route qualifiers are configured:
    ///   - If present in the request, uses actual values: "/2025"
    ///   - If not present in request, uses placeholders: "/{schoolYear}"
    /// If no route qualifiers configured, returns empty string.
    /// </summary>
    private static string BuildRouteQualifierPrefix(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings
    )
    {
        string[] routeQualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();

        if (routeQualifierSegments.Length == 0)
        {
            return string.Empty;
        }

        List<string> prefixSegments = [];

        foreach (string segmentName in routeQualifierSegments)
        {
            if (
                httpContext.Request.RouteValues.TryGetValue(segmentName, out object? value)
                && value is string stringValue
                && !string.IsNullOrWhiteSpace(stringValue)
            )
            {
                // Use actual value from route
                prefixSegments.Add(stringValue);
            }
            else
            {
                // Use placeholder
                prefixSegments.Add($"{{{segmentName}}}");
            }
        }

        return "/" + string.Join("/", prefixSegments);
    }

    /// <summary>
    /// Builds the OAuth URL by appending route qualifier prefix to the authentication service URL.
    /// </summary>
    private static string BuildOAuthUrl(string authenticationService, string routeQualifierPrefix)
    {
        // Remove trailing slash from authentication service URL if present
        string baseUrl = authenticationService.TrimEnd('/');
        return $"{baseUrl}{routeQualifierPrefix}";
    }
}
