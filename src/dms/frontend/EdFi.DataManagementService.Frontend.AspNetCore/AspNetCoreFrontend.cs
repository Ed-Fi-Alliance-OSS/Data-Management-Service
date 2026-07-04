// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
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
    private static async Task<JsonBodyExtractionResult> ExtractJsonBodyFrom(HttpRequest request)
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(GetInitialBodyBufferSize(request.ContentLength));
        int bodyLength = 0;

        try
        {
            while (true)
            {
                var readResult = await request.BodyReader.ReadAsync(request.HttpContext.RequestAborted);
                var buffer = readResult.Buffer;

                if (!buffer.IsEmpty)
                {
                    rentedBuffer = EnsureBodyBufferCapacity(rentedBuffer, bodyLength, buffer.Length);
                    buffer.CopyTo(rentedBuffer.AsSpan(bodyLength));
                    bodyLength += checked((int)buffer.Length);
                }

                request.BodyReader.AdvanceTo(buffer.End);

                if (readResult.IsCompleted)
                {
                    break;
                }
            }

            ReadOnlySpan<byte> body = StripUtf8Bom(rentedBuffer.AsSpan(0, bodyLength));

            if (body.IsEmpty || IsWhiteSpace(body))
            {
                return JsonBodyExtractionResult.Empty;
            }

            try
            {
                return new JsonBodyExtractionResult(
                    JsonNode.Parse(body),
                    null,
                    FindDuplicatePropertyPath(body)
                );
            }
            catch (Exception ex)
            {
                return new JsonBodyExtractionResult(null, ex.Message, null);
            }
        }
        finally
        {
            rentedBuffer.AsSpan(0, bodyLength).Clear();
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static async Task<string?> ExtractRawBodyFrom(HttpRequest request)
    {
        using StreamReader bodyReader = new(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true
        );
        string requestBody = await bodyReader.ReadToEndAsync();

        return string.IsNullOrEmpty(requestBody) ? null : requestBody;
    }

    private static int GetInitialBodyBufferSize(long? contentLength)
    {
        const int DefaultInitialBufferSize = 4096;

        if (contentLength is > 0 and <= int.MaxValue)
        {
            return Math.Max((int)contentLength, 1);
        }

        return DefaultInitialBufferSize;
    }

    private static byte[] EnsureBodyBufferCapacity(byte[] buffer, int length, long additionalLength)
    {
        if (additionalLength > int.MaxValue - length)
        {
            throw new InvalidOperationException("The request body is too large.");
        }

        int requiredLength = length + (int)additionalLength;

        if (requiredLength <= buffer.Length)
        {
            return buffer;
        }

        int newLength = buffer.Length;
        while (newLength < requiredLength)
        {
            newLength = checked(newLength * 2);
        }

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newLength);
        buffer.AsSpan(0, length).CopyTo(newBuffer);
        buffer.AsSpan(0, length).Clear();
        ArrayPool<byte>.Shared.Return(buffer);
        return newBuffer;
    }

    // Keep normal JSON bodies on the allocation-free span path, but match the previous
    // StreamReader/string.IsNullOrWhiteSpace behavior for BOM and Unicode whitespace.
    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> body)
    {
        const byte utf8BomFirstByte = 0xEF;
        const byte utf8BomSecondByte = 0xBB;
        const byte utf8BomThirdByte = 0xBF;

        return
            body.Length >= 3
            && body[0] == utf8BomFirstByte
            && body[1] == utf8BomSecondByte
            && body[2] == utf8BomThirdByte
            ? body[3..]
            : body;
    }

    private static bool IsWhiteSpace(ReadOnlySpan<byte> body)
    {
        for (int i = 0; i < body.Length; i++)
        {
            byte value = body[i];

            if (value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
            {
                continue;
            }

            if (value < 0x80)
            {
                return false;
            }

            return IsUtf8WhiteSpace(body[i..]);
        }

        return true;
    }

    private static bool IsUtf8WhiteSpace(ReadOnlySpan<byte> body)
    {
        while (!body.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(body, out Rune rune, out int bytesConsumed);

            if (status != OperationStatus.Done || !Rune.IsWhiteSpace(rune))
            {
                return false;
            }

            body = body[bytesConsumed..];
        }

        return true;
    }

    private static string? FindDuplicatePropertyPath(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip }
        );

        var pathStack = new Stack<JsonPathSegment>();
        var propertyNamesStack = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    propertyNamesStack.Push([]);
                    break;

                case JsonTokenType.EndObject:
                    if (propertyNamesStack.Count > 0)
                    {
                        propertyNamesStack.Pop();
                    }

                    if (pathStack.Count > 0 && !pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    break;

                case JsonTokenType.StartArray:
                    pathStack.Push(new JsonPathSegment(IsArray: true, PropertyName: null, ArrayIndex: 0));
                    break;

                case JsonTokenType.EndArray:
                    if (pathStack.Count > 0 && pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }

                    if (pathStack.Count > 0 && !pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    break;

                case JsonTokenType.PropertyName:
                    string propertyName = reader.GetString()!;

                    if (propertyNamesStack.Count > 0)
                    {
                        var currentProperties = propertyNamesStack.Peek();
                        if (!currentProperties.Add(propertyName))
                        {
                            return BuildJsonPath(pathStack, propertyName);
                        }
                    }

                    pathStack.Push(
                        new JsonPathSegment(IsArray: false, PropertyName: propertyName, ArrayIndex: 0)
                    );
                    break;

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    if (pathStack.Count > 0 && !pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }

                    if (pathStack.Count > 0 && pathStack.Peek().IsArray)
                    {
                        var arraySegment = pathStack.Pop();
                        pathStack.Push(arraySegment with { ArrayIndex = arraySegment.ArrayIndex + 1 });
                    }
                    break;
            }

            if (
                (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray)
                && pathStack.Count > 0
                && pathStack.Peek().IsArray
            )
            {
                var arraySegment = pathStack.Pop();
                pathStack.Push(arraySegment with { ArrayIndex = arraySegment.ArrayIndex + 1 });
            }
        }

        return null;
    }

    private static string BuildJsonPath(Stack<JsonPathSegment> pathStack, string duplicatePropertyName)
    {
        var segments = pathStack.ToArray();
        Array.Reverse(segments);

        var pathBuilder = new StringBuilder("$");

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.IsArray)
            {
                pathBuilder.Append($"[{segment.ArrayIndex}]");
            }
            else if (segment.PropertyName != null)
            {
                pathBuilder.Append($".{segment.PropertyName}");
            }
        }

        pathBuilder.Append($".{duplicatePropertyName}");
        return pathBuilder.ToString();
    }

    private sealed record JsonPathSegment(bool IsArray, string? PropertyName, int ArrayIndex);

    private readonly record struct JsonBodyExtractionResult(
        JsonNode? ParsedBody,
        string? ParseErrorMessage,
        string? DuplicatePropertyPath
    )
    {
        public static JsonBodyExtractionResult Empty { get; } = new(null, null, null);
    }

    /// <summary>
    /// Extracts form data from an HTTP request and converts it to a dictionary.
    /// </summary>
    private static async Task<Dictionary<string, string>> ExtractFormFrom(HttpRequest request)
    {
        var formCollection = await request.ReadFormAsync();
        return formCollection.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
    }

    private const string ContentTypeHeaderName = "Content-Type";
    private const string AuthorizationHeaderName = "Authorization";

    /// <summary>
    /// Headers that must reach core verbatim rather than through the blank-dropping,
    /// first-non-blank reduction in <see cref="ExtractHeadersFrom"/>: Content-Type (an explicit
    /// blank is rejected with 415) and Authorization (an explicit blank or a repeated header is
    /// a malformed header rejected with 401, distinct from a missing header, which reports
    /// "Authorization header is missing.").
    /// </summary>
    private static readonly string[] HeadersPreservedWhenExplicitlySent =
    [
        ContentTypeHeaderName,
        AuthorizationHeaderName,
    ];

    /// <summary>
    /// Takes an HttpRequest and returns its headers as a dictionary. Blank header values are
    /// dropped and multi-valued headers are reduced to their first non-blank value, except for
    /// the headers in <see cref="HeadersPreservedWhenExplicitlySent"/>, which are delivered
    /// verbatim (blank preserved, multiple values comma-joined) so core can classify an
    /// explicit blank or a repeated header as malformed rather than as missing or valid.
    /// </summary>
    private static Dictionary<string, string> ExtractHeadersFrom(HttpRequest request)
    {
        var headers = request
            .Headers.Select(h => new
            {
                h.Key,
                Value = h.Value.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            })
            .Where(h => h.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);

        // The filtering above discards blank values and reduces a repeated header to its first
        // non-blank value. Normalize the preserved headers to their full comma-joined value so
        // an explicit blank reaches core as present-but-invalid and a repeated header is not
        // reduced to a single authenticatable value. string.Join is used instead of
        // StringValues.ToString() because ToString() drops empty entries, which would silently
        // erase a blank duplicate sent alongside a valid value.
        foreach (string headerName in HeadersPreservedWhenExplicitlySent)
        {
            if (request.Headers.TryGetValue(headerName, out StringValues value))
            {
                headers[headerName] = string.Join(",", value.ToArray());
            }
        }

        return headers;
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
        bool includeForm,
        bool parseJsonBody = true
    )
    {
        JsonBodyExtractionResult jsonBody =
            includeBody && parseJsonBody
                ? await ExtractJsonBodyFrom(httpRequest)
                : JsonBodyExtractionResult.Empty;
        string? rawBody = includeBody && !parseJsonBody ? await ExtractRawBodyFrom(httpRequest) : null;

        return new(
            Body: rawBody,
            Form: includeForm ? await ExtractFormFrom(httpRequest) : null,
            Headers: ExtractHeadersFrom(httpRequest),
            Path: $"/{dmsPath}",
            QueryParameters: httpRequest.Query.ToDictionary(FromValidatedQueryParam, x => x.Value[^1] ?? ""),
            TraceId: ExtractTraceIdFrom(httpRequest, appSettings),
            RouteQualifiers: ExtractRouteQualifiersFrom(httpRequest, appSettings),
            Tenant: ExtractTenantFrom(httpRequest, appSettings),
            ParsedBody: jsonBody.ParsedBody,
            BodyParseErrorMessage: jsonBody.ParseErrorMessage,
            DuplicatePropertyPath: jsonBody.DuplicatePropertyPath
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
            // The _etag is stored as an opaque, unquoted value in the JSON body; serve it on the
            // ETag response header as a quoted strong validator (RFC 7232 §2.3). Other headers pass
            // through verbatim. Normalize via TryParseHeaderValue to handle any pre-quoted values,
            // and skip empty values.
            if (string.Equals(header.Key, "etag", StringComparison.OrdinalIgnoreCase))
            {
                if (EtagValue.TryParseHeaderValue(header.Value, out var etagValue))
                {
                    httpContext.Response.Headers.Append(header.Key, EtagValue.ToHeaderValue(etagValue));
                }
            }
            else
            {
                httpContext.Response.Headers.Append(header.Key, header.Value);
            }
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
        var isUrlEncodedForm =
            MediaTypeHeaderValue.TryParse(httpContext.Request.ContentType, out var mediaType)
            && mediaType.MediaType?.Equals(
                "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase
            ) == true;

        return ToResult(
            await apiService.GetTokenInfo(
                await FromRequest(
                    httpContext.Request,
                    string.Empty,
                    appSettings,
                    includeBody: !isUrlEncodedForm,
                    includeForm: isUrlEncodedForm,
                    parseJsonBody: false
                )
            ),
            httpContext,
            string.Empty
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the Change Queries availableChangeVersions request
    /// </summary>
    public static async Task<IResult> GetAvailableChangeVersions(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.GetAvailableChangeVersions(
                await FromRequest(
                    httpContext.Request,
                    string.Empty,
                    appSettings,
                    includeBody: false,
                    includeForm: false
                )
            ),
            httpContext,
            string.Empty
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for resource-scoped Change Query tracked changes requests
    /// </summary>
    public static async Task<IResult> GetTrackedChanges(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.GetTrackedChanges(
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
}
