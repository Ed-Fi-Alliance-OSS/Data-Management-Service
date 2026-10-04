// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Reads the complete education-organization projection set from the request's selected PostgreSQL
/// database inside one <c>REPEATABLE READ</c> transaction, so the statement reads one snapshot.
/// </summary>
internal sealed class PostgresqlEducationOrganizationProjectionSetReader
    : IEducationOrganizationProjectionSetReader
{
    private static readonly EducationOrganizationProjectionProvider _postgresql = new(
        SqlDialect.Pgsql,
        IsolationLevel.RepeatableRead,
        PostgresqlConnectionAcquisitionFailure.IsExpected,
        PostgresqlConnectionAcquisitionFailure.Describe,
        static (exception, describe) =>
            EducationOrganizationProjectionExecutionClassifier.ClassifyPostgresql(
                exception.SqlState,
                describe
            ),
        // SET LOCAL lasts until the transaction ends, so nothing outlives the read on a pooled
        // connection. The value is a validated integer, not client input.
        static seconds =>
            string.Create(CultureInfo.InvariantCulture, $"SET LOCAL lock_timeout = '{seconds}s'"),
        // The isolation level is per transaction.
        SessionRestoreStatement: null,
        DiscardConnectionAsync: DiscardAsync
    );

    private readonly Func<CancellationToken, Task<EducationOrganizationProjectionConnection>> _acquireAsync;
    private readonly ILogger<PostgresqlEducationOrganizationProjectionSetReader> _logger;
    private readonly IEducationOrganizationProjectionReadObserver? _observer;
    private readonly EducationOrganizationProjectionProvider _provider = _postgresql;

    public PostgresqlEducationOrganizationProjectionSetReader(
        NpgsqlDataSourceProvider dataSourceProvider,
        ILogger<PostgresqlEducationOrganizationProjectionSetReader> logger
    )
        : this(dataSourceProvider, logger, observer: null, configureProvider: null) { }

    /// <summary>
    /// The production acquisition path with the test seams: an observer, and a change to the provider
    /// behavior (for example a session restore that fails).
    /// </summary>
    internal PostgresqlEducationOrganizationProjectionSetReader(
        NpgsqlDataSourceProvider dataSourceProvider,
        ILogger<PostgresqlEducationOrganizationProjectionSetReader> logger,
        IEducationOrganizationProjectionReadObserver? observer,
        Func<
            EducationOrganizationProjectionProvider,
            EducationOrganizationProjectionProvider
        >? configureProvider
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceProvider);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observer = observer;
        _provider = configureProvider?.Invoke(_postgresql) ?? _postgresql;
        _acquireAsync = async cancellationToken =>
            AsOwned(
                await PostgresqlSeamConnection
                    .OpenGuardedAsync(dataSourceProvider, logger, cancellationToken)
                    .ConfigureAwait(false)
            );
    }

    /// <summary>
    /// Opens through the given delegate inside the same acquisition guard the seam uses, for tests.
    /// </summary>
    internal PostgresqlEducationOrganizationProjectionSetReader(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        ILogger<PostgresqlEducationOrganizationProjectionSetReader> logger,
        IEducationOrganizationProjectionReadObserver? observer = null
    )
    {
        ArgumentNullException.ThrowIfNull(openConnectionAsync);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observer = observer;
        _acquireAsync = async cancellationToken =>
            AsOwned(
                await ConnectionAcquisition
                    .GuardAsync(
                        () => openConnectionAsync(cancellationToken),
                        EffectiveTargetKind.Primary,
                        PostgresqlConnectionAcquisitionFailure.IsExpected,
                        PostgresqlConnectionAcquisitionFailure.Describe,
                        logger,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
    }

    public Task<EducationOrganizationProjectionSetResult> ReadSetAsync(
        EducationOrganizationProjectionSetReadRequest request,
        CancellationToken cancellationToken
    )
    {
        return EducationOrganizationProjectionStagedRead.ReadAsync(
            _provider,
            _acquireAsync,
            request,
            _logger,
            _observer,
            cancellationToken
        );
    }

    private static EducationOrganizationProjectionConnection AsOwned(DbConnection connection) =>
        new(connection, connection);

    /// <summary>
    /// Ends the connection's own server session. Npgsql treats the resulting <c>57P01</c> as fatal and
    /// breaks the connector, and a broken connector is destroyed when the connection is released
    /// instead of returning to the pool. This is the per-connection exclusion that works for a
    /// connection opened from an explicitly built data source: <c>NpgsqlConnection.ClearPool</c> uses
    /// the connection-string pool registry, which does not include such data sources, and the shared
    /// data source must not be disposed while other requests lease it. A session may always signal
    /// its own backend. A connection that is no longer open is already broken and is not pooled.
    /// </summary>
    private static async Task DiscardAsync(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using DbCommand terminate = connection.CreateCommand();
        terminate.CommandText = "SELECT pg_terminate_backend(pg_backend_pid());";
        try
        {
            await terminate.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.AdminShutdown)
        {
            // The session ended, which is the point.
        }
    }
}
