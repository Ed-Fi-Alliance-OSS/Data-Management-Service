// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
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
    private const string InvalidRequiredRoleMessage = "DocumentCache status endpoint role is not configured.";

    private readonly JwtAuthenticationOptions _jwtAuthenticationOptions = jwtAuthenticationOptions.Value;

    public async Task<DocumentCacheStatusAuthorizationResult> AuthorizeAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken = default
    )
    {
        EndpointRoleAuthorizationResult result = await EndpointRoleAuthorizer.AuthorizeAsync(
            jwtValidationService,
            logger,
            "DocumentCache status",
            authorizationHeader,
            documentCacheOptions.Value.Status.RequiredRole,
            _jwtAuthenticationOptions.RoleClaimType,
            cancellationToken
        );

        return result.Outcome switch
        {
            EndpointRoleAuthorizationOutcome.Authorized =>
                DocumentCacheStatusAuthorizationResult.Authorized(),
            EndpointRoleAuthorizationOutcome.Unauthorized =>
                DocumentCacheStatusAuthorizationResult.Unauthorized(result.Message!),
            EndpointRoleAuthorizationOutcome.RequiredRoleNotConfigured =>
                DocumentCacheStatusAuthorizationResult.Forbidden(InvalidRequiredRoleMessage),
            _ => DocumentCacheStatusAuthorizationResult.Forbidden(result.Message!),
        };
    }
}
