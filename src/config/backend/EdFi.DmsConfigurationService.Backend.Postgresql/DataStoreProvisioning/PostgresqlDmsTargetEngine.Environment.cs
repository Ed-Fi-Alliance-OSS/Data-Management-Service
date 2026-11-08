// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The environmental validation of the PostgreSQL adapter (7.3 items 4, 5 and 7). Every probe group runs and
/// every failing rule is reported, so an operator sees the complete picture in one restart; the lock probe
/// alone is skipped when the server could not be reached, because it would only repeat that failure.
/// Nothing is created, altered, or signalled on the server: the probes read catalogs, take and release one
/// advisory lock no target can use, and run <c>psql --version</c>. Caller cancellation propagates; every
/// other failure is a reason.
/// </summary>
public sealed partial class PostgresqlDmsTargetEngine
{
    public async Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken)
    {
        List<DmsReadinessReason> reasons = [];
        (bool serverReachable, int? serverMajorVersion) = await ValidateServerAsync(
            reasons,
            cancellationToken
        );

        PsqlVersionProbeResult psql = await PsqlVersionProbe.ProbeAsync(
            _settings.PsqlPath,
            PsqlVersionProbe.DefaultTimeout,
            _timeProvider,
            cancellationToken
        );
        if (PostgresqlEnvironment.EvaluatePsql(psql, _settings.MinimumPsqlMajorVersion) is { } psqlReason)
        {
            reasons.Add(psqlReason);
        }

        if (serverReachable && await ProbeTargetLockAsync(cancellationToken) is { } lockReason)
        {
            reasons.Add(lockReason);
        }

        return reasons.Count == 0
            ? new DmsEngineEnvironment.Ready() { ServerMajorVersion = serverMajorVersion }
            : new DmsEngineEnvironment.NotReady(reasons) { ServerMajorVersion = serverMajorVersion };
    }

    /// <summary>
    /// Connects, reads the facts, and applies the server rules; returns whether the server was reached and its
    /// major version (<c>server_version_num / 10000</c>, as <see cref="ReadServerFactsAsync"/> reports it), null
    /// when the facts could not be read.
    /// </summary>
    private async Task<(bool Reachable, int? MajorVersion)> ValidateServerAsync(
        List<DmsReadinessReason> reasons,
        CancellationToken cancellationToken
    )
    {
        NpgsqlConnection connection;
        try
        {
            connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            reasons.Add(ServerFailure(exception, PostgresqlEnvironment.ConnectOperation));
            return (false, null);
        }

        await using (connection)
        {
            try
            {
                PostgresqlEnvironmentFacts facts = await PostgresqlEnvironment.ReadFactsAsync(
                    connection,
                    cancellationToken
                );
                reasons.AddRange(PostgresqlEnvironment.Evaluate(facts));
                return (true, facts.ServerVersionNum / 10000);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                reasons.Add(ServerFailure(exception, PostgresqlEnvironment.ReadFactsOperation));
                return (true, null);
            }
        }
    }

    /// <summary>
    /// A connection-class failure is <c>ServerUnavailable</c>; anything else the server or the driver raised
    /// (an authentication refusal, a value the driver cannot parse) is <c>StartupValidationFailed</c>. Only
    /// the type chain and the SQLSTATE are kept.
    /// </summary>
    private static DmsReadinessReason ServerFailure(Exception exception, string operation) =>
        new(
            PostgresqlErrorClassifier.IsConnectionFailure(exception)
                ? DmsProvisioningFailureCode.ServerUnavailable
                : DmsProvisioningFailureCode.StartupValidationFailed,
            PostgresqlEnvironment.TargetServerProbe,
            DmsProvisioningDiagnostic.From(
                exception,
                operation,
                PostgresqlErrorClassifier.SqlStateOf(exception)
            )
        );

    /// <summary>
    /// 7.3 item 7: acquires the probe lock (<c>dms-provision-target:v1:__probe__</c>) on a holder session as
    /// an operation would, under <c>LockAcquireTimeout</c>, runs one heartbeat probe on it, and releases it.
    /// Contention past the budget is <c>LockTimeout</c>; a failed acquisition or a heartbeat that finds the
    /// holder lost is <c>LockUnavailable</c>.
    /// </summary>
    private async Task<DmsReadinessReason?> ProbeTargetLockAsync(CancellationToken cancellationToken)
    {
        DmsTargetLockResult result = await PostgresqlTargetLockSession.AcquireKeyAsync(
            _adminConnection,
            PostgresqlTargetLockSession.ProbeLockKey,
            DmsOperationId.New(),
            _settings.LockAcquireTimeout,
            _settings.LockHeartbeatInterval,
            _timeProvider,
            _logger,
            connectionOpened: null,
            cancellationToken
        );
        switch (result)
        {
            case DmsTargetLockResult.Timeout:
                return new DmsReadinessReason(
                    DmsProvisioningFailureCode.LockTimeout,
                    PostgresqlEnvironment.TargetLockProbe,
                    DmsProvisioningDiagnostic.ForOperation(PostgresqlTargetLockSession.AcquireStep)
                );
            case DmsTargetLockResult.Failure failure:
                return new DmsReadinessReason(
                    DmsProvisioningFailureCode.LockUnavailable,
                    PostgresqlEnvironment.TargetLockProbe,
                    failure.Diagnostic
                );
        }

        await using PostgresqlTargetLockSession holder = (PostgresqlTargetLockSession)
            ((DmsTargetLockResult.Acquired)result).Holder;
        try
        {
            await holder.ProbeOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new DmsReadinessReason(
                DmsProvisioningFailureCode.LockUnavailable,
                PostgresqlEnvironment.TargetLockProbe,
                DmsProvisioningDiagnostic.From(
                    exception,
                    PostgresqlEnvironment.LockHeartbeatOperation,
                    PostgresqlErrorClassifier.SqlStateOf(exception)
                )
            );
        }

        return holder.IsLost
            ? new DmsReadinessReason(
                DmsProvisioningFailureCode.LockUnavailable,
                PostgresqlEnvironment.TargetLockProbe,
                DmsProvisioningDiagnostic.ForOperation(PostgresqlEnvironment.LockHeartbeatOperation)
            )
            : null;
    }
}
