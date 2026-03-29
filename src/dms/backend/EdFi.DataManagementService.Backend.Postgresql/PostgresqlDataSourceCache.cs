// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDataSourceCache(ILogger<PostgresqlDataSourceCache> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _cache = new();
    private readonly ILogger<PostgresqlDataSourceCache> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private bool _disposed;

    public NpgsqlDataSource GetOrCreate(string connectionString)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(PostgresqlDataSourceCache));
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return _cache.GetOrAdd(
            connectionString,
            currentConnectionString =>
            {
                _logger.LogDebug(
                    "Creating new PostgreSQL NpgsqlDataSource for connection string hash: {Hash}",
                    currentConnectionString.GetHashCode()
                );

                NpgsqlDataSourceBuilder builder = new(currentConnectionString);
                NpgsqlConnectionStringBuilder connectionStringBuilder = builder.ConnectionStringBuilder;

                connectionStringBuilder.NoResetOnClose = true;

                if (string.IsNullOrWhiteSpace(connectionStringBuilder.ApplicationName))
                {
                    connectionStringBuilder.ApplicationName = "EdFi.DMS";
                }

                connectionStringBuilder.AutoPrepareMinUsages = 3;
                connectionStringBuilder.MaxAutoPrepare = 256;

                return builder.Build();
            }
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _logger.LogInformation(
            "Disposing {Count} cached PostgreSQL NpgsqlDataSource instances",
            _cache.Count
        );

        foreach (NpgsqlDataSource dataSource in _cache.Values)
        {
            try
            {
                dataSource.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing PostgreSQL NpgsqlDataSource");
            }
        }

        _cache.Clear();
    }
}
