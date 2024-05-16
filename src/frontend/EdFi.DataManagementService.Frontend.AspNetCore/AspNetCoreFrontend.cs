// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Frontend.AspNetCore;

/// <summary>
/// A thin static class that converts from ASP.NET Core to the DMS facade.
/// </summary>
public static class AspNetCoreFrontend
{
    /// <summary>
    /// Path segment value will be refined before passing over to core.
    /// This property value will be set from the front-end module.
    /// </summary>
    public static string? PathSegmentToRefine { get; set; }

    /// <summary>
    /// Takes an HttpRequest and returns the adjusted path
    /// </summary>
    private static string ExtractPathFrom(HttpRequest request)
    {
        return request.RefinedPath(PathSegmentToRefine);
    }

    /// <summary>
    /// Takes an HttpRequest and returns a deserialized request body
    /// </summary>
    private static async Task<JsonNode?> ExtractJsonBodyFrom(HttpRequest request)
    {
        using Stream body = request.Body;
        using StreamReader bodyReader = new(body);
        var requestBodyString = await bodyReader.ReadToEndAsync();

        if (string.IsNullOrEmpty(requestBodyString))
            return null;

        return JsonNode.Parse(requestBodyString);
    }

    /// <summary>
    /// Takes an HttpRequest and returns a unique trace identifier
    /// </summary>
    private static string ExtractTraceIdFrom(HttpRequest request)
    {
        return request.HttpContext.TraceIdentifier;
    }

    /// <summary>
    /// Converts an AspNetCore HttpRequest to a DMS FrontendRequest
    /// </summary>
    private static async Task<FrontendRequest> FromHttpRequest(HttpRequest HttpRequest)
    {
        return new(
            Body: await ExtractJsonBodyFrom(HttpRequest),
            Path: ExtractPathFrom(HttpRequest),
            QueryParameters: HttpRequest.Query.ToDictionary(x => x.Key, x => x.Value[^1] ?? ""),
            TraceId: ExtractTraceIdFrom(HttpRequest)
        );
    }

    /// <summary>
    /// Converts a DMS FrontendResponse to an AspNetCore IResult
    /// </summary>
    private static IResult ToResult(FrontendResponse frontendResponse, HttpResponse httpResponse)
    {
        foreach (var header in frontendResponse.Headers)
        {
            httpResponse.Headers.Append(header.Key, header.Value);
        }

        return Results.Content(
            statusCode: frontendResponse.StatusCode,
            content: frontendResponse.Body,
            contentType: "application/json",
            contentEncoding: System.Text.Encoding.UTF8
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for API POST requests to DMS
    /// </summary>
    public static async Task<IResult> Upsert(HttpContext httpContext, ICoreFacade coreFacade)
    {
        return ToResult(
            await coreFacade.Upsert(await FromHttpRequest(httpContext.Request)),
            httpContext.Response
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for all API GET by id requests to DMS
    /// </summary>
    public static async Task<IResult> GetById(HttpContext httpContext, ICoreFacade coreFacade)
    {
        return ToResult(
            await coreFacade.GetById(await FromHttpRequest(httpContext.Request)),
            httpContext.Response
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for all API PUT requests to DMS, which are "by id"
    /// </summary>
    public static async Task<IResult> UpdateById(HttpContext httpContext, ICoreFacade coreFacade)
    {
        return ToResult(
            await coreFacade.UpdateById(await FromHttpRequest(httpContext.Request)),
            httpContext.Response
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for all API DELETE requests to DMS, which are "by id"
    /// </summary>
    public static async Task<IResult> DeleteById(HttpContext httpContext, ICoreFacade coreFacade)
    {
        return ToResult(
            await coreFacade.DeleteById(await FromHttpRequest(httpContext.Request)),
            httpContext.Response
        );
    }
}
