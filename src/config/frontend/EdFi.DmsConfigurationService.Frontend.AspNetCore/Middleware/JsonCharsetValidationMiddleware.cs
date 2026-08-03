// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Rejects a JSON request that declares an unsupported charset with the Ed-Fi 415 contract before
/// minimal-API body binding reads the body. The framework's JSON body reader throws an exception the
/// binding layer does not classify when the declared charset is not a known encoding, which would
/// otherwise surface as a sanitized 500. The check mirrors the framework's own media-type semantics —
/// <c>application/json</c> and structured <c>+json</c> suffixes, with UTF-8 assumed when no charset is
/// declared — so a request this middleware passes through can never fail binding on its charset alone.
///
/// Registered after authorization so authentication and authorization outcomes are unchanged, and
/// gated on an endpoint that declares a JSON body and a request that can carry one, so an absent body
/// keeps its existing missing-body 400 precedence.
/// </summary>
public class JsonCharsetValidationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        if (DeclaresUnsupportedJsonCharset(context))
        {
            await FailureResponseWriter.WriteAsync(
                context,
                FailureResponse.ForUnsupportedMediaType(context.TraceIdentifier),
                context.RequestAborted
            );
            return;
        }

        await _next(context);
    }

    private static bool DeclaresUnsupportedJsonCharset(HttpContext context)
    {
        // Only an endpoint that declares a JSON body runs the JSON body reader.
        IAcceptsMetadata? accepts = context.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>();
        if (accepts is null || !accepts.ContentTypes.Contains("application/json"))
        {
            return false;
        }

        // A request without a body never reaches the charset decode, and its missing-body 400
        // takes precedence over the charset. This is the same body-detection feature the
        // framework's body reader consults.
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? mediaType))
        {
            return false;
        }

        // The framework reads only application/json and structured +json suffixes as JSON
        // (text/json is not JSON). Anything else is rejected by content negotiation instead.
        bool isJson =
            mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase);
        if (!isJson)
        {
            return false;
        }

        StringSegment charset = mediaType.Charset;
        if (
            !charset.HasValue
            || charset.Length == 0
            || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        try
        {
            Encoding.GetEncoding(charset.Value!);
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // The declared charset is not a known encoding, so body binding would throw. The
            // charset value itself is never echoed into the response.
            return true;
        }
    }
}
