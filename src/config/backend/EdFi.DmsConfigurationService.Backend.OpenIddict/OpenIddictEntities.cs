// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict;

/// <summary>
/// Represents an OpenIddict application entity for database storage.
/// This entity is compatible with OpenIddict's application store interface.
/// </summary>
public class OpenIddictApplication
{
    /// <summary>
    /// Gets or sets the unique identifier of the application.
    /// </summary>
    public string? Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the client identifier associated with the application.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret associated with the application.
    /// Note: In production, this should be properly hashed.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the display name of the application.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the redirect URIs (stored as JSON array).
    /// </summary>
    public string? RedirectUris { get; set; }

    /// <summary>
    /// Gets or sets the post logout redirect URIs (stored as JSON array).
    /// </summary>
    public string? PostLogoutRedirectUris { get; set; }

    /// <summary>
    /// Gets or sets the permissions (stored as JSON array).
    /// </summary>
    public string? Permissions { get; set; }

    /// <summary>
    /// Gets or sets the requirements (stored as JSON array).
    /// </summary>
    public string? Requirements { get; set; }

    /// <summary>
    /// Gets or sets custom namespace claim for Ed-Fi specific functionality.
    /// </summary>
    public string? NamespaceClaim { get; set; }

    /// <summary>
    /// Gets or sets the creation date of the application.
    /// </summary>
    public DateTimeOffset CreationDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets additional properties (stored as JSON).
    /// </summary>
    public string? Properties { get; set; }
}

/// <summary>
/// Represents an OpenIddict token entity for database storage.
/// This entity is compatible with OpenIddict's token store interface.
/// </summary>
public class OpenIddictToken
{
    /// <summary>
    /// Gets or sets the unique identifier of the token.
    /// </summary>
    public string? Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the identifier of the application associated with the token.
    /// </summary>
    public string? ApplicationId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the authorization associated with the token.
    /// </summary>
    public string? AuthorizationId { get; set; }

    /// <summary>
    /// Gets or sets the creation date of the token.
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the expiration date of the token.
    /// </summary>
    public DateTimeOffset? ExpirationDate { get; set; }

    /// <summary>
    /// Gets or sets the payload of the token (e.g., JWT content).
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// Gets or sets additional properties of the token (stored as JSON).
    /// </summary>
    public string? Properties { get; set; }

    /// <summary>
    /// Gets or sets the redemption date of the token.
    /// </summary>
    public DateTimeOffset? RedemptionDate { get; set; }

    /// <summary>
    /// Gets or sets the reference identifier of the token.
    /// </summary>
    public string? ReferenceId { get; set; }

    /// <summary>
    /// Gets or sets the status of the token (e.g., valid, revoked, expired).
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the subject (user identifier) associated with the token.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets or sets the type of the token (e.g., access_token, refresh_token).
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// Represents an OpenIddict authorization entity for database storage.
/// This entity is compatible with OpenIddict's authorization store interface.
/// </summary>
public class OpenIddictAuthorization
{
    /// <summary>
    /// Gets or sets the unique identifier of the authorization.
    /// </summary>
    public string? Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the identifier of the application associated with the authorization.
    /// </summary>
    public string? ApplicationId { get; set; }

    /// <summary>
    /// Gets or sets the creation date of the authorization.
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets additional properties of the authorization (stored as JSON).
    /// </summary>
    public string? Properties { get; set; }

    /// <summary>
    /// Gets or sets the scopes associated with the authorization (stored as JSON array).
    /// </summary>
    public string? Scopes { get; set; }

    /// <summary>
    /// Gets or sets the status of the authorization (e.g., valid, revoked).
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the subject (user identifier) associated with the authorization.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets or sets the type of the authorization.
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// Represents an OpenIddict scope entity for database storage.
/// This entity is compatible with OpenIddict's scope store interface.
/// </summary>
public class OpenIddictScope
{
    /// <summary>
    /// Gets or sets the unique identifier of the scope.
    /// </summary>
    public string? Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the description of the scope.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the display name of the scope.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the display names in different cultures (stored as JSON).
    /// </summary>
    public string? DisplayNames { get; set; }

    /// <summary>
    /// Gets or sets the name of the scope.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets additional properties of the scope (stored as JSON).
    /// </summary>
    public string? Properties { get; set; }

    /// <summary>
    /// Gets or sets the resources associated with the scope (stored as JSON array).
    /// </summary>
    public string? Resources { get; set; }
}
