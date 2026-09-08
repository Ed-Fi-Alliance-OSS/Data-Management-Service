// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Response model for OAuth token introspection endpoint
/// Matches Ed-Fi ODS/API token_info schema for backward compatibility
/// </summary>
public class TokenInfoResponse
{
    /// <summary>
    /// Indicates whether the token is currently active
    /// </summary>
    [JsonPropertyName("active")]
    public required bool Active { get; set; }

    /// <summary>
    /// Client identifier for the OAuth2 client
    /// </summary>
    [JsonPropertyName("client_id")]
    public required string ClientId { get; set; }

    /// <summary>
    /// Namespace prefixes associated with the client
    /// </summary>
    [JsonPropertyName("namespace_prefixes")]
    public required IEnumerable<string> NamespacePrefixes { get; set; }

    /// <summary>
    /// Education organizations associated with the token
    /// </summary>
    [JsonPropertyName("education_organizations")]
    public required IEnumerable<OrderedDictionary<string, object>> EducationOrganizations { get; set; }

    /// <summary>
    /// Assigned profiles for the client
    /// </summary>
    [JsonPropertyName("assigned_profiles")]
    public required IEnumerable<string> AssignedProfiles { get; set; }

    /// <summary>
    /// Claim set information
    /// </summary>
    [JsonPropertyName("claim_set")]
    public required TokenInfoClaimSet ClaimSet { get; set; }

    /// <summary>
    /// Authorized resources and their allowed operations
    /// </summary>
    [JsonPropertyName("resources")]
    public required IEnumerable<TokenInfoResource> Resources { get; set; }

    /// <summary>
    /// Authorized services and their allowed operations
    /// </summary>
    [JsonPropertyName("services")]
    public IEnumerable<TokenInfoService>? Services { get; set; }
}

/// <summary>
/// Represents claim set information
/// </summary>
public class TokenInfoClaimSet
{
    /// <summary>
    /// Name of the claim set
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}

/// <summary>
/// Represents a resource and its authorized operations
/// </summary>
public class TokenInfoResource
{
    /// <summary>
    /// Resource path (e.g., /ed-fi/students)
    /// </summary>
    [JsonPropertyName("resource")]
    public required string Resource { get; set; }

    /// <summary>
    /// List of allowed operations (e.g., Create, Read, Update, Delete)
    /// </summary>
    [JsonPropertyName("operations")]
    public required IEnumerable<string> Operations { get; set; }
}

/// <summary>
/// Represents a service and its authorized operations
/// </summary>
public class TokenInfoService
{
    /// <summary>
    /// Service name
    /// </summary>
    [JsonPropertyName("service")]
    public required string Service { get; set; }

    /// <summary>
    /// List of allowed operations
    /// </summary>
    [JsonPropertyName("operations")]
    public required IEnumerable<string> Operations { get; set; }
}
