// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Shared helper for creating standardized problem+json error responses
/// used by pipeline middlewares.
/// </summary>
internal static class ProblemDetailsResponse
{
    public static FrontendResponse Create(int statusCode, string title, string errorDetail, TraceId traceId)
    {
        var problemDetails = new
        {
            detail = errorDetail,
            type = $"urn:ed-fi:api:{title.ToLower().Replace(" ", "-")}",
            title,
            status = statusCode,
            correlationId = traceId.Value,
            errors = new[] { errorDetail },
        };

        return new FrontendResponse(
            StatusCode: statusCode,
            Body: JsonSerializer.SerializeToNode(problemDetails),
            Headers: [],
            LocationHeaderPath: null,
            ContentType: "application/problem+json"
        );
    }
}
