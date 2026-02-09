// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
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
    /// Maps the discovery endpoint to the root path and optionally with tenant and route qualifiers.
    /// Supports partial route segments - e.g., if configured with multitenancy and "districtId,schoolYear",
    /// maps "/{tenant}", "/{tenant}/{districtId}", and "/{tenant}/{districtId}/{schoolYear}".
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        string[] routeQualifierSegments = options.Value.GetRouteQualifierSegmentsArray();
        bool multiTenancy = options.Value.MultiTenancy;

        // Always map the root discovery endpoint (returns placeholders for all segments)
        endpoints.MapGet("/", BuildDiscoveryResponse);

        if (multiTenancy)
        {
            // Map tenant-only route: /{tenant}
            endpoints.MapGet("/{tenant}", BuildDiscoveryResponse);

            // Map tenant + route qualifiers: /{tenant}/{qualifier1}, /{tenant}/{qualifier1}/{qualifier2}, etc.
            for (int i = 1; i <= routeQualifierSegments.Length; i++)
            {
                string routePattern = BuildRoutePatternWithTenant(routeQualifierSegments[..i]);
                endpoints.MapGet(routePattern, BuildDiscoveryResponse);
            }
        }
        else if (routeQualifierSegments.Length > 0)
        {
            // Non-multitenancy: map route qualifiers only
            // Map each progressive pattern: /{seg0}, /{seg0}/{seg1}, /{seg0}/{seg1}/{seg2}, etc.
            for (int i = 1; i <= routeQualifierSegments.Length; i++)
            {
                string routePattern = BuildRoutePattern(routeQualifierSegments[..i]);
                endpoints.MapGet(routePattern, BuildDiscoveryResponse);
            }
        }
    }

    /// <summary>
    /// Builds the route pattern for discovery endpoint with the specified segments (no tenant).
    /// For example, with segments ["districtId", "schoolYear"], returns "/{districtId}/{schoolYear}".
    /// </summary>
    private static string BuildRoutePattern(string[] segments)
    {
        var segmentPlaceholders = string.Join("/", segments.Select(s => $"{{{s}}}"));
        return $"/{segmentPlaceholders}";
    }

    /// <summary>
    /// Builds the route pattern for discovery endpoint with tenant prefix and specified segments.
    /// For example, with segments ["districtId", "schoolYear"], returns "/{tenant}/{districtId}/{schoolYear}".
    /// </summary>
    private static string BuildRoutePatternWithTenant(string[] segments)
    {
        var segmentPlaceholders = string.Join("/", segments.Select(s => $"{{{s}}}"));
        return $"/{{tenant}}/{segmentPlaceholders}";
    }

    /// <summary>
    /// Handles the discovery API request and returns metadata about the DMS instance.
    /// When multi-tenancy is enabled and a tenant is provided, validates the tenant exists.
    /// </summary>
    private static async Task BuildDiscoveryResponse(
        HttpContext httpContext,
        IVersionProvider versionProvider,
        IDataModelInfoProvider dataModelInfoProvider,
        IOptions<AppSettings> appSettings,
        ITenantValidator tenantValidator
    )
    {
        // Validate tenant if multi-tenancy is enabled and tenant is provided in route
        if (appSettings.Value.MultiTenancy)
        {
            string? tenant = ExtractTenantFromRoute(httpContext);
            if (tenant != null)
            {
                bool isValid = await tenantValidator.ValidateTenantAsync(tenant);
                if (!isValid)
                {
                    httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await httpContext.Response.WriteAsSerializedJsonAsync(
                        new
                        {
                            detail = "The specified resource could not be found.",
                            type = "urn:ed-fi:api:not-found",
                            title = "Not Found",
                            status = 404,
                        }
                    );
                    return;
                }
            }
        }

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
                ["tokenInfo"] = $"{rootUrl}{routeQualifierPrefix}/oauth/token_info",
                ["dataManagementApi"] = $"{rootUrl}{routeQualifierPrefix}/data",
                ["xsdMetadata"] = $"{rootUrl}{routeQualifierPrefix}/metadata/xsd",
            },
        };

        await httpContext.Response.WriteAsSerializedJsonAsync(response);
    }

    /// <summary>
    /// Extracts the tenant identifier from the route values.
    /// Returns null if tenant is not present in the route.
    /// </summary>
    private static string? ExtractTenantFromRoute(HttpContext httpContext)
    {
        if (
            httpContext.Request.RouteValues.TryGetValue("tenant", out object? value)
            && value is string tenant
            && !string.IsNullOrWhiteSpace(tenant)
        )
        {
            return tenant;
        }
        return null;
    }

    /// <summary>
    /// Builds the route qualifier prefix for URLs.
    /// When multi-tenancy is enabled, includes tenant as the first segment.
    /// If segments are present in the request, uses actual values.
    /// If segments are not present in request, uses placeholders.
    /// </summary>
    private static string BuildRouteQualifierPrefix(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings
    )
    {
        string[] routeQualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();
        bool multiTenancy = appSettings.Value.MultiTenancy;

        List<string> prefixSegments = [];

        // Add tenant segment if multi-tenancy is enabled
        if (multiTenancy)
        {
            if (
                httpContext.Request.RouteValues.TryGetValue("tenant", out object? tenantValue)
                && tenantValue is string tenant
                && !string.IsNullOrWhiteSpace(tenant)
            )
            {
                prefixSegments.Add(tenant);
            }
            else
            {
                prefixSegments.Add("{tenant}");
            }
        }

        // Add route qualifier segments
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

        if (prefixSegments.Count == 0)
        {
            return string.Empty;
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
