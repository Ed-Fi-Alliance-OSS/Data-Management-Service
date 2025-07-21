// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Configuration options for JWT authentication
/// </summary>
public class JwtAuthenticationOptions
{
    /// <summary>
    /// OIDC authority URL (e.g., https://keycloak.example.com/realms/edfi)
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Expected audience value in JWT tokens
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// OIDC metadata endpoint URL
    /// </summary>
    public string MetadataAddress { get; set; } = string.Empty;

    /// <summary>
    /// Whether to require HTTPS for metadata retrieval
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Claim type to use for role claims
    /// </summary>
    public string RoleClaimType { get; set; } = "role";

    /// <summary>
    /// Clock skew tolerance in seconds
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>
    /// How often to refresh OIDC metadata in minutes
    /// </summary>
    public int RefreshIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How often to automatically refresh OIDC metadata in hours
    /// </summary>
    public int AutomaticRefreshIntervalHours { get; set; } = 24;

    /// <summary>
    /// Required role claim value for non-data endpoints (e.g., "service")
    /// </summary>
    public string ClientRole { get; set; } = string.Empty;
}
