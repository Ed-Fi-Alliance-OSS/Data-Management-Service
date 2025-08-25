// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models
{
    public class ProtocolMapper
    {
    /// <summary>
    /// The name of the claim to be mapped in the token. Equivalent to Keycloak's "Token Claim Name" in protocol mappers.
    /// This is the key under which the claim will appear in the JWT or ID token.
    // Example: "namespacePrefixes"
    /// </summary>
    [JsonPropertyName("claim.name")]
    public string ClaimName { get; set; } = string.Empty;

    /// <summary>
    /// The value to assign to the claim in the token. Equivalent to Keycloak's "Claim Value" in protocol mappers.
    /// This can be a static value, a user attribute, or a value derived from application logic.
    /// In Ed-Fi, this might represent a user's role, permissions, or other metadata required for API access control.
    /// Example: "http://ed-fi.org" (for namespacePrefixes)
    /// </summary>
    [JsonPropertyName("claim.value")]
    public string ClaimValue { get; set; } = string.Empty;

    /// <summary>
    /// The JSON type label for the claim value, indicating the data type to be used in the token (e.g., "String", "Integer", "Boolean").
    /// This helps consumers of the token interpret the claim value correctly. In Keycloak, this is set in the protocol mapper as "Claim JSON Type".
    /// Typical values: "String", "Integer", "Boolean", "Long", "Double".
    /// </summary>
    [JsonPropertyName("jsonType.label")]
    public string JsonTypeLabel { get; set; } = string.Empty;

    /// <summary>
    /// The JSON array type label for the claim value, used when the claim is an array of values (e.g., a list of roles or permissions).
    /// Specifies the type of elements within the array (e.g., "String" for an array of strings).
    /// This is useful for advanced claim mapping scenarios where the claim should be represented as a JSON array in the token.
    /// Typical values: "String", "Integer", "Boolean".
    /// </summary>
    [JsonPropertyName("jsonAType.label")]
    public string JsonATypeLabel { get; set; } = string.Empty;
    }
}
