// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Retrieves and stores DMS instance configurations from the Configuration Service API
/// </summary>
public class ConfigurationServiceDmsInstanceProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext,
    ILogger<ConfigurationServiceDmsInstanceProvider> logger
) : IDmsInstanceProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private IList<DmsInstance> _instances = new List<DmsInstance>();
    private volatile bool _isLoaded;

    /// <inheritdoc />
    public bool IsLoaded => _isLoaded;

    /// <summary>
    /// Loads DMS instances from the Configuration Service API and stores them in memory
    /// </summary>
    public async Task<IList<DmsInstance>> LoadDmsInstances()
    {
        logger.LogInformation(
            "Requesting authentication token from Configuration Service at {BaseUrl}",
            configurationServiceApiClient.Client.BaseAddress
        );

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

            logger.LogInformation("Fetching DMS instances from Configuration Service");

            IList<DmsInstance> instances = await FetchDmsInstances();

            logger.LogInformation("Successfully fetched {InstanceCount} DMS instances", instances.Count);

            // Store instances
            _instances = instances;

            foreach (DmsInstance instance in instances)
            {
                logger.LogDebug(
                    "Loaded DMS instance: ID={InstanceId}, Name='{InstanceName}', Type='{InstanceType}'",
                    instance.Id,
                    instance.InstanceName,
                    instance.InstanceType
                );
            }

            _isLoaded = true;

            logger.LogInformation("DMS instance cache updated successfully");

            return instances;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "Failed to load DMS instances from Configuration Service. Ensure the Configuration Service is running and accessible at {BaseUrl}",
                configurationServiceApiClient.Client.BaseAddress
            );
            throw new InvalidOperationException(
                $"Unable to connect to Configuration Service at {configurationServiceApiClient.Client.BaseAddress}. "
                    + "Verify that the service is running and the ConfigurationServiceSettings are configured correctly. "
                    + $"Error: {ex.Message}",
                ex
            );
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to deserialize DMS instances response from Configuration Service. The API response format may have changed."
            );
            throw new InvalidOperationException(
                "Configuration Service returned an invalid response format for DMS instances. "
                    + "This may indicate an API version mismatch or corrupted data.",
                ex
            );
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DmsInstance> GetAll()
    {
        return _instances.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public DmsInstance? GetById(long id)
    {
        return _instances.FirstOrDefault(instance => instance.Id == id);
    }

    /// <summary>
    /// Fetches DMS instances from the Configuration Service API
    /// </summary>
    private async Task<IList<DmsInstance>> FetchDmsInstances()
    {
        const string DmsInstancesEndpoint = "v2/dmsInstances/";

        logger.LogDebug("Sending GET request to {Endpoint}", DmsInstancesEndpoint);

        HttpResponseMessage response = await configurationServiceApiClient.Client.GetAsync(
            DmsInstancesEndpoint
        );

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Configuration Service returned status code {StatusCode} for DMS instances endpoint",
                response.StatusCode
            );
        }

        response.EnsureSuccessStatusCode();

        string dmsInstancesJson = await response.Content.ReadAsStringAsync();

        logger.LogDebug(
            "Received response from Configuration Service, deserializing {ByteCount} bytes",
            dmsInstancesJson.Length
        );

        List<DmsInstanceResponse>? dmsInstanceResponses = JsonSerializer.Deserialize<
            List<DmsInstanceResponse>
        >(dmsInstancesJson, _jsonOptions);

        if (dmsInstanceResponses == null)
        {
            logger.LogWarning("Deserialization returned null - treating as empty instance list");
            return [];
        }

        return dmsInstanceResponses
            .Select(response => new DmsInstance(
                response.Id,
                response.InstanceType,
                response.InstanceName,
                response.ConnectionString,
                response.DmsInstanceRouteContexts.ToDictionary(
                    rc => new RouteQualifierName(rc.ContextKey),
                    rc => new RouteQualifierValue(rc.ContextValue)
                )
            ))
            .ToList();
    }

    /// <summary>
    /// Response model matching the Configuration Service API structure
    /// </summary>
    private sealed class DmsInstanceResponse
    {
        public long Id { get; init; } = 0;
        public string InstanceType { get; init; } = string.Empty;
        public string InstanceName { get; init; } = string.Empty;
        public string? ConnectionString { get; init; } = null;
        public IList<DmsInstanceRouteContextItem> DmsInstanceRouteContexts { get; init; } = [];
    }

    /// <summary>
    /// Response model for route context items within a DMS instance response
    /// </summary>
    private sealed class DmsInstanceRouteContextItem
    {
        public long Id { get; init; } = 0;
        public long InstanceId { get; init; } = 0;
        public string ContextKey { get; init; } = string.Empty;
        public string ContextValue { get; init; } = string.Empty;
    }
}
