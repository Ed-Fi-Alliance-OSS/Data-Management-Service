// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Retrieves and stores DMS instance configurations from the Configuration Service API
/// </summary>
public class ConfigurationServiceDmsInstanceProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext,
    ILogger<ConfigurationServiceDmsInstanceProvider> logger,
    CacheSettings? cacheSettings = null,
    TimeProvider? timeProvider = null
) : IDmsInstanceProvider
{
    private const string TenantHeaderName = "Tenant";
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CacheSettings _cacheSettings = cacheSettings ?? new CacheSettings();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, TenantCacheEntry> _instancesByTenant = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantLocks = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <inheritdoc />
    public bool IsLoaded(string? tenant = null) => _instancesByTenant.ContainsKey(GetTenantKey(tenant));

    /// <summary>
    /// Loads DMS instances from the Configuration Service API and stores them in memory
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    public async Task<IList<DmsInstance>> LoadDmsInstances(string? tenant = null)
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

            logger.LogInformation("Fetching DMS instances from Configuration Service");

            IList<DmsInstance> instances = await FetchDmsInstances(configurationServiceToken, tenant);

            logger.LogInformation("Successfully fetched {InstanceCount} DMS instances", instances.Count);

            // Store instances by tenant
            _instancesByTenant[GetTenantKey(tenant)] = new TenantCacheEntry(
                instances,
                _timeProvider.GetUtcNow()
            );

            foreach (DmsInstance instance in instances)
            {
                logger.LogDebug(
                    "Loaded DMS instance: ID={InstanceId}, Name='{InstanceName}', Type='{InstanceType}'",
                    instance.Id,
                    instance.InstanceName,
                    instance.InstanceType
                );
            }

            logger.LogInformation(
                "DMS instance cache updated successfully for tenant {Tenant}",
                tenant ?? "(default)"
            );

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
    public async Task RefreshInstancesIfExpiredAsync(string? tenant = null)
    {
        if (
            !_cacheSettings.DmsInstanceCacheRefreshEnabled
            || _cacheSettings.DmsInstanceCacheExpirationSeconds <= 0
        )
        {
            return;
        }

        string tenantKey = GetTenantKey(tenant);
        if (!_instancesByTenant.TryGetValue(tenantKey, out TenantCacheEntry? cachedEntry))
        {
            return;
        }

        TimeSpan expiration = TimeSpan.FromSeconds(_cacheSettings.DmsInstanceCacheExpirationSeconds);
        if (_timeProvider.GetUtcNow() - cachedEntry.LastRefreshed < expiration)
        {
            return;
        }

        SemaphoreSlim tenantLock = GetTenantLock(tenantKey);
        await tenantLock.WaitAsync();
        try
        {
            if (
                _instancesByTenant.TryGetValue(tenantKey, out TenantCacheEntry? refreshedEntry)
                && _timeProvider.GetUtcNow() - refreshedEntry.LastRefreshed < expiration
            )
            {
                return;
            }
            logger.LogInformation(
                "DMS instance cache expired for tenant {Tenant} after {TtlSeconds}s, refreshing configuration from Configuration Service",
                LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)"),
                _cacheSettings.DmsInstanceCacheExpirationSeconds
            );

            await LoadDmsInstances(tenant);
        }
        finally
        {
            tenantLock.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DmsInstance> GetAll(string? tenant = null) =>
        _instancesByTenant.TryGetValue(GetTenantKey(tenant), out var instances)
            ? instances.Instances.ToList().AsReadOnly()
            : new List<DmsInstance>().AsReadOnly();

    /// <inheritdoc />
    public DmsInstance? GetById(long id, string? tenant = null) =>
        _instancesByTenant.TryGetValue(GetTenantKey(tenant), out var instances)
            ? instances.Instances.FirstOrDefault(instance => instance.Id == id)
            : null;

    /// <summary>
    /// Gets the cache key for a tenant, using empty string for null/empty tenant
    /// </summary>
    private static string GetTenantKey(string? tenant) => tenant ?? string.Empty;

    /// <inheritdoc />
    public bool TenantExists(string tenant) => _instancesByTenant.ContainsKey(GetTenantKey(tenant));

    /// <inheritdoc />
    public IReadOnlyList<string> GetLoadedTenantKeys() => _instancesByTenant.Keys.ToList().AsReadOnly();

    /// <inheritdoc />
    public async Task<IList<string>> LoadTenants()
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

            logger.LogInformation("Fetching tenants from Configuration Service");

            IList<string> tenants = await FetchTenants(configurationServiceToken);

            logger.LogInformation("Successfully fetched {TenantCount} tenants", tenants.Count);

            foreach (string tenant in tenants)
            {
                logger.LogDebug("Found tenant: {TenantName}", LoggingSanitizer.SanitizeForLogging(tenant));
            }

            return tenants;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "Failed to load tenants from Configuration Service. Ensure the Configuration Service is running and accessible at {BaseUrl}",
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
                "Failed to deserialize tenants response from Configuration Service. The API response format may have changed."
            );
            throw new InvalidOperationException(
                "Configuration Service returned an invalid response format for tenants. "
                    + "This may indicate an API version mismatch or corrupted data.",
                ex
            );
        }
    }

    /// <summary>
    /// Fetches tenant names from the Configuration Service API
    /// </summary>
    private async Task<IList<string>> FetchTenants(string configurationServiceToken)
    {
        const string TenantsEndpoint = "v2/tenants/";

        logger.LogDebug("Sending GET request to {Endpoint}", TenantsEndpoint);

        using var request = new HttpRequestMessage(HttpMethod.Get, TenantsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configurationServiceToken);
        // No tenant header needed for tenants endpoint
        HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Configuration Service returned status code {StatusCode} for tenants endpoint",
                response.StatusCode
            );
        }

        response.EnsureSuccessStatusCode();

        string tenantsJson = await response.Content.ReadAsStringAsync();

        logger.LogDebug(
            "Received response from Configuration Service, deserializing {ByteCount} bytes",
            tenantsJson.Length
        );

        List<TenantResponse>? tenantResponses = JsonSerializer.Deserialize<List<TenantResponse>>(
            tenantsJson,
            _jsonOptions
        );

        if (tenantResponses == null)
        {
            logger.LogWarning("Deserialization returned null - treating as empty tenant list");
            return [];
        }

        return tenantResponses.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Fetches DMS instances from the Configuration Service API
    /// </summary>
    private async Task<IList<DmsInstance>> FetchDmsInstances(string configurationServiceToken, string? tenant)
    {
        const string DmsInstancesEndpoint = "v2/dmsInstances/";

        logger.LogDebug("Sending GET request to {Endpoint}", DmsInstancesEndpoint);

        using var request = new HttpRequestMessage(HttpMethod.Get, DmsInstancesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configurationServiceToken);
        if (!string.IsNullOrEmpty(tenant))
        {
            request.Headers.Add(TenantHeaderName, tenant);
        }
        HttpResponseMessage response = await configurationServiceApiClient.Client.SendAsync(request);

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
    /// Sets the Tenant header for multi-tenant API calls
    /// </summary>
    /// <param name="tenant">The tenant identifier, or null to remove the header</param>
    // SetTenantHeader is no longer needed; per-request headers are now used for thread safety.

    private SemaphoreSlim GetTenantLock(string tenantKey) =>
        _tenantLocks.GetOrAdd(tenantKey, _ => new SemaphoreSlim(1, 1));

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

    /// <summary>
    /// Response model for tenant data from the Configuration Service API
    /// </summary>
    private sealed class TenantResponse
    {
        public long Id { get; init; } = 0;
        public string Name { get; init; } = string.Empty;
    }

    private sealed record TenantCacheEntry(IList<DmsInstance> Instances, DateTimeOffset LastRefreshed);
}
