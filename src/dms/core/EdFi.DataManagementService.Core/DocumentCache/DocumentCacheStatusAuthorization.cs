// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.DocumentCache;

public interface IDocumentCacheStatusAuthorizationService
{
    Task<DocumentCacheStatusAuthorizationResult> AuthorizeAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken = default
    );
}

public enum DocumentCacheStatusAuthorizationOutcome
{
    Authorized,
    Unauthorized,
    Forbidden,
}

public sealed record DocumentCacheStatusAuthorizationResult(
    DocumentCacheStatusAuthorizationOutcome Outcome,
    string? Message
)
{
    public bool IsAuthorized => Outcome == DocumentCacheStatusAuthorizationOutcome.Authorized;

    public static DocumentCacheStatusAuthorizationResult Authorized() =>
        new(DocumentCacheStatusAuthorizationOutcome.Authorized, null);

    public static DocumentCacheStatusAuthorizationResult Unauthorized(string message) =>
        new(DocumentCacheStatusAuthorizationOutcome.Unauthorized, message);

    public static DocumentCacheStatusAuthorizationResult Forbidden(string message) =>
        new(DocumentCacheStatusAuthorizationOutcome.Forbidden, message);
}

internal sealed class DocumentCacheStatusAuthorizationService(
    IJwtValidationService jwtValidationService,
    IOptions<DocumentCacheOptions> documentCacheOptions,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<DocumentCacheStatusAuthorizationService> logger
) : IDocumentCacheStatusAuthorizationService
{
    private const string MissingAuthorizationHeaderMessage = "Authorization header is missing.";
    private const string InvalidTokenMessage = "Invalid token";
    private const string InsufficientPermissionsMessage = "Insufficient permissions";
    private const string InvalidRequiredRoleMessage = "DocumentCache status endpoint role is not configured.";

    private readonly JwtAuthenticationOptions _jwtAuthenticationOptions = jwtAuthenticationOptions.Value;

    public async Task<DocumentCacheStatusAuthorizationResult> AuthorizeAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken = default
    )
    {
        if (authorizationHeader is null)
        {
            logger.LogDebug("DocumentCache status authorization failed: missing Authorization header");
            return DocumentCacheStatusAuthorizationResult.Unauthorized(MissingAuthorizationHeaderMessage);
        }

        AuthorizationHeaderResult headerResult = AuthorizationHeaderParser.Parse(authorizationHeader);
        if (!headerResult.IsValid)
        {
            logger.LogDebug(
                "DocumentCache status authorization failed: {ErrorDetail}",
                headerResult.ErrorDetail
            );
            return DocumentCacheStatusAuthorizationResult.Unauthorized(headerResult.ErrorDetail!);
        }

        var (principal, _) = await jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
            headerResult.Token!,
            cancellationToken
        );

        if (principal is null)
        {
            logger.LogWarning("DocumentCache status authorization failed: token validation failed");
            return DocumentCacheStatusAuthorizationResult.Unauthorized(InvalidTokenMessage);
        }

        if (!documentCacheOptions.Value.Status.TryGetRequiredRoleForEndpointMapping(out string? requiredRole))
        {
            logger.LogWarning("DocumentCache status authorization failed: RequiredRole is not valid");
            return DocumentCacheStatusAuthorizationResult.Forbidden(InvalidRequiredRoleMessage);
        }

        if (!HasExactRequiredRoleClaim(principal, _jwtAuthenticationOptions.RoleClaimType, requiredRole))
        {
            logger.LogWarning(
                "DocumentCache status authorization failed: token missing exact required role claim"
            );
            return DocumentCacheStatusAuthorizationResult.Forbidden(InsufficientPermissionsMessage);
        }

        logger.LogDebug("DocumentCache status authorization succeeded");
        return DocumentCacheStatusAuthorizationResult.Authorized();
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
