// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Service for managing and accessing API Profiles with caching support
/// </summary>
public interface IProfileService
{
    /// <summary>
    /// Gets a parsed profile by name, using cache if available
    /// </summary>
    /// <param name="profileName">The name of the profile to retrieve</param>
    /// <returns>The parsed profile definition or null if not found</returns>
    Task<ProfileDefinition?> GetProfileAsync(string profileName);

    /// <summary>
    /// Reloads all profiles from the database, invalidating the cache
    /// </summary>
    Task ReloadProfilesAsync();

    /// <summary>
    /// Checks if a specific profile exists
    /// </summary>
    /// <param name="profileName">The name of the profile to check</param>
    /// <returns>True if the profile exists, false otherwise</returns>
    Task<bool> ProfileExistsAsync(string profileName);
}

/// <summary>
/// Represents a parsed profile definition with its policy rules
/// </summary>
public record ProfileDefinition(
    string ProfileName,
    string? Description,
    ResourcePolicy[] ResourcePolicies
);

/// <summary>
/// Represents the policy rules for a specific resource in a profile
/// </summary>
public record ResourcePolicy(
    string ResourceName,
    ContentTypePolicy? ReadContentType,
    ContentTypePolicy? WriteContentType
);

/// <summary>
/// Represents content type policy (read or write) for a resource
/// </summary>
public record ContentTypePolicy(
    MemberSelection MemberSelection,
    string[] IncludedProperties,
    string[] ExcludedProperties
);

/// <summary>
/// Defines how members (properties) are selected in a profile
/// </summary>
public enum MemberSelection
{
    /// <summary>
    /// Include all properties
    /// </summary>
    IncludeAll,
    /// <summary>
    /// Include only specified properties
    /// </summary>
    IncludeOnly,
    /// <summary>
    /// Exclude specified properties
    /// </summary>
    ExcludeOnly
}
