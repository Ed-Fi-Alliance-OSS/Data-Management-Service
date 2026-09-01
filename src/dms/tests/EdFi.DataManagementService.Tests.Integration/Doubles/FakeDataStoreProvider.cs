// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <param name="Derivatives">
/// The derivative connection strings this data store publishes, exactly as the Configuration Service
/// would state them. Empty for a data store with no snapshot and no read replica.
/// </param>
/// <param name="RouteContext">
/// The route qualifiers this data store answers for, exactly as the Configuration Service would state
/// them. Empty for a data store reached without any qualifier, which is what every existing fixture
/// uses.
/// </param>
internal sealed record FakeDataStoreDefinition(
    long Id,
    string ConnectionString,
    RelationalProviderToken? RelationalProviderToken = null,
    IReadOnlyDictionary<DataStoreDerivativeType, string>? Derivatives = null,
    IReadOnlyDictionary<RouteQualifierName, RouteQualifierValue>? RouteContext = null
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
        RelationalProviderToken? relationalProviderToken = null,
        IReadOnlyDictionary<DataStoreDerivativeType, string>? derivatives = null
    ) =>
        WithInstances([
            new FakeDataStoreDefinition(id, connectionString, relationalProviderToken, derivatives),
        ]);

    public static IDataStoreProvider WithInstances(IReadOnlyList<FakeDataStoreDefinition> instances) =>
        new StaticInstanceProvider(instances);

    /// <summary>
    /// A provider whose configuration a test can replace between requests, so that adding, replacing,
    /// and removing a derivative are observable through the same refresh path production uses.
    /// </summary>
    public static MutableInstanceProvider Mutable(IReadOnlyList<FakeDataStoreDefinition> instances) =>
        new(instances);

    internal static DataStore ToDataStore(FakeDataStoreDefinition instance) =>
        new(
            Id: instance.Id,
            DataStoreType: "default",
            Name: $"integration-test-{instance.Id}",
            ConnectionString: instance.ConnectionString,
            RouteContext: instance.RouteContext is null
                ? new Dictionary<RouteQualifierName, RouteQualifierValue>()
                : new Dictionary<RouteQualifierName, RouteQualifierValue>(instance.RouteContext),
            RelationalProviderToken: instance.RelationalProviderToken,
            RelationalProviderMetadataStatus: instance.RelationalProviderToken is null
                ? RelationalProviderMetadataStatus.Missing
                : RelationalProviderMetadataStatus.Supported,
            DerivativeConnectionStrings: instance.Derivatives
        );

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

            _instances = [.. instances.Select(ToDataStore)];
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

/// <summary>
/// The same stub, with the published configuration held behind a volatile reference a test can
/// replace. Replacing it is what makes derivative replacement and removal observable end to end: a
/// request in flight keeps reading the configuration it started with, and the next request - or an
/// explicit refresh - observes the new one.
/// </summary>
internal sealed class MutableInstanceProvider : IDataStoreProvider
{
    private const string DefaultTenantKey = "";

    private IReadOnlyList<DataStore> _instances;
    private int _refreshCount;

    public MutableInstanceProvider(IReadOnlyList<FakeDataStoreDefinition> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0)
        {
            throw new ArgumentException("At least one data store must be supplied.", nameof(instances));
        }

        _instances = [.. instances.Select(FakeDataStoreProvider.ToDataStore)];
    }

    /// <summary>How many times the host asked this provider to refresh.</summary>
    public int RefreshCount => Volatile.Read(ref _refreshCount);

    /// <summary>
    /// Publishes a new configuration. Written as a whole-list replacement rather than a mutation, so a
    /// reader that captured the previous list keeps a consistent view of it.
    /// </summary>
    public void Publish(IReadOnlyList<FakeDataStoreDefinition> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0)
        {
            throw new ArgumentException("At least one data store must be supplied.", nameof(instances));
        }

        Volatile.Write(ref _instances, [.. instances.Select(FakeDataStoreProvider.ToDataStore)]);
    }

    private IReadOnlyList<DataStore> Current => Volatile.Read(ref _instances);

    public Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IList<DataStore>>([.. Current]);

    public Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _refreshCount);
        return Task.CompletedTask;
    }

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) => Current;

    public DataStore? GetById(long id, string? tenant = null) =>
        Current.FirstOrDefault(instance => instance.Id == id);

    public bool IsLoaded(string? tenant = null) => true;

    public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([DefaultTenantKey]);

    public bool TenantExists(string tenant) => true;

    public IReadOnlyList<string> GetLoadedTenantKeys() => [DefaultTenantKey];
}
