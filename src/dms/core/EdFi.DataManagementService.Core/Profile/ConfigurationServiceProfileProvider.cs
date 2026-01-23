// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Response model for deserializing application data from CMS
/// </summary>
internal record CmsApplicationResponse(
    long Id,
    string ApplicationName,
    long VendorId,
    string ClaimSetName,
    List<long> EducationOrganizationIds,
    List<long> DmsInstanceIds,
    List<long> ProfileIds
);

/// <summary>
/// Response model for deserializing profile data from CMS
/// </summary>
internal record CmsProfileResponseInternal(long Id, string Name, string Definition);

/// <summary>
/// Retrieves profile data from the Configuration Management Service API.
/// Uses per-request headers for thread safety when making concurrent requests.
/// </summary>
public class ConfigurationServiceProfileProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext,
    ILogger<ConfigurationServiceProfileProvider> logger
) : IProfileCmsProvider
{
    private const string TenantHeaderName = "Tenant";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<ApplicationProfileInfo?> GetApplicationProfileInfoAsync(
        long applicationId,
        string? tenantId
    )
    {
        try
        {
            string? token = await configurationServiceTokenHandler.GetTokenAsync(
                configurationServiceContext.clientId,
                configurationServiceContext.clientSecret,
                configurationServiceContext.scope
            );

            logger.LogDebug(
                "Fetching application profile info for applicationId: {ApplicationId}",
                applicationId
            );

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/applications/{applicationId}");
            SetRequestHeaders(request, token, tenantId);

            HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Application not found for applicationId: {ApplicationId}", applicationId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            CmsApplicationResponse? applicationResponse = JsonSerializer.Deserialize<CmsApplicationResponse>(
                responseBody,
                _jsonOptions
            );

            if (applicationResponse == null)
            {
                logger.LogError(
                    "Failed to deserialize application response for applicationId: {ApplicationId}",
                    applicationId
                );
                return null;
            }

            logger.LogDebug(
                "Successfully fetched application profile info for applicationId: {ApplicationId}, ProfileIds: [{ProfileIds}]",
                applicationId,
                string.Join(", ", applicationResponse.ProfileIds)
            );

            return new ApplicationProfileInfo(applicationResponse.Id, applicationResponse.ProfileIds);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching application profile info for applicationId: {ApplicationId}",
                applicationId
            );
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse application response for applicationId: {ApplicationId}",
                applicationId
            );
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching application profile info for applicationId: {ApplicationId}",
                applicationId
            );
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<CmsProfileResponse?> GetProfileAsync(long profileId, string? tenantId)
    {
        try
        {
            string? token = await configurationServiceTokenHandler.GetTokenAsync(
                configurationServiceContext.clientId,
                configurationServiceContext.clientSecret,
                configurationServiceContext.scope
            );

            logger.LogDebug("Fetching profile for profileId: {ProfileId}", profileId);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/profiles/{profileId}");
            SetRequestHeaders(request, token, tenantId);

            HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Profile not found for profileId: {ProfileId}", profileId);
                return null;
            }

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            CmsProfileResponseInternal? profileResponse =
                JsonSerializer.Deserialize<CmsProfileResponseInternal>(responseBody, _jsonOptions);

            if (profileResponse == null)
            {
                logger.LogError(
                    "Failed to deserialize profile response for profileId: {ProfileId}",
                    profileId
                );
                return null;
            }

            logger.LogDebug(
                "Successfully fetched profile for profileId: {ProfileId}, Name: {ProfileName}",
                profileId,
                LoggingSanitizer.SanitizeForLogging(profileResponse.Name)
            );

            return new CmsProfileResponse(
                profileResponse.Id,
                profileResponse.Name,
                profileResponse.Definition
            );
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching profile for profileId: {ProfileId}",
                profileId
            );
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse profile response for profileId: {ProfileId}", profileId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching profile for profileId: {ProfileId}",
                profileId
            );
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CmsProfileResponse>> GetProfilesAsync(string? tenantId)
    {
        try
        {
            string? token = await configurationServiceTokenHandler.GetTokenAsync(
                configurationServiceContext.clientId,
                configurationServiceContext.clientSecret,
                configurationServiceContext.scope
            );

            logger.LogDebug("Fetching profile catalog from CMS");

            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/profiles");
            SetRequestHeaders(request, token, tenantId);

            HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            CmsProfileResponseInternal[]? profileResponses =
                JsonSerializer.Deserialize<CmsProfileResponseInternal[]>(responseBody, _jsonOptions);

            if (profileResponses == null)
            {
                logger.LogWarning("Profile catalog response was empty or could not be parsed");
                return Array.Empty<CmsProfileResponse>();
            }

            var results = profileResponses
                .Select(p => new CmsProfileResponse(p.Id, p.Name, p.Definition))
                .ToList();

            logger.LogDebug("Fetched {Count} profiles from CMS", results.Count);

            return results;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP request failed while fetching profile catalog");
            return Array.Empty<CmsProfileResponse>();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse profile catalog response");
            return Array.Empty<CmsProfileResponse>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while fetching profile catalog");
            return Array.Empty<CmsProfileResponse>();
        }
    }

    /// <summary>
    /// Sets authorization and tenant headers on the request message.
    /// Uses per-request headers for thread safety instead of modifying DefaultRequestHeaders.
    /// </summary>
    private static void SetRequestHeaders(HttpRequestMessage request, string? token, string? tenantId)
    {
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrEmpty(tenantId))
        {
            request.Headers.Add(TenantHeaderName, tenantId);
        }
    }
}
