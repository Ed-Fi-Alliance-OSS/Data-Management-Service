// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
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
    /// Takes an HttpRequest and returns a deserialized request body
    /// </summary>
    private static async Task<string?> ExtractJsonBodyFrom(HttpRequest request)
    {
        using Stream body = request.Body;
        using StreamReader bodyReader = new(body);
        var requestBodyString = await bodyReader.ReadToEndAsync();

        if (string.IsNullOrEmpty(requestBodyString))
        {
            return null;
        }

        return requestBodyString;
    }

    /// <summary>
    /// Extracts form data from an HTTP request and converts it to a dictionary.
    /// </summary>
    private static async Task<Dictionary<string, string>> ExtractFormFrom(HttpRequest request)
    {
        var formCollection = await request.ReadFormAsync();
        return formCollection.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
    }

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
    /// Extracts route qualifiers from the HttpRequest based on configured segments.
    /// Returns empty dictionary if no route qualifiers are configured.
    /// </summary>
    private static Dictionary<RouteQualifierName, RouteQualifierValue> ExtractRouteQualifiersFrom(
        HttpRequest request,
        IOptions<AppSettings> options
    )
    {
        string[] routeQualifierSegments = options.Value.GetRouteQualifierSegmentsArray();

        if (routeQualifierSegments.Length == 0)
        {
            return [];
        }

        Dictionary<RouteQualifierName, RouteQualifierValue> routeQualifiers = [];

        foreach (string segmentName in routeQualifierSegments)
        {
            if (
                request.RouteValues.TryGetValue(segmentName, out object? value) && value is string stringValue
            )
            {
                routeQualifiers[new RouteQualifierName(segmentName)] = new RouteQualifierValue(stringValue);
            }
        }

        return routeQualifiers;
    }

    /// <summary>
    /// Extracts the tenant identifier from the HttpRequest route values when multitenancy is enabled.
    /// Returns null if multitenancy is disabled or tenant is not found in route.
    /// </summary>
    private static string? ExtractTenantFrom(HttpRequest request, IOptions<AppSettings> appSettings)
    {
        if (!appSettings.Value.MultiTenancy)
        {
            return null;
        }

        if (request.RouteValues.TryGetValue("tenant", out object? value) && value is string tenant)
        {
            return tenant;
        }

        return null;
    }

    /// <summary>
    /// Converts an AspNetCore HttpRequest to a DMS FrontendRequest
    /// </summary>
    private static async Task<FrontendRequest> FromRequest(
        HttpRequest httpRequest,
        string dmsPath,
        IOptions<AppSettings> appSettings,
        bool includeBody,
        bool includeForm
    )
    {
        return new(
            Body: includeBody ? await ExtractJsonBodyFrom(httpRequest) : null,
            Form: includeForm ? await ExtractFormFrom(httpRequest) : null,
            Headers: ExtractHeadersFrom(httpRequest),
            Path: $"/{dmsPath}",
            QueryParameters: httpRequest.Query.ToDictionary(FromValidatedQueryParam, x => x.Value[^1] ?? ""),
            TraceId: ExtractTraceIdFrom(httpRequest, appSettings),
            RouteQualifiers: ExtractRouteQualifiersFrom(httpRequest, appSettings),
            Tenant: ExtractTenantFrom(httpRequest, appSettings)
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

        return Results.Content(
            statusCode: frontendResponse.StatusCode,
            content: frontendResponse.Body == null
                ? null
                : JsonSerializer.Serialize(frontendResponse.Body, SharedSerializerOptions),
            contentType: frontendResponse.ContentType,
            contentEncoding: Encoding.UTF8
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for API POST requests to DMS
    /// </summary>
    /// <param name="httpContext">The HttpContext for the request</param>
    /// <param name="apiService">The injected DMS core facade</param>
    /// <param name="dmsPath">The portion of the request path relevant to DMS</param>
    /// <param name="appSettings">Application settings</param>
    public static async Task<IResult> Upsert(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.Upsert(
                await FromRequest(
                    httpContext.Request,
                    dmsPath,
                    appSettings,
                    includeBody: true,
                    includeForm: false
                )
            ),
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
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.Get(
                await FromRequest(
                    httpContext.Request,
                    dmsPath,
                    appSettings,
                    includeBody: false,
                    includeForm: false
                )
            ),
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
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.UpdateById(
                await FromRequest(
                    httpContext.Request,
                    dmsPath,
                    appSettings,
                    includeBody: true,
                    includeForm: false
                )
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
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.DeleteById(
                await FromRequest(
                    httpContext.Request,
                    dmsPath,
                    appSettings,
                    includeBody: false,
                    includeForm: false
                )
            ),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the token introspection request
    /// </summary>
    public static async Task<IResult> GetTokenInfo(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.GetTokenInfo(
                await FromRequest(
                    httpContext.Request,
                    string.Empty,
                    appSettings,
                    includeBody: !httpContext.Request.HasFormContentType,
                    includeForm: httpContext.Request.HasFormContentType
                )
            ),
            httpContext,
            string.Empty
        );
    }
}
