// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware for JWT role-based authentication in the DMS Core pipeline.
/// This middleware validates JWT tokens and checks for required roles without extracting ClientAuthorizations.
///
/// The purpose of this middleware is to provide authentication for non-CRUD resource endpoints.
/// </summary>
internal class JwtRoleAuthenticationMiddleware(
    IJwtValidationService jwtValidationService,
    ILogger<JwtRoleAuthenticationMiddleware> logger,
    IOptions<JwtAuthenticationOptions> options
) : IPipelineStep
{
    private readonly IJwtValidationService _jwtValidationService = jwtValidationService;
    private readonly ILogger<JwtRoleAuthenticationMiddleware> _logger = logger;
    private readonly JwtAuthenticationOptions _options = options.Value;

    /// <summary>
    /// Executes JWT validation with optional role-based authorization checks.
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Extract Authorization header
        if (
            !requestInfo.FrontendRequest.Headers.TryGetValue("Authorization", out var authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
        {
            _logger.LogDebug(
                "Missing or invalid Authorization header - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = CreateUnauthorizedResponse(
                "Bearer token required",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        var token = authHeader["Bearer ".Length..];

        // Validate token (we only need the principal, not ClientAuthorizations)
        var (principal, _) = await _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
            token,
            CancellationToken.None
        );

        if (principal == null)
        {
            _logger.LogWarning(
                "Token validation failed - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = CreateUnauthorizedResponse(
                "Invalid token",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Check for required role if configured
        if (!string.IsNullOrEmpty(_options.ClientRole))
        {
            var hasRequiredRole = principal.HasClaim(ClaimTypes.Role, _options.ClientRole);
            if (!hasRequiredRole)
            {
                _logger.LogWarning(
                    "Token missing required role: {RequiredRole} - {TraceId}",
                    _options.ClientRole,
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = CreateForbiddenResponse(
                    "Insufficient permissions",
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            }
        }

        _logger.LogDebug(
            "JWT role authentication successful - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        await next();
    }

    /// <summary>
    /// Creates a standardized 401 Unauthorized response with problem details format.
    /// </summary>
    private static FrontendResponse CreateUnauthorizedResponse(string errorDetail, TraceId traceId)
    {
        var problemDetails = new
        {
            detail = "Authorization failed",
            type = "urn:ed-fi:api:unauthorized",
            title = "Unauthorized",
            status = 401,
            correlationId = traceId.Value,
            errors = new[] { errorDetail },
        };

        return new FrontendResponse(
            StatusCode: 401,
            Body: JsonSerializer.Serialize(problemDetails),
            Headers: new Dictionary<string, string>
            {
                ["WWW-Authenticate"] = $"Bearer error=\"invalid_token\"",
            },
            LocationHeaderPath: null,
            ContentType: "application/problem+json"
        );
    }

    /// <summary>
    /// Creates a standardized 403 Forbidden response with problem details format.
    /// </summary>
    private static FrontendResponse CreateForbiddenResponse(string errorDetail, TraceId traceId)
    {
        var problemDetails = new
        {
            detail = "Authorization failed",
            type = "urn:ed-fi:api:forbidden",
            title = "Forbidden",
            status = 403,
            correlationId = traceId.Value,
            errors = new[] { errorDetail },
        };

        return new FrontendResponse(
            StatusCode: 403,
            Body: JsonSerializer.Serialize(problemDetails),
            Headers: [],
            LocationHeaderPath: null,
            ContentType: "application/problem+json"
        );
    }
}
