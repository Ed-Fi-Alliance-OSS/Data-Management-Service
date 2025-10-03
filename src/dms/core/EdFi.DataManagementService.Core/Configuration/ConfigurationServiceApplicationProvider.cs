// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
) : IApplicationContextProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<ApplicationContext?> GetApplicationByClientIdAsync(string clientId)
    {
        return await FetchApplicationByClientIdAsync(clientId);
    }

    /// <inheritdoc />
    public async Task<ApplicationContext?> ReloadApplicationByClientIdAsync(string clientId)
    {
        logger.LogInformation("Force reloading application context for clientId: {ClientId}", clientId);
        return await FetchApplicationByClientIdAsync(clientId);
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
