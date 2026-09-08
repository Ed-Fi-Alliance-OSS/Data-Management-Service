// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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

    /// <summary>
    /// Structured 403 authorization failure with an explicit, caller-supplied <paramref name="errors"/>
    /// array. Unlike <see cref="Forbidden"/>, this does NOT parse the input as an identity-provider
    /// payload; it is intended for endpoint-owned authorization messages (for example, a disabled
    /// feature such as client registration). Emits the documented
    /// <c>urn:ed-fi:api:security:authorization</c> contract.
    /// </summary>
    public static IResult Authorization(string correlationId, string[] errors)
    {
        return Results.Json(
            FailureResponse.ForForbidden("Authorization Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 403
        );
    }

    /// <summary>
    /// Structured 400 <c>urn:ed-fi:api:bad-request</c> response for a generic, non-field-specific
    /// client error. The <paramref name="detail"/> must contain no sensitive or internal text.
    /// </summary>
    public static IResult BadRequest(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForBadRequest(detail, correlationId),
            contentType: _errorContentType,
            statusCode: 400
        );
    }

    /// <summary>
    /// Structured 400 <c>urn:ed-fi:api:bad-request:data</c> data-validation response whose
    /// <c>validationErrors</c> are grouped from the supplied field-level failures.
    /// </summary>
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

    public static IResult Authentication(string error, string errorDescription, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForUnauthorized(
                "Authentication Failed",
                _errorDetail,
                correlationId,
                [$"{error}. {errorDescription}"]
            ),
            contentType: _errorContentType,
            statusCode: 401
        );
    }

    // invalid_client and unauthorized_client both map to the same 401 contract.
    public static IResult InvalidClient(string detail, string correlationId) =>
        Unauthorized(detail, correlationId);

    public static IResult Unauthorized(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail);
        return Results.Json(
            FailureResponse.ForUnauthorized("Authentication Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 401
        );
    }

    public static IResult Forbidden(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail);
        return Results.Json(
            FailureResponse.ForForbidden("Authorization Failed", _errorDetail, correlationId, errors),
            contentType: _errorContentType,
            statusCode: 403
        );
    }

    private const string UnexpectedProviderResponseMessage =
        "The identity provider returned an unexpected response.";

    // Only complete structured provider errors pass through; all other input uses a fixed fallback.
    private static string[] GetIdentityErrorDetails(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return [UnexpectedProviderResponseMessage];
        }

        if (!TryParseProviderError(detail, out string error, out string errorDescription))
        {
            return [UnexpectedProviderResponseMessage];
        }

        return [$"{error}. {errorDescription}"];
    }

    private static bool TryParseProviderError(string detail, out string error, out string errorDescription)
    {
        error = "";
        errorDescription = "";

        JsonObject obj;
        try
        {
            if (JsonNode.Parse(detail) is not JsonObject parsedObject)
            {
                return false;
            }
            obj = parsedObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (
            obj["error"] is not JsonValue errorValue
            || obj["error_description"] is not JsonValue descriptionValue
            || errorValue.GetValueKind() != JsonValueKind.String
            || descriptionValue.GetValueKind() != JsonValueKind.String
        )
        {
            return false;
        }

        string errorText = errorValue.GetValue<string>();
        string errorDescriptionText = descriptionValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(errorText) || string.IsNullOrWhiteSpace(errorDescriptionText))
        {
            return false;
        }

        error = errorText;
        errorDescription = errorDescriptionText;
        return true;
    }
}
