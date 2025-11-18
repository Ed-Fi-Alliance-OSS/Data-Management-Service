// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Singleton cache for NpgsqlDataSource instances keyed by connection string.
/// Ensures proper connection pooling by reusing data sources for identical connection strings.
/// </summary>
public sealed class NpgsqlDataSourceCache(
    IHostApplicationLifetime applicationLifetime,
    ILogger<NpgsqlDataSourceCache> logger
) : IDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _cache = new();
    private bool _disposed;
    private readonly ILogger<NpgsqlDataSourceCache> _logger = logger;

    // Register disposal on application shutdown
    private readonly CancellationTokenRegistration _registration =
        applicationLifetime.ApplicationStopping.Register(() => { });

    /// <summary>
    /// Gets or creates an NpgsqlDataSource for the specified connection string.
    /// </summary>
    public NpgsqlDataSource GetOrCreate(string connectionString)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(NpgsqlDataSourceCache));

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return _cache.GetOrAdd(
            connectionString,
            cs =>
            {
                _logger.LogDebug(
                    "Creating new NpgsqlDataSource for connection string hash: {Hash}",
                    cs.GetHashCode()
                );

                NpgsqlDataSourceBuilder builder = new(cs);
                var csb = builder.ConnectionStringBuilder;

                // Skip RESET/DISCARD when returning pooled connections, we manage session state explicitly.
                csb.NoResetOnClose = true;

                // Make PostgreSQL monitoring output more readable
                if (string.IsNullOrWhiteSpace(csb.ApplicationName))
                {
                    csb.ApplicationName = "EdFi.DMS";
                }

                // Let Npgsql handle plan caching automatically
                csb.AutoPrepareMinUsages = 3;
                csb.MaxAutoPrepare = 256;

                return builder.Build();
            }
        );
    }

    /// <summary>
    /// Disposes all cached NpgsqlDataSource instances.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _logger.LogInformation("Disposing {Count} cached NpgsqlDataSource instances", _cache.Count);

        foreach (var dataSource in _cache.Values)
        {
            try
            {
                dataSource.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing NpgsqlDataSource");
            }
        }

        _cache.Clear();
        _registration.Dispose();
    }
}
