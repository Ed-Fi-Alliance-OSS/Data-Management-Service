// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class RelationalBackendIntegrationTestDataStoreExtensions
{
    public static IServiceCollection AddSelectedDataStoreIntegrationTestProvider(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IDataStoreSelection, DataStoreSelection>();
        services.TryAddScoped<IDataStoreProvider, SelectedDataStoreProvider>();
        services.TryAddScoped<IConnectionStringProvider, DmsConnectionStringProvider>();

        return services;
    }

    private sealed class SelectedDataStoreProvider(IDataStoreSelection dataStoreSelection)
        : IDataStoreProvider
    {
        private const string DefaultTenantKey = "";

        public Task<IList<DataStore>> LoadDataStores(string? tenant = null) =>
            Task.FromResult<IList<DataStore>>(GetAll(tenant).ToList());

        public Task RefreshInstancesIfExpiredAsync(string? tenant = null) => Task.CompletedTask;

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
            dataStoreSelection.IsSet ? [dataStoreSelection.GetSelectedDataStore()] : [];

        public DataStore? GetById(long id, string? tenant = null)
        {
            if (!dataStoreSelection.IsSet)
            {
                return null;
            }

            var selectedDataStore = dataStoreSelection.GetSelectedDataStore();

            return selectedDataStore.Id == id ? selectedDataStore : null;
        }

        public bool IsLoaded(string? tenant = null) => dataStoreSelection.IsSet;

        public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([DefaultTenantKey]);

        public bool TenantExists(string tenant) => true;

        public IReadOnlyList<string> GetLoadedTenantKeys() => [DefaultTenantKey];
    }
}
