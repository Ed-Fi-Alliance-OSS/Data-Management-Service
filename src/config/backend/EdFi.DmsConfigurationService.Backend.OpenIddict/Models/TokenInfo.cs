// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents token information retrieved from the OpenIddict store.
/// </summary>
public class TokenInfo
{
    /// <summary>
    /// Unique identifier for the token in the OpenIddict store.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the application (client) to which this token was issued.
    /// </summary>
    public Guid ApplicationId { get; set; }

    /// <summary>
    /// The subject (sub) claim, typically representing the user or entity to whom the token was issued.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// The type of token, such as "access_token", "refresh_token", or "id_token".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The raw payload of the token, which may include claims and other metadata.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// The date and time when the token was created.
    /// </summary>
    public DateTimeOffset CreationDate { get; set; }

    /// <summary>
    /// The date and time when the token expires, if applicable.
    /// </summary>
    public DateTimeOffset? ExpirationDate { get; set; }

    /// <summary>
    /// The status of the token, such as "valid", "revoked", or "redeemed".
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Reference identifier for the token, used for reference tokens or token lookup.
    /// </summary>
    public string ReferenceId { get; set; } = string.Empty;

    /// <summary>
    /// The date and time when the token was redeemed, if applicable (e.g., for one-time-use tokens).
    /// </summary>
    public DateTimeOffset? RedemptionDate { get; set; }
}
