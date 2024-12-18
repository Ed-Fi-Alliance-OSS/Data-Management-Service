// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

internal static class FailureResults
{
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
        var (errorDetail, errors) = ConvertIdentityError(detail);
        return Results.Json(
            FailureResponse.ForBadGateway(errorDetail, correlationId, errors),
            statusCode: 502
        );
    }

    public static IResult Unauthorized(string detail, string correlationId)
    {
        var (errorDetail, errors) = ConvertIdentityError(detail);
        return Results.Json(
            FailureResponse.ForUnauthorized("Authentication Failed", errorDetail, correlationId, errors),
            statusCode: 401
        );
    }

    public static IResult Forbidden(string detail, string correlationId)
    {
        var (errorDetail, errors) = ConvertIdentityError(detail);
        return Results.Json(
            FailureResponse.ForForbidden("Authorization Failed", errorDetail, correlationId, errors),
            statusCode: 403
        );
    }

    private static (string, string[]?) ConvertIdentityError(string detail)
    {
        var errorDetail = detail;
        string[]? errors = null;

        if (IsIdentityErrorJson(detail))
        {
            var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(detail);
            if (errorResponse != null)
            {
                errorDetail = errorResponse.error;
                errors = [errorResponse.error_description];
            }
        }

        return (errorDetail, errors);
    }

    private static bool IsIdentityErrorJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("error", out _) && root.TryGetProperty("error_description", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public record ErrorResponse(string error, string error_description);
}
