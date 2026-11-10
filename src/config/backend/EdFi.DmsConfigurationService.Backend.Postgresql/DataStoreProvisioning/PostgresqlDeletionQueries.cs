// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The owned delete of 6.6 on the holder's connection: the fence, its reversal, and the guarded drop, each
/// classified by 6.9a the way the publication commands are. Every command first re-reads the instance under
/// the target name (D-13) and touches nothing when it differs; a failure of that read is a definite failure,
/// because nothing was sent. Once <c>ALLOW_CONNECTIONS false</c> has returned the target is fenced, and every
/// definite failure after it carries <see cref="DmsResidueKind.FencedTarget"/> residue, so the caller knows
/// that the fence must be reversed. Sessions are client backends on the target's <c>oid</c>; each is
/// terminated by pid and start time with the D-22 guarded statement. The statements that cannot take
/// parameters (<c>ALTER DATABASE</c>, <c>DROP DATABASE</c>) are built only from validated names (6.8); every
/// catalog read is parameterized.
/// </summary>
internal static class PostgresqlDeletionQueries
{
    public const string FenceStep = "Fence";

    /// <summary>The instance read before the fence failed; nothing was sent.</summary>
    public const string FenceVerifyStep = "Fence.Verify";

    /// <summary>The target name no longer holds the instance; nothing was fenced.</summary>
    public const string FenceInstanceStep = "Fence.InstanceChanged";

    /// <summary>The target is fenced, but exclusivity was not established within the termination bound.</summary>
    public const string FenceExclusivityStep = "Fence.Exclusivity";

    public const string UnfenceStep = "Unfence";

    /// <summary>The instance read before the unfence failed; nothing was sent.</summary>
    public const string UnfenceVerifyStep = "Unfence.Verify";

    /// <summary>The target name no longer holds the instance; nothing was unfenced.</summary>
    public const string UnfenceInstanceStep = "Unfence.InstanceChanged";

    public const string DropTargetStep = "DropTarget";

    /// <summary>The reads before the drop failed; the drop was never sent.</summary>
    public const string DropTargetVerifyStep = "DropTarget.Verify";

    /// <summary>The target name holds another instance; nothing was dropped.</summary>
    public const string DropTargetInstanceStep = "DropTarget.InstanceChanged";

    /// <summary>The target accepts connections again; an owned delete drops only a fenced target.</summary>
    public const string DropTargetNotFencedStep = "DropTarget.NotFenced";

    /// <summary>A client session is connected to the fenced target; nothing was dropped.</summary>
    public const string DropTargetSessionsStep = "DropTarget.SessionsPresent";

    /// <summary>The instance under a name together with whether it accepts connections.</summary>
    public const string TargetByName =
        "SELECT d.oid, pg_catalog.shobj_description(d.oid, 'pg_database'), d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name;";

    /// <summary>
    /// The client sessions on the instance other than this connection and the kept session (matched by pid and
    /// start time). A start time hidden from this admin (<c>pg_read_all_stats</c>) is returned as null.
    /// </summary>
    public const string OtherSessions =
        "SELECT a.pid, a.backend_start FROM pg_catalog.pg_stat_activity a WHERE a.datid = @oid AND a.backend_type = 'client backend' AND a.pid <> pg_catalog.pg_backend_pid() AND NOT (a.pid = @keepPid AND a.backend_start IS NOT DISTINCT FROM @keepStart) ORDER BY a.pid;";

    public const string SessionCount =
        "SELECT COUNT(*)::int FROM pg_catalog.pg_stat_activity a WHERE a.datid = @oid AND a.backend_type = 'client backend' AND a.pid <> pg_catalog.pg_backend_pid();";

    /// <summary>
    /// Whether this admin may signal another role's backends: a superuser, or a member of
    /// <c>pg_signal_backend</c>. A <c>42501</c> from the guarded terminate then means the session belongs to a
    /// superuser, which no such admin may end: exclusivity is not established, rather than a privilege missing.
    /// </summary>
    public const string AdminMaySignal =
        "SELECT r.rolsuper OR pg_catalog.pg_has_role(current_user, 'pg_signal_backend', 'USAGE') FROM pg_catalog.pg_roles r WHERE r.rolname = current_user;";

