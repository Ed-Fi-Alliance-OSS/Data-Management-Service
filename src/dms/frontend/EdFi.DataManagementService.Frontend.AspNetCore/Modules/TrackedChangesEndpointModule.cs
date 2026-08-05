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

        // GET and HEAD together, for the reason given in CoreEndpointModule: HEAD is GET without
        // the body, so leaving it to the terminals below would answer it 405 with Allow: GET.
        endpoints.MapMethods(
            BuildRoutePattern(routeQualifierSegments, multiTenancy, "deletes"),
            [HttpMethods.Get, HttpMethods.Head],
            GetDeletes
        );
        endpoints.MapMethods(
            BuildRoutePattern(routeQualifierSegments, multiTenancy, "keyChanges"),
            [HttpMethods.Get, HttpMethods.Head],
            GetKeyChanges
        );

        // Terminals for tracked-change routes reached with an unsupported method. They hand the
        // request to Core rather than answering here, so that authentication, tenant validation and
        // resource existence all run first - the same ordering the data-route terminal gets.
        //
        // Here WithOrder(1) IS load-bearing, unlike the terminal in CoreEndpointModule. These
        // templates are literal and therefore more specific than /data/{**dmsPath}, and
        // EndpointComparer sorts Order -> precedence -> policy comparers, so precedence is consulted
        // before the HTTP-method comparer. At Order 0 these terminals intercept POST, PUT and DELETE
        // on /deletes and /keyChanges, which today fall through to the catch-all verb endpoints and
        // into Core. Order 1 demotes them below the Order-0 verb endpoints so only genuinely
        // unmapped verbs reach them.
        endpoints
            .Map(
                BuildRoutePattern(routeQualifierSegments, multiTenancy, "deletes"),
                MethodNotAllowedForDeletes
            )
            .WithOrder(1);
        endpoints
            .Map(
                BuildRoutePattern(routeQualifierSegments, multiTenancy, "keyChanges"),
                MethodNotAllowedForKeyChanges
            )
            .WithOrder(1);
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

    // These rebuild the dmsPath from the route's literal suffix exactly as GetDeletes and
    // GetKeyChanges do, because Core parses the operation back out of the path.
    private static Task<IResult> MethodNotAllowedForDeletes(
        HttpContext httpContext,
        IApiService apiService,
        string projectNamespace,
        string endpointName,
        IOptions<AppSettings> appSettings
    ) =>
        MethodNotAllowedForTrackedChange(
            httpContext,
            apiService,
            $"{projectNamespace}/{endpointName}/deletes",
            appSettings
        );

    private static Task<IResult> MethodNotAllowedForKeyChanges(
        HttpContext httpContext,
        IApiService apiService,
        string projectNamespace,
        string endpointName,
        IOptions<AppSettings> appSettings
    ) =>
        MethodNotAllowedForTrackedChange(
            httpContext,
            apiService,
            $"{projectNamespace}/{endpointName}/keyChanges",
            appSettings
        );
}
