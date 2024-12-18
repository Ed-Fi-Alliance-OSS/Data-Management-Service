// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

internal static class FailureResults
{
    private static readonly string _errorDetail =
        "The request could not be processed. See 'errors' for details.";
    private static readonly string _errorContentType = "application/problem+json";

    public static IResult Unknown(string correlationId)
    {
        return Results.Json(FailureResponse.ForUnknown(correlationId), statusCode: 500);
    }

    public static IResult NotFound(string detail, string correlationId)
    {
        return Results.Json(FailureResponse.ForNotFound(detail, correlationId), statusCode: 404);
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

    public static IResult Unauthorized(string detail, string correlationId)
    {
        var errors = GetIdentityErrorDetails(detail, "Unauthorized");
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
