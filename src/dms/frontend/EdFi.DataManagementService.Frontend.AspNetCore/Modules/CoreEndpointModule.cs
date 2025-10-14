// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class CoreEndpointModule(IOptions<AppSettings> options) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Build the route pattern based on configured route qualifier segments
        string routePattern = BuildRoutePattern(options.Value.RouteQualifierSegments);

        endpoints.MapPost(routePattern, Upsert);
        endpoints.MapGet(routePattern, Get);
        endpoints.MapPut(routePattern, UpdateById);
        endpoints.MapDelete(routePattern, DeleteById);
    }

    /// <summary>
    /// Builds the route pattern based on configured route qualifier segments.
    /// If no segments are configured, returns "/data/{**dmsPath}".
    /// Otherwise, returns "/{segment1}/{segment2}/data/{**dmsPath}".
    /// </summary>
    private static string BuildRoutePattern(string[] routeQualifierSegments)
    {
        if (routeQualifierSegments.Length == 0)
        {
            return "/data/{**dmsPath}";
        }

        // Build pattern like "/{district}/{schoolYear}/data/{**dmsPath}"
        var segmentPlaceholders = string.Join("/", routeQualifierSegments.Select(s => $"{{{s}}}"));
        return $"/{segmentPlaceholders}/data/{{**dmsPath}}";
    }
}
