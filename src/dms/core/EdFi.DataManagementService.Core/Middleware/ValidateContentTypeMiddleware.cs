// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates the request Content-Type for baseline data resource write requests (POST/PUT).
/// An explicit, unsupported media type is rejected with 415 before the body is parsed, matching
/// ODS/API behavior. Baseline JSON (application/json, text/json) is accepted, and Ed-Fi profile
/// media types (application/vnd.ed-fi.*) are deferred to ProfileResolutionMiddleware. A missing
/// Content-Type is not rejected here.
/// </summary>
internal class ValidateContentTypeMiddleware(ILogger _logger) : IPipelineStep
{
    private const string UnsupportedMediaTypeMessage =
        "The value specified in the 'Content-Type' header is not supported by this host.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateContentTypeMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        if (IsSupportedWriteContentType(GetContentType(requestInfo.FrontendRequest)))
        {
            await next();
            return;
        }

        _logger.LogDebug(
            "Rejecting unsupported write Content-Type - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
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
    /// A baseline write Content-Type is supported when it is absent (not an explicit value),
    /// standard JSON, or an Ed-Fi profile media type (validated later by ProfileResolutionMiddleware).
    /// An explicit value that cannot be parsed, or that resolves to any other media type, is unsupported.
    /// </summary>
    private static bool IsSupportedWriteContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
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

        return IsBaselineJson(mediaType.MediaType) || IsEdFiProfileMediaType(mediaType.MediaType);
    }

    private static bool IsBaselineJson(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsEdFiProfileMediaType(string mediaType) =>
        mediaType.StartsWith("application/vnd.ed-fi.", StringComparison.OrdinalIgnoreCase);
}
