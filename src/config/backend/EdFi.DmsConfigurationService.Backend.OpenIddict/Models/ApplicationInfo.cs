// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents application information retrieved from the OpenIddict store.
/// </summary>
public class ApplicationInfo
{
    /// <summary>
    /// Unique identifier for the application (client) in OpenIddict.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The client ID used to identify the application during authentication and token requests.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The client secret used for confidential clients to authenticate with the OpenIddict server.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for the application, used for display purposes in management UIs.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// List of allowed redirect URIs for authorization code and implicit flows.
    /// After successful authentication, users are redirected to one of these URIs.
    /// </summary>
    public string[] RedirectUris { get; set; } = [];

    /// <summary>
    /// List of URIs to redirect users to after logout.
    /// </summary>
    public string[] PostLogoutRedirectUris { get; set; } = [];

    /// <summary>
    /// Permissions granted to the application, such as allowed grant types, endpoints, or scopes.
    /// Example: "token", "authorization", "refresh_token".
    /// </summary>
    public string[] Permissions { get; set; } = [];

    /// <summary>
    /// Special requirements for the application, such as PKCE or consent.
    /// Example: "require_pkce", "require_consent".
    /// </summary>
    public string[] Requirements { get; set; } = [];

    /// <summary>
    /// The type of application, such as "confidential" (server-side) or "public" (client-side).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the application was created in the OpenIddict store.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// List of scopes the application is allowed to request (e.g., "openid", "profile", "edfi_admin_api/full_access").
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// JSON string representing protocol mappers for custom claim mapping in tokens.
    /// Used to add or transform claims in issued tokens for this application.
    /// </summary>
    public string ProtocolMappers { get; set; } = string.Empty;
}
