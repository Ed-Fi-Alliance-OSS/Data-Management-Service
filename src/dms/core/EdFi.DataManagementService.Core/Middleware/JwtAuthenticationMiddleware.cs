// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware for JWT authentication in the DMS Core pipeline
/// </summary>
internal class JwtAuthenticationMiddleware(
    IJwtValidationService jwtValidationService,
    ILogger<JwtAuthenticationMiddleware> logger
) : IPipelineStep
{
    /// <summary>
    /// Executes JWT authentication validation for incoming requests.
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Extract Authorization header
        if (!requestInfo.FrontendRequest.Headers.TryGetValue("Authorization", out var authHeader))
        {
            logger.LogDebug(
                "Missing Authorization header - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = CreateUnauthorizedResponse(
                "Authorization header is missing.",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        AuthorizationHeaderResult headerResult = AuthorizationHeaderParser.Parse(authHeader);
        if (!headerResult.IsValid)
        {
            logger.LogDebug(
                "Invalid Authorization header ({ErrorDetail}) - {TraceId}",
                headerResult.ErrorDetail,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = CreateUnauthorizedResponse(
                headerResult.ErrorDetail!,
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        string token = headerResult.Token!;

        // Validate token and extract client authorizations
        (ClaimsPrincipal? principal, ClientAuthorizations? clientAuthorizations) =
            await jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                token,
                CancellationToken.None
            );

        if (principal == null || clientAuthorizations == null)
        {
            logger.LogWarning(
                "Token validation failed - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = CreateUnauthorizedResponse(
                "Invalid token",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // Update RequestInfo with client authorizations
        requestInfo.ClientAuthorizations = clientAuthorizations;

        logger.LogDebug(
            "JWT authentication successful for TokenId: {TokenId} - {TraceId}",
            clientAuthorizations.TokenId,
            requestInfo.FrontendRequest.TraceId.Value
        );

        await next();
    }

    /// <summary>
    /// Creates a standardized 401 Unauthorized response with problem details format.
    /// </summary>
    private static FrontendResponse CreateUnauthorizedResponse(string errorDetail, TraceId traceId)
    {
        return new FrontendResponse(
            StatusCode: 401,
            Body: FailureResponse.ForAuthenticationFailure(traceId, [errorDetail]),
            Headers: new Dictionary<string, string>
            {
                ["WWW-Authenticate"] = $"Bearer error=\"invalid_token\"",
            },
            LocationHeaderPath: null,
            ContentType: "application/problem+json"
        );
    }
}
