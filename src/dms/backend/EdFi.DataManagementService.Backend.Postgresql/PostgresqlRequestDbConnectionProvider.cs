// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlRequestDbConnectionProvider(
    IRequestConnectionProvider requestConnectionProvider,
    PostgresqlDataSourceCache dataSourceCache,
    ILogger<PostgresqlRequestDbConnectionProvider> logger
) : IPostgresqlDbConnectionProvider
{
    private readonly Dictionary<DmsInstanceId, NpgsqlDataSource> _cachedDataSources = [];
    private readonly IRequestConnectionProvider _requestConnectionProvider =
        requestConnectionProvider ?? throw new ArgumentNullException(nameof(requestConnectionProvider));
    private readonly PostgresqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly ILogger<PostgresqlRequestDbConnectionProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal NpgsqlDataSource GetDataSource()
    {
        RequestConnection requestConnection = _requestConnectionProvider.GetRequestConnection();

        if (_cachedDataSources.TryGetValue(requestConnection.DmsInstanceId, out NpgsqlDataSource? dataSource))
        {
            return dataSource;
        }

        _logger.LogDebug(
            "PostgreSQL request-scoped provider caching data source for instance {InstanceId}",
            requestConnection.DmsInstanceId.Value
        );

        dataSource = _dataSourceCache.GetOrCreate(requestConnection.ConnectionString);
        _cachedDataSources[requestConnection.DmsInstanceId] = dataSource;

        return dataSource;
    }
}
