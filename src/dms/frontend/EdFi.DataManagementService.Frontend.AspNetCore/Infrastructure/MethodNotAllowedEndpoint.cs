// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Answers 405 for a fixed route called with a method it does not map, instead of the 404 the
/// catch-all fallback in Program.cs would otherwise give it.
/// </summary>
/// <remarks>
/// ASP.NET Core answers 405 on its own only when no endpoint at all matches the request. The
/// fallback matches every path, so without this a GET to POST-only /oauth/token reached the
/// fallback and got 404. ODS/API answers 405.
///
/// The terminal maps every standard method EXCEPT the allowed ones, rather than being method-less
/// like CoreEndpointModule's data-route terminal. A method-less endpoint would also match a request
/// whose method IS allowed but which the allowed endpoints turn down for another reason, such as
/// TokenEndpointModule's content-type-specific POST endpoints refusing text/plain, and would then
/// misreport that request as a 405 for a method the route supports.
///
/// HEAD is not treated as GET, for the same ODS/API parity reason given in CoreEndpointModule.
/// </remarks>
internal static class MethodNotAllowedEndpoint
{
    private static readonly string[] _standardMethods =
    [
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options,
        HttpMethods.Trace,
    ];

    /// <summary>
    /// Maps a 405 terminal on <paramref name="pattern" /> for every standard method not in
    /// <paramref name="allowedMethods" />, advertising them in the Allow header.
    /// </summary>
    public static IEndpointConventionBuilder MapMethodNotAllowed(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        params string[] allowedMethods
    )
    {
        string[] otherMethods = _standardMethods
            .Where(method => !allowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        string allow = string.Join(", ", allowedMethods);

        return endpoints
            .MapMethods(
                pattern,
                otherMethods,
                (HttpContext httpContext, IOptions<AppSettings> appSettings) =>
                    WriteMethodNotAllowed(httpContext, appSettings, allow)
            )
            .ExcludeFromDescription();
    }

    /// <summary>
    /// The same status, body, Allow header and content type as the data routes' 405
    /// (MethodNotAllowedMiddleware), serialized as AspNetCoreFrontend serializes every Core response.
    /// </summary>
    private static IResult WriteMethodNotAllowed(
        HttpContext httpContext,
        IOptions<AppSettings> appSettings,
        string allow
    )
    {
        // Request.Method is one of _standardMethods here: routing only reaches this endpoint for them.
        string method = httpContext.Request.Method;

        httpContext.Response.Headers.Allow = allow;

        return Results.Content(
            statusCode: StatusCodes.Status405MethodNotAllowed,
            content: JsonSerializer.Serialize(
                FailureResponse.ForMethodNotAllowed(
                    [$"The endpoint of the request does not support the '{method}' method."],
                    ExtractTraceIdFrom(httpContext.Request, appSettings)
                ),
                SharedSerializerOptions
            ),
            contentType: "application/json; charset=utf-8",
            contentEncoding: Encoding.UTF8
        );
    }
}
