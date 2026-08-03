// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

internal sealed class DocumentCacheRefreshNotifyingDataStoreProvider : IDataStoreProvider
{
    private readonly IDataStoreProvider _dataStoreProvider;
    private readonly IDocumentCacheProjectionSupervisor _projectionSupervisor;
    private readonly ILogger<DocumentCacheRefreshNotifyingDataStoreProvider> _logger;
    private readonly Func<bool> _canNotify;

    public DocumentCacheRefreshNotifyingDataStoreProvider(
        ConfigurationServiceDataStoreProvider dataStoreProvider,
        IDocumentCacheProjectionSupervisor projectionSupervisor,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<DocumentCacheRefreshNotifyingDataStoreProvider> logger
    )
    {
        _dataStoreProvider = dataStoreProvider ?? throw new ArgumentNullException(nameof(dataStoreProvider));
        _projectionSupervisor =
            projectionSupervisor ?? throw new ArgumentNullException(nameof(projectionSupervisor));
        IHostApplicationLifetime lifetime =
            hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canNotify = () => lifetime.ApplicationStarted.IsCancellationRequested;
    }

    internal DocumentCacheRefreshNotifyingDataStoreProvider(
        IDataStoreProvider dataStoreProvider,
        IDocumentCacheProjectionSupervisor projectionSupervisor,
        ILogger<DocumentCacheRefreshNotifyingDataStoreProvider> logger,
        Func<bool>? canNotify = null
    )
    {
        _dataStoreProvider = dataStoreProvider ?? throw new ArgumentNullException(nameof(dataStoreProvider));
        _projectionSupervisor =
            projectionSupervisor ?? throw new ArgumentNullException(nameof(projectionSupervisor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canNotify = canNotify ?? (() => true);
    }

    public async Task<IList<DataStore>> LoadDataStores(string? tenant = null)
    {
        IList<DataStore> dataStores = await _dataStoreProvider.LoadDataStores(tenant).ConfigureAwait(false);
        await NotifyDocumentCacheProjectionSupervisorAsync(tenant).ConfigureAwait(false);
        return dataStores;
    }

    public async Task RefreshInstancesIfExpiredAsync(string? tenant = null)
    {
        IReadOnlyList<DataStore> beforeRefresh = _dataStoreProvider.GetAll(tenant);

        await _dataStoreProvider.RefreshInstancesIfExpiredAsync(tenant).ConfigureAwait(false);

        IReadOnlyList<DataStore> afterRefresh = _dataStoreProvider.GetAll(tenant);
        if (!DataStoreMetadataEquals(beforeRefresh, afterRefresh))
        {
            await NotifyDocumentCacheProjectionSupervisorAsync(tenant).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) => _dataStoreProvider.GetAll(tenant);

    public DataStore? GetById(long id, string? tenant = null) => _dataStoreProvider.GetById(id, tenant);

    public bool IsLoaded(string? tenant = null) => _dataStoreProvider.IsLoaded(tenant);

    public Task<IList<string>> LoadTenants() => _dataStoreProvider.LoadTenants();

    public bool TenantExists(string tenant) => _dataStoreProvider.TenantExists(tenant);

    public IReadOnlyList<string> GetLoadedTenantKeys() => _dataStoreProvider.GetLoadedTenantKeys();

    private async Task NotifyDocumentCacheProjectionSupervisorAsync(string? tenant)
    {
        if (!_canNotify())
        {
            return;
        }

        try
        {
            await _projectionSupervisor
                .RefreshAsync(DocumentCacheTargetRefreshReason.CmsRefreshNotification)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "DocumentCache target refresh notification failed after data store metadata refresh for tenant {Tenant}. Request data-store refresh will continue.",
                LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")
            );
        }
    }

    private static bool DataStoreMetadataEquals(IReadOnlyList<DataStore> left, IReadOnlyList<DataStore> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        List<DataStore> orderedLeft = left.OrderBy(dataStore => dataStore.Id).ToList();
        List<DataStore> orderedRight = right.OrderBy(dataStore => dataStore.Id).ToList();

        for (int index = 0; index < orderedLeft.Count; index++)
        {
            if (!DataStoreEquals(orderedLeft[index], orderedRight[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DataStoreEquals(DataStore left, DataStore right) =>
        left.Id == right.Id
        && string.Equals(left.DataStoreType, right.DataStoreType, StringComparison.Ordinal)
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.ConnectionString, right.ConnectionString, StringComparison.Ordinal)
        && EqualityComparer<RelationalProviderToken?>.Default.Equals(
            left.RelationalProviderToken,
            right.RelationalProviderToken
        )
        && left.RelationalProviderMetadataStatus == right.RelationalProviderMetadataStatus
        && RouteContextEquals(left.RouteContext, right.RouteContext);

    private static bool RouteContextEquals(
        Dictionary<RouteQualifierName, RouteQualifierValue> left,
        Dictionary<RouteQualifierName, RouteQualifierValue> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (leftName, leftValue) in left.OrderBy(entry => entry.Key.Value))
        {
            if (
                !right.TryGetValue(leftName, out RouteQualifierValue rightValue)
                || !string.Equals(leftValue.Value, rightValue.Value, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }
}
