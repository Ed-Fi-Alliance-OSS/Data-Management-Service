// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal sealed class SessionRelationalCommandExecutor : IRelationalCommandExecutor
{
    private readonly Func<RelationalCommand, DbCommand> _createCommand;

    public SessionRelationalCommandExecutor(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        Dialect = DetermineDialect(connection);
        _createCommand = command =>
            SessionRelationalCommandFactory.CreateCommand(connection, transaction, command);
    }

    private SessionRelationalCommandExecutor(
        Func<RelationalCommand, DbCommand> createCommand,
        SqlDialect dialect
    )
    {
        _createCommand = createCommand ?? throw new ArgumentNullException(nameof(createCommand));
        Dialect = dialect;
    }

    /// <summary>
    /// Builds an executor that routes every command through
    /// <see cref="IRelationalWriteSession.CreateCommand(RelationalCommand)"/>, so a session
    /// decorator that overrides <c>CreateCommand</c> observes every write issued inside the
    /// session (including reads such as UUID/resource lookups).
    /// </summary>
    public static IRelationalCommandExecutor ForSession(IRelationalWriteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionRelationalCommandExecutor(
            session.CreateCommand,
            DetermineDialect(session.Connection)
        );
    }

    public SqlDialect Dialect { get; }

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);

        await using var dbCommand = _createCommand(command);

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
