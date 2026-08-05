// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Response;
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

        // Terminals for tracked-change routes reached with an unsupported method.
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
                MethodNotAllowedForTrackedChange
            )
            .WithOrder(1);
        endpoints
            .Map(
                BuildRoutePattern(routeQualifierSegments, multiTenancy, "keyChanges"),
                MethodNotAllowedForTrackedChange
            )
            .WithOrder(1);
    }

    /// <summary>
    /// Answers 405 for a tracked-change route reached with an unsupported method. Answered here
    /// rather than in Core because ParsePathMiddleware parses the deletes/keyChanges suffix as the
    /// document id segment and rejects it as a malformed UUID with a 400, and
    /// ParseTrackedChangePathMiddleware 404s any other suffix so it cannot sit in a shared pipeline.
    ///
    /// The body, content type and correlation id are deliberately identical to what
    /// MethodNotAllowedMiddleware emits, from the same FailureResponse factory; only the Allow value
    /// differs. ToResult cannot be reused here because it is private and takes IFrontendResponse,
    /// whose only implementation is internal to Core, so the Results.Content call below reproduces
    /// ToResult's own tail.
    /// </summary>
    private static IResult MethodNotAllowedForTrackedChange(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings
    )
    {
        httpContext.Response.Headers.Append("Allow", "GET");

        JsonNode body = FailureResponse.ForMethodNotAllowed(
            [$"The endpoint of the request does not support the '{httpContext.Request.Method}' method."],
            ExtractTraceIdFrom(httpContext.Request, appSettings)
        );

        return Results.Content(
            statusCode: StatusCodes.Status405MethodNotAllowed,
            content: JsonSerializer.Serialize(body, SharedSerializerOptions),
            contentType: "application/json; charset=utf-8",
            contentEncoding: Encoding.UTF8
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
