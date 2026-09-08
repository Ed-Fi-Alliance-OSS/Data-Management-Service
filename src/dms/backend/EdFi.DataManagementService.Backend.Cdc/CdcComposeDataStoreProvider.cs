// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Host-side local Compose address translation after authoritative CMS selection/decryption.
/// Changes only the known container endpoint; database, credentials and source identity remain
/// CMS-owned. The HTTP host never registers this adapter.
/// </summary>
public sealed class CdcComposeDataStoreProvider(
    IDataStoreProvider inner,
    DocumentCacheTargetKey target,
    string provider,
    int hostPort
) : IDataStoreProvider
{
    public static void Register(
        IServiceCollection services,
        IConfiguration configuration,
        DocumentCacheTargetKey target
    )
    {
        if (configuration["Cdc:Compose:DatabaseHostPort"] is not { } portText)
        {
            return;
        }
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("CDC Compose database endpoint is invalid.");
        }
        var descriptor = services.Single(d => d.ServiceType == typeof(IDataStoreProvider));
        services.Remove(descriptor);
        services.AddSingleton<IDataStoreProvider>(sp => new CdcComposeDataStoreProvider(
            (IDataStoreProvider)descriptor.ImplementationFactory!(sp),
            target,
            configuration["AppSettings:Datastore"]!,
            port
        ));
    }

    public async Task<IList<DataStore>> LoadDataStores(
        [AllowNull] string tenant = null!,
        CancellationToken cancellationToken = default
    ) => Select(await inner.LoadDataStores(tenant, cancellationToken), tenant ?? "").ToList();

    public Task RefreshInstancesIfExpiredAsync(
        [AllowNull] string tenant = null!,
        CancellationToken cancellationToken = default
    ) => inner.RefreshInstancesIfExpiredAsync(tenant, cancellationToken);

    public IReadOnlyList<DataStore> GetAll([AllowNull] string tenant = null!) =>
        Select(inner.GetAll(tenant), tenant ?? "");

    public DataStore GetById(long id, [AllowNull] string tenant = null!) =>
        GetAll(tenant).SingleOrDefault(d => d.Id == id)!;

    public bool IsLoaded([AllowNull] string tenant = null!) => inner.IsLoaded(tenant);

    public Task<IList<string>> LoadTenants() => inner.LoadTenants();

    public bool TenantExists(string tenant) => inner.TenantExists(tenant);

    public IReadOnlyList<string> GetLoadedTenantKeys() => inner.GetLoadedTenantKeys();

    private IReadOnlyList<DataStore> Select(IEnumerable<DataStore> stores, string tenant) =>
        stores
            .Where(d => target.Equals(DocumentCacheTargetKey.Create(tenant ?? "", d.Id)))
            .Select(Translate)
            .ToArray();

    private DataStore Translate(DataStore store)
    {
        if (provider == "postgresql")
        {
            var connection = new NpgsqlConnectionStringBuilder(store.ConnectionString);
            if (connection.Host != "dms-postgresql" || connection.Port != 5432)
            {
                throw new InvalidOperationException(
                    "CDC CMS target is outside the selected Compose endpoint."
                );
            }
            connection.Host = "127.0.0.1";
            connection.Port = hostPort;
            return store with { ConnectionString = connection.ConnectionString };
        }
        if (provider != "mssql")
        {
            throw new InvalidOperationException("CDC Compose provider is invalid.");
        }
        var sql = new SqlConnectionStringBuilder(store.ConnectionString);
        if (sql.DataSource is not ("dms-mssql,1433" or "dms-mssql" or "tcp:dms-mssql,1433"))
        {
            throw new InvalidOperationException("CDC CMS target is outside the selected Compose endpoint.");
        }
        sql.DataSource = $"127.0.0.1,{hostPort}";
        return store with { ConnectionString = sql.ConnectionString };
    }
}
