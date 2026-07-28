// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;
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
            BadHttpRequestException badHttpRequest => MapBadHttpRequest(badHttpRequest, httpContext),
            // Must be matched before the generic FluentValidation.ValidationException arm below, since
            // ParameterValidationException derives from it.
            ParameterValidationException parameterValidationException =>
                FailureResponse.ForParameterValidation(
                    parameterValidationException.Errors.Select(e => e.ErrorMessage).ToArray(),
                    traceId
                ),
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
    /// Sub-classifies a framework <see cref="BadHttpRequestException"/> by status code and, for 400,
    /// by <see cref="Exception.InnerException"/> and the original endpoint's <see cref="IAcceptsMetadata"/>
    /// — never by message text.
    /// </summary>
    private static JsonNode MapBadHttpRequest(BadHttpRequestException exception, HttpContext httpContext)
    {
        string traceId = httpContext.TraceIdentifier;
        return exception.StatusCode switch
        {
            StatusCodes.Status400BadRequest => MapBadRequest(exception, httpContext, traceId),
            StatusCodes.Status415UnsupportedMediaType => FailureResponse.ForUnsupportedMediaType(traceId),
            _ => FailureResponse.ForUnclassifiedStatus(
                exception.StatusCode,
                ReasonPhrases.GetReasonPhrase(exception.StatusCode),
                traceId
            ),
        };
    }

    private static JsonNode MapBadRequest(
        BadHttpRequestException exception,
        HttpContext httpContext,
        string traceId
    )
    {
        if (exception.InnerException is JsonException)
        {
            return FailureResponse.ForDataValidation(
                [new ValidationFailure("$", "The request body contains invalid JSON.")],
                traceId
            );
        }

        // Exception handling clears the active endpoint but preserves the original endpoint on this feature.
        bool endpointAcceptsBody =
            httpContext
                .Features.Get<IExceptionHandlerFeature>()
                ?.Endpoint?.Metadata.GetMetadata<IAcceptsMetadata>()
            is not null;
        if (endpointAcceptsBody)
        {
            return FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                traceId,
                ["A non-empty request body is required."]
            );
        }

        return FailureResponse.ForParameterValidation(
            ["The request contains one or more invalid parameters."],
            traceId
        );
    }
}
