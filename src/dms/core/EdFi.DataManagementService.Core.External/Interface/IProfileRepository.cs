// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// Repository interface for managing API Profile definitions
/// </summary>
public interface IProfileRepository
{
    /// <summary>
    /// Retrieves a profile by its name
    /// </summary>
    /// <param name="profileName">The name of the profile to retrieve</param>
    /// <returns>The profile information or null if not found</returns>
    Task<ProfileInfo?> GetProfileByNameAsync(string profileName);

    /// <summary>
    /// Retrieves all profiles
    /// </summary>
    /// <returns>A list of all profiles</returns>
    Task<IReadOnlyList<ProfileInfo>> GetAllProfilesAsync();

    /// <summary>
    /// Creates a new profile
    /// </summary>
    /// <param name="profileName">The unique name for the profile</param>
    /// <param name="description">Optional description of the profile</param>
    /// <param name="profileDefinition">The XML definition of the profile</param>
    /// <returns>The ID of the created profile</returns>
    Task<long> CreateProfileAsync(string profileName, string? description, string profileDefinition);

    /// <summary>
    /// Updates an existing profile
    /// </summary>
    /// <param name="profileName">The name of the profile to update</param>
    /// <param name="description">Optional description of the profile</param>
    /// <param name="profileDefinition">The XML definition of the profile</param>
    /// <returns>True if the profile was updated, false if not found</returns>
    Task<bool> UpdateProfileAsync(string profileName, string? description, string profileDefinition);

    /// <summary>
    /// Deletes a profile by name
    /// </summary>
    /// <param name="profileName">The name of the profile to delete</param>
    /// <returns>True if the profile was deleted, false if not found</returns>
    Task<bool> DeleteProfileAsync(string profileName);

    /// <summary>
    /// Gets the timestamp of the most recently updated profile for cache invalidation
    /// </summary>
    /// <returns>The latest update timestamp or null if no profiles exist</returns>
    Task<DateTime?> GetLatestUpdateTimestampAsync();
}

/// <summary>
/// Profile information returned from the repository
/// </summary>
public record ProfileInfo(
    long Id,
    string ProfileName,
    string? Description,
    string ProfileDefinition,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