    public static string FenceStatement(DmsTargetDatabaseName target) =>
        $"ALTER DATABASE {PostgresqlIdentifiers.Quote(target.Value)} WITH ALLOW_CONNECTIONS false;";

    public static string UnfenceStatement(DmsTargetDatabaseName target) =>
        $"ALTER DATABASE {PostgresqlIdentifiers.Quote(target.Value)} WITH ALLOW_CONNECTIONS true;";

    /// <summary>
    /// 6.6 step 3: the name must still hold <paramref name="instance"/>; then <c>ALLOW_CONNECTIONS false</c>, and
    /// every other client session on the instance, except <paramref name="keep"/>, is terminated and proven
    /// gone, re-enumerating until none remains or <paramref name="terminationTimeout"/> has passed (D-22). A
    /// session whose start time is hidden, or whose termination the server refuses while this admin lacks
    /// <c>pg_signal_backend</c>, is <see cref="DmsCommandFailureClass.PrivilegeMissing"/>; a session that cannot
    /// be ended (a superuser's under a non-superuser admin, or one still listed at the bound) is
    /// <see cref="DmsCommandFailureClass.ObjectInUse"/>. Both carry the fenced residue, as does any exception
    /// raised once the fence has returned, including a cancellation: the fence stands and must be reversed.
    /// </summary>
    public static async Task<DmsCommandOutcome> FenceAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        DmsServerSessionId session,
        TimeSpan terminationTimeout,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(timeProvider);
        (DmsCommandOutcome? refused, DmsInstanceIdentity.Postgresql? current, _) = await VerifyAsync(
            connection,
            target,
            instance,
            FenceVerifyStep,
            FenceInstanceStep,
            cancellationToken
        );
        if (refused is not null)
        {
            return refused;
        }

        try
        {
            await ExecuteAsync(connection, FenceStatement(target), cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return PostgresqlStagingQueries.Classify(exception, session, FenceStep);
        }

        try
        {
            return await EstablishExclusivityAsync(
                connection,
                target,
                current!.Oid,
                keep,
                terminationTimeout,
                commandTimeout,
                timeProvider,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Fenced(
                target,
                DmsCommandFailureClass.Rejected,
                PostgresqlErrorClassifier.Diagnostic(exception, FenceExclusivityStep)
            );
        }
    }

    /// <summary>
    /// Reverses <see cref="FenceAsync"/>: the name must still hold <paramref name="instance"/> (otherwise a
    /// definite <see cref="DmsCommandFailureClass.DoesNotExist"/> that touches nothing), then
    /// <c>ALLOW_CONNECTIONS true</c>.
    /// </summary>
    public static async Task<DmsCommandOutcome> UnfenceAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        (DmsCommandOutcome? refused, _, _) = await VerifyAsync(
            connection,
            target,
            instance,
            UnfenceVerifyStep,
            UnfenceInstanceStep,
            cancellationToken
        );
        if (refused is not null)
        {
            return refused;
        }

        try
        {
            await ExecuteAsync(connection, UnfenceStatement(target), cancellationToken);
            return new DmsCommandOutcome.Succeeded();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return PostgresqlStagingQueries.Classify(exception, session, UnfenceStep);
        }
    }

