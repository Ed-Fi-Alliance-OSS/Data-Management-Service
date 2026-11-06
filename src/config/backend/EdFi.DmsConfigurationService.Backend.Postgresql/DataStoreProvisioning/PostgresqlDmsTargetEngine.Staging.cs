// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The staging stage of the PostgreSQL engine (6.3 steps 4 and 5, 6.7): the staging database is created
/// and claimed on the holder session; the replay runs through <see cref="PsqlRestoreRunner"/> after the
/// D-16 role check, and a replay whose outcome is in doubt is resolved here, because only this engine can
/// address the <c>psql</c> session by its application name (D-7b): every such session is terminated with
/// verification within <c>SessionTerminationTimeout</c>, then the two singleton tables are read on the
/// staging database; both present means the single transaction committed, neither means it rolled back,
/// anything else stays unresolved. The staging drop runs on any session after re-verifying the instance.
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
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);
        PrivateOperationDirectory directory = PrivateOperationDirectory.Attach(
            Path.GetDirectoryName(artifact.FilePath)
                ?? throw new ArgumentException("The artifact has no directory.", nameof(artifact)),
            operation
        );

        // D-16 before the replay: the dump's ownership statements name the role.
        await using (
            NpgsqlConnection maintenance = await _adminConnection.OpenMaintenanceAsync(cancellationToken)
        )
        {
            DmsServerSessionId maintenanceSession = await PostgresqlSessionQueries.ReadOwnSessionAsync(
                maintenance,
                cancellationToken
            );
            DmsCommandOutcome<DmsInstanceIdentity>? refused =
                await PostgresqlStagingQueries.EnsureEnqueueOwnerAsync(
                    maintenance,
                    maintenanceSession,
                    cancellationToken
                );
            if (refused is not null)
            {
                return refused;
            }
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
            case PsqlRunResult.Exited { ExitCode: 0 }:
                return await ReadStagingInstanceAsync(staging, RestoreInstanceStep, cancellationToken);
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
            default:
                return await ResolveReplayAsync(staging, operation, run);
        }
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
    /// D-7b for a replay whose process did not end on its own with a verdict: on a token of its own, bounded
    /// by <c>SessionTerminationTimeout</c> so the caller's cancellation cannot skip it (6.10), every session
    /// carrying the replay's application name on the staging database is terminated with verification; one
    /// that cannot be proven gone is the <c>Unknown</c> session. With every session gone, the staging
    /// database's singleton tables decide: both readable means the transaction committed and the staging
    /// instance is returned; neither means it rolled back, a definite failure named after what ended the
    /// replay; anything else is <c>Unknown</c> with no session left to terminate.
    /// </summary>
    private async Task<DmsCommandOutcome<DmsInstanceIdentity>> ResolveReplayAsync(
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
            _ => new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null),
        };
    }

    private enum ReplayState
    {
        Committed,
        RolledBack,
        Inconclusive,
    }

    /// <summary>
    /// Whether the replay's single transaction committed: the identity and effective-schema singleton tables
    /// are both readable (committed), both missing (rolled back), or anything else (inconclusive, including
    /// a staging database that is gone or unreachable).
    /// </summary>
    private async Task<ReplayState> ReadReplayStateAsync(
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
            bool? identityPresent = await TableReadableAsync(
                connection,
                PostgresqlInventoryQueries.SourceIdentityRows,
                cancellationToken
            );
            bool? effectiveSchemaPresent = await TableReadableAsync(
                connection,
                PostgresqlInventoryQueries.EffectiveSchemaRows,
                cancellationToken
            );
            return (identityPresent, effectiveSchemaPresent) switch
            {
                (true, true) => ReplayState.Committed,
                (false, false) => ReplayState.RolledBack,
                _ => ReplayState.Inconclusive,
            };
        }
    }

    /// <summary>True when the query returns at least one row, false when its relation is missing, null when it fails otherwise.</summary>
    private static async Task<bool?> TableReadableAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using NpgsqlCommand command = new(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken);
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsMissingRelation(exception.SqlState))
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
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
