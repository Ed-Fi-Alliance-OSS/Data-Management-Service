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

        // /data/v3 alias for parity with ODS API URLs so SDK consumers (e.g. EdFi.LoadTools'
        // SdkConfigurationFactory) that hardcode the historical /data/v3 base reach the same handlers.
        string v3AliasPattern = routePattern.Replace("/data/{**dmsPath}", "/data/v3/{**dmsPath}");

        foreach (var pattern in new[] { routePattern, v3AliasPattern })
        {
            endpoints.MapPost(pattern, Upsert);
            endpoints.MapGet(pattern, Get);
            endpoints.MapPut(pattern, UpdateById);
            endpoints.MapDelete(pattern, DeleteById);
        }
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
