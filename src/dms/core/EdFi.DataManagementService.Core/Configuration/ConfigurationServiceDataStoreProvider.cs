// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Retrieves and stores data store configurations from the Configuration Service API
/// </summary>
public class ConfigurationServiceDataStoreProvider(
    ConfigurationServiceApiClient configurationServiceApiClient,
    IConfigurationServiceTokenHandler configurationServiceTokenHandler,
    ConfigurationServiceContext configurationServiceContext,
    ILogger<ConfigurationServiceDataStoreProvider> logger,
    IConnectionStringDecryptionService connectionStringDecryptionService,
    CacheSettings? cacheSettings = null,
    TimeProvider? timeProvider = null,
    IEnumerable<IDataStoreOwnershipReconciler>? reconcilers = null
) : IDataStoreProvider
{
    private const string TenantHeaderName = "Tenant";
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CacheSettings _cacheSettings = cacheSettings ?? new CacheSettings();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IConnectionStringDecryptionService _connectionStringDecryptionService =
        connectionStringDecryptionService;
    private readonly ConcurrentDictionary<string, TenantCacheEntry> _instancesByTenant = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantLocks = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Registration order, captured once. The interface documents that reconcilers run in order, so
    /// enumerating a lazily-evaluated sequence per publication - which could yield a different order,
    /// or different instances - would quietly break that promise.
    /// </summary>
    private readonly ImmutableArray<IDataStoreOwnershipReconciler> _reconcilers =
        reconcilers?.ToImmutableArray() ?? [];

    /// <summary>
    /// Serializes tenant-entry replacement, version assignment, owner projection, and reconciliation,
    /// so two overlapping tenant loads cannot interleave and a later version's reconciliation cannot
    /// begin before an earlier one has finished. Deliberately not held across CMS network I/O or
    /// DataStore construction, both of which happen before it is taken.
    /// </summary>
    private readonly SemaphoreSlim _publicationLock = new(1, 1);

    /// <summary>Mutated only under <see cref="_publicationLock" />.</summary>
    private long _ownershipVersion;

    /// <inheritdoc />
    public bool IsLoaded(string? tenant = null) => _instancesByTenant.ContainsKey(GetTenantKey(tenant));

    /// <summary>
    /// Loads data stores from the Configuration Service API and stores them in memory
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    public async Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
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
                configurationServiceContext.scope,
                cancellationToken
            );

            logger.LogInformation("Fetching data stores from Configuration Service");

            IList<DataStore> instances = await FetchDataStores(
                configurationServiceToken,
                tenant,
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation("Successfully fetched {InstanceCount} data stores", instances.Count);

            // Store instances by tenant and publish the resulting ownership, atomically with
            // respect to every other tenant load. Everything above this point - the token, the
            // fetch, and DataStore construction - is deliberately outside the lock, so a slow or
            // hanging Configuration Service cannot block another tenant from publishing.
            await PublishConfiguredOwnershipAsync(tenant, instances, cancellationToken);

            foreach (DataStore instance in instances)
            {
                logger.LogDebug(
                    "Loaded data store: ID={DataStoreId}, Name='{Name}', Type='{DataStoreType}'",
                    instance.Id,
                    instance.Name,
                    instance.DataStoreType
                );
            }
            string sanitizedTenant = LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)");
            logger.LogInformation(
                "Data store cache updated successfully for tenant {Tenant}",
                sanitizedTenant
            );

            return instances;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(
                ex,
                "Failed to load data stores from Configuration Service. Ensure the Configuration Service is running and accessible at {BaseUrl}",
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
                "Failed to deserialize data stores response from Configuration Service. The API response format may have changed."
            );
            throw new InvalidOperationException(
                "Configuration Service returned an invalid response format for data stores. "
                    + "This may indicate an API version mismatch or corrupted data.",
                ex
            );
        }
    }

    /// <inheritdoc />
    public async Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !_cacheSettings.DataStoreCacheRefreshEnabled
            || _cacheSettings.DataStoreCacheExpirationSeconds <= 0
        )
        {
            return;
        }

        string tenantKey = GetTenantKey(tenant);
        if (!_instancesByTenant.TryGetValue(tenantKey, out TenantCacheEntry? cachedEntry))
        {
            return;
        }

        TimeSpan expiration = TimeSpan.FromSeconds(_cacheSettings.DataStoreCacheExpirationSeconds);
        if (_timeProvider.GetUtcNow() - cachedEntry.LastRefreshed < expiration)
        {
            return;
        }

        SemaphoreSlim tenantLock = GetTenantLock(tenantKey);
        await tenantLock.WaitAsync(cancellationToken);
        try
        {
            if (
                _instancesByTenant.TryGetValue(tenantKey, out TenantCacheEntry? refreshedEntry)
                && _timeProvider.GetUtcNow() - refreshedEntry.LastRefreshed < expiration
            )
            {
                return;
            }
            string sanitizedTenant = LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)");
            logger.LogInformation(
                "Data store cache expired for tenant {Tenant} after {TtlSeconds}s, refreshing configuration from Configuration Service",
                sanitizedTenant,
                _cacheSettings.DataStoreCacheExpirationSeconds
            );

            await LoadDataStores(tenant, cancellationToken);
        }
        finally
        {
            tenantLock.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
        _instancesByTenant.TryGetValue(GetTenantKey(tenant), out var instances)
            ? instances.Instances.ToList().AsReadOnly()
            : new List<DataStore>().AsReadOnly();

    /// <inheritdoc />
    public DataStore? GetById(long id, string? tenant = null) =>
        _instancesByTenant.TryGetValue(GetTenantKey(tenant), out var instances)
            ? instances.Instances.FirstOrDefault(instance => instance.Id == id)
            : null;

    /// <summary>
    /// Gets the cache key for a tenant, using empty string for null/empty tenant
    /// </summary>
    /// <summary>
    /// Replaces the tenant entry and publishes the resulting global ownership, in one critical
    /// section, so the version, the projected owner union, and the reconciliation that consumes them
    /// describe the same instant.
    /// </summary>
    /// <remarks>
    /// Reached only after a successful load: every failure path above throws before this is called, so
    /// a failed or cancelled load replaces no entry, assigns no version, and invokes no reconciler.
    ///
    /// Lock-free readers are unaffected. GetById and GetAll never take this lock, so a request in
    /// flight sees either the previous entry or the new one - which is what lets it keep the
    /// configuration it selected its target from.
    /// </remarks>
    private async Task PublishConfiguredOwnershipAsync(
        string? tenant,
        IList<DataStore> instances,
        CancellationToken cancellationToken
    )
    {
        await _publicationLock.WaitAsync(cancellationToken);

        try
        {
            _instancesByTenant[GetTenantKey(tenant)] = new TenantCacheEntry(
                instances,
                _timeProvider.GetUtcNow()
            );

            // Projected while holding the lock, so every tenant entry is quiescent for writes and the
            // union is a real snapshot rather than a walk over a moving collection.
            DataStoreOwnershipSnapshot snapshot = new(++_ownershipVersion, ProjectConfiguredOwners());

            foreach (IDataStoreOwnershipReconciler reconciler in _reconcilers)
            {
                try
                {
                    reconciler.Reconcile(snapshot);
                }
                catch (Exception exception)
                {
                    // One failing reconciler must not fail the load or stop the next one: the
                    // configuration is already published and correct, and a reconciler that could not
                    // act on it will be handed the full authoritative snapshot again by the next
                    // publication.
                    //
                    // Only the exception's type is logged, never the exception itself and never its
                    // message, data, or inner exceptions. A reconciler is arbitrary code that was just
                    // handed every configured connection string; an exception it throws is exactly the
                    // kind of value likely to quote one back, and passing it to the logger would put
                    // that in the log.
                    // S6667 asks for the caught exception to be passed to the logger. That is the
                    // right default and the wrong thing here, for the reason above: the exception is
                    // the untrusted value. Its type is logged instead, which is the part that helps
                    // an operator without carrying anything the reconciler put in it.
#pragma warning disable S6667
                    logger.LogWarning(
                        "Ownership reconciler {Reconciler} failed with {ExceptionType} for tenant {Tenant} at ownership version {Version}. Configuration was published; the next publication will deliver the complete snapshot again",
                        LoggingSanitizer.SanitizeForLogging(reconciler.GetType().Name),
                        LoggingSanitizer.SanitizeForLogging(exception.GetType().Name),
                        LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)"),
                        snapshot.Version
                    );
#pragma warning restore S6667
                }
            }
        }
        finally
        {
            _publicationLock.Release();
        }
    }

    /// <summary>
    /// Every configured target of every loaded tenant: one owner per data store with a non-blank
    /// connection string, plus one per recognized derivative.
    /// </summary>
    /// <remarks>
    /// The union spans tenants because a consumer deciding what it may stop owning cannot answer that
    /// from one tenant alone - a connection string this tenant dropped may still be claimed by
    /// another. Strings are copied verbatim and nothing is parsed, so a provider-invalid derivative is
    /// published like any other owner and cannot abort a publication.
    ///
    /// Called only under the publication lock.
    /// </remarks>
    private ImmutableArray<ConfiguredTargetOwner> ProjectConfiguredOwners()
    {
        ImmutableArray<ConfiguredTargetOwner>.Builder owners =
            ImmutableArray.CreateBuilder<ConfiguredTargetOwner>();

        foreach (KeyValuePair<string, TenantCacheEntry> tenantEntry in _instancesByTenant)
        {
            foreach (DataStore dataStore in tenantEntry.Value.Instances)
            {
                if (!string.IsNullOrWhiteSpace(dataStore.ConnectionString))
                {
                    owners.Add(
                        new ConfiguredTargetOwner(
                            tenantEntry.Key,
                            dataStore.Id,
                            EffectiveTargetKind.Primary,
                            dataStore.ConnectionString
                        )
                    );
                }

                foreach (KeyValuePair<DataStoreDerivativeType, string> derivative in dataStore.Derivatives)
                {
                    owners.Add(
                        new ConfiguredTargetOwner(
                            tenantEntry.Key,
                            dataStore.Id,
                            derivative.Key == DataStoreDerivativeType.Snapshot
                                ? EffectiveTargetKind.Snapshot
                                : EffectiveTargetKind.ReadReplica,
                            derivative.Value
                        )
                    );
                }
            }
        }

        return owners.ToImmutable();
    }

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
        const string TenantsEndpoint = "v3/tenants/";

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
    /// Fetches data stores from the Configuration Service API
    /// </summary>
    private async Task<IList<DataStore>> FetchDataStores(
        string configurationServiceToken,
        string? tenant,
        CancellationToken cancellationToken
    )
    {
        const string DataStoresEndpoint = "v3/dataStores/";

        logger.LogDebug("Sending GET request to {Endpoint}", DataStoresEndpoint);

        using var request = new HttpRequestMessage(HttpMethod.Get, DataStoresEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configurationServiceToken);
        if (!string.IsNullOrEmpty(tenant))
        {
            request.Headers.Add(TenantHeaderName, tenant);
        }
        HttpResponseMessage response = await configurationServiceApiClient
            .Client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Configuration Service returned status code {StatusCode} for data stores endpoint",
                response.StatusCode
            );
        }

        response.EnsureSuccessStatusCode();

        string dataStoresJson = await response.Content.ReadAsStringAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogDebug(
            "Received response from Configuration Service, deserializing {ByteCount} bytes",
            dataStoresJson.Length
        );

        List<DataStoreResponse>? dataStoreResponses = JsonSerializer.Deserialize<List<DataStoreResponse>>(
            dataStoresJson,
            _jsonOptions
        );

        if (dataStoreResponses == null)
        {
            logger.LogWarning("Deserialization returned null - treating as empty instance list");
            return [];
        }

        return dataStoreResponses.Select(response => BuildDataStore(response, tenant)).ToList();
    }

    /// <summary>
    /// Builds one data store from its Configuration Service response.
    /// </summary>
    private DataStore BuildDataStore(DataStoreResponse response, string? tenant)
    {
        (
            RelationalProviderToken? relationalProviderToken,
            RelationalProviderMetadataStatus relationalProviderMetadataStatus
        ) = NormalizeRelationalProviderMetadata(response);

        return new DataStore(
            response.Id,
            response.DataStoreType,
            response.Name,
            // The primary connection string is decrypted here, inside the enclosing projection, so an
            // undecryptable primary fails the whole tenant data-store load. Derivatives are decrypted
            // one at a time in their own fault boundary below, so an unusable optional derivative
            // cannot take its parent, its siblings, or another data store down with it.
            _connectionStringDecryptionService.DecryptFromBase64(response.ConnectionString),
            response.DataStoreContexts.ToDictionary(
                rc => new RouteQualifierName(rc.ContextKey),
                rc => new RouteQualifierValue(rc.ContextValue)
            ),
            relationalProviderToken,
            relationalProviderMetadataStatus,
            BuildDerivatives(response, tenant)
        );
    }

    /// <summary>
    /// Builds the usable derivatives of one data store. A derivative is usable only when its type is
    /// recognized, its stored connection string is present and non-blank, and that string decrypts to a
    /// non-blank value. Every other state means the derivative is not configured.
    /// </summary>
    private List<KeyValuePair<DataStoreDerivativeType, string>> BuildDerivatives(
        DataStoreResponse response,
        string? tenant
    )
    {
        List<KeyValuePair<DataStoreDerivativeType, string>> derivatives = [];
        string sanitizedTenant = LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)");

