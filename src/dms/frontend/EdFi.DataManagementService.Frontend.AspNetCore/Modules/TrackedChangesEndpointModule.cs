// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class TrackedChangesEndpointModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        string[] routeQualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();
        bool multiTenancy = appSettings.Value.MultiTenancy;

        endpoints.MapGet(BuildRoutePattern(routeQualifierSegments, multiTenancy, "deletes"), GetDeletes);
        endpoints.MapGet(
            BuildRoutePattern(routeQualifierSegments, multiTenancy, "keyChanges"),
            GetKeyChanges
        );
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

    private static Task<IResult> GetDeletes(
        HttpContext httpContext,
        IApiService apiService,
        string projectNamespace,
        string endpointName,
        IOptions<AppSettings> appSettings
    ) =>
        GetTrackedChanges(httpContext, apiService, $"{projectNamespace}/{endpointName}/deletes", appSettings);

    private static Task<IResult> GetKeyChanges(
        HttpContext httpContext,
        IApiService apiService,
        string projectNamespace,
        string endpointName,
        IOptions<AppSettings> appSettings
    ) =>
        GetTrackedChanges(
            httpContext,
            apiService,
            $"{projectNamespace}/{endpointName}/keyChanges",
            appSettings
        );
}
