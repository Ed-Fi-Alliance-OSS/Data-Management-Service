// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Middleware-oriented factory for complete typed <see cref="FrontendResponse" />
/// problem+json responses.
/// </summary>
/// <remarks>
/// Use this when middleware owns the entire response and must set both the body and
/// <c>application/problem+json</c> content type. This differs from the legacy
/// <see cref="Response.FailureResponse" /> helper, which only creates JSON error bodies
/// for callers that wrap them in their own <see cref="FrontendResponse" />.
/// </remarks>
internal static class ProblemDetailsResponse
{
    private static readonly string _typePrefix = "urn:ed-fi:api";

    /// <summary>503 - database instance not routed or connection string missing</summary>
    public static readonly string ServiceConfigurationError = $"{_typePrefix}:service-configuration-error";

    /// <summary>503 - dms.EffectiveSchema not found; database must be provisioned</summary>
    public static readonly string DatabaseNotProvisioned = $"{_typePrefix}:database-not-provisioned";

    /// <summary>503 - dms.EffectiveSchema exists but contains malformed fingerprint content</summary>
    public static readonly string DatabaseFingerprintValidationError =
        $"{_typePrefix}:database-fingerprint-validation-error";

    /// <summary>403 - client has no authorized database instances</summary>
    public static readonly string AuthorizationDenied = $"{_typePrefix}:authorization-denied";

    /// <summary>404 - route qualifiers do not match any DMS instance</summary>
    public static readonly string RouteResolutionError = $"{_typePrefix}:route-resolution-error";

    /// <summary>400 - ambiguous routing (multiple instances match) shares the legacy route-resolution type</summary>
    public static readonly string AmbiguousRouteResolution = RouteResolutionError;

    public static FrontendResponse Create(
        int statusCode,
        string type,
        string title,
        string errorDetail,
        TraceId traceId,
        bool includeValidationErrors = false
    )
    {
        return Create(statusCode, type, title, errorDetail, [errorDetail], traceId, includeValidationErrors);
    }

    public static FrontendResponse Create(
        int statusCode,
        string type,
        string title,
        string detail,
        string[] errors,
        TraceId traceId,
        bool includeValidationErrors = false
    )
    {
        JsonObject problemDetails = new()
        {
            ["detail"] = detail,
            ["type"] = type,
            ["title"] = title,
            ["status"] = statusCode,
            ["correlationId"] = traceId.Value,
            ["errors"] = JsonSerializer.SerializeToNode(errors),
        };

        if (includeValidationErrors)
        {
            problemDetails["validationErrors"] = JsonSerializer.SerializeToNode(
                new Dictionary<string, string[]>()
            );
        }

        return new FrontendResponse(
            StatusCode: statusCode,
            Body: problemDetails,
            Headers: [],
            LocationHeaderPath: null,
            ContentType: "application/problem+json"
        );
    }
}
