// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Old.Postgresql;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlRelationalWriteSessionFactory : IRelationalWriteSessionFactory
{
    private readonly Func<CancellationToken, Task<DbConnection>> _openConnectionAsync;
    private readonly IsolationLevel _isolationLevel;

    public PostgresqlRelationalWriteSessionFactory(
        NpgsqlDataSourceProvider dataSourceProvider,
        IOptions<DatabaseOptions> databaseOptions
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceProvider);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        _isolationLevel = databaseOptions.Value.IsolationLevel;
        _openConnectionAsync = async cancellationToken =>
            await dataSourceProvider.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal PostgresqlRelationalWriteSessionFactory(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        IsolationLevel isolationLevel
    )
    {
        _openConnectionAsync =
            openConnectionAsync ?? throw new ArgumentNullException(nameof(openConnectionAsync));
        _isolationLevel = isolationLevel;
    }

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _openConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var transaction = await connection
                .BeginTransactionAsync(_isolationLevel, cancellationToken)
                .ConfigureAwait(false);
            return new RelationalWriteSession(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
