// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The environmental validation of the SQL Server adapter (7.3 items 4, 6 and 7, the fifth part of this class).
/// Every probe group runs and every failing rule is reported, so an operator sees the complete picture in one
/// restart. The staging directory is checked whatever the server's state; the visibility probe and the lock probe
/// are skipped when the server could not be reached, because they would only repeat that failure. Nothing is
/// created, altered, or killed on the server: the probes read server properties and permissions, ask the server
/// for the header of a zero-byte probe file the real staging routine wrote (and deleted afterwards), and take and
/// release one application lock no target can use. Caller cancellation propagates as
/// <see cref="OperationCanceledException"/>, whatever form the driver gave it; every other failure is a reason.
/// </summary>
public sealed partial class MssqlDmsTargetEngine
{
    /// <summary>Test seam: the server version floor; production uses <see cref="MssqlEnvironment.MinimumProductMajorVersion"/>.</summary>
    internal int MinimumProductMajorVersion { get; init; } = MssqlEnvironment.MinimumProductMajorVersion;

    public async Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ValidateEnvironmentCoreAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The environmental validation was cancelled.",
                exception,
                cancellationToken
            );
        }
    }

    private async Task<DmsEngineEnvironment> ValidateEnvironmentCoreAsync(CancellationToken cancellationToken)
    {
        List<DmsReadinessReason> reasons = [];
        (bool serverReachable, int? serverMajorVersion) = await ValidateServerAsync(
            reasons,
            cancellationToken
        );

        bool directoryExists = ValidateStagingDirectory(reasons);
        if (serverReachable && directoryExists)
        {
            reasons.AddRange(await ProbeStagingVisibilityAsync(cancellationToken));
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
    /// Connects to <c>master</c>, reads the facts, and applies the server rules; returns whether the server was
    /// reached and the major version read (null when the facts could not be read).
    /// </summary>
    private async Task<(bool Reachable, int? MajorVersion)> ValidateServerAsync(
        List<DmsReadinessReason> reasons,
        CancellationToken cancellationToken
    )
    {
        SqlConnection connection;
        try
        {
            connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            reasons.Add(
                ServerFailure(
                    exception,
                    MssqlEnvironment.TargetServerProbe,
                    MssqlEnvironment.ConnectOperation
                )
            );
            return (false, null);
        }

        await using (connection)
        {
            try
            {
                MssqlEnvironmentFacts facts = await MssqlEnvironment.ReadFactsAsync(
                    connection,
                    _adminConnection.CommandTimeoutSeconds,
                    cancellationToken
                );
                reasons.AddRange(MssqlEnvironment.Evaluate(facts, MinimumProductMajorVersion));
                return (true, facts.ProductMajorVersion);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                reasons.Add(
                    ServerFailure(
                        exception,
                        MssqlEnvironment.TargetServerProbe,
                        MssqlEnvironment.ReadFactsOperation
                    )
                );
                return (true, null);
            }
        }
    }

    /// <summary>
    /// D-18 on the CMS host: the directory exists and, on Unix, is owned by this process's user and its mode grants
    /// no group or other write. False when the directory does not exist, so the visibility probe is not attempted
    /// in it.
    /// </summary>
    private bool ValidateStagingDirectory(List<DmsReadinessReason> reasons)
    {
        string path = _settings.Staging.StagingPath;
        bool exists = Directory.Exists(path);
        UnixFileMode? mode = null;
        bool? ownedByProcess = null;
        if (exists && !OperatingSystem.IsWindows())
        {
            try
            {
                mode = File.GetUnixFileMode(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                reasons.Add(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        DmsProvisioningDiagnostic.From(
                            exception,
                            MssqlEnvironment.StagingDirectoryModeOperation
                        )
                    )
                );
                return true;
            }

            try
            {
                ownedByProcess = UnixFileOwnership.IsOwnedByEffectiveUser(path);
            }
            catch (IOException exception)
            {
                reasons.Add(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        DmsProvisioningDiagnostic.From(
                            exception,
                            MssqlEnvironment.StagingDirectoryOwnerOperation
                        )
                    )
                );
                return true;
            }
        }

        if (MssqlEnvironment.EvaluateStagingDirectory(exists, mode, ownedByProcess) is { } reason)
        {
            reasons.Add(reason);
        }

        return exists;
    }

    /// <summary>
    /// 7.3 item 6 and D-21 with the real staging routine (<see cref="MssqlStagingProbe"/>). A failure to reach the
    /// server during the probe is a reason under this probe; the probe has already tried to delete its file.
    /// </summary>
    private async Task<IReadOnlyList<DmsReadinessReason>> ProbeStagingVisibilityAsync(
        CancellationToken cancellationToken
    )
    {
        MssqlStagingProbe probe = new(
            new MssqlBackupStager(_settings.Staging, _timeProvider, _logger),
            _adminConnection,
            _logger
        );
        try
        {
            return MssqlEnvironment.EvaluateStagingProbe(await probe.RunAsync(cancellationToken));
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return
            [
                ServerFailure(
                    exception,
                    MssqlEnvironment.StagingVisibilityProbe,
                    MssqlEnvironment.StagingProbeServerOperation
                ),
            ];
        }
    }

    /// <summary>
    /// A connection-class failure is <c>ServerUnavailable</c>; anything else the server or the driver raised (a
    /// login refusal, a value the driver cannot parse) is <c>StartupValidationFailed</c>. Only the type chain and
    /// the first error number are kept.
    /// </summary>
    private static DmsReadinessReason ServerFailure(Exception exception, string probe, string operation) =>
        new(
            MssqlErrorClassifier.IsConnectionFailure(exception)
                ? DmsProvisioningFailureCode.ServerUnavailable
                : DmsProvisioningFailureCode.StartupValidationFailed,
            probe,
            MssqlErrorClassifier.Diagnostic(exception, operation)
        );

    /// <summary>
    /// 7.3 item 7: acquires the probe lock (<see cref="MssqlTargetLockSession.ProbeResource"/>) on a holder session
    /// as an operation would, under <c>LockAcquireTimeout</c>, runs one heartbeat probe on it, and releases it.
    /// Contention past the budget is <c>LockTimeout</c>; a failed acquisition, or a heartbeat that fails or finds
    /// the lock no longer held, is <c>LockUnavailable</c>.
    /// </summary>
    private async Task<DmsReadinessReason?> ProbeTargetLockAsync(CancellationToken cancellationToken)
    {
        DmsTargetLockResult result = await MssqlTargetLockSession.AcquireResourceAsync(
            _adminConnection,
            MssqlTargetLockSession.ProbeResource,
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
                    MssqlEnvironment.TargetLockProbe,
                    DmsProvisioningDiagnostic.ForOperation(MssqlTargetLockSession.AcquireStep)
                );
            case DmsTargetLockResult.Failure failure:
                return new DmsReadinessReason(
                    DmsProvisioningFailureCode.LockUnavailable,
                    MssqlEnvironment.TargetLockProbe,
                    failure.Diagnostic
                );
        }

        await using MssqlTargetLockSession holder = (MssqlTargetLockSession)
            ((DmsTargetLockResult.Acquired)result).Holder;
        try
        {
            await holder.ProbeOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new DmsReadinessReason(
                DmsProvisioningFailureCode.LockUnavailable,
                MssqlEnvironment.TargetLockProbe,
                MssqlErrorClassifier.Diagnostic(exception, MssqlEnvironment.LockHeartbeatOperation)
            );
        }

        return holder.IsLost
            ? new DmsReadinessReason(
                DmsProvisioningFailureCode.LockUnavailable,
                MssqlEnvironment.TargetLockProbe,
                DmsProvisioningDiagnostic.ForOperation(MssqlEnvironment.LockHeartbeatOperation)
            )
            : null;
    }
}
