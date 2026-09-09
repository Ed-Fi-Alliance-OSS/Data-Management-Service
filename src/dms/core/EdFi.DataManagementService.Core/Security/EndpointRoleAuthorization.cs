// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

public enum EndpointRoleAuthorizationOutcome
{
    Authorized,
    Unauthorized,
    Forbidden,

    /// <summary>
    /// The endpoint's required role is missing or does not satisfy <see cref="EndpointRequiredRole"/>.
    /// Callers map this to their own forbidden message, because the operator-facing wording names the
    /// specific endpoint whose role is unusable. Defensive only: an endpoint whose role is unusable is
    /// not mapped, so no request should reach this outcome.
    /// </summary>
    RequiredRoleNotConfigured,
}

public sealed record EndpointRoleAuthorizationResult(
    EndpointRoleAuthorizationOutcome Outcome,
    string? Message
)
{
    public bool IsAuthorized => Outcome == EndpointRoleAuthorizationOutcome.Authorized;
}

/// <summary>
/// The bearer-to-role authorization sequence shared by every protected non-data endpoint: parse the
/// Authorization header, validate the token, then require one exact ordinal role claim under the
/// configured role claim type. There is deliberately no fallback to <c>JwtAuthentication:ClientRole</c>
/// and no fallback to the legacy <c>role</c>, <c>roles</c>, or <see cref="ClaimTypes.Role"/> claim types.
/// </summary>
/// <remarks>
/// Static rather than injected so each caller supplies its own <see cref="ILogger"/>. That keeps every
/// log record attributed to the calling service's category, which existing callers' tests assert.
/// </remarks>
internal static class EndpointRoleAuthorizer
{
    public const string MissingAuthorizationHeaderMessage = "Authorization header is missing.";
    public const string InvalidTokenMessage = "Invalid token";
    public const string InsufficientPermissionsMessage = "Insufficient permissions";

    public static async Task<EndpointRoleAuthorizationResult> AuthorizeAsync(
        IJwtValidationService jwtValidationService,
        ILogger logger,
        string endpointName,
        string? authorizationHeader,
        string? requiredRole,
        string roleClaimType,
        CancellationToken cancellationToken
    )
    {
        if (authorizationHeader is null)
        {
            logger.LogDebug(
                "{Endpoint} authorization failed: missing Authorization header",
                endpointName
            );
            return new(
                EndpointRoleAuthorizationOutcome.Unauthorized,
                MissingAuthorizationHeaderMessage
            );
        }

        AuthorizationHeaderResult headerResult = AuthorizationHeaderParser.Parse(authorizationHeader);
        if (!headerResult.IsValid)
        {
            logger.LogDebug(
                "{Endpoint} authorization failed: {ErrorDetail}",
                endpointName,
                headerResult.ErrorDetail
            );
            return new(EndpointRoleAuthorizationOutcome.Unauthorized, headerResult.ErrorDetail!);
        }

        var (principal, _) = await jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
            headerResult.Token!,
            cancellationToken
        );

        if (principal is null)
        {
            logger.LogWarning(
                "{Endpoint} authorization failed: token validation failed",
                endpointName
            );
            return new(EndpointRoleAuthorizationOutcome.Unauthorized, InvalidTokenMessage);
        }

        if (!EndpointRequiredRole.IsValid(requiredRole))
        {
            logger.LogWarning(
                "{Endpoint} authorization failed: RequiredRole is not valid",
                endpointName
            );
            return new(EndpointRoleAuthorizationOutcome.RequiredRoleNotConfigured, null);
        }

        if (!HasExactRequiredRoleClaim(principal, roleClaimType, requiredRole))
        {
            logger.LogWarning(
                "{Endpoint} authorization failed: token missing exact required role claim under configured claim type {RoleClaimType}",
                endpointName,
                LoggingSanitizer.SanitizeForLogging(roleClaimType)
            );
            return new(EndpointRoleAuthorizationOutcome.Forbidden, InsufficientPermissionsMessage);
        }

        logger.LogDebug("{Endpoint} authorization succeeded", endpointName);
        return new(EndpointRoleAuthorizationOutcome.Authorized, null);
    }

    private static bool HasExactRequiredRoleClaim(
        ClaimsPrincipal principal,
        string roleClaimType,
        string requiredRole
    ) =>
        principal.Claims.Any(claim =>
            string.Equals(claim.Type, roleClaimType, StringComparison.Ordinal)
            && string.Equals(claim.Value, requiredRole, StringComparison.Ordinal)
        );
}
