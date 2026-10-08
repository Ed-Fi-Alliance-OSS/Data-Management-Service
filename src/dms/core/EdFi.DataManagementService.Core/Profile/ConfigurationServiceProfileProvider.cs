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
    List<long> DataStoreIds,
    List<long> ProfileIds
);

/// <summary>
/// Response model for deserializing profile data from CMS
/// </summary>
internal record CmsProfileResponseInternal(long Id, string Name, string Definition);

/// <summary>
/// Retrieves profile data from the Configuration Management Service API.
/// Uses per-request headers for thread safety when making concurrent requests.
/// A CMS 404 for a single profile is the only outcome reported as absent (<c>null</c>). Every
/// other failure, including a 404 for the application, is logged once here and thrown as
/// <see cref="ProfileDataUnavailableException" />, so a dependency failure is never mistaken for
/// "no profile" and never cached as a successful result.
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

            using var request = new HttpRequestMessage(HttpMethod.Get, $"v3/applications/{applicationId}");
            SetRequestHeaders(request, token, tenantId);
            request.Options.Set(ConfigurationServiceResponseHandler.AllowNotFoundResponse, true);

            HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

            // The application id came from this client's resolved application context, so CMS
            // not knowing it means the two disagree (for example, the application was deleted
            // while that context is still cached). Reading that as "no profiles assigned" would
            // cache unprofiled access, so it fails closed like every other failure.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogError(
                    "Application not found in CMS although its client resolved, for applicationId: {ApplicationId}",
                    applicationId
                );
                throw ApplicationProfileInfoUnavailable(applicationId);
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
                throw ApplicationProfileInfoUnavailable(applicationId);
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
            throw ApplicationProfileInfoUnavailable(applicationId, ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse application response for applicationId: {ApplicationId}",
                applicationId
            );
            throw ApplicationProfileInfoUnavailable(applicationId, ex);
        }
        catch (Exception ex) when (ex is not ProfileDataUnavailableException)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching application profile info for applicationId: {ApplicationId}",
                applicationId
            );
            throw ApplicationProfileInfoUnavailable(applicationId, ex);
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

            using var request = new HttpRequestMessage(HttpMethod.Get, $"v3/profiles/{profileId}");
            SetRequestHeaders(request, token, tenantId);
            // A profile listed by GET /v3/profiles can still be deleted before its detail is fetched.
            // That 404 means the profile is gone, so the catalog skips it rather than failing the
            // whole attempt. (CMS leaves XSD-invalid profiles out of the list, so they never reach
            // this fetch.)
            request.Options.Set(ConfigurationServiceResponseHandler.AllowNotFoundResponse, true);

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
                throw ProfileUnavailable(profileId);
            }

            logger.LogDebug(
                "Successfully fetched profile for profileId: {ProfileId}, Name: {ProfileName}",
                profileId,
                LoggingSanitizer.SanitizeInternalValueForLogging(profileResponse.Name)
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
            throw ProfileUnavailable(profileId, ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse profile response for profileId: {ProfileId}", profileId);
            throw ProfileUnavailable(profileId, ex);
        }
        catch (Exception ex) when (ex is not ProfileDataUnavailableException)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching profile for profileId: {ProfileId}",
                profileId
            );
            throw ProfileUnavailable(profileId, ex);
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

            // No AllowNotFoundResponse: the list endpoint always exists, so a 404 here is a failure,
            // never an empty catalog.
            using var request = new HttpRequestMessage(HttpMethod.Get, "v3/profiles");
            SetRequestHeaders(request, token, tenantId);

            HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();
            CmsProfileResponseInternal[]? profileResponses =
                JsonSerializer.Deserialize<CmsProfileResponseInternal[]>(responseBody, _jsonOptions);

            if (profileResponses == null)
            {
                logger.LogError("Profile catalog response was empty or could not be parsed");
                throw CatalogUnavailable();
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
            throw CatalogUnavailable(ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse profile catalog response");
            throw CatalogUnavailable(ex);
        }
        catch (Exception ex) when (ex is not ProfileDataUnavailableException)
        {
            logger.LogError(ex, "Unexpected error while fetching profile catalog");
            throw CatalogUnavailable(ex);
        }
    }

    private static ProfileDataUnavailableException ApplicationProfileInfoUnavailable(
        long applicationId,
        Exception? innerException = null
    ) =>
        new(
            $"Application profile info for applicationId {applicationId} is unavailable from the Configuration Service",
            innerException
        );

    private static ProfileDataUnavailableException ProfileUnavailable(
        long profileId,
        Exception? innerException = null
    ) => new($"Profile {profileId} is unavailable from the Configuration Service", innerException);

    private static ProfileDataUnavailableException CatalogUnavailable(Exception? innerException = null) =>
        new("The profile catalog is unavailable from the Configuration Service", innerException);

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
