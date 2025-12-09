// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Implementation of IProfileService with caching support
/// </summary>
internal class ProfileService(
    IProfileRepository _profileRepository,
    ILogger<ProfileService> _logger
) : IProfileService
{
    private readonly ConcurrentDictionary<string, ProfileDefinition> _profileCache = new();
    private DateTime? _lastCacheRefreshTime;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public async Task<ProfileDefinition?> GetProfileAsync(string profileName)
    {
        _logger.LogDebug("Getting profile: {ProfileName}", profileName);

        // Check if we need to refresh the cache
        await RefreshCacheIfNeededAsync();

        // Try to get from cache
        if (_profileCache.TryGetValue(profileName, out var cachedProfile))
        {
            _logger.LogDebug("Profile {ProfileName} found in cache", profileName);
            return cachedProfile;
        }

        _logger.LogDebug("Profile {ProfileName} not found in cache", profileName);
        return null;
    }

    public async Task ReloadProfilesAsync()
    {
        _logger.LogInformation("Reloading all profiles from database");

        await _cacheLock.WaitAsync();
        try
        {
            _profileCache.Clear();
            await LoadAllProfilesIntoCacheAsync();
            _lastCacheRefreshTime = DateTime.UtcNow;
        }
        finally
        {
            _cacheLock.Release();
        }

        _logger.LogInformation("Successfully reloaded {Count} profiles", _profileCache.Count);
    }

    public async Task<bool> ProfileExistsAsync(string profileName)
    {
        await RefreshCacheIfNeededAsync();
        return _profileCache.ContainsKey(profileName);
    }

    private async Task RefreshCacheIfNeededAsync()
    {
        // Check if cache needs refresh by comparing timestamps
        var latestUpdateTime = await _profileRepository.GetLatestUpdateTimestampAsync();

        // If cache is empty or there's a newer profile, refresh
        if (
            !_profileCache.Any()
            || (
                latestUpdateTime.HasValue
                && (_lastCacheRefreshTime == null || latestUpdateTime > _lastCacheRefreshTime)
            )
        )
        {
            _logger.LogDebug(
                "Cache refresh needed. Last refresh: {LastRefresh}, Latest update: {LatestUpdate}",
                _lastCacheRefreshTime,
                latestUpdateTime
            );

            await ReloadProfilesAsync();
        }
    }

    private async Task LoadAllProfilesIntoCacheAsync()
    {
        try
        {
            var profiles = await _profileRepository.GetAllProfilesAsync();

            foreach (var profile in profiles)
            {
                try
                {
                    var parsedProfile = ProfileXmlParser.Parse(
                        profile.ProfileDefinition,
                        profile.Description
                    );

                    _profileCache[profile.ProfileName] = parsedProfile;
                    _logger.LogDebug("Loaded profile: {ProfileName}", profile.ProfileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to parse profile {ProfileName}. Skipping.",
                        profile.ProfileName
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load profiles from database");
            throw new InvalidOperationException("Failed to load profiles from database", ex);
        }
    }
}
