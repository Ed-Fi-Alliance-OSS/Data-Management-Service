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
    public required IReadOnlyList<string> NamespacePrefixes { get; set; }

    /// <summary>
    /// Education organizations associated with the token
    /// </summary>
    [JsonPropertyName("education_organizations")]
    public required IReadOnlyList<TokenInfoEducationOrganization> EducationOrganizations { get; set; }

    /// <summary>
    /// Assigned profiles for the client
    /// </summary>
    [JsonPropertyName("assigned_profiles")]
    public required IReadOnlyList<string> AssignedProfiles { get; set; }

    /// <summary>
    /// Claim set information
    /// </summary>
    [JsonPropertyName("claim_set")]
    public required TokenInfoClaimSet ClaimSet { get; set; }

    /// <summary>
    /// Authorized resources and their allowed operations
    /// </summary>
    [JsonPropertyName("resources")]
    public required IReadOnlyList<TokenInfoResource> Resources { get; set; }

    /// <summary>
    /// Authorized services and their allowed operations
    /// </summary>
    [JsonPropertyName("services")]
    public IReadOnlyList<TokenInfoService>? Services { get; set; }
}

/// <summary>
/// Represents an education organization in the token info response
/// </summary>
public class TokenInfoEducationOrganization
{
    /// <summary>
    /// Education organization identifier
    /// </summary>
    [JsonPropertyName("education_organization_id")]
    public required long EducationOrganizationId { get; set; }

    /// <summary>
    /// Name of the institution
    /// </summary>
    [JsonPropertyName("name_of_institution")]
    public required string NameOfInstitution { get; set; }

    /// <summary>
    /// Type of education organization (e.g., edfi.School, edfi.LocalEducationAgency)
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>
    /// Local education agency identifier (for schools)
    /// </summary>
    [JsonPropertyName("local_education_agency_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LocalEducationAgencyId { get; set; }

    /// <summary>
    /// Education service center identifier (for local education agencies)
    /// </summary>
    [JsonPropertyName("education_service_center_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EducationServiceCenterId { get; set; }
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
    public required IReadOnlyList<string> Operations { get; set; }
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
    public required IReadOnlyList<string> Operations { get; set; }
}

/// <summary>
/// Request model for OAuth token introspection endpoint
/// </summary>
public class TokenInfoRequest
{
    /// <summary>
    /// The token to introspect
    /// </summary>
    public string? Token { get; set; }
}
