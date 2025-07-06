// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware for JWT authentication in the DMS Core pipeline
/// </summary>
internal class JwtAuthenticationMiddleware : IPipelineStep
{
    private readonly IJwtValidationService _jwtValidationService;
    private readonly ILogger<JwtAuthenticationMiddleware> _logger;
    private readonly JwtAuthenticationOptions _options;

    public JwtAuthenticationMiddleware(
        IJwtValidationService jwtValidationService,
        ILogger<JwtAuthenticationMiddleware> logger,
        IOptions<JwtAuthenticationOptions> options
    )
    {
        _jwtValidationService = jwtValidationService;
        _logger = logger;
        _options = options.Value;
    }

    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        // Feature flag check for gradual rollout
        if (!_options.Enabled)
        {
            _logger.LogDebug("JWT authentication is disabled, skipping middleware");
            await next();
            return;
        }

        // Check if client is enabled for JWT authentication (gradual rollout)
        if (_options.EnabledForClients.Count > 0)
        {
            // Extract client ID from existing ClientAuthorizations if available
            var clientId =
                requestData.ClientAuthorizations == No.ClientAuthorizations
                    ? null
                    : requestData.ClientAuthorizations.TokenId;
            if (clientId == null || !_options.EnabledForClients.Contains(clientId))
            {
                _logger.LogDebug(
                    "JWT authentication not enabled for client: {ClientId}",
                    clientId ?? "unknown"
                );
                await next();
                return;
            }
        }

        // Extract Authorization header
        if (
            !requestData.FrontendRequest.Headers.TryGetValue("Authorization", out var authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
        {
            _logger.LogDebug(
                "Missing or invalid Authorization header - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );

            requestData.FrontendResponse = CreateUnauthorizedResponse(
                "Bearer token required",
                requestData.FrontendRequest.TraceId
            );
            return;
        }

        var token = authHeader.Substring("Bearer ".Length);

        // Validate token and extract client authorizations
        var (principal, clientAuthorizations) =
            await _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );

        if (principal == null || clientAuthorizations == null)
        {
            _logger.LogWarning(
                "Token validation failed - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );

            requestData.FrontendResponse = CreateUnauthorizedResponse(
                "Invalid token",
                requestData.FrontendRequest.TraceId
            );
            return;
        }

        // Update RequestData with client authorizations
        requestData.ClientAuthorizations = clientAuthorizations;

        _logger.LogDebug(
            "JWT authentication successful for TokenId: {TokenId} - {TraceId}",
            clientAuthorizations.TokenId,
            requestData.FrontendRequest.TraceId.Value
        );

        await next();
    }

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
}
