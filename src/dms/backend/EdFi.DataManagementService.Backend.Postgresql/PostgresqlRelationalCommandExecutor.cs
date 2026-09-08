// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlRelationalCommandExecutor : IRelationalCommandExecutor
{
    public SqlDialect Dialect => SqlDialect.Pgsql;

    private readonly Func<CancellationToken, Task<DbConnection>> _openConnectionAsync;
    private readonly ILogger<PostgresqlRelationalCommandExecutor> _logger;

    public PostgresqlRelationalCommandExecutor(
        NpgsqlDataSourceProvider dataSourceProvider,
        ILogger<PostgresqlRelationalCommandExecutor> logger
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceProvider);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _openConnectionAsync = cancellationToken =>
            PostgresqlSeamConnection.OpenGuardedAsync(dataSourceProvider, logger, cancellationToken);
    }

    /// <summary>
    /// Test seam: an injected open, guarded exactly as the production one is.
    /// </summary>
    /// <param name="targetKind">
    /// The kind the guard classifies against. Primary by default, which is what every existing caller
    /// means, so a test that cares about the snapshot boundary is the only one that names it.
    /// </param>
    internal PostgresqlRelationalCommandExecutor(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        ILogger<PostgresqlRelationalCommandExecutor> logger,
        EffectiveTargetKind targetKind = EffectiveTargetKind.Primary
    )
    {
        ArgumentNullException.ThrowIfNull(openConnectionAsync);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _openConnectionAsync = cancellationToken =>
            ConnectionAcquisition.GuardAsync(
                () => openConnectionAsync(cancellationToken),
                targetKind,
                PostgresqlConnectionAcquisitionFailure.IsExpected,
                logger,
                cancellationToken
            );
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
            "Executing PostgreSQL relational command with {ParameterCount} parameters",
            command.Parameters.Count
        );

        await using var connection = await _openConnectionAsync(cancellationToken).ConfigureAwait(false);
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
