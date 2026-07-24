// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        // This handler only writes the error response. For 500s, RequestLoggingMiddleware logs the
        // handled exception as the canonical structured HttpRequestFailed event (via
        // IExceptionHandlerFeature), so logging here would double-count request errors. Exceptions
        // handled as 4xx are client errors: the request logs as a normal completion event and the
        // details (for validation) travel in the response body.
        var traceId = httpContext.TraceIdentifier;

        // Preserve the TraceId response header that clients and tests use for correlation.
        httpContext.Response.Headers["TraceId"] = traceId;

        JsonNode failure = exception switch
        {
            BadHttpRequestException badHttpRequest => MapBadHttpRequest(badHttpRequest, traceId),
            FluentValidation.ValidationException validationException => FailureResponse.ForDataValidation(
                validationException.Errors,
                traceId
            ),
            _ => FailureResponse.ForUnknown(traceId),
        };

        // The transport status is derived from the failure node, so the body and HTTP status can never
        // diverge; the writer also sets the problem-details content type and the correlationId.
        await FailureResponseWriter.WriteAsync(httpContext, failure, cancellationToken);
        return true;
    }

    /// <summary>
    /// Framework-thrown <see cref="BadHttpRequestException"/> carries a status code (400 for malformed
    /// input; 413 when Kestrel's request-body-size limit is exceeded). Documented statuses map to their
    /// taxonomy; any other reachable status maps to RFC 9457 <c>about:blank</c> (D-08). The exception
    /// message is never surfaced. (415 is primarily produced as a framework result and shaped by
    /// FrameworkErrorResponseMiddleware; it is mapped here defensively in case it arrives as a
    /// BadHttpRequestException.)
    /// </summary>
    private static JsonNode MapBadHttpRequest(BadHttpRequestException exception, string traceId) =>
        exception.StatusCode switch
        {
            StatusCodes.Status400BadRequest => FailureResponse.ForBadRequest(
                "The request was malformed or invalid.",
                traceId
            ),
            StatusCodes.Status415UnsupportedMediaType => FailureResponse.ForUnsupportedMediaType(traceId),
            _ => FailureResponse.ForUnclassifiedStatus(
                exception.StatusCode,
                ReasonPhrases.GetReasonPhrase(exception.StatusCode),
                traceId
            ),
        };
}
