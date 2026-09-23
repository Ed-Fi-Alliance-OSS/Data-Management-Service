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
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
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
    private const string IfNoneMatchHeaderName = "If-None-Match";

    /// <summary>
    /// Headers that must reach core verbatim rather than through the blank-dropping,
    /// first-non-blank reduction in <see cref="ExtractHeadersFrom"/>: Content-Type (an explicit
    /// blank is rejected with 415) and Authorization (an explicit blank or a repeated header is
    /// a malformed header rejected with 401, distinct from a missing header, which reports
    /// "Authorization header is missing."), and If-None-Match (multiple values form one
    /// comma-separated entity-tag list whose members must all reach core).
    /// </summary>
    private static readonly string[] HeadersPreservedWhenExplicitlySent =
    [
        ContentTypeHeaderName,
        AuthorizationHeaderName,
        IfNoneMatchHeaderName,
    ];

    /// <summary>
    /// Takes an HttpRequest and returns its headers as a dictionary. Blank header values are
    /// dropped and multi-valued headers are reduced to their first non-blank value, except for
    /// the headers in <see cref="HeadersPreservedWhenExplicitlySent"/>, which are delivered
    /// verbatim (blank preserved, multiple values comma-joined) so core can validate the complete
    /// field value or apply list semantics.
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
        // an explicit blank reaches core as present-but-invalid and a multi-valued header is not
        // reduced before core can validate or interpret it. string.Join is used instead of
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
    /// Takes an HttpRequest and returns its correlation ID as a normalized
    /// <see cref="TraceId"/>. This is the single ingestion point: both candidate sources - the
    /// configured client header and the server-generated identifier - are normalized here, so
    /// every downstream log event and error response body carries the identical value.
    /// </summary>
    /// <remarks>
    /// A client-supplied header that normalizes to nothing but whitespace falls through to the
    /// server-generated identifier, so blankness is tested after normalization rather than before
    /// it. Why: <c>reference/adr-correlation-id-normalization.md</c>.
    ///
    /// The test is <see cref="string.IsNullOrWhiteSpace(string?)"/> rather than a length check
    /// because whitespace is neither control nor format, and so is retained by the
    /// correlation-ID allowlist by design: an all-whitespace header would otherwise survive
    /// normalization intact and become the correlation ID. Kestrel strips leading and trailing
    /// ASCII optional whitespace, but decodes header bytes as Latin-1 by default, so U+00A0
    /// NO-BREAK SPACE - whitespace to .NET and not OWS to Kestrel - reaches here. Internal
    /// whitespace in a correlation ID is still accepted whole; only the all-blank case falls back.
    ///
    /// Total, and in particular does not surface <see cref="OptionsValidationException"/>, so no
    /// caller needs to guard this call.
    /// </remarks>
    public static TraceId ExtractTraceIdFrom(HttpRequest request, IOptions<AppSettings> options) =>
        IngestCorrelationIdFrom(request, options).TraceId;

    /// <summary>
    /// The key under which the one ingestion result for a request is cached on
    /// <see cref="HttpContext.Items"/>.
    /// </summary>
    /// <remarks>
    /// Namespaced rather than a bare word, because <c>HttpContext.Items</c> is shared with every
    /// other middleware, framework component and third-party package in the pipeline.
    /// </remarks>
    internal const string CorrelationIdItemsKey =
        "EdFi.DataManagementService.Frontend.CorrelationIdIngestion";

    /// <summary>
    /// The key under which this request's one <c>AppSettings</c> read outcome - the resolved
    /// value, or the fact that reading it threw <see cref="OptionsValidationException"/> - is
    /// cached on <see cref="HttpContext.Items"/>, so a second consumer in the same request
    /// (<c>LoggingMiddleware</c> ingesting the correlation ID and, separately, redacting an
    /// identity route) reuses it rather than reading, and on a host stuck with invalid
    /// configuration re-throwing, again.
    /// </summary>
    internal const string AppSettingsSnapshotItemsKey =
        "EdFi.DataManagementService.Frontend.AppSettingsSnapshot";

    /// <summary>
    /// Reads <see cref="IOptions{TOptions}.Value"/> at most once per request, caching the outcome
    /// - including an unreadable one - on <see cref="HttpContext.Items"/>. Every other consumer in
    /// the same request calls this instead of reading <paramref name="options"/> directly, so the
    /// total cost of a host whose <c>AppSettings</c> fail validation stays one read (and one
    /// thrown <see cref="OptionsValidationException"/>) per request, not one per consumer.
    /// </summary>
    /// <returns>The resolved settings, or null when the options value could not be read.</returns>
    internal static AppSettings? TryReadAppSettings(HttpContext context, IOptions<AppSettings> options)
    {
        IDictionary<object, object?> items = context.Items;
        if (items.TryGetValue(AppSettingsSnapshotItemsKey, out object? cached))
        {
            return cached as AppSettings;
        }

        AppSettings? result;
        try
        {
            result = options.Value;
        }
        catch (OptionsValidationException)
        {
            result = null;
        }

        items[AppSettingsSnapshotItemsKey] = result;
        return result;
    }

    /// <summary>
    /// <see cref="ExtractTraceIdFrom"/> plus the derived facts about what normalization did to a
    /// client-supplied value, for the one caller - <c>LoggingMiddleware</c> - that reports them.
    /// </summary>
    /// <remarks>
    /// Computed at most once per request and cached on <see cref="HttpContext.Items"/>, so
    /// "normalized once" is a property of the code rather than of the function happening to be
    /// pure. A cache miss still computes, so a caller that somehow runs ahead of the
    /// request-logging middleware simply becomes the one that populates it. Total: every request
    /// gets an ingestion, including one whose <c>AppSettings</c> cannot be read, through
    /// <see cref="IngestUnreadableConfiguration"/> below rather than through any caller's own
    /// fallback.
    /// </remarks>
    internal static CorrelationIdIngestion IngestCorrelationIdFrom(
        HttpRequest request,
        IOptions<AppSettings> options
    )
    {
        IDictionary<object, object?> items = request.HttpContext.Items;
        if (
            items.TryGetValue(CorrelationIdItemsKey, out object? cached)
            && cached is CorrelationIdIngestion ingested
        )
        {
            return ingested;
        }

        AppSettings? appSettings = TryReadAppSettings(request.HttpContext, options);
        if (appSettings is null)
        {
            return IngestUnreadableConfiguration(request.HttpContext);
        }

        int maxLength = appSettings.CorrelationIdMaxLength;
        string headerName = appSettings.CorrelationIdHeader;

        // Empty when the setting names no header or the request omits it, which is the same
        // starting point as a header sent empty. Normalize("") short-circuits to string.Empty,
        // so all three of those cases reach the fallback below through one code path.
        string clientSupplied =
            !string.IsNullOrEmpty(headerName)
            && request.Headers.TryGetValue(headerName, out StringValues headerValue)
                ? headerValue.ToString()
                : string.Empty;

        CorrelationIdNormalizer.NormalizationDetail detail = CorrelationIdNormalizer.NormalizeWithDetail(
            clientSupplied,
            maxLength
        );

        string normalized = detail.Value;
        bool fellBack = string.IsNullOrWhiteSpace(normalized);
        if (fellBack)
        {
            normalized = CorrelationIdNormalizer.Normalize(request.HttpContext.TraceIdentifier, maxLength);
        }

        // A header that was absent, disabled by configuration, or sent empty is not "a value the
        // client supplied", so nothing about it is reportable and the flags below are all false.
        // That is what keeps the normal path - which is the overwhelming majority of requests -
        // free of a per-request log line.
        bool clientSuppliedAValue = !string.IsNullOrEmpty(clientSupplied);

        CorrelationIdIngestion result = new(
            TraceId: new TraceId(normalized),
            ClientSuppliedAValue: clientSuppliedAValue,
            SuppliedLength: clientSupplied.Length,
            Truncated: clientSuppliedAValue && detail.Truncated,
            CharactersRemoved: clientSuppliedAValue && detail.CharactersRemoved,
            FellBackToServerIdentifier: clientSuppliedAValue && fellBack
        );

        return CacheIngestionOn(request.HttpContext, result);
    }

    /// <summary>
    /// This request's one ingestion result for a host whose <c>AppSettings</c> cannot be read at
    /// all, because validating them is what failed.
    /// </summary>
    /// <remarks>
    /// The decision lives here rather than at the two call sites that encounter it -
    /// <c>LoggingMiddleware</c> ingesting the request, and
    /// <c>ReportInvalidConfigurationMiddleware</c> reading the correlation ID for the 500 body it
    /// short-circuits with - which would otherwise each need a <c>catch</c> making this same
    /// policy choice, one policy in two places and free to drift apart.
    ///
    /// <see cref="Configuration.AppSettings.DefaultCorrelationIdMaxLength"/> stands in for the
    /// cap there is no validated value for, and the server-generated identifier still goes
    /// through the same <see cref="CorrelationIdNormalizer"/> as every other path, so this one
    /// cannot emit a differently-shaped value.
    /// <see cref="CorrelationIdIngestion.ClientSuppliedAValue"/> is false because no client value
    /// was considered at all: the header <i>name</i> lives in the configuration that failed to
    /// validate, so the <c>CorrelationIdModified</c> notice stays silent. The result is cached
    /// like any other, which is what makes the 500 body a client reads and the <c>TraceId</c> it
    /// searches the logs for one value rather than two that merely agree.
    ///
    /// What this mode does to a host, and why: <c>reference/adr-correlation-id-normalization.md</c>.
    /// </remarks>
    private static CorrelationIdIngestion IngestUnreadableConfiguration(HttpContext context) =>
        CacheIngestionOn(
            context,
            CorrelationIdIngestion.ForServerGeneratedIdentifier(
                CorrelationIdNormalizer.Normalize(
                    context.TraceIdentifier,
                    AppSettings.DefaultCorrelationIdMaxLength
                )
            )
        );

    /// <summary>
    /// Records <paramref name="ingestion"/> as this request's one ingestion result, returning what
    /// it stored so a caller can cache and return in a single expression.
    /// </summary>
    /// <remarks>
    /// Exists so that <see cref="CorrelationIdItemsKey"/> is written in exactly one place, and is
    /// private so that place stays inside this class - no middleware can write the key behind the
    /// ingestion point's back.
    /// </remarks>
    private static CorrelationIdIngestion CacheIngestionOn(
        HttpContext context,
        CorrelationIdIngestion ingestion
    )
    {
        context.Items[CorrelationIdItemsKey] = ingestion;
        return ingestion;
    }

    /// <summary>
    /// One request's correlation ID together with the derived facts about how it was reached.
    /// Carries no part of the client-supplied value beyond its length and its normalized form.
    /// </summary>
    /// <param name="TraceId">The normalized correlation ID every log event and error response body carries.</param>
    /// <param name="ClientSuppliedAValue">
    /// Whether the configured correlation header was present on the request with a non-empty value.
    /// </param>
    /// <param name="SuppliedLength">
    /// The length in UTF-16 code units of the client-supplied value, or zero when there was none.
    /// </param>
    /// <param name="Truncated">Whether the client-supplied value exceeded the configured length cap.</param>
    /// <param name="CharactersRemoved">
    /// Whether the allowlist removed at least one character from the client-supplied value.
    /// </param>
    /// <param name="FellBackToServerIdentifier">
    /// Whether the client-supplied value normalized to blank and was replaced in full by
    /// <see cref="HttpContext.TraceIdentifier"/>.
    /// </param>
    internal readonly record struct CorrelationIdIngestion(
        TraceId TraceId,
        bool ClientSuppliedAValue,
        int SuppliedLength,
        bool Truncated,
        bool CharactersRemoved,
        bool FellBackToServerIdentifier
    )
    {
        /// <summary>
        /// Whether the value the client sent is not the value the request is correlated by. False
        /// when no value was supplied, and false when the supplied value survived normalization
        /// unchanged - the two cases that must stay silent.
        /// </summary>
        public bool WasModified =>
            ClientSuppliedAValue && (Truncated || CharactersRemoved || FellBackToServerIdentifier);

        /// <summary>
        /// The ingestion result for a request whose correlation ID came from the server-generated
        /// trace identifier without any client-supplied value being considered. Lives here rather
        /// than at the call site so <c>AspNetCoreFrontend</c> remains the only production file that
        /// constructs a <see cref="Core.External.Model.TraceId"/>.
        /// </summary>
        /// <param name="normalized">The already-normalized server-generated identifier.</param>
        internal static CorrelationIdIngestion ForServerGeneratedIdentifier(string normalized) =>
            new(
                TraceId: new TraceId(normalized),
                ClientSuppliedAValue: false,
                SuppliedLength: 0,
                Truncated: false,
                CharactersRemoved: false,
                FellBackToServerIdentifier: false
            );
    }

    /// <summary>
    /// The route segment naming the partitions operation. Held here rather than shared with Core,
    /// whose recognition constant is internal to a different assembly, and held to that constant by a
    /// frontend unit test that builds its request from it, seeing it through <c>InternalsVisibleTo</c>.
    /// Renaming the segment on one side alone fails that test: canonicalization would otherwise
    /// stop reaching the operation Core recognizes, leaving the partition count to be validated under
    /// the client's spelling.
    /// </summary>
    private const string PartitionsPathSegment = "partitions";

    /// <summary>
    /// Whether the path names the partitions operation, which is the only place the generic
    /// <c>number</c> parameter is a paging control rather than a possible resource query field.
    /// </summary>
    /// <remarks>
    /// The segment must be the third of the <c>{project}/{resource}/partitions</c> shape. Testing only
    /// the final segment would also recognize a two-segment collection whose resource is itself named
    /// <c>partitions</c>, where <c>number</c> is an ordinary query field and rewriting its spelling
    /// would change which field is filtered on or which name an unknown-field error reports. The
    /// position requirement is what makes that impossible rather than merely unlikely, so nothing
    /// here rests on an assumption about which resource names a schema declares.
    /// </remarks>
    /// <remarks>
    /// Trailing slashes are tolerated, and the shape check deliberately stops there rather than
    /// re-implementing Core's path expression, which is stricter: it accepts a single trailing slash
    /// and requires every segment to be non-empty. Core classifies the path before any query parameter
    /// name is read, so a path it does not recognize is answered as not found whatever this returns for
    /// it, and a name canonicalized here for such a path never reaches validation. A second definition
    /// of path shape held in step with Core's would add drift without changing a served response.
    /// </remarks>
    private static bool IsPartitionsPath(string dmsPath)
    {
        ReadOnlySpan<char> path = dmsPath.AsSpan().TrimEnd('/');

        int projectSeparator = path.IndexOf('/');
        if (projectSeparator < 0)
        {
            return false;
        }

        ReadOnlySpan<char> afterProject = path[(projectSeparator + 1)..];
        int resourceSeparator = afterProject.IndexOf('/');
        if (resourceSeparator < 0)
        {
            return false;
        }

        // Everything after the resource segment, not just the next segment: a longer path leaves the
        // separators in this span, so it cannot equal the bare segment name.
        return afterProject[(resourceSeparator + 1)..]
            .Equals(PartitionsPathSegment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The canonical spellings of the query parameter names recognized on every request: the
    /// traditional paging and count controls and the cursor paging controls.
    /// </summary>
    private static readonly string[] QueryParameterNamesCanonicalizedEverywhere =
    [
        "limit",
        "offset",
        "totalCount",
        "pageToken",
        "pageSize",
    ];

    /// <summary>
    /// The canonical spelling of the partition count, recognized only on the partitions operation.
    /// </summary>
    private const string PartitionNumberParameterName = "number";

    /// <summary>
    /// Canonicalizes the query parameter names Core matches exactly. A name that is not recognized is
    /// returned exactly as supplied.
    /// </summary>
    /// <remarks>
    /// The cursor parameters are canonicalized everywhere. The partition count is canonicalized only
    /// on the partitions operation, because <c>number</c> is generic enough to collide with a
    /// resource query field, and rewriting its spelling elsewhere would change resource filtering and
    /// unknown-field error text on collections this feature does not otherwise touch.
    /// </remarks>
    /// <remarks>
    /// Recognition is an ordinal case-insensitive comparison, which is the same relation the query
    /// collection uses for its own keys. Matching it exactly is what keeps the result usable as a
    /// dictionary key: a canonical spelling is produced only for a name ordinally case-insensitively
    /// equal to it, so any two names producing it are equal to each other and are already a single
    /// query collection entry, while an unrecognized name passes through and cannot equal a canonical
    /// spelling without having been recognized. The comparison is also independent of the server's
    /// culture, which these fixed protocol tokens require; a Turkish locale, for example, lowercases
    /// <c>I</c> to a dotless <c>ı</c>, and a culture-sensitive fold would leave every name containing
    /// that letter unrecognized.
    /// </remarks>
    private static string FromValidatedQueryParam(
        KeyValuePair<string, StringValues> queryParam,
        bool canonicalizePartitionNumber
    )
    {
        string suppliedName = queryParam.Key;

        string? canonicalName = Array.Find(
            QueryParameterNamesCanonicalizedEverywhere,
            name => string.Equals(name, suppliedName, StringComparison.OrdinalIgnoreCase)
        );

        if (canonicalName is not null)
        {
            return canonicalName;
        }

        return
            canonicalizePartitionNumber
            && string.Equals(suppliedName, PartitionNumberParameterName, StringComparison.OrdinalIgnoreCase)
            ? PartitionNumberParameterName
            : suppliedName;
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

        bool canonicalizePartitionNumber = IsPartitionsPath(dmsPath);

        return new(
            Body: rawBody,
            Form: includeForm ? await ExtractFormFrom(httpRequest) : null,
            Headers: ExtractHeadersFrom(httpRequest),
            Path: $"/{dmsPath}",
            // Repeated exact names and case variants already collapse to one entry in the query
            // collection, which retains every value in request order, so taking the final value is
            // last-value-wins. Canonicalizing a name uses the same comparison as the query collection's
            // own comparer, so two entries it holds separately cannot produce one canonical key.
            QueryParameters: httpRequest.Query.ToDictionary(
                queryParam => FromValidatedQueryParam(queryParam, canonicalizePartitionNumber),
                x => x.Value[^1] ?? ""
            ),
            TraceId: ExtractTraceIdFrom(httpRequest, appSettings),
            RouteQualifiers: ExtractRouteQualifiersFrom(httpRequest, appSettings),
            Tenant: ExtractTenantFrom(httpRequest, appSettings),
            ParsedBody: jsonBody.ParsedBody,
            BodyParseErrorMessage: jsonBody.ParseErrorMessage,
            DuplicatePropertyPath: jsonBody.DuplicatePropertyPath,
            ResponseContentCoding: HttpMethods.IsGet(httpRequest.Method)
                ? ResolveResponseContentCoding(httpRequest.HttpContext)
                : ResponseContentCoding.Identity
        );
    }

    /// <summary>
    /// Uses the same registered provider as ASP.NET Core response compression to select the content
    /// coding that a successful JSON response will use. Absence of the service means compression is
    /// disabled. The provider is consulted again by the compression middleware when the body is
    /// written; negotiation is deterministic for the unchanged request headers.
    /// </summary>
    internal static ResponseContentCoding ResolveResponseContentCoding(HttpContext httpContext)
    {
        if (
            GetResponseCompressionProvider(httpContext) is not { } responseCompressionProvider
            || !responseCompressionProvider.CheckRequestAcceptsCompression(httpContext)
        )
        {
            return ResponseContentCoding.Identity;
        }

        if (
            responseCompressionProvider.GetCompressionProvider(httpContext)?.EncodingName
            is not { } encodingName
        )
        {
            return ResponseContentCoding.Identity;
        }

        if (string.Equals(encodingName, "br", StringComparison.OrdinalIgnoreCase))
        {
            return ResponseContentCoding.Brotli;
        }

        if (string.Equals(encodingName, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            return ResponseContentCoding.Gzip;
        }

        throw new InvalidOperationException(
            $"Response compression selected unsupported content coding '{encodingName}'. "
                + "Register a stable served-etag variant code before enabling this provider."
        );
    }

    private static IResponseCompressionProvider? GetResponseCompressionProvider(HttpContext httpContext)
    {
        if (httpContext.RequestServices is null)
        {
            return null;
        }

        return httpContext.RequestServices.GetService<IResponseCompressionProvider>();
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
            // ETag response header as a quoted strong validator (RFC 9110 §8.8.3). Other headers pass
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

        // Every data-plane GET's target selection depends on Use-Snapshot: a parsed true routes the
        // same URI to a different physical database, which can change the representation, its
        // validator, or the status itself - a missing snapshot answers 404 where the primary answers
        // 200. Declared for every GET emitted through this boundary, error statuses included, so a
        // cache never reuses a response across requests that differ on the selector.
        if (HttpMethods.IsGet(httpContext.Request.Method))
        {
            AppendVaryHeaderIfMissing(httpContext.Response, "Use-Snapshot");
        }

        // Successful resource GETs can vary by readable profile/media type. They can also vary by
        // content coding when response compression is enabled, including query responses whose item
        // etags carry the selected coding and 304 responses whose empty body bypasses compression
        // middleware. Declare both request selectors at the serving boundary so caches never reuse a
        // representation across incompatible Accept or Accept-Encoding values.
        if (
            HttpMethods.IsGet(httpContext.Request.Method)
            && frontendResponse.StatusCode is StatusCodes.Status200OK or StatusCodes.Status304NotModified
        )
        {
            AppendVaryHeaderIfMissing(httpContext.Response, "Accept");

            if (GetResponseCompressionProvider(httpContext) is not null)
            {
                AppendVaryHeaderIfMissing(httpContext.Response, "Accept-Encoding");
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

    private static void AppendVaryHeaderIfMissing(HttpResponse response, string fieldName)
    {
        if (
            !response
                .Headers.GetCommaSeparatedValues("Vary")
                .Contains(fieldName, StringComparer.OrdinalIgnoreCase)
        )
        {
            response.Headers.Append("Vary", fieldName);
        }
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
                ),
                httpContext.RequestAborted
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
                ),
                httpContext.RequestAborted
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
                ),
                httpContext.RequestAborted
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

    /// <summary>
    /// ASP.NET Core entry point for data-route requests whose HTTP method is not one of the
    /// supported verbs. Core decides between 404 (unknown resource) and 405 (unsupported method),
    /// and ToResult carries Core's Allow header and content type to the response.
    /// </summary>
    public static async Task<IResult> MethodNotAllowed(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.MethodNotAllowed(
                await FromRequest(
                    httpContext.Request,
                    dmsPath,
                    appSettings,
                    includeBody: false,
                    includeForm: false
                ),
                httpContext.Request.Method
            ),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for tracked-change route requests (/deletes, /keyChanges) whose
    /// HTTP method is not one of the supported verbs. Goes through Core for the same reason the
    /// data-route entry point does: authentication, tenant validation and resource existence must
    /// all precede the 405.
    /// </summary>
    public static async Task<IResult> MethodNotAllowedForTrackedChange(
        HttpContext httpContext,
        IApiService apiService,
        string dmsPath,
        IOptions<AppSettings> appSettings
    )
    {
        return ToResult(
            await apiService.MethodNotAllowedForTrackedChange(
                await FromRequest(
                    httpContext.Request,
                    dmsPath,
                    appSettings,
                    includeBody: false,
                    includeForm: false
                ),
                httpContext.Request.Method
            ),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// The literal route segment every identity operation route is anchored on, used both to
    /// derive the redacted route-template path (D11) and to locate the real request path's tail
    /// for the Location header math in <see cref="ToResult"/>.
    /// </summary>
    private const string IdentitiesRouteSegment = "/identity/v2/identities";

    /// <summary>
    /// The deployment's default Kestrel request-line budget, applied only when
    /// <see cref="IOptions{TOptions}"/> of <see cref="KestrelServerOptions"/> supplies no usable
    /// value (for example under a host that never configured Kestrel limits). Matches the
    /// documented and runtime-verified default for this deployment (Probe (c), design.md).
    /// </summary>
    private const int DefaultIdentityMaxRequestLineSize = 8192;

    /// <summary>
    /// The real, unredacted request path (no leading slash), used only as the dmsPath argument to
    /// <see cref="ToResult"/>. <see cref="ToResult"/> strips exactly this many characters off the
    /// tail of the real request URL to compute the Location header's base, so for identity routes
    /// the value must span the whole path - tenant, route qualifiers, and all - because
    /// <see cref="RequestInfo.IdentityPollPathPrefix"/> is itself already tenant/qualifier-qualified
    /// (it is derived from <see cref="BuildIdentityTemplatePath"/>, which carries that prefix on
    /// <see cref="FrontendRequest.Path"/> for D11). Stripping only the identities tail here, as a
    /// non-identity dmsPath would, would leave the tenant/qualifier prefix in the computed base and
    /// double it when Core's already-qualified Location path is appended.
    /// </summary>
    private static string ExtractIdentityDmsPath(HttpRequest request) =>
        (request.Path.Value ?? string.Empty).TrimStart('/');

    /// <summary>
    /// The redacted route-template path carried on <see cref="FrontendRequest.Path"/> for D11:
    /// tenant and route-qualifier segments are literal (read from the real request path), while
    /// any identifier segment is replaced by a placeholder so the identifier itself never reaches
    /// a log. Also the source Core's ComputeIdentityPollPathPrefix reads to build the poll URL.
    /// </summary>
    private static string BuildIdentityTemplatePath(HttpRequest request, string operationSuffix)
    {
        string requestPath = request.Path.Value ?? string.Empty;
        int segmentIndex = requestPath.IndexOf(IdentitiesRouteSegment, StringComparison.Ordinal);
        string prefix = segmentIndex >= 0 ? requestPath[..segmentIndex] : string.Empty;
        return $"{prefix}{IdentitiesRouteSegment}{operationSuffix}";
    }

    /// <summary>
    /// The configured Kestrel request-line budget, read from the running server so Core can bound
    /// a composed poll path against the real deployment limit rather than a hard-coded guess.
    /// </summary>
    private static int ResolveMaxRequestLineSize(IOptions<KestrelServerOptions> kestrelOptions) =>
        kestrelOptions.Value?.Limits.MaxRequestLineSize ?? DefaultIdentityMaxRequestLineSize;

    /// <summary>
    /// Converts an AspNetCore HttpRequest to a DMS FrontendRequest for one of the five identity
    /// operation entry points. Unlike <see cref="FromRequest"/>, the path is supplied by the
    /// caller as the already-redacted route template rather than derived from a dmsPath route
    /// value, and no query-parameter canonicalization applies (identity has no partitions
    /// operation).
    /// </summary>
    private static async Task<FrontendRequest> FromIdentityRequest(
        HttpRequest httpRequest,
        string path,
        IOptions<AppSettings> appSettings,
        IOptions<KestrelServerOptions> kestrelOptions,
        bool includeBody
    )
    {
        JsonBodyExtractionResult jsonBody = includeBody
            ? await ExtractJsonBodyFrom(httpRequest)
            : JsonBodyExtractionResult.Empty;

        return new(
            Body: null,
            Form: null,
            Headers: ExtractHeadersFrom(httpRequest),
            Path: path,
            QueryParameters: httpRequest.Query.ToDictionary(
                queryParam => FromValidatedQueryParam(queryParam, canonicalizePartitionNumber: false),
                x => x.Value[^1] ?? ""
            ),
            TraceId: ExtractTraceIdFrom(httpRequest, appSettings),
            RouteQualifiers: ExtractRouteQualifiersFrom(httpRequest, appSettings),
            Tenant: ExtractTenantFrom(httpRequest, appSettings),
            ParsedBody: jsonBody.ParsedBody,
            BodyParseErrorMessage: jsonBody.ParseErrorMessage,
            DuplicatePropertyPath: jsonBody.DuplicatePropertyPath,
            ResponseContentCoding: HttpMethods.IsGet(httpRequest.Method)
                ? ResolveResponseContentCoding(httpRequest.HttpContext)
                : ResponseContentCoding.Identity,
            MaxRequestLineSize: ResolveMaxRequestLineSize(kestrelOptions)
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the identity create request: POST /identity/v2/identities
    /// </summary>
    public static async Task<IResult> IdentityCreate(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> appSettings,
        IOptions<KestrelServerOptions> kestrelOptions
    )
    {
        string dmsPath = ExtractIdentityDmsPath(httpContext.Request);
        FrontendRequest frontendRequest = await FromIdentityRequest(
            httpContext.Request,
            BuildIdentityTemplatePath(httpContext.Request, string.Empty),
            appSettings,
            kestrelOptions,
            includeBody: true
        );

        return ToResult(
            await apiService.IdentityCreate(frontendRequest, httpContext.RequestAborted),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the identity get-by-id request: GET /identity/v2/identities/{id}.
    /// Also reached by GET /identity/v2/identities/results with id = "results", since that path has
    /// no further segment for the results-polling route to match.
    /// </summary>
    public static async Task<IResult> IdentityGetById(
        HttpContext httpContext,
        IApiService apiService,
        string id,
        IOptions<AppSettings> appSettings,
        IOptions<KestrelServerOptions> kestrelOptions
    )
    {
        string dmsPath = ExtractIdentityDmsPath(httpContext.Request);
        FrontendRequest frontendRequest = await FromIdentityRequest(
            httpContext.Request,
            BuildIdentityTemplatePath(httpContext.Request, "/{id}"),
            appSettings,
            kestrelOptions,
            includeBody: false
        );

        return ToResult(
            await apiService.IdentityGetById(frontendRequest, id, httpContext.RequestAborted),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the identity find request: POST /identity/v2/identities/find
    /// </summary>
    public static async Task<IResult> IdentityFind(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> appSettings,
        IOptions<KestrelServerOptions> kestrelOptions
    )
    {
        string dmsPath = ExtractIdentityDmsPath(httpContext.Request);
        FrontendRequest frontendRequest = await FromIdentityRequest(
            httpContext.Request,
            BuildIdentityTemplatePath(httpContext.Request, "/find"),
            appSettings,
            kestrelOptions,
            includeBody: true
        );

        return ToResult(
            await apiService.IdentityFind(frontendRequest, httpContext.RequestAborted),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the identity search request: POST /identity/v2/identities/search
    /// </summary>
    public static async Task<IResult> IdentitySearch(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> appSettings,
        IOptions<KestrelServerOptions> kestrelOptions
    )
    {
        string dmsPath = ExtractIdentityDmsPath(httpContext.Request);
        FrontendRequest frontendRequest = await FromIdentityRequest(
            httpContext.Request,
            BuildIdentityTemplatePath(httpContext.Request, "/search"),
            appSettings,
            kestrelOptions,
            includeBody: true
        );

        return ToResult(
            await apiService.IdentitySearch(frontendRequest, httpContext.RequestAborted),
            httpContext,
            dmsPath
        );
    }

    /// <summary>
    /// ASP.NET Core entry point for the identity asynchronous job results poll request:
    /// GET /identity/v2/identities/results/{token}
    /// </summary>
    public static async Task<IResult> IdentityResults(
        HttpContext httpContext,
        IApiService apiService,
        string token,
        IOptions<AppSettings> appSettings,
        IOptions<KestrelServerOptions> kestrelOptions
    )
    {
        string dmsPath = ExtractIdentityDmsPath(httpContext.Request);
        FrontendRequest frontendRequest = await FromIdentityRequest(
            httpContext.Request,
            BuildIdentityTemplatePath(httpContext.Request, "/results/{token}"),
            appSettings,
            kestrelOptions,
            includeBody: false
        );

        return ToResult(
            await apiService.IdentityResults(frontendRequest, token, httpContext.RequestAborted),
            httpContext,
            dmsPath
        );
    }
}
