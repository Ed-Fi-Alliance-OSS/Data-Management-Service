// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlRelationalWriteSessionFactory : IRelationalWriteSessionFactory
{
    private readonly Func<CancellationToken, Task<DbConnection>> _openConnectionAsync;
    private readonly IsolationLevel _isolationLevel;

    public MssqlRelationalWriteSessionFactory(
        IDmsInstanceSelection dmsInstanceSelection,
        IOptions<DatabaseOptions> databaseOptions
    )
        : this(dmsInstanceSelection, connectionString => new SqlConnection(connectionString), databaseOptions)
    { }

    internal MssqlRelationalWriteSessionFactory(
        IDmsInstanceSelection dmsInstanceSelection,
        Func<string, DbConnection> createConnection,
        IOptions<DatabaseOptions> databaseOptions
    )
    {
        ArgumentNullException.ThrowIfNull(dmsInstanceSelection);
        ArgumentNullException.ThrowIfNull(createConnection);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        _isolationLevel = databaseOptions.Value.IsolationLevel;
        _openConnectionAsync = async cancellationToken =>
        {
            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();
            var connectionString = selectedInstance.ConnectionString;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Selected DMS instance '{selectedInstance.Id}' does not have a valid connection string."
                );
            }

            var connection = createConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        };
    }

    internal MssqlRelationalWriteSessionFactory(
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
