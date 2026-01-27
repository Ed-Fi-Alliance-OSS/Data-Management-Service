// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Endpoint module for OAuth token introspection
/// </summary>
public class TokenInfoModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Build the route pattern based on configured route qualifier segments and multitenancy
        string routePattern = BuildRoutePattern(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            appSettings.Value.MultiTenancy
        );

        endpoints.MapPost($"{routePattern}/oauth/token_info", GetTokenInfo);
    }

    /// <summary>
    /// Builds the route pattern based on configured route qualifier segments and multitenancy setting.
    /// When multitenancy is enabled, prepends {tenant} as the first route segment.
    /// Examples:
    /// - No multitenancy, no qualifiers: "/"
    /// - No multitenancy, with qualifiers: "/{districtId}/{schoolYear}/"
    /// - Multitenancy, no qualifiers: "/{tenant}/"
    /// - Multitenancy, with qualifiers: "/{tenant}/{districtId}/{schoolYear}/"
    /// </summary>
    internal static string BuildRoutePattern(string[] routeQualifierSegments, bool multiTenancy)
    {
        var segments = new List<string>();

        if (multiTenancy)
        {
            segments.Add("{tenant}");
        }

        if (routeQualifierSegments.Any())
        {
            segments.AddRange(routeQualifierSegments.Select(s => $"{{{s}}}"));
        }

        return segments.Count == 0 ? String.Empty : $"/{string.Join("/", segments)}";
    }
}
