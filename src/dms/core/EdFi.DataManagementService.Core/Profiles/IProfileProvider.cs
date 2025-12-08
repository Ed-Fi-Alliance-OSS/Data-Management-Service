// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profiles.Model;

namespace EdFi.DataManagementService.Core.Profiles;

/// <summary>
/// Provides access to loaded API profiles.
/// </summary>
public interface IProfileProvider
{
    /// <summary>
    /// Gets all loaded profiles.
    /// </summary>
    ApiProfile[] GetAllProfiles();

    /// <summary>
    /// Gets a profile by name.
    /// </summary>
    /// <param name="profileName">The name of the profile</param>
    /// <returns>The profile, or null if not found</returns>
    ApiProfile? GetProfileByName(string profileName);

    /// <summary>
    /// Gets all profiles that apply to a specific resource.
    /// </summary>
    /// <param name="resourceName">The name of the resource</param>
    /// <returns>Array of profiles that define rules for this resource</returns>
    ApiProfile[] GetProfilesForResource(string resourceName);

    /// <summary>
    /// Gets a specific resource definition from a profile.
    /// </summary>
    /// <param name="profileName">The name of the profile</param>
    /// <param name="resourceName">The name of the resource</param>
    /// <returns>The resource definition, or null if not found</returns>
    ProfileResource? GetProfileResource(string profileName, string resourceName);
}
