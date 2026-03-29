// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.External.Backend;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlRequestDbConnectionProvider(
    IRequestConnectionProvider requestConnectionProvider,
    PostgresqlDataSourceCache dataSourceCache
) : IPostgresqlDbConnectionProvider
{
    private readonly IRequestConnectionProvider _requestConnectionProvider =
        requestConnectionProvider ?? throw new ArgumentNullException(nameof(requestConnectionProvider));
    private readonly PostgresqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal NpgsqlDataSource GetDataSource()
    {
        RequestConnection requestConnection = _requestConnectionProvider.GetRequestConnection();
        return _dataSourceCache.GetOrCreate(requestConnection.ConnectionString);
    }
}
