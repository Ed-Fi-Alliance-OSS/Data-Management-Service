// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        return Results.Json(FailureResponse.ForBadGateway(detail, correlationId), statusCode: 502);
    }

    public static IResult Unauthorized(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForUnauthorized("Authentication Failed", detail, correlationId),
            statusCode: 401
        );
    }

    public static IResult Forbidden(string detail, string correlationId)
    {
        return Results.Json(
            FailureResponse.ForForbidden("Authorization Failed", detail, correlationId),
            statusCode: 403
        );
    }
}
