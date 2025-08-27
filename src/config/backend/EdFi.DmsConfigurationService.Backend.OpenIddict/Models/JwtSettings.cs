// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models
{
    public class JwtSettings
    {
    /// <summary>
    /// The issuer (iss) claim for the JWT, representing the authority that issued the token.
    /// Should match the expected issuer in token validation.
    /// Example: "http://dms-config-service:8081"
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// The audience (aud) claim for the JWT, representing the intended recipient(s) of the token.
    /// Used to restrict which APIs or services can accept the token.
    /// Example: "account"
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// The token expiration time in minutes. Determines how long the JWT is valid after issuance.
    /// Example: 30 (token expires in 30 minutes)
    /// </summary>
    public int ExpirationMinutes { get; set; } = 30;
    }
}
