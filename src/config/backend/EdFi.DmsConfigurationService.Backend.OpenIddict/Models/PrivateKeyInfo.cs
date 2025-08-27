// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents private key information retrieved from the OpenIddict store.
/// </summary>
public class PrivateKeyInfo
{
    /// <summary>
    /// The private key in PEM or other encoded format, used for cryptographic signing of JWTs and other tokens.
    /// This key should be kept secure and never exposed publicly.
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The identifier for the key, typically used in JWT headers as "kid" to indicate which key was used to sign the token.
    /// Useful for key rotation and validation.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;
}
