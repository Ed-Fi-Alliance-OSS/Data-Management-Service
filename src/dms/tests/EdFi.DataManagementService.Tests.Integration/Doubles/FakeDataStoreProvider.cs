// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Builds an <see cref="IDataStoreProvider"/> stub that exposes a single in-memory
/// data store pointing at a test-leased database connection string. The instance has
/// no route-qualifier context, so it is returned for any tenant the caller asks about.
/// </summary>
internal static class FakeDataStoreProvider
{
    public static IDataStoreProvider WithSingleInstance(long id, string connectionString) =>
        new SingleInstanceProvider(id, connectionString);

    private sealed class SingleInstanceProvider : IDataStoreProvider
    {
        private const string DefaultTenantKey = "";
        private readonly DataStore _instance;
        private readonly IReadOnlyList<DataStore> _instances;

        public SingleInstanceProvider(long id, string connectionString)
        {
            _instance = new DataStore(
                Id: id,
                DataStoreType: "default",
                Name: "integration-test",
                ConnectionString: connectionString,
                RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
            );
            _instances = [_instance];
        }

        public Task<IList<DataStore>> LoadDataStores(string? tenant = null) =>
            Task.FromResult<IList<DataStore>>([_instance]);

        public Task RefreshInstancesIfExpiredAsync(string? tenant = null) => Task.CompletedTask;

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) => _instances;

        public DataStore? GetById(long id, string? tenant = null) => id == _instance.Id ? _instance : null;

        public bool IsLoaded(string? tenant = null) => true;

        public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([DefaultTenantKey]);

        public bool TenantExists(string tenant) => true;

        public IReadOnlyList<string> GetLoadedTenantKeys() => [DefaultTenantKey];
    }
}
