// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Scoped service that provides the appropriate NpgsqlDataSource for the current request
/// by retrieving the selected DMS instance and using the singleton cache.
/// </summary>
public sealed class NpgsqlDataSourceProvider(
    IDmsInstanceSelection dmsInstanceSelection,
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<NpgsqlDataSourceProvider> logger
)
{
    private readonly Dictionary<long, NpgsqlDataSource> _cachedDataSources = new();

    /// <summary>
    /// Gets the NpgsqlDataSource for the current request's DMS instance.
    /// Caches data sources by instance ID to avoid repeated creation.
    /// Cache persists for the lifetime of this provider instance.
    /// </summary>
    public NpgsqlDataSource DataSource
    {
        get
        {
            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();

            // Check if we've already cached this instance's data source
            if (_cachedDataSources.TryGetValue(selectedInstance.Id, out var cachedDataSource))
            {
                return cachedDataSource;
            }

            // Cache miss - create and cache the data source
            string connectionString = selectedInstance.ConnectionString!;

            logger.LogDebug(
                "NpgsqlDataSourceProvider caching data source for instance {InstanceId}",
                selectedInstance.Id
            );

            var dataSource = dataSourceCache.GetOrCreate(connectionString);
            _cachedDataSources[selectedInstance.Id] = dataSource;

            return dataSource;
        }
    }
}
