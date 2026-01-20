// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Retrieves application context from the Configuration Service API
/// </summary>
public class ConfigurationServiceApplicationProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext,
    ILogger<ConfigurationServiceApplicationProvider> logger
) : IApplicationContextProvider, IConfigurationServiceApplicationProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ApplicationDetailsResponse
    {
        public List<long> ProfileIds { get; init; } = [];
    }

    private sealed record ProfileSummaryResponse
    {
        public string Name { get; init; } = string.Empty;
    }

    /// <inheritdoc />
    public async Task<ApplicationContext?> GetApplicationByClientIdAsync(string clientId)
    {
        return await FetchApplicationByClientIdAsync(clientId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetApplicationProfilesByClientIdAsync(string clientId)
    {
        try
        {
            ApplicationContext? applicationContext = await FetchApplicationByClientIdAsync(clientId);

            if (applicationContext == null)
            {
                logger.LogWarning(
                    "Unable to resolve application when retrieving profiles for clientId: {ClientId}",
                    clientId
                );
                return Array.Empty<string>();
            }

            IReadOnlyList<long> profileIds = await GetProfileIdsForApplicationAsync(
                applicationContext.ApplicationId
            );

            if (profileIds.Count == 0)
            {
                logger.LogDebug(
                    "No profiles assigned to applicationId: {ApplicationId} for clientId: {ClientId}",
                    applicationContext.ApplicationId,
                    clientId
                );
                return Array.Empty<string>();
            }

            IReadOnlyList<string> profileNames = await GetProfileNamesAsync(profileIds);
            return profileNames;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error retrieving profiles for clientId: {ClientId}", clientId);
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public async Task<ApplicationContext?> ReloadApplicationByClientIdAsync(string clientId)
    {
        logger.LogInformation("Force reloading application context for clientId: {ClientId}", clientId);
        return await FetchApplicationByClientIdAsync(clientId);
    }

    private async Task<IReadOnlyList<long>> GetProfileIdsForApplicationAsync(long applicationId)
    {
        try
        {
            logger.LogDebug(
                "Fetching application details for profile assignments. ApplicationId: {ApplicationId}",
                applicationId
            );

            HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
                $"/v2/applications/{applicationId}"
            );

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning(
                    "Application not found when retrieving profiles. ApplicationId: {ApplicationId}",
                    applicationId
                );
                return Array.Empty<long>();
            }

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            ApplicationDetailsResponse? applicationDetails =
                JsonSerializer.Deserialize<ApplicationDetailsResponse>(responseBody, _jsonOptions);

            if (applicationDetails?.ProfileIds == null || applicationDetails.ProfileIds.Count == 0)
            {
                logger.LogDebug(
                    "Application has no profile assignments. ApplicationId: {ApplicationId}",
                    applicationId
                );
                return Array.Empty<long>();
            }

            return applicationDetails.ProfileIds;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching application profiles. ApplicationId: {ApplicationId}",
                applicationId
            );
            return Array.Empty<long>();
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse application profile response. ApplicationId: {ApplicationId}",
                applicationId
            );
            return Array.Empty<long>();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching application profiles. ApplicationId: {ApplicationId}",
                applicationId
            );
            return Array.Empty<long>();
        }
    }

    private async Task<IReadOnlyList<string>> GetProfileNamesAsync(IEnumerable<long> profileIds)
    {
        var ids = profileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Array.Empty<string>();
        }

        var fetchTasks = ids.Select(async profileId =>
            (ProfileId: profileId, Name: await FetchProfileNameAsync(profileId))
        );

        (long ProfileId, string? Name)[] results = await Task.WhenAll(fetchTasks);

        var names = results
            .Where(result => !string.IsNullOrWhiteSpace(result.Name))
            .Select(result => result.Name!)
            .ToList();

        if (names.Count == 0)
        {
            logger.LogWarning(
                "No valid profile names retrieved despite profile assignments. ProfileIds: [{ProfileIds}]",
                string.Join(", ", ids)
            );
        }

        return names;
    }

    private async Task<string?> FetchProfileNameAsync(long profileId)
    {
        try
        {
            logger.LogDebug("Fetching profile details for profileId: {ProfileId}", profileId);

            HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
                $"/v2/profiles/{profileId}"
            );

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("Profile not found for profileId: {ProfileId}", profileId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            ProfileSummaryResponse? profileResponse = JsonSerializer.Deserialize<ProfileSummaryResponse>(
                responseBody,
                _jsonOptions
            );

            if (profileResponse == null || string.IsNullOrWhiteSpace(profileResponse.Name))
            {
                logger.LogWarning("Profile response missing name. ProfileId: {ProfileId}", profileId);
                return null;
            }

            return profileResponse.Name;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching profile details. ProfileId: {ProfileId}",
                profileId
            );
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse profile response. ProfileId: {ProfileId}", profileId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching profile details. ProfileId: {ProfileId}",
                profileId
            );
            return null;
        }
    }

    private async Task<ApplicationContext?> FetchApplicationByClientIdAsync(string clientId)
    {
        try
        {
            // Get token for the Configuration Service API
            string? configurationServiceToken = await configurationServiceTokenHandler.GetTokenAsync(
                configurationServiceContext.clientId,
                configurationServiceContext.clientSecret,
                configurationServiceContext.scope
            );

            configurationServiceApiClient.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", configurationServiceToken);

            logger.LogDebug("Fetching application context for clientId: {ClientId}", clientId);

            HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
                $"/v2/apiClients/{clientId}"
            );

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Application not found for clientId: {ClientId}", clientId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            ApplicationContext? applicationContext = JsonSerializer.Deserialize<ApplicationContext>(
                responseBody,
                _jsonOptions
            );

            if (applicationContext == null)
            {
                logger.LogError(
                    "Failed to deserialize application context for clientId: {ClientId}",
                    clientId
                );
                return null;
            }

            logger.LogDebug(
                "Successfully fetched application context for clientId: {ClientId}, ApplicationId: {ApplicationId}, DmsInstanceIds: [{DmsInstanceIds}]",
                clientId,
                applicationContext.ApplicationId,
                string.Join(", ", applicationContext.DmsInstanceIds)
            );

            return applicationContext;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching application context for clientId: {ClientId}",
                clientId
            );
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse application context response for clientId: {ClientId}",
                clientId
            );
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching application context for clientId: {ClientId}",
                clientId
            );
            return null;
        }
    }
}
