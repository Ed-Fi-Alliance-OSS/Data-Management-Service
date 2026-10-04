// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Reads the complete education-organization projection set from the request's selected SQL Server
/// database inside one <c>SERIALIZABLE</c> transaction, so the fully consumed statement reads a set
/// consistent with some serial order.
/// </summary>
/// <remarks>
/// Serializable holds shared and range locks on the four tables until the transaction ends, so it can
/// block concurrent writers for the statement and commit, and the read can be chosen as a deadlock
/// victim. Both are transient. DMS provisioning does not enable snapshot isolation, and the read does
/// not use it.
/// </remarks>
internal sealed class MssqlEducationOrganizationProjectionSetReader
    : IEducationOrganizationProjectionSetReader
{
    private static readonly EducationOrganizationProjectionProvider _provider = new(
        SqlDialect.Mssql,
        IsolationLevel.Serializable,
        MssqlConnectionAcquisitionFailure.IsExpected,
        MssqlConnectionAcquisitionFailure.Describe,
        static (exception, describe) =>
            EducationOrganizationProjectionExecutionClassifier.ClassifyMssql(
                exception is SqlException sqlException ? sqlException.Number : null,
                describe
            ),
        // The value is a validated integer, not client input.
        static seconds =>
            string.Create(CultureInfo.InvariantCulture, $"SET LOCK_TIMEOUT {checked(seconds * 1000)}"),
        // BeginTransaction sets the session's isolation level and it outlives the transaction. The
        // pool's connection reset restores LOCK_TIMEOUT but not the isolation level, so without this
        // the next request on the pooled connection would run SERIALIZABLE.
        SessionRestoreStatement: "SET TRANSACTION ISOLATION LEVEL READ COMMITTED"
    );

    private readonly Func<CancellationToken, Task<EducationOrganizationProjectionConnection>> _acquireAsync;
    private readonly ILogger<MssqlEducationOrganizationProjectionSetReader> _logger;
    private readonly IEducationOrganizationProjectionReadObserver? _observer;

    public MssqlEducationOrganizationProjectionSetReader(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition,
        ILogger<MssqlEducationOrganizationProjectionSetReader> logger
    )
    {
        ArgumentNullException.ThrowIfNull(dataStoreSelection);
        ArgumentNullException.ThrowIfNull(acquisition);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _acquireAsync = async cancellationToken =>
            AsOwned(
                await MssqlSeamConnection
                    .OpenAsync(dataStoreSelection, acquisition, logger, cancellationToken)
                    .ConfigureAwait(false)
            );
    }

    /// <summary>
    /// Opens through the given delegate inside the same acquisition guard the seam uses, for tests.
    /// </summary>
    internal MssqlEducationOrganizationProjectionSetReader(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        ILogger<MssqlEducationOrganizationProjectionSetReader> logger,
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
                        async () =>
                            MssqlLeasedConnection.WithoutLease(
                                await openConnectionAsync(cancellationToken).ConfigureAwait(false)
                            ),
                        EffectiveTargetKind.Primary,
                        MssqlConnectionAcquisitionFailure.IsExpected,
                        MssqlConnectionAcquisitionFailure.Describe,
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

    private static EducationOrganizationProjectionConnection AsOwned(MssqlLeasedConnection leased) =>
        new(leased.Connection, leased);
}
