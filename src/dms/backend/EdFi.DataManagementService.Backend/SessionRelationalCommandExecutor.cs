// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal sealed class SessionRelationalCommandExecutor(DbConnection connection, DbTransaction transaction)
    : IRelationalCommandExecutor
{
    private readonly DbConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly DbTransaction _transaction =
        transaction ?? throw new ArgumentNullException(nameof(transaction));

    public SqlDialect Dialect { get; } = DetermineDialect(connection);

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);

        await using var dbCommand = SessionRelationalCommandFactory.CreateCommand(
            _connection,
            _transaction,
            command
        );

        await using var reader = new DbRelationalCommandReader(
            await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)
        );

        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static SqlDialect DetermineDialect(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var connectionType = connection.GetType();
        var typeName = connectionType.FullName ?? connectionType.Name;

        return typeName switch
        {
            _ when typeName.Contains("Npgsql", StringComparison.Ordinal) => SqlDialect.Pgsql,
            _ when typeName.Contains("SqlClient", StringComparison.Ordinal) => SqlDialect.Mssql,
            _ => throw new NotSupportedException(
                $"Unsupported DbConnection type '{typeName}' for relational dialect detection."
            ),
        };
    }
}
