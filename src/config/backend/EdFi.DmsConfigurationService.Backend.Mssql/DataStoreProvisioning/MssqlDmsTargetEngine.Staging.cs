// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Runtime.CompilerServices;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The staging stage of the SQL Server engine (6.4 steps 4 to 7, 6.7). The restore creates the staging database,
/// so the create only reserves the name. The restore stages the backup (D-18), reads and plans it on a work session
/// named <c>dms-provision:&lt;op&gt;</c> (D-11), and runs <c>RESTORE DATABASE … WITH MOVE</c> on that session under
/// <c>RestoreTimeout</c>. Nothing before the <c>RESTORE</c> can create a database, so every failure there is
/// definite. A <c>RESTORE</c> rejected in its own response is definite too; one that ended in doubt (connection
/// loss, the bound, the caller) is resolved here (D-7b): the work session is killed and proven gone on a token of
/// its own, then the staging name decides: absent or <c>RESTORING</c> with the operation's primary file means the
/// restore did not complete (a definite failure named after what ended it; a restoring database is cleanup's), and
/// <c>ONLINE</c> with that file means it did. A completed restore is re-hashed (D-18) before it is reported. The
/// staged file is deleted once the outcome is known (6.4 step 13). The staging drop re-verifies the instance, or
/// for a reservation the primary file name, and drops in one guarded batch on any session.
/// </summary>
public sealed partial class MssqlDmsTargetEngine
{
    /// <summary>The <c>Diagnostic.Operation</c> of a <c>RESTORE</c> the server rejected in its own response.</summary>
    public const string RestoreStep = "RestoreIntoStaging";

    /// <summary>The <c>Diagnostic.Operation</c> of a completed restore whose database is not under the staging name as planned.</summary>
    public const string RestoreInstanceStep = "RestoreIntoStaging.Instance";

    /// <summary>The <c>Diagnostic.Operation</c> of a backup read the server refused for lack of a privilege.</summary>
    public const string ReadBackupStep = "RestoreIntoStaging.ReadBackup";

    /// <summary>The <c>Diagnostic.Operation</c> of a server that reports no default data or log directory.</summary>
    public const string DefaultPathsStep = "RestoreIntoStaging.DefaultPaths";

    /// <summary>The <c>Diagnostic.Operation</c> of a backup the server refuses to read (<c>BackupInvalid</c>).</summary>
    public const string BackupUnreadableStep = DmsEngineOperations.PlanRestorePrefix + "Unreadable";

    public const string DropStagingStep = "DropStaging";

    /// <summary>The <c>Diagnostic.Operation</c> of a drop refused because the staging name holds another instance.</summary>
    public const string DropStagingInstanceStep = "DropStaging.InstanceChanged";

    internal static readonly EventId RestoreResolutionFailed = new(1438_42, nameof(RestoreResolutionFailed));

    /// <summary>
    /// Test seam: the statement sent as the restore, from the plan and the staged file's server path; production
    /// sends <see cref="MssqlRestorePlan.RestoreStatement"/>.
    /// </summary>
    internal Func<MssqlRestorePlan, string, string> RestoreStatement { get; init; } =
        static (plan, serverPath) => plan.RestoreStatement(serverPath);

    /// <summary>
    /// Test seam: runs with the staged backup after a completed restore and before the post-restore re-hash, the
    /// point where a writer could replace the file; production has none.
    /// </summary>
    internal Func<MssqlStagedBackup, Task>? AfterRestore { get; init; }

