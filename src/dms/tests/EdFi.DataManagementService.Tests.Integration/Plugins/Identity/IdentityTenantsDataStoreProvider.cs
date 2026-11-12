// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A data store provider that knows tenants, loads no data stores, and answers one route-context
/// data store per district only through <see cref="GetAll"/>.
/// </summary>
/// <remarks>
/// <para>
/// The identity pipeline resolves tenant existence through <see cref="LoadTenants"/> alone and maps
/// no data store step. Loading no data stores and reporting no loaded tenant keys is what lets the
/// host start and serve identity requests without anything dialing a database: the startup
/// instance validation and the connection-string provider walk <see cref="GetLoadedTenantKeys"/>.
/// </para>
/// <para>
/// With multi-tenancy on, the metadata routes answer only under the full route context, and the
/// route validator accepts a request only when <see cref="GetAll"/> holds a data store whose route
/// context matches its district and school year. One data store per district, with no connection
/// string, is what lets a case read the identity OpenAPI document under the tenant's route.
/// </para>
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

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
        tenant is not null && tenants.Contains(tenant, StringComparer.Ordinal)
            ? RouteContextDataStoresOf(tenant)
            : [];

    /// <summary>
    /// One data store per district of the tenant, carrying the district and the shared school year
    /// under the configured route qualifier names.
    /// </summary>
    private IReadOnlyList<DataStore> RouteContextDataStoresOf(string tenant)
    {
        string[] qualifierNames = IdentityTestClients.RouteQualifierSegments.Split(',');
        int tenantIndex = tenants.ToList().IndexOf(tenant);

        return IdentityTestClients
            .DistrictsOf(tenant)
            .Select(
                (district, districtIndex) =>
                    new DataStore(
                        Id: (tenantIndex * 100) + districtIndex + 1,
                        DataStoreType: "Test",
                        Name: $"{tenant}-{district}",
                        ConnectionString: null,
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName(qualifierNames[0])] = new RouteQualifierValue(district),
                            [new RouteQualifierName(qualifierNames[1])] = new RouteQualifierValue(
                                IdentityTestClients.SchoolYear
                            ),
                        }
                    )
            )
            .ToList();
    }

    public DataStore? GetById(long id, string? tenant = null) => null;

    public bool IsLoaded(string? tenant = null) => true;

    public Task<IList<string>> LoadTenants(CancellationToken cancellationToken = default) =>
        Task.FromResult<IList<string>>([.. tenants]);

    public bool TenantExists(string tenant) => true;

    public IReadOnlyList<string> GetLoadedTenantKeys() => [];
}
