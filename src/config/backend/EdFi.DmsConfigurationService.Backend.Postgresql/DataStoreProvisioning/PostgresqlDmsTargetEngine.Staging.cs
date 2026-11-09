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
/// The staging stage of the PostgreSQL engine (6.3 steps 4 and 5, 6.7): the staging database is created
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

    /// <summary>The <c>Diagnostic.Operation</c> of a replay whose session ended in doubt and whose transaction was found rolled back.</summary>
    public const string RestoreRolledBackStep = DmsEngineOperations.RestoreRolledBack;

    /// <summary>The <c>Diagnostic.Operation</c> of a replay the restore bound ended, found rolled back.</summary>
    public const string RestoreTimedOutStep = DmsEngineOperations.RestoreTimedOut;

    /// <summary>The <c>Diagnostic.Operation</c> of a replay the caller's cancellation ended, found rolled back.</summary>
    public const string RestoreCancelledStep = DmsEngineOperations.RestoreCancelled;

    /// <summary>The <c>Diagnostic.Operation</c> of a completed replay whose staging database is no longer under its name.</summary>
    public const string RestoreInstanceStep = "RestoreIntoStaging.Instance";

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
        CancellationToken cancellationToken
    )
    {
        // The target is not consulted: a dump records no database name to compare with (D-11 is SQL Server's).
        ArgumentNullException.ThrowIfNull(artifact);
        PrivateOperationDirectory directory = PrivateOperationDirectory.Attach(
            Path.GetDirectoryName(artifact.FilePath)
                ?? throw new ArgumentException("The artifact has no directory.", nameof(artifact)),
            operation
        );

        // D-16 before the replay: the dump's ownership statements name the role. A failure here, before any
        // process exists, leaves the staging database as created, so it is a definite outcome.
        DmsCommandOutcome<DmsInstanceIdentity>? refused;
        try
        {
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
            return NotStarted(exception, cancellationToken);
        }

        if (refused is not null)
        {
            return refused;
        }

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
            return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.From(exception, RestoreStartStep)
                )
            );
        }

        switch (run)
        {
            case PsqlRunResult.NotStarted notStarted:
                return NotStarted(notStarted.Cause, cancellationToken);
            case PsqlRunResult.Exited { ExitCode: 0 }:
                return await ReadCommittedInstanceAsync(staging, operation);
            case PsqlRunResult.Exited { SqlState: { } sqlState } exited
                when PostgresqlErrorClassifier.FailureClassOf(sqlState) is { } failureClass:
                // ON_ERROR_STOP ended the replay on a server-reported rejection: the single transaction
                // never committed (6.9a).
                return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                    new DmsCommandFailure(
                        failureClass,
                        new DmsProvisioningDiagnostic(
                            string.Empty,
                            exited.SqlState,
                            PsqlRestoreRunner.RestoreStep
                        )
                    )
                );
            case PsqlRunResult.Unconfirmed unconfirmed:
                // No lookup proves anything while the process may still run: it can connect after the
                // enumeration, so the outcome stays unknown and the process is the operator's (7.6).
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
    /// or deadline arriving after the replay cannot discard that outcome (6.10); a read that still fails is
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
    /// cancellation cannot skip it (6.10), every session carrying the replay's application name on the
    /// staging database is terminated with verification; one that cannot be proven gone is the
    /// <c>Unknown</c> session. With the process and every session gone, the staging database's singleton
    /// tables decide: both existing means the transaction committed and the staging instance is returned;
    /// neither means it rolled back, a definite failure named after what ended the replay; anything else is
    /// <c>Unknown</c> and <see cref="DmsCommandOutcome{T}.Unknown.Quiescent"/>, since nothing of the
    /// command remains to change the state.
    /// </summary>
    internal async Task<DmsCommandOutcome<DmsInstanceIdentity>> ResolveReplayAsync(
        DmsDatabaseName staging,
        DmsOperationId operation,
        PsqlRunResult run
    )
    {
        // A resolution that fails proves nothing: the lookup, a termination, or the bounded wait may stop it
        // anywhere, so the outcome stays unknown, names no session and is not quiescent (D-7b).
        string failure;
        try
        {
            return await ResolveProvenReplayAsync(staging, operation, run);
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
        return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null);
    }

    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> ResolveProvenReplayAsync(
        DmsDatabaseName staging,
        DmsOperationId operation,
        PsqlRunResult run
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
                return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session);
            }
        }

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
    /// The replay's fate from the two tables (6.3 step 5): both existing is a commit even when a table is
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

    private static string RolledBackStepOf(PsqlRunResult run) =>
        run switch
        {
            PsqlRunResult.Killed { Reason: PsqlKillReason.TimedOut } => RestoreTimedOutStep,
            PsqlRunResult.Unconfirmed { Reason: PsqlKillReason.TimedOut } => RestoreTimedOutStep,
            PsqlRunResult.Killed { Reason: PsqlKillReason.Cancelled } => RestoreCancelledStep,
            PsqlRunResult.Unconfirmed { Reason: PsqlKillReason.Cancelled } => RestoreCancelledStep,
            _ => RestoreRolledBackStep,
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
