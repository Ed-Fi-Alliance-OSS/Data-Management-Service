// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Scoped service that provides the appropriate NpgsqlDataSource for the current request
/// by retrieving the per-request connection string and using the singleton cache.
/// </summary>
public sealed class NpgsqlDataSourceProvider(
    IRequestConnectionStringProvider requestConnectionStringProvider,
    NpgsqlDataSourceCache dataSourceCache
)
{
    private NpgsqlDataSource? _dataSource;

    /// <summary>
    /// Gets the NpgsqlDataSource for the current request's connection string.
    /// Lazily initialized on first access.
    /// </summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ??= dataSourceCache.GetOrCreate(requestConnectionStringProvider.GetConnectionString());
}
