// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        // This handler only writes the error response. For 500s, RequestLoggingMiddleware
        // logs the handled exception as the canonical structured HttpRequestFailed event
        // (via IExceptionHandlerFeature), so logging here would double-count request errors.
        // Exceptions handled as 400s are client errors: the request logs as a normal
        // completion event and the error details are returned in the response body.
        var relaxedSerializer = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };

        var traceId = httpContext.TraceIdentifier;
        var response = httpContext.Response;
        response.ContentType = "application/problem+json";
        response.Headers["TraceId"] = traceId;

        switch (exception)
        {
            case BadHttpRequestException badHttpRequest:
                // Do not surface the framework message: it can echo raw route/body values back to the
                // caller. In Development ASP.NET Core enables ThrowOnBadRequest, so framework binding
                // failures reach this handler rather than the empty-body status-code-page path. Preserve
                // the exception's own status code (a content-type mismatch is 415, not 400) and emit the
                // matching Ed-Fi contract with a fixed, generic detail, so the throwing and non-throwing
                // paths produce the same safe response in every environment.
                if (badHttpRequest.StatusCode == (int)HttpStatusCode.UnsupportedMediaType)
                {
                    response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                    await response.WriteAsync(
                        JsonSerializer.Serialize(
                            FailureResponse.ForUnsupportedMediaType(
                                "The request content type is not supported.",
                                traceId
                            ),
                            relaxedSerializer
                        ),
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await response.WriteAsync(
                        JsonSerializer.Serialize(
                            FailureResponse.ForBadRequest("The request was invalid.", traceId),
                            relaxedSerializer
                        ),
                        cancellationToken: cancellationToken
                    );
                }
                break;
            case FluentValidation.ValidationException validationException:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForDataValidation(validationException.Errors, traceId),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await response.WriteAsync(
                    JsonSerializer.Serialize(FailureResponse.ForUnknown(traceId), relaxedSerializer),
                    cancellationToken: cancellationToken
                );
                break;
        }
        return true;
    }
}
