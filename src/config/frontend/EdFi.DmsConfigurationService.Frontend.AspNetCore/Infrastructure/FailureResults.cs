// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

internal static class FailureResults
{
    private static readonly string _errorDetail =
        "The request could not be processed. See 'errors' for details.";
    private static readonly string _errorContentType = "application/problem+json";
    private static readonly JsonSerializerOptions _relaxedSerializer = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes a Problem Details response body directly to the HTTP response. Intended for middleware
    /// that runs before the endpoint/DI plumbing an IResult depends on: IResult.ExecuteAsync resolves
    /// services (ILoggerFactory, JSON options) from HttpContext.RequestServices, which may be
    /// unavailable in early middleware. Endpoint handlers should return the IResult helpers instead.
    /// </summary>
    public static Task WriteAsync(HttpContext httpContext, JsonNode body, int statusCode)
    {
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = _errorContentType;
        return httpContext.Response.WriteAsync(JsonSerializer.Serialize(body, _relaxedSerializer));
    }

    public static IResult Unknown(string correlationId)
    {
        return Results.Json(
            FailureResponse.ForUnknown(correlationId),
            contentType: _errorContentType,
            statusCode: 500
        );
    }

    public static IResult NotFound(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForNotFound(detail, correlationId),
            contentType: _errorContentType,
            statusCode: 404
        );
    }

    public static IResult BadRequest(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForBadRequest(detail, correlationId),
            contentType: _errorContentType,
            statusCode: 400
        );
    }

    public static IResult MethodNotAllowed(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForMethodNotAllowed(detail, correlationId),
            contentType: _errorContentType,
            statusCode: 405
        );
    }

    public static IResult UnsupportedMediaType(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForUnsupportedMediaType(detail, correlationId),
            contentType: _errorContentType,
            statusCode: 415
        );
    }

    public static IResult DataValidation(
        IEnumerable<ValidationFailure> validationFailures,
        string correlationId
    )
    {
        return Results.Json(
            FailureResponse.ForDataValidation(validationFailures, correlationId),
            contentType: _errorContentType,
            statusCode: 400
        );
    }

    public static IResult BadGateway(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail);
        return Results.Json(
            FailureResponse.ForBadGateway(_errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 502
        );
    }

    public static IResult InvalidClient(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail, "invalid_client");
        return Results.Json(
            FailureResponse.ForUnauthorized("Authentication Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 401
        );
    }

    public static IResult Unauthorized(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail, "unauthorized_client");
        return Results.Json(
            FailureResponse.ForUnauthorized("Authentication Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 401
        );
    }

    // Authentication failure with caller-supplied error messages. Unlike the identity-provider
    // overload above, the errors are used verbatim (no prefixing or JSON parsing), so framework
    // authentication challenges can surface a clean, specific message.
    public static IResult Unauthorized(string[] errors, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForUnauthorized("Authentication Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 401
        );
    }

    public static IResult Forbidden(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail, "Forbidden");
        return Results.Json(
            FailureResponse.ForForbidden("Authorization Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 403
        );
    }

    // Authorization failure with caller-supplied error messages. Unlike the identity-provider
    // overload above, the errors are used verbatim (no "Forbidden. " prefix or JSON parsing),
    // so endpoints and authorization handling can surface a clean, specific message.
    public static IResult Forbidden(string[] errors, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForForbidden("Authorization Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 403
        );
    }

    // Attempts to read `{ "error": "...", "error_description": "..."}` from the response
    // body, with sensible fallback mechanism if the response is in a different format.
    private static string[]? GetIdentityErrorDetails(string detail, string title = "")
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        string error = title;
        string errorDescription = detail;

        try
        {
            if (JsonNode.Parse(detail) is JsonNode parsed && parsed is JsonObject obj)
            {
                error = obj["error"]?.ToString() ?? error;
                errorDescription = obj["error_description"]?.ToString() ?? errorDescription;
            }
        }
        catch
        {
            // Ignoring parsing errors, returning the default formatted message.
        }
        error = !string.IsNullOrEmpty(error) ? $"{error}. " : "";
        return [$"{error}{errorDescription}"];
    }
}
