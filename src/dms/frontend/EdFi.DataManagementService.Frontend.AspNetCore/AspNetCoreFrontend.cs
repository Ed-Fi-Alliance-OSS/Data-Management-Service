// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using AppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore;

/// <summary>
/// A thin static class that converts from ASP.NET Core to the DMS facade.
/// </summary>
public static class AspNetCoreFrontend
{
    internal static JsonSerializerOptions SharedSerializerOptions { get; } =
        new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    /// <summary>
    /// Takes an HttpRequest and returns a deserialized, not null or empty request Headers
    /// </summary>
    private static Dictionary<string, string> ExtractHeadersFrom(HttpRequest request) =>
        request
            .Headers.Select(h => new
            {
                h.Key,
                Value = h.Value.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            })
            .Where(h => h.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes an HttpRequest and returns a unique trace identifier
    /// </summary>
    public static TraceId ExtractTraceIdFrom(HttpRequest request, IOptions<AppSettings> options)
    {
        string headerName = options.Value.CorrelationIdHeader;
        if (
            !string.IsNullOrEmpty(headerName)
            && request.Headers.TryGetValue(headerName, out var correlationId)
            && !string.IsNullOrEmpty(correlationId)
        )
        {
            return new TraceId(correlationId!);
        }
        return new TraceId(request.HttpContext.TraceIdentifier);
    }

    private static string FromValidatedQueryParam(KeyValuePair<string, StringValues> queryParam)
    {
        switch (queryParam.Key.ToLower())
        {
            case "limit":
                return "limit";
            case "offset":
                return "offset";
            case "totalcount":
                return "totalCount";
            default:
                return queryParam.Key;
        }
    }

    /// <summary>
    /// Converts an AspNetCore HttpRequest to a DMS FrontendRequest
    /// </summary>
    private static FrontendRequest FromRequest(
        HttpRequest httpRequest,
        string dmsPath,
        IOptions<AppSettings> options,
        bool includeBody
    )
    {
        return new(
            Body: null,
            Headers: ExtractHeadersFrom(httpRequest),
            Path: $"/{dmsPath}",
            QueryParameters: httpRequest.Query.ToDictionary(FromValidatedQueryParam, x => x.Value[^1] ?? ""),
            TraceId: ExtractTraceIdFrom(httpRequest, options),
            BodyStream: includeBody ? httpRequest.Body : null
        );
    }

    /// <summary>
    /// Converts a DMS FrontendResponse to an AspNetCore IResult
    /// </summary>
    private static IResult ToResult(
        IFrontendResponse frontendResponse,
        HttpContext httpContext,
        string dmsPath
    )
    {
        if (frontendResponse.LocationHeaderPath != null)
        {
            string urlBeforeDmsPath = httpContext
                .Request.UrlWithPathSegment()[..^dmsPath.Length]
                .TrimEnd('/');
            httpContext.Response.Headers.Append(
                "Location",
                $"{urlBeforeDmsPath}{frontendResponse.LocationHeaderPath}"
            );
        }
        foreach (var header in frontendResponse.Headers)
        {
            httpContext.Response.Headers.Append(header.Key, header.Value);
        }

        if (frontendResponse is IStreamableFrontendResponse streamableFrontendResponse)
        {
            return new StreamResult(
                frontendResponse.StatusCode,
                frontendResponse.ContentType,
                streamableFrontendResponse.WriteBodyAsync
            );
        }

        return Results.Json(
            data: frontendResponse.Body,
            options: SharedSerializerOptions,
            contentType: frontendResponse.ContentType,
            statusCode: frontendResponse.StatusCode
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for API POST requests to DMS
    /// </summary>
    /// <param name="httpContext">The HttpContext for the request</param>
    /// <param name="apiService">The injected DMS core facade</param>
    /// <param name="dmsPath">The portion of the request path relevant to DMS</param>
    public static async Task<IResult> Upsert(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> options
    )
    {
        return ToResult(
            await apiService.Upsert(FromRequest(httpContext.Request, dmsPath, options, includeBody: true)),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for all API GET by id requests to DMS
    /// </summary>
    public static async Task<IResult> Get(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> options
    )
    {
        return ToResult(
            await apiService.Get(FromRequest(httpContext.Request, dmsPath, options, includeBody: false)),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for all API PUT requests to DMS, which are "by id"
    /// </summary>
    public static async Task<IResult> UpdateById(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> options
    )
    {
        return ToResult(
            await apiService.UpdateById(
                FromRequest(httpContext.Request, dmsPath, options, includeBody: true)
            ),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for all API DELETE requests to DMS, which are "by id"
    /// </summary>
    public static async Task<IResult> DeleteById(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> options
    )
    {
        return ToResult(
            await apiService.DeleteById(
                FromRequest(httpContext.Request, dmsPath, options, includeBody: false)
            ),
            httpContext,
            dmsPath
        );
    }

    private sealed class StreamResult(
        int statusCode,
        string? contentType,
        Func<Stream, CancellationToken, Task> writer
    ) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                httpContext.Response.ContentType = contentType;
            }

            await writer(httpContext.Response.Body, httpContext.RequestAborted);
        }
    }
}
