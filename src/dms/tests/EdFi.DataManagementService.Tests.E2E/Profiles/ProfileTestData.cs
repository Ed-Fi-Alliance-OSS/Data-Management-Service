// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;

namespace EdFi.DataManagementService.Tests.E2E.Profiles;

/// <summary>
/// Stores profile IDs after they are created in the Configuration Service.
/// This allows tests to reference profiles by name and look up their IDs.
/// </summary>
public static class ProfileTestData
{
    /// <summary>
    /// Dictionary mapping profile names to their IDs in the Configuration Service.
    /// Thread-safe for parallel test execution.
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> _profileIdsByName = new();

    /// <summary>
    /// Indicates whether profiles have been initialized for this test run.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// Registers a profile ID by name after successful creation.
    /// </summary>
    /// <param name="profileName">The profile name</param>
    /// <param name="profileId">The profile ID from the Configuration Service</param>
    public static void RegisterProfile(string profileName, int profileId)
    {
        _profileIdsByName[profileName] = profileId;
    }

    /// <summary>
    /// Gets the profile ID for a given profile name.
    /// </summary>
    /// <param name="profileName">The profile name to look up</param>
    /// <returns>The profile ID if found</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the profile name is not registered</exception>
    public static int GetProfileId(string profileName)
    {
        if (_profileIdsByName.TryGetValue(profileName, out int profileId))
        {
            return profileId;
        }

        throw new KeyNotFoundException(
            $"Profile '{profileName}' not found. Available profiles: {string.Join(", ", _profileIdsByName.Keys)}"
        );
    }

    /// <summary>
    /// Tries to get the profile ID for a given profile name.
    /// </summary>
    /// <param name="profileName">The profile name to look up</param>
    /// <param name="profileId">The profile ID if found</param>
    /// <returns>True if the profile was found, false otherwise</returns>
    public static bool TryGetProfileId(string profileName, out int profileId)
    {
        return _profileIdsByName.TryGetValue(profileName, out profileId);
    }

    /// <summary>
    /// Gets all registered profile names.
    /// </summary>
    public static IEnumerable<string> RegisteredProfileNames => _profileIdsByName.Keys;

    /// <summary>
    /// Gets the count of registered profiles.
    /// </summary>
    public static int RegisteredProfileCount => _profileIdsByName.Count;

    /// <summary>
    /// Marks profiles as initialized after successful setup.
    /// </summary>
    public static void MarkInitialized()
    {
        IsInitialized = true;
    }

    /// <summary>
    /// Clears all registered profiles. Used for test cleanup or re-initialization.
    /// </summary>
    public static void Clear()
    {
        _profileIdsByName.Clear();
        IsInitialized = false;
    }
}
