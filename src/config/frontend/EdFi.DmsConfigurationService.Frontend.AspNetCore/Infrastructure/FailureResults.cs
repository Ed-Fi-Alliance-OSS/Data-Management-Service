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
    private static readonly string _authenticationFailedDetail = "The caller could not be authenticated.";
    private static readonly string _authorizationDeniedDetail =
        "Access to the resource could not be authorized.";

    // Canonical Ed-Fi authorization-denied detail (Ed-Fi Error Response Knowledge Base) reported when an
    // endpoint denies an operation, such as registration while it is disabled.
    private static readonly string _operationDeniedDetail =
        "Access to the requested data could not be authorized.";
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

    // The status defaults to 400 but is overridable so a preserved framework client-error status (e.g. a
    // 413 with no specific Ed-Fi type) can still return the generic bad-request body at its own status.
    public static IResult BadRequest(string detail, string correlationId, int status = 400)
    {
        return Results.Json(
            FailureResponse.ForBadRequest(detail, correlationId, status),
            contentType: _errorContentType,
            statusCode: status
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

    // A duplicate/non-unique identity conflict (409, urn:ed-fi:api:conflict:non-unique-identity). The
    // caller-supplied errors are used verbatim; validationErrors stays empty.
    public static IResult NonUniqueIdentity(string detail, string correlationId, string[]? errors = null)
    {
        return Results.Json(
            FailureResponse.ForNonUniqueIdentity(detail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 409
        );
    }

    // A reference to a resource that does not exist (409, urn:ed-fi:api:conflict:unresolved-reference).
    public static IResult UnresolvedReference(string detail, string correlationId, string[]? errors = null)
    {
        return Results.Json(
            FailureResponse.ForUnresolvedReference(detail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 409
        );
    }

    // The item is referenced by existing item(s) and cannot be acted on (409,
    // urn:ed-fi:api:conflict:dependent-item-exists).
    public static IResult DependentItemExists(string detail, string correlationId, string[]? errors = null)
    {
        return Results.Json(
            FailureResponse.ForDependentItemExists(detail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 409
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
    // authentication challenges can surface a clean, specific message. The detail is the canonical
    // Ed-Fi authentication detail; the scenario-specific reason is carried in the errors array.
    public static IResult Unauthorized(string[] errors, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForUnauthorized(
                "Authentication Failed",
                _authenticationFailedDetail,
                correlationId,
                errors
            ),
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

    // Authorization failure raised by the framework authorization middleware (an authenticated caller
    // whose access to a secured endpoint was denied), with caller-supplied error messages used verbatim
    // (no "Forbidden. " prefix or JSON parsing). Reports the "Authorization Denied" contract with the
    // resource-scoped detail. Endpoints that deny an operation should use AuthorizationFailed instead,
    // which reports the same title with the Knowledge Base "requested data" detail.
    public static IResult Forbidden(string[] errors, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForForbidden(
                "Authorization Denied",
                _authorizationDeniedDetail,
                correlationId,
                errors
            ),
            contentType: _errorContentType,
            statusCode: 403
        );
    }

    // Authorization failure raised by an endpoint that denies an operation (e.g. registration when it is
    // disabled), with caller-supplied error messages used verbatim. Reports the canonical Ed-Fi
    // "Authorization Denied" authorization contract (urn:ed-fi:api:security:authorization, HTTP 403) with
    // the Knowledge Base detail "Access to the requested data could not be authorized." The framework
    // authorization middleware's Forbidden overload above reports the same title with a resource-scoped
    // detail for an authenticated caller's denied access.
    public static IResult AuthorizationFailed(string[] errors, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForForbidden(
                "Authorization Denied",
                _operationDeniedDetail,
                correlationId,
                errors
            ),
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
