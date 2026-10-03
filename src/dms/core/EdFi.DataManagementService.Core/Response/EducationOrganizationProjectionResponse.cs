// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// The failure responses the education-organization projection's target-resolution, schema and
/// mapping steps write.
/// </summary>
/// <remarks>
/// Every one is served as <c>application/problem+json</c>. The media type is part of the contract:
/// the Configuration Service reads a problem <c>type</c> only from that media type, so the
/// <see cref="FrontendResponse"/> default of <c>application/json</c> would hide the type it
/// classifies on.
/// </remarks>
internal static class EducationOrganizationProjectionResponse
{
    internal const string ProblemContentType = "application/problem+json";

    /// <summary>A projection-owned problem with its fixed literals and an empty <c>errors</c>.</summary>
    public static FrontendResponse For(EducationOrganizationProjectionProblem problem, TraceId traceId) =>
        new(
            StatusCode: problem.Status,
            Body: FailureResponse.ForEducationOrganizationProjection(problem, traceId),
            Headers: [],
            ContentType: ProblemContentType
        );

    /// <summary>
    /// The shared 503 <c>service-unavailable</c> body, for a tenant data-store catalog that could not
    /// be loaded from the Configuration Service.
    /// </summary>
    public static FrontendResponse ServiceUnavailable(TraceId traceId) =>
        new(
            StatusCode: 503,
            Body: FailureResponse.ForServiceUnavailable(traceId),
            Headers: [],
            ContentType: ProblemContentType
        );
}
