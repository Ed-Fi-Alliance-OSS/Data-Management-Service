// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheAdministrativeMutex(
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<PostgresqlDocumentCacheAdministrativeMutex> logger
) : IDocumentCacheAdministrativeMutex
{
    internal const long LockNamespace = 811646948;

    internal const string AcquireSql = """
        SELECT pg_advisory_lock(
            (811646948::bigint << 32)
            | (
                SELECT database.oid::bigint
                FROM pg_database AS database
                WHERE database.datname = current_database()
            )
        );
        """;

    internal const string ReleaseSql = """
        SELECT pg_advisory_unlock(
            (811646948::bigint << 32)
            | (
                SELECT database.oid::bigint
                FROM pg_database AS database
                WHERE database.datname = current_database()
            )
        );
        """;

    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly ILogger<PostgresqlDocumentCacheAdministrativeMutex> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    public async Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
        DocumentCacheTargetConnectionInput connectionInput,
        CancellationToken cancellationToken = default
    )
    {
        string connectionString = DocumentCacheAdministrativeMutexGuards.RequireConnectionString(
            connectionInput,
            ProviderToken
        );

        NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await ExecuteAcquireAsync(connection, cancellationToken).ConfigureAwait(false);
            int backendPid = await ExecuteBackendPidAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return new PostgresqlDocumentCacheAdministrativeMutexLease(connection, _logger, backendPid);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAcquireAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = AcquireSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteBackendPidAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid();";

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private sealed class PostgresqlDocumentCacheAdministrativeMutexLease(
        NpgsqlConnection connection,
        ILogger logger,
        int backendPid
    ) : DocumentCacheAdministrativeMutexLease(RelationalProviderToken.Postgresql, connection)
    {
        private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        private readonly int _backendPid = backendPid;

        protected override async Task ValidateSessionAsync(CancellationToken cancellationToken)
        {
            int currentBackendPid = await ExecuteBackendPidAsync(
                    (NpgsqlConnection)Connection,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (currentBackendPid != _backendPid)
            {
                throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken);
            }
        }

        protected override async Task ReleaseAsync(
            DbConnection connection,
            CancellationToken cancellationToken
        )
        {
            await using NpgsqlCommand command = ((NpgsqlConnection)connection).CreateCommand();
            command.CommandText = ReleaseSql;

            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is true)
            {
                return;
            }

            _logger.LogWarning(
                "PostgreSQL DocumentCache administrative mutex release did not find the expected session lock."
            );

            throw new InvalidOperationException(
                "PostgreSQL DocumentCache administrative mutex release did not find the expected session lock."
            );
        }
    }
}
