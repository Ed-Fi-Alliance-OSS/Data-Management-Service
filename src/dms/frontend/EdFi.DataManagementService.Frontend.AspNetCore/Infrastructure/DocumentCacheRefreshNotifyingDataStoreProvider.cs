// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

internal sealed class DocumentCacheRefreshNotifyingDataStoreProvider : IDataStoreProvider
{
    private readonly IDataStoreProvider _dataStoreProvider;
    private readonly IDocumentCacheProjectionRefreshSignal _projectionRefreshSignal;
    private readonly ILogger<DocumentCacheRefreshNotifyingDataStoreProvider> _logger;
    private readonly Func<bool> _canNotify;

    public DocumentCacheRefreshNotifyingDataStoreProvider(
        ConfigurationServiceDataStoreProvider dataStoreProvider,
        IDocumentCacheProjectionRefreshSignal projectionRefreshSignal,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<DocumentCacheRefreshNotifyingDataStoreProvider> logger
    )
    {
        _dataStoreProvider = dataStoreProvider ?? throw new ArgumentNullException(nameof(dataStoreProvider));
        _projectionRefreshSignal =
            projectionRefreshSignal ?? throw new ArgumentNullException(nameof(projectionRefreshSignal));
        IHostApplicationLifetime lifetime =
            hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canNotify = () => lifetime.ApplicationStarted.IsCancellationRequested;
    }

    internal DocumentCacheRefreshNotifyingDataStoreProvider(
        IDataStoreProvider dataStoreProvider,
        IDocumentCacheProjectionRefreshSignal projectionRefreshSignal,
        ILogger<DocumentCacheRefreshNotifyingDataStoreProvider> logger,
        Func<bool>? canNotify = null
    )
    {
        _dataStoreProvider = dataStoreProvider ?? throw new ArgumentNullException(nameof(dataStoreProvider));
        _projectionRefreshSignal =
            projectionRefreshSignal ?? throw new ArgumentNullException(nameof(projectionRefreshSignal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canNotify = canNotify ?? (() => true);
    }

    public async Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<DataStore> beforeLoad = _dataStoreProvider.GetAll(tenant);

        IList<DataStore> dataStores = await _dataStoreProvider
            .LoadDataStores(tenant, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<DataStore> afterLoad = _dataStoreProvider.GetAll(tenant);
        if (!DataStoreMetadataEquals(beforeLoad, afterLoad))
        {
            NotifyDocumentCacheProjectionSupervisor(tenant);
        }

        return dataStores;
    }

    public async Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<DataStore> beforeRefresh = _dataStoreProvider.GetAll(tenant);

        await _dataStoreProvider
            .RefreshInstancesIfExpiredAsync(tenant, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<DataStore> afterRefresh = _dataStoreProvider.GetAll(tenant);
        if (!DataStoreMetadataEquals(beforeRefresh, afterRefresh))
        {
            NotifyDocumentCacheProjectionSupervisor(tenant);
        }
    }

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) => _dataStoreProvider.GetAll(tenant);

    public DataStore? GetById(long id, string? tenant = null) => _dataStoreProvider.GetById(id, tenant);

    public bool IsLoaded(string? tenant = null) => _dataStoreProvider.IsLoaded(tenant);

    public Task<IList<string>> LoadTenants() => _dataStoreProvider.LoadTenants();

    public bool TenantExists(string tenant) => _dataStoreProvider.TenantExists(tenant);

    public IReadOnlyList<string> GetLoadedTenantKeys() => _dataStoreProvider.GetLoadedTenantKeys();

    private void NotifyDocumentCacheProjectionSupervisor(string? tenant)
    {
        if (!_canNotify())
        {
            return;
        }

        try
        {
            _projectionRefreshSignal.SignalRefresh();
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
        && RouteContextEquals(left.RouteContext, right.RouteContext)
        && DerivativesEqual(left.Derivatives, right.Derivatives);

    /// <summary>
    /// Compares configured derivative connection strings by content. A change confined to a derivative
    /// is a data store metadata change like any other, so leaving it out of this comparison would let a
    /// refresh that only replaced or removed a derivative pass unnoticed. This is an in-memory ordinal
    /// comparison of already-loaded configuration: it opens no database connection, and connection
    /// strings are compared but never logged.
    /// </summary>
    private static bool DerivativesEqual(
        ImmutableDictionary<DataStoreDerivativeType, string> left,
        ImmutableDictionary<DataStoreDerivativeType, string> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((DataStoreDerivativeType type, string leftConnectionString) in left)
        {
            if (
                !right.TryGetValue(type, out string? rightConnectionString)
                || !string.Equals(leftConnectionString, rightConnectionString, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }

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
