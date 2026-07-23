// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

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

        // Switch order follows the approved decision tree: query-parameter validation, then request-body
        // (FluentValidation) validation, then framework binding/deserialization failures, then the generic
        // fallback. The exception types are disjoint, but the order documents intent.
        switch (exception)
        {
            case ParameterValidationException parameterValidation:
                // Semantically-invalid query parameters (e.g. limit=0, an unknown orderBy). Reported with
                // the Ed-Fi "Parameter Validation Failed" (urn:ed-fi:api:bad-request:parameter) contract.
                // Messages are already sanitized (value-free).
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteAsync(
                    response,
                    FailureResponse.ForParameterValidation(parameterValidation.Errors.ToArray(), traceId),
                    relaxedSerializer,
                    cancellationToken
                );
                break;
            case FluentValidation.ValidationException validationException:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteAsync(
                    response,
                    FailureResponse.ForDataValidation(validationException.Errors, traceId),
                    relaxedSerializer,
                    cancellationToken
                );
                break;
            case BadHttpRequestException badHttpRequest:
                await HandleBadHttpRequestAsync(
                    httpContext,
                    badHttpRequest,
                    traceId,
                    relaxedSerializer,
                    cancellationToken
                );
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteAsync(
                    response,
                    FailureResponse.ForUnknown(traceId),
                    relaxedSerializer,
                    cancellationToken
                );
                break;
        }
        return true;
    }

    // Classifies a framework request/binding failure. Do not surface the framework message: it can echo raw
    // route or body values back to the caller. With ThrowOnBadRequest forced on (Program.cs), these reach
    // this handler in every environment rather than the bodyless status-code-pages path.
    private static async Task HandleBadHttpRequestAsync(
        HttpContext httpContext,
        BadHttpRequestException badHttpRequest,
        string traceId,
        JsonSerializerOptions serializer,
        CancellationToken cancellationToken
    )
    {
        var response = httpContext.Response;

        switch (badHttpRequest.StatusCode)
        {
            case (int)HttpStatusCode.UnsupportedMediaType:
                response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                await WriteAsync(
                    response,
                    FailureResponse.ForUnsupportedMediaType(
                        "The request content type is not supported.",
                        traceId
                    ),
                    serializer,
                    cancellationToken
                );
                return;
            case (int)HttpStatusCode.RequestEntityTooLarge:
                response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                await WriteAsync(
                    response,
                    FailureResponse.ForBadRequest(
                        "The request payload is too large.",
                        traceId,
                        (int)HttpStatusCode.RequestEntityTooLarge
                    ),
                    serializer,
                    cancellationToken
                );
                return;
        }

        // A malformed JSON request body surfaces as a JsonException inner exception. Report it as the Ed-Fi
        // "Data Validation Failed" (urn:ed-fi:api:bad-request:data) contract with a best-effort JSON path
        // and a fixed, sanitized message; the raw parser text is never surfaced. Note: an empty chunked
        // body and a declared-charset mismatch also surface as a JsonException with no stable discriminator,
        // so they receive the same sanitized contract (an approved implementation exception).
        if (badHttpRequest.InnerException is JsonException jsonException)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            var failure = new ValidationFailure(
                string.IsNullOrEmpty(jsonException.Path) ? "$" : jsonException.Path,
                "The request body contains invalid JSON."
            );
            await WriteAsync(
                response,
                FailureResponse.ForDataValidation([failure], traceId),
                serializer,
                cancellationToken
            );
            return;
        }

        // A query-parameter binding failure (e.g. offset=abc) on a list endpoint carrying
        // ParameterValidationMetadata is reported as urn:ed-fi:api:bad-request:parameter. Classification uses
        // the endpoint metadata plus the raw query values (never the framework exception message).
        if (TryClassifyQueryParameterFailure(httpContext, out string[] parameterErrors))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteAsync(
                response,
                FailureResponse.ForParameterValidation(parameterErrors, traceId),
                serializer,
                cancellationToken
            );
            return;
        }

        // Every other framework request failure (invalid route binding, empty/missing body, other statuses)
        // uses the generic bad-request type with the framework status preserved and a fixed, generic detail.
        response.StatusCode = badHttpRequest.StatusCode;
        await WriteAsync(
            response,
            FailureResponse.ForBadRequest("The request was invalid.", traceId, badHttpRequest.StatusCode),
            serializer,
            cancellationToken
        );
    }

    // Returns true and the sanitized per-parameter messages when the matched endpoint declares typed query
    // parameters (via ParameterValidationMetadata) and a present query value fails its invariant parser.
    // The original matched endpoint is read from IExceptionHandlerFeature.Endpoint because the exception
    // middleware clears HttpContext.GetEndpoint() before this handler runs.
    private static bool TryClassifyQueryParameterFailure(HttpContext httpContext, out string[] errors)
    {
        errors = [];

        var endpoint = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        var metadata = endpoint?.Metadata.GetMetadata<ParameterValidationMetadata>();
        if (metadata is null)
        {
            return false;
        }

        var query = httpContext.Request.Query;
        var messages = new List<string>();
        foreach (var parameter in metadata.Parameters)
        {
            // Query keys are matched case-insensitively by IQueryCollection. A scalar minimal-API binder
            // receives the joined StringValues, so the parameter is a cause when that value does not parse.
            if (
                query.TryGetValue(parameter.WireName, out var values)
                && !parameter.IsBindable(values.ToString())
            )
            {
                messages.Add($"'{parameter.WireName}' must be an integer.");
            }
        }

        if (messages.Count == 0)
        {
            return false;
        }

        errors = messages.ToArray();
        return true;
    }

    private static Task WriteAsync(
        HttpResponse response,
        System.Text.Json.Nodes.JsonNode body,
        JsonSerializerOptions serializer,
        CancellationToken cancellationToken
    ) =>
        response.WriteAsync(JsonSerializer.Serialize(body, serializer), cancellationToken: cancellationToken);
}
