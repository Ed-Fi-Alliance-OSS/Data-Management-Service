// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class CoreEndpointModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Build the route pattern based on configured route qualifier segments and multitenancy
        string routePattern = BuildRoutePattern(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            appSettings.Value.MultiTenancy
        );

        endpoints.MapPost(routePattern, Upsert);
        endpoints.MapGet(routePattern, Get);
        endpoints.MapPut(routePattern, UpdateById);
        endpoints.MapDelete(routePattern, DeleteById);
    }

    /// <summary>
    /// Builds the route pattern based on configured route qualifier segments and multitenancy setting.
    /// When multitenancy is enabled, prepends {tenant} as the first route segment.
    /// Examples:
    /// - No multitenancy, no qualifiers: "/data/{**dmsPath}"
    /// - No multitenancy, with qualifiers: "/{districtId}/{schoolYear}/data/{**dmsPath}"
    /// - Multitenancy, no qualifiers: "/{tenant}/data/{**dmsPath}"
    /// - Multitenancy, with qualifiers: "/{tenant}/{districtId}/{schoolYear}/data/{**dmsPath}"
    /// </summary>
    internal static string BuildRoutePattern(string[] routeQualifierSegments, bool multiTenancy)
    {
        var tenantSegment = multiTenancy ? "{tenant}/" : "";

        if (routeQualifierSegments.Length == 0)
        {
            return $"/{tenantSegment}data/{{**dmsPath}}";
        }

        // Build pattern like "/{tenant}/{district}/{schoolYear}/data/{**dmsPath}"
        var segmentPlaceholders = string.Join("/", routeQualifierSegments.Select(s => $"{{{s}}}"));
        return $"/{tenantSegment}{segmentPlaceholders}/data/{{**dmsPath}}";
    }
}
