// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Management;

/// <summary>
/// Authorizes a request to a claimset management endpoint against
/// <c>AppSettings:ManagementEndpoints:RequiredRole</c>.
/// </summary>
public interface IManagementEndpointAuthorizationService
{
    Task<EndpointRoleAuthorizationResult> AuthorizeAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken = default
    );
}

internal sealed class ManagementEndpointAuthorizationService(
    IJwtValidationService jwtValidationService,
    IOptions<ManagementEndpointsOptions> managementEndpointsOptions,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<ManagementEndpointAuthorizationService> logger
) : IManagementEndpointAuthorizationService
{
    private const string EndpointName = "Claimset management endpoint";

    public Task<EndpointRoleAuthorizationResult> AuthorizeAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken = default
    ) =>
        EndpointRoleAuthorizer.AuthorizeAsync(
            jwtValidationService,
            logger,
            EndpointName,
            authorizationHeader,
            managementEndpointsOptions.Value.RequiredRole,
            jwtAuthenticationOptions.Value.RoleClaimType,
            cancellationToken
        );
}