    /// <summary>
    /// 6.6 step 5: the name must hold <paramref name="instance"/>, still fenced, with no client session on it;
    /// then <c>DROP DATABASE … WITH (FORCE)</c>. A name found empty, before or by the drop, is the definite
    /// <see cref="DmsCommandFailureClass.DoesNotExist"/> under <see cref="DmsEngineOperations.DropTargetAbsent"/>;
    /// another instance under the name is <see cref="DmsCommandFailureClass.DoesNotExist"/> under
    /// <see cref="DropTargetInstanceStep"/>; a target that accepts connections again, or still has a session, is
    /// <see cref="DmsCommandFailureClass.ObjectInUse"/>. None of these touches the database.
    /// </summary>
    public static async Task<DmsCommandOutcome> DropTargetAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        (DmsCommandOutcome? refused, DmsInstanceIdentity.Postgresql? current, bool allowsConnections) =
            await VerifyAsync(
                connection,
                target,
                instance,
                DropTargetVerifyStep,
                DropTargetInstanceStep,
                cancellationToken
            );
        if (refused is not null)
        {
            return
                current is null
                && refused is DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist }
                ? Failed(DmsCommandFailureClass.DoesNotExist, DmsEngineOperations.DropTargetAbsent)
                : refused;
        }

        if (allowsConnections)
        {
            return Failed(DmsCommandFailureClass.ObjectInUse, DropTargetNotFencedStep);
        }

        int sessions;
        try
        {
            sessions = await SessionCountAsync(connection, current!.Oid, cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    PostgresqlErrorClassifier.Diagnostic(exception, DropTargetVerifyStep)
                )
            );
        }
        catch (OperationCanceledException canceled)
        {
            throw new DmsCommandNotDispatchedException(canceled);
        }

        if (sessions > 0)
        {
            return Failed(DmsCommandFailureClass.ObjectInUse, DropTargetSessionsStep);
        }

        try
        {
            await ExecuteAsync(
                connection,
                PostgresqlStagingQueries.DropDatabaseStatement(target),
                cancellationToken
            );
            return new DmsCommandOutcome.Succeeded();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return Failed(DmsCommandFailureClass.DoesNotExist, DmsEngineOperations.DropTargetAbsent);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return PostgresqlStagingQueries.Classify(exception, session, DropTargetStep);
        }
    }

    /// <summary>
    /// D-22 under the fence: terminates every other client session with proof until none is listed; the bound
    /// is the whole <paramref name="terminationTimeout"/>, shared by every termination it issues.
    /// </summary>
    private static async Task<DmsCommandOutcome> EstablishExclusivityAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName target,
        uint oid,
        DmsServerSessionId? keep,
        TimeSpan terminationTimeout,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        long started = timeProvider.GetTimestamp();
        while (true)
        {
            IReadOnlyList<(int Pid, DateTimeOffset? StartedAt)> others = await OtherSessionsAsync(
                connection,
                oid,
                keep,
                cancellationToken
            );
            if (others.Count == 0)
            {
                return new DmsCommandOutcome.Succeeded();
            }

            foreach ((int pid, DateTimeOffset? startedAt) in others)
            {
                if (startedAt is null)
                {
                    // Another role's session whose start time this admin may not read cannot be addressed safely.
                    return Fenced(
                        target,
                        DmsCommandFailureClass.PrivilegeMissing,
                        DmsProvisioningDiagnostic.ForOperation(PostgresqlSessionQueries.IdentifySessionStep)
                    );
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

                DmsSessionTermination verdict = await PostgresqlSessionQueries.TerminateAsync(
                    connection,
                    new DmsServerSessionId(pid, startedAt.Value),
                    remaining,
                    commandTimeout,
                    timeProvider,
                    cancellationToken
                );
                if (verdict is DmsSessionTermination.NotTerminated refused)
                {
                    return await RefusedAsync(connection, target, refused, cancellationToken);
                }
            }

            if (timeProvider.GetElapsedTime(started) >= terminationTimeout)
            {
                return Fenced(
                    target,
                    DmsCommandFailureClass.ObjectInUse,
                    DmsProvisioningDiagnostic.ForOperation(FenceExclusivityStep)
                );
            }
        }
    }

    /// <summary>
    /// A termination that did not end the session. A refused signal (<c>42501</c>) is a missing privilege only
    /// when this admin may not signal other roles at all; an admin that may has met a superuser's session, which
    /// is not established exclusivity. A hidden start time and a timeout keep their own verdicts.
    /// </summary>
    private static async Task<DmsCommandOutcome> RefusedAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName target,
        DmsSessionTermination.NotTerminated refused,
        CancellationToken cancellationToken
    )
    {
        DmsProvisioningDiagnostic diagnostic =
            refused.Diagnostic ?? DmsProvisioningDiagnostic.ForOperation(FenceExclusivityStep);
        if (
            refused.Reason == DmsSessionTerminationFailure.PrivilegeMissing
            && diagnostic.Operation == PostgresqlSessionQueries.TerminateSessionStep
            && await AdminMaySignalAsync(connection, cancellationToken)
        )
        {
            return Fenced(
                target,
                DmsCommandFailureClass.ObjectInUse,
                new DmsProvisioningDiagnostic(
                    diagnostic.ExceptionTypeChain,
                    diagnostic.ProviderErrorCode,
                    FenceExclusivityStep
                )
            );
        }

        return refused.Reason == DmsSessionTerminationFailure.PrivilegeMissing
            ? Fenced(target, DmsCommandFailureClass.PrivilegeMissing, diagnostic)
            : Fenced(
                target,
                DmsCommandFailureClass.ObjectInUse,
                DmsProvisioningDiagnostic.ForOperation(FenceExclusivityStep)
            );
    }

    /// <summary>
    /// D-13 before a command: null when <paramref name="name"/> still holds <paramref name="expected"/>, with the
    /// current instance and whether it accepts connections; otherwise the definite failure to return. A read that
    /// fails proves only that nothing was sent; a cancellation before anything was sent propagates as
    /// <see cref="DmsCommandNotDispatchedException"/>, because the command that follows was never handed to the driver.
    /// </summary>
    private static async Task<(
        DmsCommandOutcome? Refused,
        DmsInstanceIdentity.Postgresql? Current,
        bool AllowsConnections
    )> VerifyAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName name,
        DmsInstanceIdentity expected,
        string verifyStep,
        string instanceStep,
        CancellationToken cancellationToken
    )
    {
        DmsInstanceIdentity.Postgresql? current;
        bool allowsConnections;
        try
        {
            (current, allowsConnections) = await ReadTargetAsync(connection, name, cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return (
                new DmsCommandOutcome.Failed(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.Rejected,
                        PostgresqlErrorClassifier.Diagnostic(exception, verifyStep)
                    )
                ),
                null,
                false
            );
        }
        catch (OperationCanceledException canceled)
        {
            throw new DmsCommandNotDispatchedException(canceled);
        }

        return current is not null && current.IsSameStableInstance(expected)
            ? (null, current, allowsConnections)
            : (Failed(DmsCommandFailureClass.DoesNotExist, instanceStep), current, allowsConnections);
    }

    private static async Task<(
        DmsInstanceIdentity.Postgresql? Instance,
        bool AllowsConnections
    )> ReadTargetAsync(NpgsqlConnection connection, DmsDatabaseName name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(TargetByName, connection);
        command.Parameters.AddWithValue("name", name.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, false);
        }

        uint oid = await reader.GetFieldValueAsync<uint>(0, cancellationToken);
        string claim = await reader.IsDBNullAsync(1, cancellationToken) ? string.Empty : reader.GetString(1);
        return (new DmsInstanceIdentity.Postgresql(oid, claim), reader.GetBoolean(2));
    }

    private static async Task<IReadOnlyList<(int Pid, DateTimeOffset? StartedAt)>> OtherSessionsAsync(
        NpgsqlConnection connection,
        uint oid,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    )
    {
        List<(int Pid, DateTimeOffset? StartedAt)> sessions = [];
        await using NpgsqlCommand command = new(OtherSessions, connection);
        command.Parameters.Add(new NpgsqlParameter<uint>("oid", NpgsqlDbType.Oid) { TypedValue = oid });
        command.Parameters.AddWithValue("keepPid", keep is { } kept ? (int)kept.Id : 0);
        command.Parameters.Add(
            new NpgsqlParameter("keepStart", NpgsqlDbType.TimestampTz)
            {
                Value = keep is { } keptStart ? keptStart.StartedAt.UtcDateTime : DBNull.Value,
            }
        );
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? startedAt = await reader.IsDBNullAsync(1, cancellationToken)
                ? null
                : new DateTimeOffset(
                    DateTime.SpecifyKind(
                        await reader.GetFieldValueAsync<DateTime>(1, cancellationToken),
                        DateTimeKind.Utc
                    ),
                    TimeSpan.Zero
                );
            sessions.Add((reader.GetInt32(0), startedAt));
        }

        return sessions;
    }

    private static async Task<int> SessionCountAsync(
        NpgsqlConnection connection,
        uint oid,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(SessionCount, connection);
        command.Parameters.Add(new NpgsqlParameter<uint>("oid", NpgsqlDbType.Oid) { TypedValue = oid });
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<bool> AdminMaySignalAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(AdminMaySignal, connection);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

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

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
