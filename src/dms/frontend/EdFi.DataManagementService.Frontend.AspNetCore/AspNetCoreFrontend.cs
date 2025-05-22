// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
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
    /// Takes an HttpRequest and returns a deserialized request Headers
    /// </summary>
    private static async Task<string?> ExtractJsonIfMatchFromAsync(HttpRequest request)
    {
        // Try to get If-Match from headers
        var ifMatch = request.Headers["If-Match"].FirstOrDefault();

        if (string.IsNullOrEmpty(ifMatch))
        {
            // Enable buffering to safely read body
            request.EnableBuffering();
            request.Body.Position = 0;

            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("IfMatch", out var ifMatchProp))
                {
                    ifMatch = ifMatchProp.GetString();
                }
            }
        }

        if (!string.IsNullOrEmpty(ifMatch))
        {
            var json = JsonSerializer.Serialize(new { IfMatch = ifMatch });
            return json;
        }

        return null;
    }

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
    private static async Task<FrontendRequest> FromRequest(
        HttpRequest HttpRequest,
        string dmsPath,
        IOptions<AppSettings> options
    )
    {
        var apiClientDetails = HttpRequest.HttpContext?.Items["ApiClientDetails"] as ClientAuthorizations;
        return new(
            Body: await ExtractJsonBodyFrom(HttpRequest),
            Header: await ExtractJsonIfMatchFromAsync(HttpRequest),
            Path: $"/{dmsPath}",
            QueryParameters: HttpRequest.Query.ToDictionary(FromValidatedQueryParam, x => x.Value[^1] ?? ""),
            TraceId: ExtractTraceIdFrom(HttpRequest, options),
            ClientAuthorizations: apiClientDetails
                ?? new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
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

        IResult result = Results.Content(
            statusCode: frontendResponse.StatusCode,
            content: frontendResponse.Body == null
                ? null
                : JsonSerializer.Serialize(
                    frontendResponse.Body,
                    new JsonSerializerOptions()
                    {
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = true,
                    }
                ),
            contentType: frontendResponse.ContentType,
            contentEncoding: System.Text.Encoding.UTF8
        );

        return result;
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
            await apiService.Upsert(await FromRequest(httpContext.Request, dmsPath, options)),
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
            await apiService.Get(await FromRequest(httpContext.Request, dmsPath, options)),
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
            await apiService.UpdateById(await FromRequest(httpContext.Request, dmsPath, options)),
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
            await apiService.DeleteById(await FromRequest(httpContext.Request, dmsPath, options)),
            httpContext,
            dmsPath
        );
    }
}
