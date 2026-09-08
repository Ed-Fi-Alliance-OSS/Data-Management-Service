// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that tenant information is present and valid when multitenancy is enabled.
/// Returns 400 Bad Request if multitenancy is enabled but tenant is missing or invalid.
/// </summary>
internal partial class TenantValidationMiddleware(bool multiTenancyEnabled, ILogger logger) : IPipelineStep
{
    /// <summary>
    /// Maximum allowed length for a tenant identifier
    /// </summary>
    private const int MaxTenantLength = 256;

    /// <summary>
    /// Regex pattern for valid tenant identifiers: alphanumeric, hyphens, and underscores only
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidTenantPattern();

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering TenantValidationMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        if (!multiTenancyEnabled)
        {
            await next();
            return;
        }

        string? tenant = requestInfo.FrontendRequest.Tenant;

        // Check for missing tenant
        if (string.IsNullOrEmpty(tenant))
        {
            logger.LogWarning(
                "TenantValidationMiddleware: Multitenancy is enabled but tenant is missing from request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.BadRequest,
                Body: FailureResponse.ForBadRequest(
                    "The tenant identifier is required when multi-tenancy is enabled. "
                        + "Include the tenant as the first segment of the URL path.",
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors: [],
                    errors: ["Missing tenant identifier in URL path."]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        // Validate tenant format
        string? validationError = ValidateTenantFormat(tenant);
        if (validationError != null)
        {
            logger.LogWarning(
                "TenantValidationMiddleware: Invalid tenant format for tenant {Tenant} - {ValidationError} - {TraceId}",
                LoggingSanitizer.SanitizeForLogging(tenant),
                validationError,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.BadRequest,
                Body: FailureResponse.ForBadRequest(
                    "The tenant identifier has an invalid format.",
                    requestInfo.FrontendRequest.TraceId,
                    validationErrors: [],
                    errors: [validationError]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        await next();
    }

    /// <summary>
    /// Validates the tenant identifier format.
    /// Returns an error message if invalid, or null if valid.
    /// </summary>
    private static string? ValidateTenantFormat(string tenant)
    {
        if (tenant.Length > MaxTenantLength)
        {
            return $"Tenant identifier exceeds maximum length of {MaxTenantLength} characters.";
        }

        if (!ValidTenantPattern().IsMatch(tenant))
        {
            return "Tenant identifier must contain only alphanumeric characters, hyphens, and underscores.";
        }

        return null;
    }
}
