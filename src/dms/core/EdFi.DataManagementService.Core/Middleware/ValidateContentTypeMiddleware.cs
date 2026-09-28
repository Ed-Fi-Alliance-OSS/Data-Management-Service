// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// The set of Content-Type values a <see cref="ValidateContentTypeMiddleware"/> instance accepts.
/// </summary>
internal enum ContentTypePolicy
{
    /// <summary>
    /// The current baseline data resource write behavior: a missing header, standard JSON
    /// (application/json, text/json), or an Ed-Fi profile media type (application/vnd.ed-fi.*),
    /// with profile validation deferred to ProfileResolutionMiddleware.
    /// </summary>
    ResourceWrite,

    /// <summary>
    /// A missing header or standard JSON (application/json, text/json) only. Every other value,
    /// including Ed-Fi profile media types, is rejected with 415.
    /// </summary>
    BaselineJsonOnly,
}

/// <summary>
/// Validates the request Content-Type for an incoming request before its body is parsed,
/// rejecting an explicit, unsupported media type with 415, matching ODS/API behavior. A missing
/// Content-Type is never rejected here. Which media types beyond baseline JSON (application/json,
/// text/json) are supported depends on the configured <see cref="ContentTypePolicy"/>: under
/// <see cref="ContentTypePolicy.ResourceWrite"/>, used for data resource POST/PUT, an Ed-Fi profile
/// media type (application/vnd.ed-fi.*) is also accepted, with profile validation itself deferred
/// to ProfileResolutionMiddleware; under <see cref="ContentTypePolicy.BaselineJsonOnly"/>, every
/// other value - profile media types included - is rejected.
/// </summary>
internal class ValidateContentTypeMiddleware(
    ILogger _logger,
    ContentTypePolicy _policy = ContentTypePolicy.ResourceWrite
) : IPipelineStep
{
    private const string UnsupportedMediaTypeMessage =
        "The value specified in the 'Content-Type' header is not supported by this host.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateContentTypeMiddleware - {TraceId}",
            LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
        );

        if (IsSupportedContentType(GetContentType(requestInfo.FrontendRequest)))
        {
            await next();
            return;
        }

        _logger.LogDebug(
            "Rejecting unsupported Content-Type under policy {ContentTypePolicy} - {TraceId}",
            _policy,
            LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
        );

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 415,
            Body: ForUnsupportedMediaType(
                UnsupportedMediaTypeMessage,
                requestInfo.FrontendRequest.TraceId,
                [UnsupportedMediaTypeMessage]
            ),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }

    private static string? GetContentType(FrontendRequest frontendRequest) =>
        frontendRequest.Headers.TryGetValue("Content-Type", out string? value) ? value : null;

    /// <summary>
    /// A Content-Type is supported when the header is absent (null) or standard JSON. Under
    /// <see cref="ContentTypePolicy.ResourceWrite"/> an Ed-Fi profile media type is also supported
    /// (validated later by ProfileResolutionMiddleware); under
    /// <see cref="ContentTypePolicy.BaselineJsonOnly"/> it is not. Only a missing header is exempt;
    /// an explicit empty or whitespace value is malformed and, like any value that cannot be parsed
    /// or resolves to another media type, is unsupported.
    /// </summary>
    private bool IsSupportedContentType(string? contentType)
    {
        // Only a missing header is exempt. An explicit empty or whitespace value is malformed and
        // falls through to the parse check below, which rejects it before any body parsing.
        if (contentType is null)
        {
            return true;
        }

        if (
            !MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? mediaType)
            || string.IsNullOrEmpty(mediaType.MediaType)
        )
        {
            return false;
        }

        if (IsBaselineJson(mediaType.MediaType))
        {
            return true;
        }

        return _policy == ContentTypePolicy.ResourceWrite && IsEdFiProfileMediaType(mediaType.MediaType);
    }

    private static bool IsBaselineJson(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsEdFiProfileMediaType(string mediaType) =>
        mediaType.StartsWith("application/vnd.ed-fi.", StringComparison.OrdinalIgnoreCase);
}
