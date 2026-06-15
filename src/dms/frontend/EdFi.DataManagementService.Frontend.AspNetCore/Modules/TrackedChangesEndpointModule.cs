// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Temporary empty response shim for resource-scoped Change Query routes advertised by ApiSchema OpenAPI.
/// Remove this module when DMS implements real /deletes and /keyChanges runtime behavior.
/// </summary>
public class TrackedChangesEndpointModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        string[] routeQualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();
        bool multiTenancy = appSettings.Value.MultiTenancy;

        endpoints.MapGet(BuildRoutePattern(routeQualifierSegments, multiTenancy, "deletes"), GetStub);
        endpoints.MapGet(BuildRoutePattern(routeQualifierSegments, multiTenancy, "keyChanges"), GetStub);
    }

    internal static string BuildRoutePattern(
        string[] routeQualifierSegments,
        bool multiTenancy,
        string trackedChangeSegment
    )
    {
        var tenantSegment = multiTenancy ? "{tenant}/" : "";

        if (routeQualifierSegments.Length == 0)
        {
            return $"/{tenantSegment}data/{{projectNamespace}}/{{endpointName}}/{trackedChangeSegment}";
        }

        var segmentPlaceholders = string.Join("/", routeQualifierSegments.Select(s => $"{{{s}}}"));
        return $"/{tenantSegment}{segmentPlaceholders}/data/{{projectNamespace}}/{{endpointName}}/{trackedChangeSegment}";
    }

    private static IResult GetStub(HttpContext httpContext)
    {
        if (ShouldIncludeTotalCount(httpContext.Request.Query))
        {
            httpContext.Response.Headers.Append("Total-Count", "0");
        }

        return Results.Json(Array.Empty<object>());
    }

    private static bool ShouldIncludeTotalCount(IQueryCollection query)
    {
        foreach (var queryParameter in query)
        {
            if (!string.Equals(queryParameter.Key, "totalCount", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (queryParameter.Value.Count == 0)
            {
                return false;
            }

            return string.Equals(
                queryParameter.Value[queryParameter.Value.Count - 1],
                "true",
                StringComparison.OrdinalIgnoreCase
            );
        }

        return false;
    }
}
