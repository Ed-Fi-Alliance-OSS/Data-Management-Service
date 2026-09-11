// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Authorization configuration for the claimset management endpoints. The role is deliberately not
/// validated by an <c>IValidateOptions</c> implementation: an unusable role leaves those endpoints
/// unmapped, it must never fail service startup.
/// </summary>
public sealed class ManagementEndpointsOptions
{
    public const string SectionName = "AppSettings:ManagementEndpoints";

    /// <summary>
    /// The single literal role token a bearer must carry, under
    /// <c>JwtAuthentication:RoleClaimType</c>, to reach the claimset management endpoints. Empty by
    /// default, which leaves those endpoints unmapped.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonIgnoreAttribute"/> keeps the configured role out of any settings serialization.
    /// </remarks>
    [JsonIgnore]
    public string? RequiredRole { get; set; }

    public bool TryGetRequiredRoleForEndpointMapping(
        [NotNullWhen(true)] out string? requiredRoleForEndpointMapping
    )
    {
        if (EndpointRequiredRole.IsValid(RequiredRole))
        {
            requiredRoleForEndpointMapping = RequiredRole;
            return true;
        }

        requiredRoleForEndpointMapping = null;
        return false;
    }
}