#pragma warning disable S3267 // Loops should be simplified with "LINQ" expressions - False positive: this loop has several early exits, per-item logging, and duplicate detection against what it has already accepted
        foreach (DataStoreDerivativeItem derivative in response.DataStoreDerivatives)
#pragma warning restore S3267
        {
            if (
                !DataStoreDerivativeTypeNames.TryParseExact(
                    derivative.DerivativeType,
                    out DataStoreDerivativeType derivativeType
                )
            )
            {
                logger.LogError(
                    "Ignoring a data store derivative with unrecognized type '{DerivativeType}' for tenant {Tenant}, parent data store {DataStoreId}",
                    LoggingSanitizer.SanitizeForLogging(derivative.DerivativeType ?? "(none)"),
                    sanitizedTenant,
                    response.Id
                );
                continue;
            }

            // Checked before decryption. A missing row and a null, empty, or whitespace stored value are
            // ordinary not-configured states rather than configuration defects, so they are not errors
            // and are deliberately distinguishable from the undecryptable case by producing no log.
            if (string.IsNullOrWhiteSpace(derivative.ConnectionString))
            {
                continue;
            }

            string plainText;

            try
            {
                // The decryption service returns null only for a null or empty input, which the check
                // above has already excluded, so normalizing to an empty string here changes no
                // outcome: the blank check below treats null, empty, and whitespace alike as not
                // configured, and this keeps the local non-nullable.
                plainText =
                    _connectionStringDecryptionService.DecryptFromBase64(derivative.ConnectionString)
                    ?? string.Empty;
            }
            catch (InvalidOperationException ex)
            {
                // The decryption service wraps invalid Base64, a payload no longer than the IV, and a
                // wrong-key failure in this one exception type. Catching only it keeps an unrelated
                // runtime or programming defect from being reinterpreted as absent configuration.
                // Nothing about the ciphertext, the plaintext, the key, or any connection string is
                // logged; the tenant, parent data store, and derivative type identify the bad row.
                logger.LogError(
                    ex,
                    "Unable to decrypt the connection string for the {DerivativeType} derivative of tenant {Tenant}, parent data store {DataStoreId}. That derivative is treated as not configured; the parent data store and its remaining derivatives are unaffected",
                    derivativeType,
                    sanitizedTenant,
                    response.Id
                );
                continue;
            }

            if (string.IsNullOrWhiteSpace(plainText))
            {
                continue;
            }

            if (derivatives.Exists(accepted => accepted.Key == derivativeType))
            {
                logger.LogError(
                    "Ignoring a duplicate {DerivativeType} derivative for tenant {Tenant}, parent data store {DataStoreId}, and retaining the first. At most one derivative of each type may exist per data store, so a duplicate is a violated configuration invariant rather than a supported way to replace a derivative",
                    derivativeType,
                    sanitizedTenant,
                    response.Id
                );
                continue;
            }

            derivatives.Add(new KeyValuePair<DataStoreDerivativeType, string>(derivativeType, plainText));
        }

        return derivatives;
    }

    private static (
        RelationalProviderToken? Token,
        RelationalProviderMetadataStatus Status
    ) NormalizeRelationalProviderMetadata(DataStoreResponse response)
    {
        string? providerMetadata = response.ProviderToken;
        if (string.IsNullOrWhiteSpace(providerMetadata))
        {
            providerMetadata = response.RelationalProviderToken;
        }
        if (string.IsNullOrWhiteSpace(providerMetadata))
        {
            providerMetadata = response.Provider;
        }

        if (string.IsNullOrWhiteSpace(providerMetadata))
        {
            return (null, RelationalProviderMetadataStatus.Missing);
        }

        return RelationalProviderToken.TryNormalize(providerMetadata, out RelationalProviderToken? token)
            ? (token, RelationalProviderMetadataStatus.Supported)
            : (null, RelationalProviderMetadataStatus.Unknown);
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
    private sealed class DataStoreResponse
    {
        public long Id { get; init; } = 0;
        public string DataStoreType { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? ConnectionString { get; init; } = null;
        public string? ProviderToken { get; init; } = null;
        public string? RelationalProviderToken { get; init; } = null;
        public string? Provider { get; init; } = null;
        public IList<DataStoreContextItem> DataStoreContexts { get; init; } = [];
        public IList<DataStoreDerivativeItem> DataStoreDerivatives { get; init; } = [];
    }

    /// <summary>
    /// Response model for route context items within a data store response
    /// </summary>
    private sealed class DataStoreContextItem
    {
        public long Id { get; init; } = 0;
        public long DataStoreId { get; init; } = 0;
        public string ContextKey { get; init; } = string.Empty;
        public string ContextValue { get; init; } = string.Empty;
    }

    /// <summary>
    /// Response model for derivative items within a data store response. Both string members are
    /// nullable because the Configuration Service may omit either, and because both absence and a null
    /// value carry meaning: an unrecognized or missing type is ignored, and a missing connection string
    /// means that derivative is not configured. The connection string arrives Base64-encoded and
    /// encrypted, exactly like the parent's.
    /// </summary>
    private sealed class DataStoreDerivativeItem
    {
        public long Id { get; init; } = 0;
        public long DataStoreId { get; init; } = 0;
        public string? DerivativeType { get; init; } = null;
        public string? ConnectionString { get; init; } = null;
    }

    /// <summary>
    /// Response model for tenant data from the Configuration Service API
    /// </summary>
    private sealed class TenantResponse
    {
        public long Id { get; init; } = 0;
        public string Name { get; init; } = string.Empty;
    }

    private sealed record TenantCacheEntry(IList<DataStore> Instances, DateTimeOffset LastRefreshed);
}
