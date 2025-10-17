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

    /// <summary>
    /// Gets the NpgsqlDataSource for the current request's DMS instance.
    /// Caches the data source for the lifetime of this scoped provider instance.
    /// Since the provider is truly scoped, the selected instance won't change during the request.
    /// </summary>
    public NpgsqlDataSource DataSource
    {
        get
        {
            // Service is truly scoped, so selectedInstance won't change during request
            if (_cachedDataSource != null)
            {
                return _cachedDataSource;
            }

            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();
            string connectionString = selectedInstance.ConnectionString!;

            logger.LogDebug(
                "NpgsqlDataSourceProvider caching data source for instance {InstanceId}",
                selectedInstance.Id
            );

            _cachedDataSource = dataSourceCache.GetOrCreate(connectionString);
            return _cachedDataSource;
        }
    }
}
