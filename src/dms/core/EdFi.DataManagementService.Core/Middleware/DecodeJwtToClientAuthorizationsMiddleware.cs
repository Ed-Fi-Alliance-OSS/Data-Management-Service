// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that decodes JWT tokens and extracts client authorization details
/// </summary>
internal class DecodeJwtToClientAuthorizationsMiddleware(
    ILogger<DecodeJwtToClientAuthorizationsMiddleware> logger,
    IJwtTokenValidator jwtTokenValidator,
    IApiClientDetailsProvider apiClientDetailsProvider,
    IOptions<IdentitySettings> identitySettings
) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DecodeJwtToClientAuthorizationsMiddleware - {TraceId}",
            requestData.FrontendRequest.TraceId.Value
        );

        // Extract Authorization header
        if (!requestData.FrontendRequest.Headers.TryGetValue("Authorization", out var authHeader))
        {
            RespondUnauthorized(requestData, "Missing Authorization header");
            return;
        }

        // Validate Bearer format
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RespondUnauthorized(requestData, "Invalid Authorization header format");
            return;
        }

        string token = authHeader["Bearer ".Length..];

        // Validate JWT
        JwtValidationResult validationResult;
        try
        {
            validationResult = await jwtTokenValidator.ValidateTokenAsync(token, identitySettings.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during JWT token validation");
            RespondUnauthorized(requestData, "Token validation error");
            return;
        }

        if (!validationResult.IsValid)
        {
            RespondUnauthorized(requestData, validationResult.ErrorMessage);
            return;
        }

        // Validate required role claim
        if (!HasRequiredRole(validationResult.Claims, identitySettings.Value))
        {
            RespondUnauthorized(requestData, "Insufficient permissions");
            return;
        }

        // Extract client authorizations from claims
        var tokenHash = token.GetHashCode().ToString();
        var clientAuthorizations = apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
            tokenHash,
            validationResult.Claims
        );

        // Store in RequestData
        requestData.ClientAuthorizations = clientAuthorizations;

        await next();
    }

    private static bool HasRequiredRole(List<Claim> claims, IdentitySettings settings)
    {
        return claims.Exists(c => c.Type == settings.RoleClaimType && c.Value == settings.ClientRole);
    }

    private static void RespondUnauthorized(RequestData requestData, string error)
    {
        requestData.FrontendResponse = new FrontendResponse(
            StatusCode: 401,
            Body: new JsonObject { ["error"] = error },
            Headers: [],
            ContentType: MediaTypeNames.Application.Json
        );
    }
}
