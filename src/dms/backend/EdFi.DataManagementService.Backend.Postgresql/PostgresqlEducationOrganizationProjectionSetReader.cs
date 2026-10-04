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

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Reads the complete education-organization projection set from the request's selected PostgreSQL
/// database inside one <c>REPEATABLE READ</c> transaction, so the statement reads one snapshot.
/// </summary>
internal sealed class PostgresqlEducationOrganizationProjectionSetReader
    : IEducationOrganizationProjectionSetReader
{
    private static readonly EducationOrganizationProjectionProvider _provider = new(
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
        SessionRestoreStatement: null
    );

    private readonly Func<CancellationToken, Task<EducationOrganizationProjectionConnection>> _acquireAsync;
    private readonly ILogger<PostgresqlEducationOrganizationProjectionSetReader> _logger;
    private readonly IEducationOrganizationProjectionReadObserver? _observer;

    public PostgresqlEducationOrganizationProjectionSetReader(
        NpgsqlDataSourceProvider dataSourceProvider,
        ILogger<PostgresqlEducationOrganizationProjectionSetReader> logger
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceProvider);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
}
