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
/// by reading the effective target the request selected and using the singleton cache.
/// Uses a Dictionary cache to handle potential scope issues where the provider may be
/// used across different target contexts.
/// </summary>
public sealed class NpgsqlDataSourceProvider(
    IDataStoreSelection dataStoreSelection,
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<NpgsqlDataSourceProvider> logger
)
{
    private readonly Dictionary<string, NpgsqlDataSource> _cachedDataSources = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the NpgsqlDataSource for the target the current request selected.
    /// Reads the target on each access to handle cases where selection may occur in a
    /// different scope context.
    /// </summary>
    /// <remarks>
    /// Keyed by the target's connection string rather than the parent's id, because a parent and its
    /// derivatives share an id but are different databases. Reading the target throws when none was
    /// selected; there is deliberately no fallback to the parent.
    /// </remarks>
    public NpgsqlDataSource DataSource
    {
        get
        {
            // Always read the current target to handle potential scope issues
            var target = dataStoreSelection.GetEffectiveTarget();

            // Check if we've already cached this target's data source
            if (_cachedDataSources.TryGetValue(target.ConnectionString, out var cachedDataSource))
            {
                return cachedDataSource;
            }

            logger.LogDebug(
                "NpgsqlDataSourceProvider caching data source for a {TargetKind} target",
                target.Kind
            );

            var dataSource = dataSourceCache.GetOrCreate(target.ConnectionString);
            _cachedDataSources[target.ConnectionString] = dataSource;

            return dataSource;
        }
    }
}
