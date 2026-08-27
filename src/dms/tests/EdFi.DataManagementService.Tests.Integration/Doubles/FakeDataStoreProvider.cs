// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

internal sealed record FakeDataStoreDefinition(
    long Id,
    string ConnectionString,
    RelationalProviderToken? RelationalProviderToken = null
);

/// <summary>
/// Builds an <see cref="IDataStoreProvider"/> stub that exposes in-memory
/// data stores pointing at test-leased database connection strings. The instances
/// have no route-qualifier context, so they are returned for any tenant the caller asks about.
/// </summary>
internal static class FakeDataStoreProvider
{
    public static IDataStoreProvider WithSingleInstance(
        long id,
        string connectionString,
        RelationalProviderToken? relationalProviderToken = null
    ) => WithInstances([new FakeDataStoreDefinition(id, connectionString, relationalProviderToken)]);

    public static IDataStoreProvider WithInstances(IReadOnlyList<FakeDataStoreDefinition> instances) =>
        new StaticInstanceProvider(instances);

    private sealed class StaticInstanceProvider : IDataStoreProvider
    {
        private const string DefaultTenantKey = "";
        private readonly IReadOnlyList<DataStore> _instances;

        public StaticInstanceProvider(IReadOnlyList<FakeDataStoreDefinition> instances)
        {
            ArgumentNullException.ThrowIfNull(instances);
            if (instances.Count == 0)
            {
                throw new ArgumentException("At least one data store must be supplied.", nameof(instances));
            }

            _instances = instances
                .Select(instance => new DataStore(
                    Id: instance.Id,
                    DataStoreType: "default",
                    Name: $"integration-test-{instance.Id}",
                    ConnectionString: instance.ConnectionString,
                    RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>(),
                    RelationalProviderToken: instance.RelationalProviderToken,
                    RelationalProviderMetadataStatus: instance.RelationalProviderToken is null
                        ? RelationalProviderMetadataStatus.Missing
                        : RelationalProviderMetadataStatus.Supported
                ))
                .ToArray();
        }

        public Task<IList<DataStore>> LoadDataStores(
            string? tenant = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IList<DataStore>>([.. _instances]);

        public Task RefreshInstancesIfExpiredAsync(
            string? tenant = null,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) => _instances;

        public DataStore? GetById(long id, string? tenant = null) =>
            _instances.FirstOrDefault(instance => instance.Id == id);

        public bool IsLoaded(string? tenant = null) => true;

        public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([DefaultTenantKey]);

        public bool TenantExists(string tenant) => true;

        public IReadOnlyList<string> GetLoadedTenantKeys() => [DefaultTenantKey];
    }
}
