// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>One exclusivity observation of the fence: whether every session was visible, and how many others used the database.</summary>
internal readonly record struct MssqlFenceObservation(bool Visible, int Others);

/// <summary>
/// The owned delete of 6.6 (SQL Server) on the holder's connection: the fence, its reversal, and the guarded drop,
/// classified by 6.9a the way the publication commands are. Each is one guarded batch without <c>TRY/CATCH</c>: the
/// name, the <c>database_id</c>, the primary file and the <c>create_date</c> snapshot the delete inspected are checked
/// and only then is the statement run, so the error that refuses it reaches the client with its own number (D-13). The
/// snapshot is part of the guard here, unlike the publication commands, because nothing in a delete changes it (single-
/// and multi-user mode leave it as it is) while SQL Server reuses a freed <c>database_id</c> at once and a database
/// created outside the provisioner under the same name gets the same default file name: only the snapshot tells such a
/// replacement apart. A guard that fails answers a one-row result and touches
/// nothing. The fence is <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c>, which ends every session on the database and
/// keeps none (the caller closes its own session first); once it has returned the target is fenced, and every
/// definite failure after it carries <see cref="DmsResidueKind.FencedTarget"/> residue, so the caller knows that the
/// fence must be reversed. Exclusivity is then proven by observation (D-22): no other session may be using the
/// database, read with the visibility the count needs. Nothing is killed after the fence: a client that took the
/// single-user slot is left alone and the fence is reported as not established. <c>ALTER DATABASE</c> and
/// <c>DROP DATABASE</c> take no parameters, so their names are quoted from validated names (6.8); the guards and every
/// catalog read are parameterized.
/// </summary>
internal static class MssqlDeletionQueries
{
    public const string FenceStep = "Fence";

    /// <summary>The target name no longer holds the instance; nothing was fenced.</summary>
    public const string FenceInstanceStep = "Fence.InstanceChanged";

    /// <summary>The target is fenced, but another session still used it at the end of the termination bound.</summary>
    public const string FenceExclusivityStep = "Fence.Exclusivity";

    public const string UnfenceStep = "Unfence";

    /// <summary>The target name no longer holds the instance; nothing was unfenced.</summary>
    public const string UnfenceInstanceStep = "Unfence.InstanceChanged";

    public const string DropTargetStep = "DropTarget";

    /// <summary>The target name holds another instance; nothing was dropped.</summary>
    public const string DropTargetInstanceStep = "DropTarget.InstanceChanged";

    /// <summary>The target is not in single-user mode; an owned delete drops only a fenced target.</summary>
    public const string DropTargetNotFencedStep = "DropTarget.NotFenced";

    /// <summary>The guard's one-row results: the name does not hold the instance, the name is empty, or it is not fenced.</summary>
    private const int GuardInstanceChanged = 0;

    private const int GuardAbsent = 1;
    private const int GuardNotFenced = 2;

    /// <summary>
    /// The guard every command shares: the database under <c>@name</c> is <c>@id</c>, its primary file is <c>@file</c>,
    /// and its <c>create_date</c> is <c>@createDate</c>.
    /// </summary>
    private const string InstanceGuard =
        "EXISTS (SELECT 1 FROM sys.databases d WHERE d.name = @name AND d.database_id = @id AND d.create_date = @createDate AND (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1) = @file)";

    /// <summary>
    /// Whether every session is visible, then the number of sessions other than this one that use the database: as
    /// their current database, or by holding a database lock on it (a three-part read from elsewhere). Either refuses
    /// <c>DROP DATABASE</c> (<c>3702</c>). The counts run only when every session is visible: without the permission
    /// <c>sys.dm_tran_locks</c> raises an error rather than showing fewer rows. This session's own database lock,
    /// which the fence leaves it holding, is not counted.
    /// </summary>
    public const string OtherSessions =
        "DECLARE @visible int = "
        + MssqlSessionQueries.SessionsVisible
        + "; IF @visible = 1 SELECT 1, (SELECT COUNT(*) FROM (SELECT s.session_id FROM sys.dm_exec_sessions s WHERE s.database_id = @id AND s.session_id <> @@SPID UNION SELECT l.request_session_id FROM sys.dm_tran_locks l WHERE l.resource_type = N'DATABASE' AND l.resource_database_id = @id AND l.request_session_id <> @@SPID) AS u); ELSE SELECT 0, 0;";

    public static string GuardedFence(DmsTargetDatabaseName target) =>
        $"IF {InstanceGuard} ALTER DATABASE {MssqlIdentifiers.Quote(target.Value)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ELSE SELECT CONVERT(int, {GuardInstanceChanged});";

    public static string GuardedUnfence(DmsTargetDatabaseName target) =>
        $"IF {InstanceGuard} ALTER DATABASE {MssqlIdentifiers.Quote(target.Value)} SET MULTI_USER; ELSE SELECT CONVERT(int, {GuardInstanceChanged});";

