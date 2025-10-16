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
    private NpgsqlDataSource? _cachedDataSource;
    private long? _cachedInstanceId;

    /// <summary>
    /// Gets the NpgsqlDataSource for the current request's DMS instance connection string.
    /// Caches the result for the lifetime of this provider instance (scoped to the request).
    /// Cache is invalidated if the selected instance changes.
    /// </summary>
    public NpgsqlDataSource DataSource
    {
        get
        {
            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();

            // If we have a cached data source and it's for the same instance, return it
            if (_cachedDataSource != null && _cachedInstanceId == selectedInstance.Id)
            {
                return _cachedDataSource;
            }

            // Selected instance changed or no cache yet - get/create the data source
            string connectionString = selectedInstance.ConnectionString!;

            logger.LogDebug("NpgsqlDataSourceProvider using instance {InstanceId}", selectedInstance.Id);

            _cachedDataSource = dataSourceCache.GetOrCreate(connectionString);
            _cachedInstanceId = selectedInstance.Id;
            return _cachedDataSource;
        }
    }
}
