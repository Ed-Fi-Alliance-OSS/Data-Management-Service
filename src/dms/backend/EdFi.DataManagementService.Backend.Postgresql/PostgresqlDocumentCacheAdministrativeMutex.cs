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

    internal static readonly string AcquireSql = $"""
        SELECT pg_advisory_lock(
            ({LockNamespace}::bigint << 32)
            | (
                SELECT database.oid::bigint
                FROM pg_database AS database
                WHERE database.datname = current_database()
            )
        );
        """;

    internal static readonly string ReleaseSql = $"""
        SELECT pg_advisory_unlock(
            ({LockNamespace}::bigint << 32)
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

        // The mutex session outlives this method, so the lease that keeps its data source alive
        // travels with the connection rather than being released here.
        LeasedNpgsqlConnection leased = await _dataSourceCache
            .OpenLeasedConnectionAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await ExecuteAcquireAsync(leased.Connection, cancellationToken).ConfigureAwait(false);
            int backendPid = await ExecuteBackendPidAsync(leased.Connection, cancellationToken)
                .ConfigureAwait(false);
            return new PostgresqlDocumentCacheAdministrativeMutexLease(leased, _logger, backendPid);
        }
        catch
        {
            await leased.DisposeAsync().ConfigureAwait(false);
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
        LeasedNpgsqlConnection leased,
        ILogger logger,
        int backendPid
    )
        : DocumentCacheAdministrativeMutexLease(
            RelationalProviderToken.Postgresql,
            leased.Connection,
            leased.Lease
        )
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