    /// <summary>
    /// SQL Server only reserves the staging name: the restore creates the database (6.4). Nothing is sent to the
    /// server; the reservation names the primary file the restored database will carry.
    /// </summary>
    public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    )
    {
        RequireHolder(holder);
        ArgumentNullException.ThrowIfNull(claim);
        RequireStagingOf(staging, claim.Operation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
            new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                new DmsInstanceIdentity.MssqlReservation(
                    MssqlRestorePlanner.PrimaryFileNameFor(claim.Operation)
                )
            )
        );
    }

    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireStagingOf(staging, operation);
        MssqlBackupStager stager = new(_settings.Staging, _timeProvider, _logger);

        // 6.4 step 4. The stager deletes what it created on every failure and on cancellation.
        MssqlStagingResult staged;
        try
        {
            staged = await stager.StageAsync(artifact, operation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NotRestored(DmsProvisioningDiagnostic.ForOperation(DmsEngineOperations.RestoreCancelled));
        }

        if (staged is MssqlStagingResult.Failed refused)
        {
            return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(DmsCommandFailureClass.Rejected, refused.Diagnostic)
            )
            {
                Residue = refused.Residue is { } residue ? [residue] : [],
            };
        }

        MssqlStagedBackup backup = ((MssqlStagingResult.Staged)staged).Backup;
        DmsCommandOutcome<DmsInstanceIdentity> outcome;
        try
        {
            outcome = await RestoreStagedAsync(staging, target, backup, operation, cancellationToken);
        }
        catch
        {
            if (!await stager.DeleteAsync(backup))
            {
                LogStagedBackupResidue(backup);
            }

            throw;
        }

        // 6.4 step 13: the staged file is no longer needed once the outcome, and with it the re-hash, is known.
        return await stager.DeleteAsync(backup)
            ? outcome
            : outcome with
            {
                Residue =
                [
                    .. outcome.Residue,
                    new DmsCleanupResidue(DmsResidueKind.StagedBackup, backup.FileName),
                ],
            };
    }

    /// <summary>
    /// Drops this operation's staging database on a fresh maintenance session, whatever its state (a restore that did
    /// not complete leaves it <c>RESTORING</c>). The database under the name must be <paramref name="stagingInstance"/>
    /// (<c>database_id</c> and primary file) or, for a reservation, carry the reserved primary file name; an absent
    /// name is success, and another database under it is a definite refusal that touches nothing.
    /// </summary>
    public async Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(stagingInstance);
        if (stagingInstance is not (DmsInstanceIdentity.Mssql or DmsInstanceIdentity.MssqlReservation))
        {
            throw new ArgumentException(
                "The SQL Server engine drops only SQL Server instances and reservations.",
                nameof(stagingInstance)
            );
        }

        await using SqlConnection connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        int timeout = _adminConnection.CommandTimeoutSeconds;
        DmsServerSessionId session = await MssqlStagingQueries.ReadOwnSessionAsync(
            connection,
            timeout,
            cancellationToken
        );
        MssqlStagingState? state;
        try
        {
            state = await MssqlStagingQueries.ReadStateAsync(connection, staging, timeout, cancellationToken);
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return ClassifyDrop(exception, session);
        }

        if (state is null)
        {
            return new DmsCommandOutcome.Succeeded();
        }

        bool ours = stagingInstance switch
        {
            DmsInstanceIdentity.Mssql expected => state.DatabaseId == expected.DatabaseId
                && state.PrimaryFile.Length > 0
                && string.Equals(state.PrimaryFile, expected.PrimaryFileTag, StringComparison.Ordinal),
            DmsInstanceIdentity.MssqlReservation reservation => reservation.IsPrimaryFile(state.PrimaryFile),
            _ => false,
        };
        if (!ours)
        {
            return DropRefused(
                DmsCommandFailureClass.DoesNotExist,
                DmsProvisioningDiagnostic.ForOperation(DropStagingInstanceStep)
            );
        }

        (MssqlStagingQueries.DropVerdict verdict, int? number, byte severity) dropped;
        try
        {
            dropped = await MssqlStagingQueries.DropAsync(
                connection,
                staging,
                state,
                timeout,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return ClassifyDrop(exception, session);
        }

        return dropped.verdict switch
        {
            MssqlStagingQueries.DropVerdict.Dropped => new DmsCommandOutcome.Succeeded(),
            MssqlStagingQueries.DropVerdict.GuardFailed => DropRefused(
                DmsCommandFailureClass.DoesNotExist,
                DmsProvisioningDiagnostic.ForOperation(DropStagingInstanceStep)
            ),
            _ => DropRefused(
                MssqlErrorClassifier.FailureClassOf(dropped.number ?? 0, dropped.severity)
                    ?? DmsCommandFailureClass.Rejected,
                new DmsProvisioningDiagnostic(
                    string.Empty,
                    dropped.number?.ToString(CultureInfo.InvariantCulture),
                    DropStagingStep
                )
            ),
        };
    }

    /// <summary>
    /// 6.4 steps 5 to 7 with the staged backup: the work session, the plan, the restore and its outcome. Every failure
    /// before the <c>RESTORE</c> is definite, because nothing before it creates a database.
    /// </summary>
    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreStagedAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        MssqlStagedBackup backup,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        SqlConnection work;
        try
        {
            work = await _adminConnection.OpenAsync(
                MssqlAdminConnection.MaintenanceDatabaseName,
                operation.RestoreApplicationName,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NotStarted(exception, cancellationToken);
        }

        await using (work)
        {
            (
                DmsServerSessionId session,
                MssqlRestorePlan? plan,
                DmsCommandOutcome<DmsInstanceIdentity>? refused
            ) = await PlanOnWorkSessionAsync(work, target, backup, operation, cancellationToken);
            if (plan is null)
            {
                return refused!;
            }

            // 6.4 step 6.
            (DmsCommandFailure? rejected, DmsProvisioningDiagnostic? ended) = await RunRestoreAsync(
                work,
                session,
                RestoreStatement(plan, backup.ServerPath),
                cancellationToken
            );
            if (rejected is not null)
            {
                // Rejected in the restore's own response: nothing committed. A database the restore left
                // RESTORING carries the operation's primary file name and is cleanup's to drop.
                return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(rejected);
            }

            if (ended is not null)
            {
                return await ResolveRestoreAsync(staging, plan, backup, session, ended, operation);
            }

            // 6.4 step 7 on a token of its own: the restore committed, so the caller's cancellation no longer
            // skips the reads that report it.
            return await JudgeRestoredAsync(
                staging,
                plan,
                backup,
                new DmsCommandFailure(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(RestoreInstanceStep)
                ),
                operation
            );
        }
    }

    /// <summary>
    /// 6.4 step 5 on the work session: its own server id, the default directories, and the backup's header and file
    /// list judged by the planner (D-11). Returns the session with the plan, or with the definite failure that
    /// refused it: nothing here creates a database, so every failure is definite.
    /// </summary>
    private async Task<(
        DmsServerSessionId Session,
        MssqlRestorePlan? Plan,
        DmsCommandOutcome<DmsInstanceIdentity>? Refused
    )> PlanOnWorkSessionAsync(
        SqlConnection work,
        DmsTargetDatabaseName target,
        MssqlStagedBackup backup,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        int timeout = _adminConnection.CommandTimeoutSeconds;
        DmsServerSessionId session = default;
        string? dataPath;
        string? logPath;
        try
        {
            session = await MssqlStagingQueries.ReadOwnSessionAsync(work, timeout, cancellationToken);
            (dataPath, logPath) = await MssqlStagingQueries.ReadDefaultPathsAsync(
                work,
                timeout,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (session, null, NotStarted(exception, cancellationToken));
        }

        if (dataPath is null || logPath is null)
        {
            return (session, null, NotRestored(DmsProvisioningDiagnostic.ForOperation(DefaultPathsStep)));
        }

        MssqlBackupDescription description;
        try
        {
            description = await MssqlRestorePlanner.ReadAsync(
                work,
                backup.ServerPath,
                timeout,
                cancellationToken
            );
        }
        catch (SqlException exception)
            when (!cancellationToken.IsCancellationRequested
                && MssqlErrorClassifier.FailureClassOf(exception) is { } failureClass
            )
        {
            return (session, null, BackupReadRefused(exception, failureClass));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (session, null, NotStarted(exception, cancellationToken));
        }

        return MssqlRestorePlanner.Plan(description, operation, target, dataPath, logPath) switch
        {
            MssqlRestorePlanResult.Planned planned => (session, planned.Plan, null),
            MssqlRestorePlanResult.Refused defect => (session, null, NotRestored(defect.Diagnostic)),
            _ => throw new InvalidOperationException("Unknown restore plan result."),
        };
    }

    /// <summary>
    /// Runs <paramref name="statement"/> on the work session under <c>RestoreTimeout</c>. Returns no failure and no
    /// ending when it completed; the failure when the server rejected it in its own response; otherwise the
    /// diagnostic of what ended it in doubt, named after the caller's cancellation, the bound, or anything else. With
    /// the token handed to the driver, a cancellation after the <c>RESTORE</c> had streamed its progress messages was
    /// observed not to end the batch, nor to let a kill registered on the same token run, until the batch ended on its
    /// own (row 5ao). So the token is not handed to the driver: when it fires (the caller or the bound) the work
    /// session is killed from a fresh maintenance session (D-7b), which ends the batch whatever state the driver is
    /// in, and the kill has finished before this returns; the driver timeout stays a backstop.
    /// </summary>
    private async Task<(DmsCommandFailure? Rejected, DmsProvisioningDiagnostic? Ended)> RunRestoreAsync(
        SqlConnection work,
        DmsServerSessionId session,
        string statement,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource bound = new(_settings.RestoreTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            bound.Token
        );
        StrongBox<Task?> killing = new(null);
        try
        {
            await using CancellationTokenRegistration onCancel = linked.Token.Register(() =>
                Volatile.Write(ref killing.Value, Task.Run(() => KillWorkSessionAsync(session)))
            );
            await using SqlCommand command = new(statement, work);
            command.CommandTimeout = MssqlAdminConnection.DriverTimeoutSeconds(_settings.RestoreTimeout);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            return (null, null);
        }
        catch (SqlException exception)
            when (!linked.IsCancellationRequested
                && MssqlErrorClassifier.FailureClassOf(exception) is { } failureClass
            )
        {
            return (
                new DmsCommandFailure(failureClass, MssqlErrorClassifier.Diagnostic(exception, RestoreStep)),
                null
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                null,
                MssqlErrorClassifier.Diagnostic(
                    exception,
                    EndedStepOf(exception, cancellationToken, bound.Token)
                )
            );
        }
        finally
        {
            // A kill started by the token is finished before anything reads the session's fate.
            if (Volatile.Read(ref killing.Value) is { } kill)
            {
                await kill;
            }
        }
    }

    /// <summary>
    /// D-7b for a restore that ended in doubt: the work session is killed and proven gone on a token of its own,
    /// bounded by <c>SessionTerminationTimeout</c> plus the command timeout, so the caller's cancellation cannot
    /// skip it (6.10). A session not proven gone is the <c>Unknown</c> session; with it gone the staging name
    /// decides (<see cref="JudgeRestoredAsync"/>), a restore that did not complete failing with
    /// <paramref name="ended"/>, which names what ended it.
    /// </summary>
    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> ResolveRestoreAsync(
        DmsDatabaseName staging,
        MssqlRestorePlan plan,
        MssqlStagedBackup backup,
        DmsServerSessionId session,
        DmsProvisioningDiagnostic ended,
        DmsOperationId operation
    )
    {
        try
        {
            using CancellationTokenSource bound = new(
                _settings.SessionTerminationTimeout + _adminConnection.CommandTimeout,
                _timeProvider
            );
            DmsSessionTermination verdict = await TerminateSessionAsync(
                session,
                _settings.SessionTerminationTimeout,
                bound.Token
            );
            if (verdict is DmsSessionTermination.NotTerminated)
            {
                return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Only the type chain leaves the catch: a driver error may quote the connection.
            return UnresolvedRestore(operation, DmsProvisioningDiagnostic.TypeChain(exception));
        }

        return await JudgeRestoredAsync(
            staging,
            plan,
            backup,
            new DmsCommandFailure(DmsCommandFailureClass.Rejected, ended),
            operation
        );
    }

    /// <summary>
    /// What the staging name holds once nothing of the restore runs, read on a fresh maintenance session under a
    /// bound of its own: absent, or <c>RESTORING</c> with the planned primary file, is <paramref name="notCompleted"/>;
    /// <c>ONLINE</c> with that file is a completed restore, reported only after the staged file still hashes to
    /// <c>artifactSha256</c> (D-18); anything else, or a read that fails, is <c>Unknown</c> without a session.
    /// </summary>
    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> JudgeRestoredAsync(
        DmsDatabaseName staging,
        MssqlRestorePlan plan,
        MssqlStagedBackup backup,
        DmsCommandFailure notCompleted,
        DmsOperationId operation
    )
    {
        string failure;
        try
        {
            using CancellationTokenSource bound = new(
                _settings.RestoreTimeout + _adminConnection.CommandTimeout,
                _timeProvider
            );
            MssqlStagingState? state;
            await using (SqlConnection connection = await _adminConnection.OpenMaintenanceAsync(bound.Token))
            {
                state = await MssqlStagingQueries.ReadStateAsync(
                    connection,
                    staging,
                    _adminConnection.CommandTimeoutSeconds,
                    bound.Token
                );
            }

            switch (state)
            {
                case null:
                    return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(notCompleted);
                case { } other
                    when !string.Equals(
                        other.PrimaryFile,
                        plan.PrimaryDataFilePath,
                        StringComparison.Ordinal
                    ):
                    // Not the database this restore planned: nothing proves what happened to it.
                    return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null);
                case { State: MssqlStagingQueries.RestoringState }:
                    return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(notCompleted);
                case { State: MssqlStagingQueries.OnlineState } online:
                    return await RehashAsync(online, backup, bound.Token);
                default:
                    return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = DmsProvisioningDiagnostic.TypeChain(exception);
        }

        return UnresolvedRestore(operation, failure);
    }

    /// <summary>D-18 defense in depth: a completed restore is reported only while the staged file still hashes to <c>artifactSha256</c>.</summary>
    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> RehashAsync(
        MssqlStagingState restored,
        MssqlStagedBackup backup,
        CancellationToken cancellationToken
    )
    {
        if (AfterRestore is not null)
        {
            await AfterRestore(backup);
        }

        return await MssqlBackupStager.VerifyAsync(backup, cancellationToken) switch
        {
            MssqlStagedFileCheck.Intact => new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                restored.Instance
            ),
            MssqlStagedFileCheck.Unreadable unreadable => NotRestored(
                new DmsProvisioningDiagnostic(
                    unreadable.Diagnostic.ExceptionTypeChain,
                    null,
                    DmsEngineOperations.StagedBackupChanged
                )
            ),
            _ => NotRestored(DmsProvisioningDiagnostic.ForOperation(DmsEngineOperations.StagedBackupChanged)),
        };
    }

    private DmsCommandOutcome<DmsInstanceIdentity> UnresolvedRestore(DmsOperationId operation, string failure)
    {
        _logger.LogWarning(
            RestoreResolutionFailed,
            "The restore outcome of operation {Operation} could not be resolved: {ExceptionTypeChain}",
            operation.Value,
            failure
        );
        return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null);
    }

    /// <summary>
    /// Ends a restore whose token fired by killing its work session on a token of its own, bounded like the
    /// resolution. It only ends the batch: the resolution that follows proves the session gone, so a failure here is
    /// ignored.
    /// </summary>
    private async Task KillWorkSessionAsync(DmsServerSessionId session)
    {
        try
        {
            using CancellationTokenSource bound = new(
                _settings.SessionTerminationTimeout + _adminConnection.CommandTimeout,
                _timeProvider
            );
            await TerminateSessionAsync(session, _settings.SessionTerminationTimeout, bound.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The resolution's own termination decides; only the type chain would be loggable.
        }
    }

    private void LogStagedBackupResidue(MssqlStagedBackup backup) =>
        _logger.LogError(
            MssqlBackupStager.StagedFileResidue,
            "DMSPROV-StagedBackupResidue: the staged backup {ResourceId} of an interrupted restore could not be deleted",
            backup.FileName
        );

    /// <summary>
    /// What ended a restore in doubt: the caller's cancellation, the restore bound (or the driver's timeout, which
    /// carries the same bound), or anything else, such as a lost connection.
    /// </summary>
    private static string EndedStepOf(
        Exception exception,
        CancellationToken callerToken,
        CancellationToken boundToken
    )
    {
        if (callerToken.IsCancellationRequested)
        {
            return DmsEngineOperations.RestoreCancelled;
        }

        return
            boundToken.IsCancellationRequested
            || MssqlErrorClassifier.ErrorNumberOf(exception) == MssqlErrorClassifier.ClientTimeout
            ? DmsEngineOperations.RestoreTimedOut
            : DmsEngineOperations.RestoreRolledBack;
    }

    /// <summary>A request before the restore failed without a server verdict: nothing was restored.</summary>
    private static DmsCommandOutcome<DmsInstanceIdentity> NotStarted(
        Exception exception,
        CancellationToken cancellationToken
    ) =>
        NotRestored(
            MssqlErrorClassifier.Diagnostic(
                exception,
                cancellationToken.IsCancellationRequested
                    ? DmsEngineOperations.RestoreCancelled
                    : DmsEngineOperations.RestoreNotStarted
            )
        );

    /// <summary>
    /// The server refused to read the staged backup: a file it cannot open is not visible to it (a staging
    /// failure), a privilege refusal is the admin's, and any other refusal is the backup's own (D-11).
    /// </summary>
    private static DmsCommandOutcome<DmsInstanceIdentity> BackupReadRefused(
        SqlException exception,
        DmsCommandFailureClass failureClass
    )
    {
        if (
            MssqlErrorClassifier
                .ErrorNumbersOf(exception)
                .Contains(MssqlErrorClassifier.BackupDeviceCannotOpen)
        )
        {
            return NotRestored(MssqlErrorClassifier.Diagnostic(exception, MssqlBackupStager.NotVisibleStep));
        }

        return failureClass == DmsCommandFailureClass.PrivilegeMissing
            ? new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.PrivilegeMissing,
                    MssqlErrorClassifier.Diagnostic(exception, ReadBackupStep)
                )
            )
            : NotRestored(MssqlErrorClassifier.Diagnostic(exception, BackupUnreadableStep));
    }

    private static DmsCommandOutcome<DmsInstanceIdentity> NotRestored(DmsProvisioningDiagnostic diagnostic) =>
        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
            new DmsCommandFailure(DmsCommandFailureClass.Rejected, diagnostic)
        );

    private static DmsCommandOutcome DropRefused(
        DmsCommandFailureClass failureClass,
        DmsProvisioningDiagnostic diagnostic
    ) => new DmsCommandOutcome.Failed(new DmsCommandFailure(failureClass, diagnostic));

    /// <summary>A drop request that raised: a rejection in its own response is definite; anything else leaves the drop in doubt on <paramref name="session"/>.</summary>
    private static DmsCommandOutcome ClassifyDrop(Exception exception, DmsServerSessionId session) =>
        exception is SqlException && MssqlErrorClassifier.FailureClassOf(exception) is { } failureClass
            ? DropRefused(failureClass, MssqlErrorClassifier.Diagnostic(exception, DropStagingStep))
            : new DmsCommandOutcome.Unknown(session);

    private static void RequireStagingOf(DmsDatabaseName staging, DmsOperationId operation)
    {
        if (staging != operation.StagingDatabaseName)
        {
            throw new ArgumentException(
                "The staging name must be the operation's generated staging name.",
                nameof(staging)
            );
        }
    }

    private static void RequireHolder(IDmsTargetLockHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (holder is not MssqlTargetLockSession)
        {
            throw new ArgumentException(
                "The SQL Server engine mutates only through holders it acquired.",
                nameof(holder)
            );
        }
    }
}
