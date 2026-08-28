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
) : IApplicationContextProvider, IConfigurationServiceApplicationProvider
{
    private const short MinimumOwnershipTokenId = 1;
    private const short MaximumOwnershipTokenId = 32767;
    private const int MaximumOwnershipTokenCount = 1999;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<ApplicationContextResult> GetApplicationByClientIdAsync(string clientId, string? tenant)
    {
        return await FetchApplicationByClientIdAsync(clientId, tenant);
    }

    /// <inheritdoc />
    public async Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(
        string clientId,
        string? tenant
    )
    {
        logger.LogInformation("Force reloading application context for clientId: {ClientId}", clientId);
        return await FetchApplicationByClientIdAsync(clientId, tenant);
    }

    private async Task<ApplicationContextResult> FetchApplicationByClientIdAsync(
        string clientId,
        string? tenant
    )
    {
        try
        {
            string configurationServiceToken = await configurationServiceTokenHandler.GetTokenAsync(
                configurationServiceContext.clientId,
                configurationServiceContext.clientSecret,
                configurationServiceContext.scope
            );

            logger.LogDebug("Fetching application context for clientId: {ClientId}", clientId);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/apiClients/{clientId}");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                configurationServiceToken
            );

            if (tenant is not null)
            {
                request.Headers.Add("Tenant", tenant);
            }

            request.Options.Set(ConfigurationServiceResponseHandler.AllowNotFoundResponse, true);
            using HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(
                request
            );

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Application not found for clientId: {ClientId}", clientId);
                return new ApplicationContextResult.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Configuration Service returned {StatusCode} while fetching application context for clientId: {ClientId}",
                    response.StatusCode,
                    clientId
                );
                return new ApplicationContextResult.Unavailable();
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            ApplicationContext? applicationContext = DeserializeApplicationContext(responseBody);

            if (
                applicationContext is null
                || !string.Equals(applicationContext.ClientId, clientId, StringComparison.Ordinal)
                || applicationContext.ClientUuid == Guid.Empty
                || !HasValidOwnershipConfiguration(applicationContext)
            )
            {
                logger.LogError(
                    "Failed to deserialize application context for clientId: {ClientId}",
                    clientId
                );
                return new ApplicationContextResult.Unavailable();
            }

            logger.LogDebug(
                "Successfully fetched application context for clientId: {ClientId}, ApplicationId: {ApplicationId}",
                clientId,
                applicationContext.ApplicationId
            );

            return new ApplicationContextResult.Success(applicationContext);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "HTTP request failed while fetching application context for clientId: {ClientId}",
                clientId
            );
            return new ApplicationContextResult.Unavailable();
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to parse application context response for clientId: {ClientId}",
                clientId
            );
            return new ApplicationContextResult.Unavailable();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error while fetching application context for clientId: {ClientId}",
                clientId
            );
            return new ApplicationContextResult.Unavailable();
        }
    }

    private static ApplicationContext? DeserializeApplicationContext(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);

        if (
            document.RootElement.ValueKind != JsonValueKind.Object
            || !HasRequiredProperties(document.RootElement)
        )
        {
            return null;
        }

        ApplicationContext? applicationContext = JsonSerializer.Deserialize<ApplicationContext>(
            responseBody,
            _jsonOptions
        );

        return
            applicationContext is not null
            && !string.IsNullOrWhiteSpace(applicationContext.ClientId)
            && applicationContext.DataStoreIds is not null
            && applicationContext.OwnershipTokenIds is not null
            ? applicationContext
            : null;
    }

    private static bool HasRequiredProperties(JsonElement applicationContext)
    {
        return HasProperty(applicationContext, "id")
            && HasProperty(applicationContext, "applicationId")
            && HasProperty(applicationContext, "clientId")
            && HasProperty(applicationContext, "clientUuid")
            && HasProperty(applicationContext, "dataStoreIds")
            && HasProperty(applicationContext, "creatorOwnershipTokenId")
            && HasProperty(applicationContext, "ownershipTokenIds");
    }

    private static bool HasValidOwnershipConfiguration(ApplicationContext applicationContext)
    {
        bool creatorIsValid =
            applicationContext.CreatorOwnershipTokenId is null
            || IsValidOwnershipTokenId(applicationContext.CreatorOwnershipTokenId.Value);

        return creatorIsValid
            && applicationContext.OwnershipTokenIds.Count <= MaximumOwnershipTokenCount
            && applicationContext.OwnershipTokenIds.Distinct().Count()
                == applicationContext.OwnershipTokenIds.Count
            && applicationContext.OwnershipTokenIds.All(IsValidOwnershipTokenId);
    }

    private static bool IsValidOwnershipTokenId(short tokenId) =>
        tokenId is >= MinimumOwnershipTokenId and <= MaximumOwnershipTokenId;

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        return element
            .EnumerateObject()
            .Any(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }
}
