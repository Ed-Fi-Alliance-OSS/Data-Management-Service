// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Profiles.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Profiles;

/// <summary>
/// Manages loading and providing access to API profiles.
/// </summary>
public class ProfileProvider : IProfileProvider
{
    private readonly ILogger<ProfileProvider> _logger;
    private readonly Dictionary<string, ApiProfile> _profilesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ApiProfile>> _profilesByResource = new(StringComparer.OrdinalIgnoreCase);

    public ProfileProvider(
        IOptions<AppSettings> appSettings,
        ProfileXmlLoader xmlLoader,
        ILogger<ProfileProvider> logger)
    {
        _logger = logger;

        var settings = appSettings.Value;

        // Only load profiles if enabled
        if (!settings.EnableProfiles)
        {
            _logger.LogInformation("API Profiles feature is disabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.ProfilesPath))
        {
            _logger.LogWarning("EnableProfiles is true but ProfilesPath is not configured");
            return;
        }

        LoadProfiles(settings.ProfilesPath, xmlLoader);
    }

    private void LoadProfiles(string profilesPath, ProfileXmlLoader xmlLoader)
    {
        var profiles = xmlLoader.LoadProfilesFromDirectory(profilesPath);

        foreach (var profile in profiles)
        {
            // Store by profile name
            if (_profilesByName.ContainsKey(profile.Name))
            {
                _logger.LogWarning("Duplicate profile name '{ProfileName}' - skipping", profile.Name);
                continue;
            }

            _profilesByName[profile.Name] = profile;

            // Index by resource name for fast lookup
            var resourceNames = profile.Resources.Select(r => r.Name);
            foreach (var resourceName in resourceNames)
            {
                if (!_profilesByResource.ContainsKey(resourceName))
                {
                    _profilesByResource[resourceName] = new List<ApiProfile>();
                }

                _profilesByResource[resourceName].Add(profile);
            }
        }

        _logger.LogInformation(
            "Loaded {ProfileCount} profiles covering {ResourceCount} resources",
            _profilesByName.Count,
            _profilesByResource.Count
        );
    }

    public ApiProfile[] GetAllProfiles()
    {
        return _profilesByName.Values.ToArray();
    }

    public ApiProfile? GetProfileByName(string profileName)
    {
        _profilesByName.TryGetValue(profileName, out var profile);
        return profile;
    }

    public ApiProfile[] GetProfilesForResource(string resourceName)
    {
        if (_profilesByResource.TryGetValue(resourceName, out var profiles))
        {
            return profiles.ToArray();
        }

        return [];
    }

    public ProfileResource? GetProfileResource(string profileName, string resourceName)
    {
        var profile = GetProfileByName(profileName);
        if (profile == null)
        {
            return null;
        }

        return Array.Find(profile.Resources, r =>
            string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase));
    }
}
