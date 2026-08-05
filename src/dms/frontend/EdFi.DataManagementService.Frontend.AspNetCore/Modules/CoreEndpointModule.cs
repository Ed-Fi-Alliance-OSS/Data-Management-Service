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
        // HEAD is GET without the response body (RFC 9110 section 9.3.2), and section 9.1 requires
        // general-purpose servers to support both. Mapped on the GET endpoint rather than left to
        // the terminal below, which would otherwise answer HEAD with a 405 whose own Allow header
        // lists GET. Kestrel suppresses the body.
        endpoints.MapMethods(routePattern, [HttpMethods.Get, HttpMethods.Head], Get);
        endpoints.MapPut(routePattern, UpdateById);
        endpoints.MapDelete(routePattern, DeleteById);

        // Terminal for data-route requests whose method is none of the verbs above. Core decides
        // 404-vs-405 (unknown resource vs unsupported method), matching ODS/API's ordering.
        //
        // This shares the verb endpoints' exact route template, so precedence ties, and were this
        // terminal left at Order 0 the Order would tie too - EndpointComparer would then fall
        // through to HttpMethodMatcherPolicy's metadata comparer, which ranks method-constrained
        // endpoints ahead of method-less ones, and the verbs would still win on their own merits.
        // WithOrder(1) is therefore defensive rather than load-bearing here: it keeps the terminal
        // demoted if the template is ever narrowed. Any order below MapFallback's int.MaxValue
        // works. Contrast TrackedChangesEndpointModule, where the order IS load-bearing.
        endpoints.Map(routePattern, MethodNotAllowed).WithOrder(1);
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
