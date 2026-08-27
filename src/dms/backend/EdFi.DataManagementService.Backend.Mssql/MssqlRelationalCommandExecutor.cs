// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlRelationalCommandExecutor : IRelationalCommandExecutor
{
    public SqlDialect Dialect => SqlDialect.Mssql;

    private readonly Func<CancellationToken, Task<MssqlLeasedConnection>> _openConnectionAsync;
    private readonly ILogger<MssqlRelationalCommandExecutor> _logger;

    public MssqlRelationalCommandExecutor(
        IDataStoreSelection dataStoreSelection,
        ILogger<MssqlRelationalCommandExecutor> logger
    )
        : this(dataStoreSelection, new MssqlConnectionAcquisition(), logger) { }

    internal MssqlRelationalCommandExecutor(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition,
        ILogger<MssqlRelationalCommandExecutor> logger
    )
    {
        ArgumentNullException.ThrowIfNull(dataStoreSelection);
        ArgumentNullException.ThrowIfNull(acquisition);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _openConnectionAsync = cancellationToken =>
            MssqlSeamConnection.OpenAsync(dataStoreSelection, acquisition, cancellationToken);
    }

    internal MssqlRelationalCommandExecutor(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        ILogger<MssqlRelationalCommandExecutor> logger
    )
    {
        ArgumentNullException.ThrowIfNull(openConnectionAsync);

        _openConnectionAsync = async cancellationToken =>
            MssqlLeasedConnection.WithoutLease(
                await openConnectionAsync(cancellationToken).ConfigureAwait(false)
            );
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(readAsync);

        _logger.LogDebug(
            "Executing SQL Server relational command with {ParameterCount} parameters",
            command.Parameters.Count
        );

        await using var leased = await _openConnectionAsync(cancellationToken).ConfigureAwait(false);
        DbConnection connection = leased.Connection;
        await using var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command.CommandText;

        AddParameters(dbCommand, command.Parameters);

        await using var reader = new DbRelationalCommandReader(
            await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)
        );

        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(DbCommand dbCommand, IReadOnlyList<RelationalParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            var dbParameter = dbCommand.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            parameter.ConfigureParameter?.Invoke(dbParameter);
            dbCommand.Parameters.Add(dbParameter);
        }
    }
}
