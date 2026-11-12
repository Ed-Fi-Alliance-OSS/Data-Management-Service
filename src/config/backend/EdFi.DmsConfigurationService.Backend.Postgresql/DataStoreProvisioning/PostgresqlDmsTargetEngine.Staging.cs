// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
using System.Globalization;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The staging stage of the PostgreSQL engine: the staging database is created
/// and claimed on the holder session; the replay runs through <see cref="PsqlRestoreRunner"/> after the
/// D-16 role check, and a replay whose outcome is in doubt is resolved here, because only this engine can
/// address the <c>psql</c> session by its application name (D-7b): every such session is terminated with
/// verification within <c>SessionTerminationTimeout</c>, then the two singleton tables are read on the
/// staging database; both present means the single transaction committed, neither means it rolled back,
/// anything else stays unresolved. Resolution needs the process proven ended first: a process whose end
/// the runner could not confirm may still open its session after any lookup, so it is never resolved; it
/// is reported as residue with an unknown outcome. The staging drop runs on any session after
/// re-verifying the instance.
/// </summary>
public sealed partial class PostgresqlDmsTargetEngine
{
    /// <summary>The <c>Diagnostic.Operation</c> of a replay the process could not even start.</summary>
    public const string RestoreStartStep = "RestoreIntoStaging.Start";

    /// <summary>The <c>Diagnostic.Operation</c> of a replay whose transaction rolled back for a reason a retry can outlive: its session ended in doubt and it was found rolled back, or the server rejected it with a retryable SQLSTATE.</summary>
    public const string RestoreRolledBackStep = DmsEngineOperations.RestoreRolledBack;

    /// <summary>The <c>Diagnostic.Operation</c> of a replay the restore bound ended, found rolled back.</summary>
    public const string RestoreTimedOutStep = DmsEngineOperations.RestoreTimedOut;

    /// <summary>The <c>Diagnostic.Operation</c> of a replay the caller's cancellation ended, found rolled back.</summary>
    public const string RestoreCancelledStep = DmsEngineOperations.RestoreCancelled;

    /// <summary>The <c>Diagnostic.Operation</c> of a completed replay whose staging database is no longer under its name.</summary>
    public const string RestoreInstanceStep = "RestoreIntoStaging.Instance";

    /// <summary>
    /// The <c>Diagnostic.Operation</c> of a replay refused before <c>psql</c> started because the server authenticates
    /// the administrative login to the staging database through GSSAPI, which <c>psql</c> cannot do: its login would
    /// come from this process's Kerberos credentials, which the replay's cleared environment does not carry. The
    /// orchestrator reports it as a permanent <c>RestoreFailed</c> and drops the staging database.
    /// </summary>
    public const string RestoreGssAuthenticationStep = "RestoreIntoStaging.GssAuthentication";

    /// <summary>
    /// The <c>Diagnostic.Operation</c> of a replay refused before <c>psql</c> started because the server refused the
    /// administrative login or a privilege it needs (<see cref="PostgresqlErrorClassifier.IsAccessRefusal"/>), such as a
    /// <c>pg_hba.conf</c> rule that matches the maintenance database but not the staging database. Its class is
    /// <see cref="DmsCommandFailureClass.PrivilegeMissing"/>, which the orchestrator reports as a permanent
    /// <c>AdminPrivilegeMissing</c>, and it drops the staging database.
    /// </summary>
    public const string RestoreAccessRefusedStep = "RestoreIntoStaging.AccessRefused";

