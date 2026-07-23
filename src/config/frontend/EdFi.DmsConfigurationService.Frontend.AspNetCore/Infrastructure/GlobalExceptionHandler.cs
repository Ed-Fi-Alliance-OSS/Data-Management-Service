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
            case ParameterValidationException parameterValidation:
                // Query-parameter validation failure (e.g. limit=0, an unknown orderBy). Reported with the
                // Ed-Fi "Parameter Validation Failed" (urn:ed-fi:api:bad-request:parameter) contract rather
                // than the request-body "data" contract. Messages are already sanitized (value-free).
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        FailureResponse.ForParameterValidation(parameterValidation.Errors.ToArray(), traceId),
                        relaxedSerializer
                    ),
                    cancellationToken: cancellationToken
                );
                break;
            case BadHttpRequestException badHttpRequest:
                // Do not surface the framework message: it can echo raw route or body values back to the
                // caller. In Development, ASP.NET Core enables ThrowOnBadRequest, so framework binding
                // failures reach this handler rather than the empty response status code page path.
                // Preserve the exception's own status code and always emit a machine-readable Ed-Fi
                // contract with a fixed, generic detail so the response is never empty and the throwing
                // and non-throwing paths behave the same in every environment. A 415 uses its specific
                // Ed-Fi type. Every other framework status uses the generic bad request type with its
                // status preserved, because the Ed-Fi Error Response Knowledge Base defines no type for a
                // 413 or for other framework request errors.
                switch (badHttpRequest.StatusCode)
                {
                    case (int)HttpStatusCode.UnsupportedMediaType:
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
                        break;
                    case (int)HttpStatusCode.RequestEntityTooLarge:
                        response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                        await response.WriteAsync(
                            JsonSerializer.Serialize(
                                FailureResponse.ForBadRequest(
                                    "The request payload is too large.",
                                    traceId,
                                    (int)HttpStatusCode.RequestEntityTooLarge
                                ),
                                relaxedSerializer
                            ),
                            cancellationToken: cancellationToken
                        );
                        break;
                    default:
                        response.StatusCode = badHttpRequest.StatusCode;
                        await response.WriteAsync(
                            JsonSerializer.Serialize(
                                FailureResponse.ForBadRequest(
                                    "The request was invalid.",
                                    traceId,
                                    badHttpRequest.StatusCode
                                ),
                                relaxedSerializer
                            ),
                            cancellationToken: cancellationToken
                        );
                        break;
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
