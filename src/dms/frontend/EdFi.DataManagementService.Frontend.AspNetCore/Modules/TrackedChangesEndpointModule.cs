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

        // GET alone, for the reason given in CoreEndpointModule: ODS/API answers HEAD with a 405,
        // so HEAD falls through to the terminals below rather than being mapped here.
        endpoints.MapGet(BuildRoutePattern(routeQualifierSegments, multiTenancy, "deletes"), GetDeletes);
        endpoints.MapGet(
            BuildRoutePattern(routeQualifierSegments, multiTenancy, "keyChanges"),
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
        //
        // A deliberate consequence: POST, PUT and DELETE on these routes still reach the data
        // pipeline, where ParsePathMiddleware reads the /deletes or /keyChanges suffix as a document
        // id and rejects it as a malformed uuid with a 400. The Allow: GET these terminals emit
        // therefore states what this terminal accepts, not what the whole URL accepts. Dropping the
        // WithOrder(1) calls would make those three verbs answer 405 here as well.
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