    internal static readonly EventId ReplayResolutionFailed = new(1438_35, nameof(ReplayResolutionFailed));

    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    )
    {
        PostgresqlTargetLockSession session = RequireHolder(holder);
        ArgumentNullException.ThrowIfNull(claim);
        return await session.RunAsync(
            token =>
                PostgresqlStagingQueries.CreateAsync(
                    session.Connection,
                    staging,
                    claim,
                    session.Session,
                    token
                ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }

    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken,
        Action? restoreStarting = null
    )
    {
        // The target is not consulted: a dump records no database name to compare with (D-11 is SQL Server's).
        ArgumentNullException.ThrowIfNull(artifact);

        // The operation directory, the staging database's authentication, then D-16 before the replay: the dump's
        // ownership statements name the role. A failure here, before any process exists, leaves the staging database
        // as created, so it is a definite outcome (a directory removed while the operation waited included).
        PrivateOperationDirectory directory;
        DmsCommandOutcome<DmsInstanceIdentity>? refused;
        try
        {
            directory = PrivateOperationDirectory.Attach(
                Path.GetDirectoryName(artifact.FilePath)
                    ?? throw new ArgumentException("The artifact has no directory.", nameof(artifact)),
                operation
            );
            if (await IsStagingAuthenticatedThroughGssAsync(staging, cancellationToken))
            {
                return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.Rejected,
                        DmsProvisioningDiagnostic.ForOperation(RestoreGssAuthenticationStep)
                    )
                );
            }

            await using NpgsqlConnection maintenance = await _adminConnection.OpenMaintenanceAsync(
                cancellationToken
            );
            DmsServerSessionId maintenanceSession = await PostgresqlSessionQueries.ReadOwnSessionAsync(
                maintenance,
                cancellationToken
            );
            refused = await PostgresqlStagingQueries.EnsureEnqueueOwnerAsync(
                maintenance,
                maintenanceSession,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PreReplayFailed(exception, cancellationToken);
        }

        if (refused is not null)
        {
            return refused;
        }

        // The directory, authentication and role checks are done: the caller's step deadline restarts here,
        // so RestoreTimeout bounds the replay alone, as the runner's own bound does.
        restoreStarting?.Invoke();
        PsqlRunResult run;
        try
        {
            run = await _runner.RunAsync(
                _adminConnection,
                staging,
                artifact,
                directory,
                operation,
                cancellationToken
            );
        }
        catch (Win32Exception exception)
        {
            // The process never started, so nothing reached the server.
            return StartFailed(exception, cancellationToken);
        }

        switch (run)
        {
            case PsqlRunResult.NotStarted notStarted:
                return NotStarted(notStarted.Cause, cancellationToken);
            case PsqlRunResult.Exited { ExitCode: 0 }:
                return await ReadCommittedInstanceAsync(staging, operation);
            case PsqlRunResult.Exited
            {
                ExitCode: PsqlRestoreRunner.ScriptErrorExitCode,
                SqlState: { } sqlState
            } when PostgresqlErrorClassifier.FailureClassOf(sqlState) is { } failureClass:
                // ON_ERROR_STOP ended the replay on a server-reported rejection: the single transaction
                // never committed. Only exit 3 says the last message is the error that stopped the
                // script; after any other exit it may be an earlier notice.
                return ScriptStopped(failureClass, sqlState);
            case PsqlRunResult.Unconfirmed unconfirmed:
                // No lookup proves anything while the process may still run: it can connect after the
                // enumeration, so the outcome stays unknown and the process is the operator's.
                return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null)
                {
                    Residue =
                    [
                        new DmsCleanupResidue(
                            DmsResidueKind.ChildProcess,
                            ReplayProcessResourceId(operation, unconfirmed.ProcessId)
                        ),
                    ],
                };
            default:
                return await ResolveReplayAsync(staging, operation, run);
        }
    }

    /// <summary>The residue id of a replay process: the replay's application name and the process id, never a path or an argument.</summary>
    internal static string ReplayProcessResourceId(DmsOperationId operation, int processId) =>
        $"{operation.RestoreApplicationName}/{processId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The definite failure of a replay that <c>ON_ERROR_STOP</c> ended on the server's rejection
    /// <paramref name="sqlState"/>, whose class <paramref name="failureClass"/> already proves nothing committed.
    /// Whether a retry can succeed is a separate question: a rejection that clears on its own
    /// (<see cref="PostgresqlErrorClassifier.IsRetryableRejection"/>) is named <c>RestoreRolledBack</c>, which the
    /// orchestrator reports as a transient <c>RestoreFailed</c>; any other is the replay's own step, permanent.
    /// </summary>
    internal static DmsCommandOutcome<DmsInstanceIdentity> ScriptStopped(
        DmsCommandFailureClass failureClass,
        string sqlState
    ) =>
        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
            new DmsCommandFailure(
                failureClass,
                new DmsProvisioningDiagnostic(
                    string.Empty,
                    sqlState,
                    PostgresqlErrorClassifier.IsRetryableRejection(sqlState)
                        ? RestoreRolledBackStep
                        : PsqlRestoreRunner.RestoreStep
                )
            )
        );

    /// <summary>
    /// Whether the server authenticates the administrative login to <paramref name="staging"/> through GSSAPI, asked on
    /// a session to that database itself: <c>pg_hba.conf</c> is matched per database, so how a session to the
    /// maintenance database was authenticated (readiness, the lock, the role preparation) proves nothing here. The
    /// session reaches the one configured server with the user and TLS settings <c>psql</c> receives, so the server
    /// applies the rule it will apply to <c>psql</c>. It is closed before <c>psql</c> starts.
    /// </summary>
    private async Task<bool> IsStagingAuthenticatedThroughGssAsync(
        DmsDatabaseName staging,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlConnection connection = await _adminConnection.OpenAsync(
            staging.Value,
            PostgresqlAdminConnection.SessionApplicationName,
            cancellationToken
        );
        return await PostgresqlEnvironment.IsGssAuthenticatedAsync(connection, cancellationToken);
    }

    /// <summary>
    /// A step before the replay failed, so no process exists and the outcome is definite. A server refusal of the
    /// login or a privilege (<see cref="PostgresqlErrorClassifier.IsAccessRefusal"/>) needs an operator, so it keeps
    /// its SQLSTATE under <see cref="RestoreAccessRefusedStep"/> as <see cref="DmsCommandFailureClass.PrivilegeMissing"/>;
    /// anything else, or any failure under a cancelled token, is <see cref="NotStarted"/>.
    /// </summary>
    internal static DmsCommandOutcome<DmsInstanceIdentity> PreReplayFailed(
        Exception exception,
        CancellationToken cancellationToken
    ) =>
        !cancellationToken.IsCancellationRequested
        && exception is PostgresException postgres
        && PostgresqlErrorClassifier.IsAccessRefusal(postgres.SqlState)
            ? new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.PrivilegeMissing,
                    PostgresqlErrorClassifier.Diagnostic(exception, RestoreAccessRefusedStep)
                )
            )
            : NotStarted(exception, cancellationToken);

    /// <summary>
    /// A <c>psql</c> process the system could not create. Exhausted process, memory, or file-descriptor resources
    /// can clear without any change, so they are <see cref="NotStarted"/> and retryable; anything else (a missing
    /// or non-executable file, a refused permission) needs the operator and stays <see cref="RestoreStartStep"/>.
    /// </summary>
    internal static DmsCommandOutcome<DmsInstanceIdentity> StartFailed(
        Win32Exception exception,
        CancellationToken cancellationToken
    ) =>
        IsResourceExhaustion(exception.NativeErrorCode)
            ? NotStarted(exception, cancellationToken)
            : new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.From(exception, RestoreStartStep)
                )
            );

    /// <summary>
    /// The process-creation failure codes that mean a temporary shortage. On Linux, .NET reports the
    /// <c>fork</c>/<c>exec</c> <c>errno</c>: EAGAIN (11, the process limit), ENOMEM (12), ENFILE (23), EMFILE (24).
    /// On Windows, <c>CreateProcess</c> reports ERROR_NOT_ENOUGH_MEMORY (8), ERROR_OUTOFMEMORY (14),
    /// ERROR_NO_SYSTEM_RESOURCES (1450), ERROR_COMMITMENT_LIMIT (1455). Other platforms number <c>errno</c>
    /// differently, so nothing there is judged temporary.
    /// </summary>
    internal static bool IsResourceExhaustion(int nativeErrorCode)
    {
        if (OperatingSystem.IsWindows())
        {
            return nativeErrorCode is 8 or 14 or 1450 or 1455;
        }

        return OperatingSystem.IsLinux() && nativeErrorCode is 11 or 12 or 23 or 24;
    }

    /// <summary>
    /// A replay that never started, because something it needs failed before any process existed: a definite
    /// failure, so cleanup may drop the staging database. A cancelled token is named as a cancellation, whatever
    /// form the driver gave it, so the orchestrator tells the caller's from the deadline's; anything else is
    /// <c>RestoreNotStarted</c> with its type chain only.
    /// </summary>
    private static DmsCommandOutcome<DmsInstanceIdentity> NotStarted(
        Exception exception,
        CancellationToken cancellationToken
    ) =>
        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
            new DmsCommandFailure(
                DmsCommandFailureClass.Rejected,
                cancellationToken.IsCancellationRequested
                    ? DmsProvisioningDiagnostic.ForOperation(RestoreCancelledStep)
                    : DmsProvisioningDiagnostic.From(exception, DmsEngineOperations.RestoreNotStarted)
            )
        );

    /// <summary>
    /// The staging instance after <c>psql</c> exited 0, which proves the single transaction committed and leaves
    /// nothing of the replay running. The read runs on a <c>CommandTimeout</c> bound of its own, so a cancellation
    /// or deadline arriving after the replay cannot discard that outcome; a read that still fails is
    /// <c>Unknown</c> and quiescent, so the orchestrator's reads decide the state and cleanup may drop the database.
    /// </summary>
    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> ReadCommittedInstanceAsync(
        DmsDatabaseName staging,
        DmsOperationId operation
    )
    {
        using CancellationTokenSource bound = new(_adminConnection.CommandTimeout, _timeProvider);
        string failure;
        try
        {
            return await ReadStagingInstanceAsync(staging, RestoreInstanceStep, bound.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Only the type chain leaves the catch: a driver error may quote the connection.
            failure = DmsProvisioningDiagnostic.TypeChain(exception);
        }

        _logger.LogWarning(
            ReplayResolutionFailed,
            "The replay outcome of operation {Operation} could not be resolved: {ExceptionTypeChain}",
            operation.Value,
            failure
        );
        return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null) { Quiescent = true };
    }

    public async Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(stagingInstance);
        await using NpgsqlConnection connection = await _adminConnection.OpenMaintenanceAsync(
            cancellationToken
        );
        DmsServerSessionId session = await PostgresqlSessionQueries.ReadOwnSessionAsync(
            connection,
            cancellationToken
        );
        return await PostgresqlStagingQueries.DropStagingAsync(
            connection,
            staging,
            stagingInstance,
            session,
            cancellationToken
        );
    }

    /// <summary>
    /// D-7b for a replay whose process is proven ended (it exited, or it was killed and its exit observed)
    /// without a verdict: on a token of its own, bounded by <c>SessionTerminationTimeout</c> so the caller's
    /// cancellation cannot skip it, every session carrying the replay's application name on the
    /// staging database is terminated with verification; one that cannot be proven gone is the
    /// <c>Unknown</c> session, and a lookup or termination that fails proves nothing, so the outcome stays
    /// <c>Unknown</c>, names no session and is not quiescent. With the process and every session gone, nothing
    /// of the command remains to change the state, so the singleton tables are read on a <c>CommandTimeout</c>
    /// bound of their own, which the terminations cannot have spent: both existing means the transaction
    /// committed and the staging instance is returned; neither means it rolled back, a definite failure named
    /// by <see cref="RolledBackStepOf"/>; anything else, or a read that fails, is <c>Unknown</c> and
    /// <see cref="DmsCommandOutcome{T}.Unknown.Quiescent"/>, so the orchestrator's reads decide the state.
    /// </summary>
    internal async Task<DmsCommandOutcome<DmsInstanceIdentity>> ResolveReplayAsync(
        DmsDatabaseName staging,
        DmsOperationId operation,
        PsqlRunResult run
    )
    {
        try
        {
            if (await EndReplaySessionsAsync(staging, operation) is { } unproven)
            {
                return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(unproven);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The lookup, a termination, or the bounded wait stopped before every session was proven gone.
            LogReplayResolutionFailed(operation, exception);
            return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null);
        }

        using CancellationTokenSource bound = new(_adminConnection.CommandTimeout, _timeProvider);
        try
        {
            ReplayState state = await ReadReplayStateAsync(staging, bound.Token);
            return state switch
            {
                ReplayState.Committed => await ReadStagingInstanceAsync(
                    staging,
                    RestoreInstanceStep,
                    bound.Token
                ),
                ReplayState.RolledBack => new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.Rejected,
                        new DmsProvisioningDiagnostic(string.Empty, SqlStateOf(run), RolledBackStepOf(run))
                    )
                ),
                _ => new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null) { Quiescent = true },
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Every session is proven gone, so the quiescence proof stands though the outcome was not read.
            LogReplayResolutionFailed(operation, exception);
            return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null) { Quiescent = true };
        }
    }

    /// <summary>
    /// Terminates every session carrying the replay's application name on <paramref name="staging"/> within
    /// <c>SessionTerminationTimeout</c>; the first one that cannot be proven gone, or null when none remains.
    /// </summary>
    private async Task<DmsServerSessionId?> EndReplaySessionsAsync(
        DmsDatabaseName staging,
        DmsOperationId operation
    )
    {
        using CancellationTokenSource bound = new(_settings.SessionTerminationTimeout, _timeProvider);
        IReadOnlyList<DmsServerSessionId> sessions = await FindSessionsAsync(
            staging,
            operation.RestoreApplicationName,
            bound.Token
        );
        foreach (DmsServerSessionId session in sessions)
        {
            DmsSessionTermination verdict = await TerminateSessionAsync(
                session,
                _settings.SessionTerminationTimeout,
                bound.Token
            );
            if (verdict is DmsSessionTermination.NotTerminated)
            {
                return session;
            }
        }

        return null;
    }

    private void LogReplayResolutionFailed(DmsOperationId operation, Exception exception) =>
        // Only the type chain leaves the catch: a driver error may quote the connection.
        _logger.LogWarning(
            ReplayResolutionFailed,
            "The replay outcome of operation {Operation} could not be resolved: {ExceptionTypeChain}",
            operation.Value,
            DmsProvisioningDiagnostic.TypeChain(exception)
        );

    /// <summary>What the singleton tables say about the replay's single transaction.</summary>
    internal enum ReplayState
    {
        /// <summary>Both tables exist, whatever rows they hold: the transaction committed, and validation judges the contents.</summary>
        Committed,

        /// <summary>Neither table exists: the transaction rolled back.</summary>
        RolledBack,

        /// <summary>One table exists, a read failed, or the database could not be reached.</summary>
        Inconclusive,
    }

    /// <summary>Whether one singleton table exists; its rows are not this question's concern.</summary>
    internal enum ReplayTableState
    {
        Missing,
        Exists,
        Unreadable,
    }

    /// <summary>
    /// The replay's fate from the two tables: both existing is a commit even when a table is
    /// empty, because only a committed transaction leaves a table behind; an empty or invalid singleton is
    /// a content problem for validation, not evidence of a rollback.
    /// </summary>
    internal static ReplayState ClassifyReplayState(
        ReplayTableState identity,
        ReplayTableState effectiveSchema
    ) =>
        (identity, effectiveSchema) switch
        {
            (ReplayTableState.Exists, ReplayTableState.Exists) => ReplayState.Committed,
            (ReplayTableState.Missing, ReplayTableState.Missing) => ReplayState.RolledBack,
            _ => ReplayState.Inconclusive,
        };

    /// <summary>
    /// Whether the replay's single transaction committed, by <see cref="ClassifyReplayState"/> over the
    /// identity and effective-schema tables; a staging database that is gone or unreachable is inconclusive.
    /// </summary>
    internal async Task<ReplayState> ReadReplayStateAsync(
        DmsDatabaseName staging,
        CancellationToken cancellationToken
    )
    {
        NpgsqlConnection connection;
        try
        {
            connection = await _adminConnection.OpenAsync(
                staging.Value,
                PostgresqlAdminConnection.SessionApplicationName,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ReplayState.Inconclusive;
        }

        await using (connection)
        {
            ReplayTableState identity = await TableStateAsync(
                connection,
                PostgresqlInventoryQueries.SourceIdentityRows,
                cancellationToken
            );
            ReplayTableState effectiveSchema = await TableStateAsync(
                connection,
                PostgresqlInventoryQueries.EffectiveSchemaRows,
                cancellationToken
            );
            return ClassifyReplayState(identity, effectiveSchema);
        }
    }

    /// <summary>Exists when the table's query runs, whatever it returns; Missing on an undefined relation or schema; Unreadable on any other failure.</summary>
    private static async Task<ReplayTableState> TableStateAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using NpgsqlCommand command = new(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return ReplayTableState.Exists;
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsMissingRelation(exception.SqlState))
        {
            return ReplayTableState.Missing;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ReplayTableState.Unreadable;
        }
    }

    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> ReadStagingInstanceAsync(
        DmsDatabaseName staging,
        string step,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlConnection connection = await _adminConnection.OpenMaintenanceAsync(
            cancellationToken
        );
        DmsInstanceIdentity.Postgresql? instance = await PostgresqlStagingQueries.ReadInstanceAsync(
            connection,
            staging,
            cancellationToken
        );
        return instance is null
            ? new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(step)
                )
            )
            : new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance);
    }

    private static string? SqlStateOf(PsqlRunResult run) =>
        run is PsqlRunResult.Exited { SqlState: { } sqlState } ? sqlState : null;

    /// <summary>
    /// What a replay found rolled back is named by, which decides its retry category in the orchestrator: the
    /// restore bound and the caller's cancellation name themselves. <c>RestoreRolledBack</c>, which is transient,
    /// needs evidence that the connection, not the script, ended the replay: a SQLSTATE that leaves the outcome in
    /// doubt (connection class, cancellation, shutdown), or <c>psql</c>'s exit code 2, which it gives only for a
    /// connection that could not be made or went bad. Any other end (exit 3 with no readable SQLSTATE: the script
    /// stopped on an error; exit 1: a fatal error of <c>psql</c>'s own; a process ended from outside) proves that
    /// nothing committed, not that a retry can succeed, so it is the replay's own step, a permanent
    /// <c>RestoreFailed</c>.
    /// </summary>
    internal static string RolledBackStepOf(PsqlRunResult run) =>
        run switch
        {
            PsqlRunResult.Killed { Reason: PsqlKillReason.TimedOut } => RestoreTimedOutStep,
            PsqlRunResult.Unconfirmed { Reason: PsqlKillReason.TimedOut } => RestoreTimedOutStep,
            PsqlRunResult.Killed { Reason: PsqlKillReason.Cancelled } => RestoreCancelledStep,
            PsqlRunResult.Unconfirmed { Reason: PsqlKillReason.Cancelled } => RestoreCancelledStep,
            PsqlRunResult.Exited { SqlState: { } sqlState }
                when PostgresqlErrorClassifier.FailureClassOf(sqlState) is null => RestoreRolledBackStep,
            PsqlRunResult.Exited { ExitCode: PsqlRestoreRunner.ConnectionFailedExitCode } =>
                RestoreRolledBackStep,
            _ => PsqlRestoreRunner.RestoreStep,
        };

    private static PostgresqlTargetLockSession RequireHolder(IDmsTargetLockHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return holder as PostgresqlTargetLockSession
            ?? throw new ArgumentException(
                "The PostgreSQL engine mutates only through holders it acquired.",
                nameof(holder)
            );
    }
}
