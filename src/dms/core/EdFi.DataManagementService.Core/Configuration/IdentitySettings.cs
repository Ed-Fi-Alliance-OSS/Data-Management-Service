// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Configuration settings for JWT token validation and identity provider integration
/// </summary>
public class IdentitySettings
{
    /// <summary>
    /// The authority URL of the identity provider (e.g., Keycloak)
    /// </summary>
    public required string Authority { get; set; }

    /// <summary>
    /// Whether to require HTTPS for metadata endpoints
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The expected audience for JWT tokens
    /// </summary>
    public required string Audience { get; set; }

    /// <summary>
    /// The claim type to use for role validation
    /// </summary>
    public required string RoleClaimType { get; set; }

    /// <summary>
    /// The required role for API client access
    /// </summary>
    public required string ClientRole { get; set; }
}
