// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A data store provider that knows tenants and no data stores.
/// </summary>
/// <remarks>
/// The identity pipeline resolves tenant existence through <see cref="LoadTenants"/> alone and maps
/// no data store step. Returning no data stores is what lets the host start and serve identity
/// requests without anything dialing a database.
/// </remarks>
internal sealed class IdentityTenantsDataStoreProvider(IReadOnlyList<string> tenants) : IDataStoreProvider
{
    public Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IList<DataStore>>([]);

    public Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) => [];

    public DataStore? GetById(long id, string? tenant = null) => null;

    public bool IsLoaded(string? tenant = null) => true;

    public Task<IList<string>> LoadTenants(CancellationToken cancellationToken = default) =>
        Task.FromResult<IList<string>>([.. tenants]);

    public bool TenantExists(string tenant) => true;

    public IReadOnlyList<string> GetLoadedTenantKeys() => [];
}