    /// <summary>
    /// The owned drop: an empty name, another instance, and a target that is not in single-user mode each answer their
    /// own one-row result; only our fenced instance is dropped.
    /// </summary>
    public static string GuardedDrop(DmsTargetDatabaseName target) =>
        $"IF NOT EXISTS (SELECT 1 FROM sys.databases d WHERE d.name = @name) SELECT CONVERT(int, {GuardAbsent}); "
        + $"ELSE IF NOT {InstanceGuard} SELECT CONVERT(int, {GuardInstanceChanged}); "
        + $"ELSE IF (SELECT d.user_access_desc FROM sys.databases d WHERE d.name = @name) <> N'SINGLE_USER' SELECT CONVERT(int, {GuardNotFenced}); "
        + $"ELSE DROP DATABASE {MssqlIdentifiers.Quote(target.Value)};";

    /// <summary>
    /// 6.6 step 3 (SQL Server): the name must still hold <paramref name="instance"/>, then
    /// <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c>; then, every <see cref="MssqlSessionQueries.VerificationInterval"/>
    /// until <paramref name="terminationTimeout"/> has passed, no other session may use the database (D-22). Sessions
    /// the admin cannot see are <see cref="DmsCommandFailureClass.PrivilegeMissing"/> under
    /// <see cref="MssqlSessionQueries.IdentifySessionStep"/>; a session still there at the bound is
    /// <see cref="DmsCommandFailureClass.ObjectInUse"/>. Both carry the fenced residue, as does any exception raised
    /// once the fence has returned, including a cancellation: the fence stands and must be reversed.
    /// </summary>
    public static async Task<DmsCommandOutcome> FenceAsync(
        SqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity.Mssql instance,
        DmsServerSessionId session,
        TimeSpan terminationTimeout,
        int commandTimeoutSeconds,
        TimeProvider timeProvider,
        Func<int, MssqlFenceObservation, MssqlFenceObservation>? observationHook,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(timeProvider);
        DmsCommandNotDispatchedException.ThrowIfCancellationRequested(cancellationToken);
        int? guard;
        try
        {
            guard = await RunGuardedAsync(
                connection,
                GuardedFence(target),
                target,
                instance,
                commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return Classify(exception, session, FenceStep);
        }

        if (guard is not null)
        {
            return Failed(DmsCommandFailureClass.DoesNotExist, FenceInstanceStep);
        }

        try
        {
            return await EstablishExclusivityAsync(
                connection,
                target,
                instance,
                terminationTimeout,
                commandTimeoutSeconds,
                timeProvider,
                observationHook,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Fenced(
                target,
                DmsCommandFailureClass.Rejected,
                MssqlErrorClassifier.Diagnostic(exception, FenceExclusivityStep)
            );
        }
    }

    /// <summary>
    /// Reverses <see cref="FenceAsync"/>: the name must still hold <paramref name="instance"/> (otherwise a definite
    /// <see cref="DmsCommandFailureClass.DoesNotExist"/> that touches nothing), then <c>SET MULTI_USER</c>. A session
    /// holding the single-user slot refuses it (<c>5064</c>, object in use) and the fence stands.
    /// </summary>
    public static async Task<DmsCommandOutcome> UnfenceAsync(
        SqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity.Mssql instance,
        DmsServerSessionId session,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        int? guard;
        try
        {
            guard = await RunGuardedAsync(
                connection,
                GuardedUnfence(target),
                target,
                instance,
                commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return Classify(exception, session, UnfenceStep);
        }

        return guard is null
            ? new DmsCommandOutcome.Succeeded()
            : Failed(DmsCommandFailureClass.DoesNotExist, UnfenceInstanceStep);
    }

    /// <summary>
    /// 6.6 step 5 (SQL Server): the name must hold <paramref name="instance"/>, still in single-user mode; then
    /// <c>DROP DATABASE</c> in the same batch. An empty name is the definite
    /// <see cref="DmsCommandFailureClass.DoesNotExist"/> under <see cref="DmsEngineOperations.DropTargetAbsent"/>;
    /// another instance is <see cref="DmsCommandFailureClass.DoesNotExist"/> under
    /// <see cref="DropTargetInstanceStep"/>; a target that is not in single-user mode is
    /// <see cref="DmsCommandFailureClass.ObjectInUse"/>. None of these touches the database. Unlike PostgreSQL's
    /// <c>WITH (FORCE)</c>, the drop ends no session: one still using the database refuses it (<c>3702</c>, object in
    /// use), which is the server's own check that no session remains.
    /// </summary>
    public static async Task<DmsCommandOutcome> DropTargetAsync(
        SqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity.Mssql instance,
        DmsServerSessionId session,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        DmsCommandNotDispatchedException.ThrowIfCancellationRequested(cancellationToken);
        int? guard;
        try
        {
            guard = await RunGuardedAsync(
                connection,
                GuardedDrop(target),
                target,
                instance,
                commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return Classify(exception, session, DropTargetStep);
        }

        return guard switch
        {
            null => new DmsCommandOutcome.Succeeded(),
            GuardAbsent => Failed(DmsCommandFailureClass.DoesNotExist, DmsEngineOperations.DropTargetAbsent),
            GuardNotFenced => Failed(DmsCommandFailureClass.ObjectInUse, DropTargetNotFencedStep),
            _ => Failed(DmsCommandFailureClass.DoesNotExist, DropTargetInstanceStep),
        };
    }

    /// <summary>
    /// D-22 under the fence: observes the sessions using the database until none remains; the bound is the whole
    /// <paramref name="terminationTimeout"/>. Nothing is killed: <c>ROLLBACK IMMEDIATE</c> has already ended every
    /// session that was there, and the holder now holds the single-user slot, so a session listed now is one the
    /// server has not yet removed.
    /// </summary>
    private static async Task<DmsCommandOutcome> EstablishExclusivityAsync(
        SqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity.Mssql instance,
        TimeSpan terminationTimeout,
        int commandTimeoutSeconds,
        TimeProvider timeProvider,
        Func<int, MssqlFenceObservation, MssqlFenceObservation>? observationHook,
        CancellationToken cancellationToken
    )
    {
        long started = timeProvider.GetTimestamp();
        int observation = 0;
        while (true)
        {
            MssqlFenceObservation observed = await ObserveOtherSessionsAsync(
                connection,
                instance,
                commandTimeoutSeconds,
                cancellationToken
            );
            (bool visible, int others) = observationHook is null
                ? observed
                : observationHook(observation, observed);
            observation++;
            if (!visible)
            {
                return Fenced(
                    target,
                    DmsCommandFailureClass.PrivilegeMissing,
                    DmsProvisioningDiagnostic.ForOperation(MssqlSessionQueries.IdentifySessionStep)
                );
            }

            if (others == 0)
            {
                return new DmsCommandOutcome.Succeeded();
            }

            TimeSpan remaining = terminationTimeout - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return Fenced(
                    target,
                    DmsCommandFailureClass.ObjectInUse,
                    DmsProvisioningDiagnostic.ForOperation(FenceExclusivityStep)
                );
            }

            await Task.Delay(
                remaining < MssqlSessionQueries.VerificationInterval
                    ? remaining
                    : MssqlSessionQueries.VerificationInterval,
                timeProvider,
                cancellationToken
            );
        }
    }

    private static async Task<MssqlFenceObservation> ObserveOtherSessionsAsync(
        SqlConnection connection,
        DmsInstanceIdentity.Mssql instance,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(OtherSessions, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = instance.DatabaseId });
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The session observation returned no row.");
        }

        bool visible = !await reader.IsDBNullAsync(0, cancellationToken) && reader.GetInt32(0) == 1;
        return new MssqlFenceObservation(visible, reader.GetInt32(1));
    }

    /// <summary>
    /// Runs a guarded batch and reads its whole response: the guard's one-row result when it came back (nothing was
    /// run), null when the statement ran. An error the statement raised surfaces here as a <see cref="SqlException"/>,
    /// because draining the response reads every token the server sent.
    /// </summary>
    private static async Task<int?> RunGuardedAsync(
        SqlConnection connection,
        string batch,
        DmsDatabaseName name,
        DmsInstanceIdentity.Mssql instance,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(batch, connection) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name.Value });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = instance.DatabaseId });
        command.Parameters.Add(
            new SqlParameter("@file", SqlDbType.NVarChar, 260) { Value = instance.PrimaryFileTag }
        );
        command.Parameters.Add(
            new SqlParameter("@createDate", SqlDbType.DateTime)
            {
                Value = instance.CreateDateSnapshot.UtcDateTime,
            }
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        int? guard =
            reader.FieldCount > 0 && await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : null;
        bool more;
        do
        {
            more = await reader.NextResultAsync(cancellationToken);
        } while (more);

        return guard;
    }

    /// <summary>An error the server reported for the statement is definite; anything else leaves it in doubt on <paramref name="session"/>.</summary>
    private static DmsCommandOutcome Classify(Exception exception, DmsServerSessionId session, string step) =>
        exception is SqlException && MssqlErrorClassifier.FailureClassOf(exception) is { } failureClass
            ? new DmsCommandOutcome.Failed(
                new DmsCommandFailure(failureClass, MssqlErrorClassifier.Diagnostic(exception, step))
            )
            : new DmsCommandOutcome.Unknown(session);

    private static DmsCommandOutcome Failed(DmsCommandFailureClass failureClass, string step) =>
        new DmsCommandOutcome.Failed(
            new DmsCommandFailure(failureClass, DmsProvisioningDiagnostic.ForOperation(step))
        );

    /// <summary>A definite failure raised once the fence has returned: the target stays fenced until reversed.</summary>
    private static DmsCommandOutcome Fenced(
        DmsTargetDatabaseName target,
        DmsCommandFailureClass failureClass,
        DmsProvisioningDiagnostic diagnostic
    ) =>
        new DmsCommandOutcome.Failed(new DmsCommandFailure(failureClass, diagnostic))
        {
            Residue = [new DmsCleanupResidue(DmsResidueKind.FencedTarget, target.Value)],
        };
}
