// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Represents public key information retrieved from the OpenIddict store.
/// </summary>
public class PublicKeyInfo
{
    /// <summary>
    /// The identifier for the public key, typically used in JWT headers as "kid" to indicate which key should be used for signature validation.
    /// Useful for key rotation and matching the public key to its corresponding private key.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// The public key in binary format, used to validate JWT signatures and other cryptographic operations.
    /// This key is distributed to clients and services that need to verify tokens issued by the server.
    /// </summary>
    public byte[] PublicKey { get; set; } = [];
}
