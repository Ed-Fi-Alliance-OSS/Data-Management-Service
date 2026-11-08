// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// Session termination on SQL Server (D-22). A session is addressed by its <c>session_id</c> together with its
/// <c>login_time</c>, because SQL Server hands a freed <c>session_id</c> to the next session at once. The
/// termination is one batch: the catalog row is matched, <c>KILL</c> is issued only when the
/// <c>login_time</c> matches, and the session is observed again before the batch returns, so the guard and
/// the kill are separated by one statement on the server rather than a round trip. <c>KILL</c> takes no
/// variable, so the validated id is written into the batch as a number. <c>KILL</c> returns at once while the
/// session rolls its transaction back; the verdict is read from <c>sys.dm_exec_sessions</c>, re-read until
/// the session is gone or the deadline passes. That view lists only the caller's own session to a login
/// without <c>VIEW SERVER STATE</c> (<c>VIEW SERVER PERFORMANCE STATE</c> from SQL Server 2022), so every
/// observation reads that permission in the same statement, and a login that may not see other sessions
/// cannot identify the session: that is a privilege failure, never absence, and nothing is killed.
/// </summary>
internal static class MssqlSessionQueries
{
    /// <summary>The <c>Diagnostic.Operation</c> of a <c>KILL</c> the server refused.</summary>
    public const string TerminateSessionStep = "TerminateSession";

    /// <summary>The <c>Diagnostic.Operation</c> of a session the admin may not see (<c>VIEW SERVER STATE</c>).</summary>
    public const string IdentifySessionStep = "IdentifySession";

    /// <summary>The <c>Diagnostic.Operation</c> of an observation after the kill that the termination budget cut short.</summary>
    public const string VerifySessionStep = "VerifySession";

    /// <summary>The highest <c>session_id</c>: the column is a <c>smallint</c>.</summary>
    public const int MaximumSessionId = short.MaxValue;

    /// <summary>
    /// 1 when the caller may see every session in <c>sys.dm_exec_sessions</c>. The 2022 permission is checked
    /// where it exists (<c>HAS_PERMS_BY_NAME</c> returns null for an unknown permission name), the older one
    /// otherwise; both count implied permissions, so <c>sysadmin</c> and <c>VIEW SERVER STATE</c> qualify.
    /// </summary>
    internal const string SessionsVisible =
        "CONVERT(int, COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER PERFORMANCE STATE'), HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE')))";

    /// <summary>Null when no session has the id, 1 when it has the expected <c>login_time</c>, 0 when another session now has the id.</summary>
    private const string SessionMatched =
        "(SELECT CASE WHEN s.login_time = @loginTime THEN 1 ELSE 0 END FROM sys.dm_exec_sessions s WHERE s.session_id = @sessionId)";

    /// <summary>The session's state with the visibility it was read under, in one statement.</summary>
    public const string SessionObservation = "SELECT " + SessionsVisible + ", " + SessionMatched + ";";

    /// <summary>The longest deadline a termination accepts; the setting's range (4.3) is narrower.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromHours(1);

    /// <summary>How often the catalog is re-read while a killed session is still listed (rolling back).</summary>
    public static readonly TimeSpan VerificationInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Validates a termination request before any connection is opened and yields the <c>session_id</c>: a
    /// positive <c>smallint</c>, with a deadline that is positive and at most <see cref="MaximumDeadline"/>.
    /// </summary>
    public static short ValidateRequest(DmsServerSessionId session, TimeSpan deadline)
    {
        if (session.Id is <= 0 or > MaximumSessionId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(session),
                "A SQL Server session id is a positive smallint session_id."
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deadline, MaximumDeadline);
        return (short)session.Id;
    }

    /// <summary>
    /// The guarded kill for <paramref name="sessionId"/>. The first statement reads the visibility and the
    /// match together; <c>KILL</c> runs only when both hold, inside <c>TRY</c> so its refusal is returned as a
    /// number rather than raised; the last statement observes the session again. One row:
    /// <c>visible</c>, <c>matched</c>, <c>kill_error</c>, then the after-kill <c>visible</c> and <c>matched</c>.
    /// </summary>
    public static string GuardedKill(short sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionId, (short)0);
        string literal = sessionId.ToString(CultureInfo.InvariantCulture);
        return $"""
            SET NOCOUNT ON;
            DECLARE @visible int, @matched int, @killError int;
            SELECT @visible = {SessionsVisible}, @matched = {SessionMatched};
            IF @visible = 1 AND @matched = 1
            BEGIN
                BEGIN TRY
                    KILL {literal};
                END TRY
                BEGIN CATCH
                    SET @killError = ERROR_NUMBER();
                END CATCH;
            END;
            SELECT @visible, @matched, @killError, {SessionsVisible}, {SessionMatched};
            """;
    }

    /// <summary>What one observation says: the visibility it was read under decides first, then the match.</summary>
    public static MssqlSessionObservation ObservationOf(int? visible, int? matched) =>
        (visible, matched) switch
        {
            (not 1, _) => MssqlSessionObservation.IdentityHidden,
            (_, null) => MssqlSessionObservation.Absent,
            (_, 1) => MssqlSessionObservation.Present,
            _ => MssqlSessionObservation.SessionIdReused,
        };

    /// <summary>
    /// The verdict of the guarded kill's row, or null when the kill was issued and the session is still listed,
    /// so the caller keeps observing until the deadline. A session the admin may not see is never killed and
    /// is a privilege failure under <see cref="IdentifySessionStep"/>; no row or another <c>login_time</c> is
    /// absence; <c>6102</c> (no <c>ALTER ANY CONNECTION</c>) is a privilege failure carrying the number;
    /// <c>6106</c> (no such session) means the session ended between the guard and the kill, which the
    /// after-kill observation must confirm; any other refusal (<c>6104</c> own session, <c>6107</c> system
    /// session) is <see cref="DmsSessionTerminationFailure.Refused"/> with the number.
    /// </summary>
    public static DmsSessionTermination? VerdictOf(MssqlGuardedKillRow row) =>
        VerdictOf(row, ObservationOf(row.VisibleAfter, row.MatchedAfter));

    /// <summary>
    /// <see cref="VerdictOf(MssqlGuardedKillRow)"/> with the after-kill observation given separately, as the
    /// protocol passes it once any observation hook has seen it.
    /// </summary>
    public static DmsSessionTermination? VerdictOf(MssqlGuardedKillRow row, MssqlSessionObservation after)
    {
        if (ObservationOf(row.Visible, row.Matched) is MssqlSessionObservation.IdentityHidden)
        {
            return IdentityHidden();
        }

        if (row.Matched is not 1)
        {
            return new DmsSessionTermination.AlreadyAbsent();
        }

        return row.KillError switch
        {
            MssqlErrorClassifier.KillNoPermission => new DmsSessionTermination.NotTerminated(
                DmsSessionTerminationFailure.PrivilegeMissing,
                KillDiagnostic(MssqlErrorClassifier.KillNoPermission)
            ),
            MssqlErrorClassifier.KillNoSuchSession => after switch
            {
                MssqlSessionObservation.Absent or MssqlSessionObservation.SessionIdReused =>
                    new DmsSessionTermination.AlreadyAbsent(),
                MssqlSessionObservation.IdentityHidden => IdentityHidden(),
                _ => new DmsSessionTermination.NotTerminated(
                    DmsSessionTerminationFailure.Refused,
                    KillDiagnostic(MssqlErrorClassifier.KillNoSuchSession)
                ),
            },
            { } refused => new DmsSessionTermination.NotTerminated(
                DmsSessionTerminationFailure.Refused,
                KillDiagnostic(refused)
            ),
            null => AfterKill(after),
        };
    }

    /// <summary>The verdict of an observation after the kill was issued, or null while the session is still listed.</summary>
    public static DmsSessionTermination? AfterKill(MssqlSessionObservation observation) =>
        observation switch
        {
            MssqlSessionObservation.Absent or MssqlSessionObservation.SessionIdReused =>
                new DmsSessionTermination.Terminated(),
            MssqlSessionObservation.IdentityHidden => IdentityHidden(),
            MssqlSessionObservation.Present => null,
            _ => throw new InvalidOperationException(
                "The session observation is not one of the four states."
            ),
        };

    /// <summary>
    /// The D-22 protocol on a fresh maintenance session of <paramref name="adminConnection"/>: the guarded kill,
    /// then observations every <see cref="VerificationInterval"/> while the killed session is still listed,
    /// until it is gone (<see cref="DmsSessionTermination.Terminated"/>) or <paramref name="deadline"/> has
    /// passed (<see cref="DmsSessionTerminationFailure.TimedOut"/>, the session left to finish its rollback).
    /// The deadline is one budget, measured on <paramref name="timeProvider"/> from before the connection opens,
    /// that bounds everything: opening the connection, the batch, each observation (through the token, and
    /// through a driver timeout no longer than the remaining budget), and each wait, which never outlasts the
    /// remaining time; no observation starts once it has passed. A request or wait the budget cuts short is
    /// <see cref="DmsSessionTerminationFailure.TimedOut"/>, never absence: a cut request carries a diagnostic
    /// with its type chain under <see cref="TerminateSessionStep"/> (connection or batch) or
    /// <see cref="VerifySessionStep"/> (observation), because a kill batch cut short may or may not have killed
    /// the session. Caller cancellation is distinct and wins over the budget: it propagates as
    /// <see cref="OperationCanceledException"/>, whatever form the driver's cancellation took. Every other
    /// exception propagates: an exception is not a proven-gone verdict. The batch is <see cref="GuardedKill"/>
    /// unless a test passes another, and a test may pass <paramref name="observationHook"/>, which sees every
    /// observation (0 for the one in the batch, then 1, 2, … for each re-read) and returns the one the protocol
    /// acts on.
    /// </summary>
    public static async Task<DmsSessionTermination> TerminateAsync(
        MssqlAdminConnection adminConnection,
        DmsServerSessionId session,
        TimeSpan deadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Func<short, string>? guardedKill = null,
        Func<int, MssqlSessionObservation, MssqlSessionObservation>? observationHook = null
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentNullException.ThrowIfNull(timeProvider);
        short sessionId = ValidateRequest(session, deadline);
        DateTime loginTime = session.StartedAt.UtcDateTime;
        long started = timeProvider.GetTimestamp();
        using CancellationTokenSource budget = new(deadline, timeProvider);
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            budget.Token
        );
        MssqlGuardedKillRow? killRow = null;
        try
        {
            await using SqlConnection connection = await adminConnection.OpenMaintenanceAsync(bounded.Token);
            killRow = await KillAsync(
                connection,
                (guardedKill ?? GuardedKill)(sessionId),
                sessionId,
                loginTime,
                CommandTimeoutWithin(
                    adminConnection.CommandTimeout,
                    deadline - timeProvider.GetElapsedTime(started)
                ),
                bounded.Token
            );
            MssqlGuardedKillRow row = killRow.Value;
            return VerdictOf(
                    row,
                    Hooked(observationHook, 0, ObservationOf(row.VisibleAfter, row.MatchedAfter))
                )
                ?? await VerifyAsync(
                    new MssqlAddressedSession(connection, sessionId, loginTime),
                    adminConnection.CommandTimeout,
                    started,
                    deadline,
                    timeProvider,
                    observationHook,
                    bounded.Token
                );
        }
        catch (Exception exception)
            when (cancellationToken.IsCancellationRequested
                && exception is SqlException or OperationCanceledException
            )
        {
            throw new OperationCanceledException(
                "The session termination was cancelled.",
                exception,
                cancellationToken
            );
        }
        catch (Exception exception)
            when (budget.IsCancellationRequested && exception is SqlException or OperationCanceledException)
        {
            // A wait the budget ended proves nothing either way; a request it ended may have done its work.
            if (exception is not SqlException)
            {
                return TimedOut();
            }

            string step = killRow is null ? TerminateSessionStep : VerifySessionStep;
            return new DmsSessionTermination.NotTerminated(
                DmsSessionTerminationFailure.TimedOut,
                DmsProvisioningDiagnostic.From(exception, step)
            );
        }
    }

    /// <summary>
    /// The re-reads after a kill that left the session listed: each wait is capped at the remaining time, no
    /// observation starts once the deadline has passed, and each observation's driver timeout is within the
    /// remaining time. Observation 1, 2, … pass through <paramref name="observationHook"/> when a test gives one.
    /// </summary>
    private static async Task<DmsSessionTermination> VerifyAsync(
        MssqlAddressedSession addressed,
        TimeSpan commandTimeout,
        long started,
        TimeSpan deadline,
        TimeProvider timeProvider,
        Func<int, MssqlSessionObservation, MssqlSessionObservation>? observationHook,
        CancellationToken cancellationToken
    )
    {
        int observation = 0;
        while (true)
        {
            TimeSpan remaining = deadline - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return TimedOut();
            }

            await Task.Delay(
                remaining < VerificationInterval ? remaining : VerificationInterval,
                timeProvider,
                cancellationToken
            );
            remaining = deadline - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return TimedOut();
            }

            observation++;
            MssqlSessionObservation observed = await ObserveAsync(
                addressed.Connection,
                addressed.SessionId,
                addressed.LoginTime,
                CommandTimeoutWithin(commandTimeout, remaining),
                cancellationToken
            );
            DmsSessionTermination? verdict = AfterKill(Hooked(observationHook, observation, observed));
            if (verdict is not null)
            {
                return verdict;
            }
        }
    }

    /// <summary>The driver timeout for a request: the configured command timeout, or the remaining budget when that is shorter.</summary>
    public static int CommandTimeoutWithin(TimeSpan commandTimeout, TimeSpan remaining) =>
        MssqlAdminConnection.DriverTimeoutSeconds(remaining < commandTimeout ? remaining : commandTimeout);

    /// <summary>What the catalog says about <paramref name="session"/> now; a session the admin may not see is reported as such, never as absence.</summary>
    public static Task<MssqlSessionObservation> ObserveAsync(
        SqlConnection connection,
        DmsServerSessionId session,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ObserveAsync(
            connection,
            ValidateRequest(session, MaximumDeadline),
            session.StartedAt.UtcDateTime,
            commandTimeoutSeconds,
            cancellationToken
        );
    }

    private static async Task<MssqlGuardedKillRow> KillAsync(
        SqlConnection connection,
        string batch,
        short sessionId,
        DateTime loginTime,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(
            batch,
            connection,
            sessionId,
            loginTime,
            commandTimeoutSeconds
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The guarded kill returned no row.");
        }

        return new MssqlGuardedKillRow(
            await IntOrNullAsync(reader, 0, cancellationToken),
            await IntOrNullAsync(reader, 1, cancellationToken),
            await IntOrNullAsync(reader, 2, cancellationToken),
            await IntOrNullAsync(reader, 3, cancellationToken),
            await IntOrNullAsync(reader, 4, cancellationToken)
        );
    }

    private static async Task<MssqlSessionObservation> ObserveAsync(
        SqlConnection connection,
        short sessionId,
        DateTime loginTime,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(
            SessionObservation,
            connection,
            sessionId,
            loginTime,
            commandTimeoutSeconds
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The session observation returned no row.");
        }

        return ObservationOf(
            await IntOrNullAsync(reader, 0, cancellationToken),
            await IntOrNullAsync(reader, 1, cancellationToken)
        );
    }

    private static SqlCommand Command(
        string sql,
        SqlConnection connection,
        short sessionId,
        DateTime loginTime,
        int commandTimeoutSeconds
    )
    {
        SqlCommand command = new(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@sessionId", SqlDbType.SmallInt) { Value = sessionId });
        command.Parameters.Add(new SqlParameter("@loginTime", SqlDbType.DateTime) { Value = loginTime });
        return command;
    }

    private static async Task<int?> IntOrNullAsync(
        SqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken
    ) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetInt32(ordinal);

    private static DmsSessionTermination.NotTerminated IdentityHidden() =>
        new(
            DmsSessionTerminationFailure.PrivilegeMissing,
            DmsProvisioningDiagnostic.ForOperation(IdentifySessionStep)
        );

    private static DmsSessionTermination.NotTerminated TimedOut() =>
        new(DmsSessionTerminationFailure.TimedOut);

    private static MssqlSessionObservation Hooked(
        Func<int, MssqlSessionObservation, MssqlSessionObservation>? observationHook,
        int observation,
        MssqlSessionObservation observed
    ) => observationHook is null ? observed : observationHook(observation, observed);

    /// <summary>A refusal the batch caught: no exception reached the client, so the chain is empty and only the number is kept.</summary>
    private static DmsProvisioningDiagnostic KillDiagnostic(int errorNumber) =>
        new(string.Empty, errorNumber.ToString(CultureInfo.InvariantCulture), TerminateSessionStep);
}

/// <summary>The guarded kill's row: the guard's visibility and match, the caught <c>KILL</c> error, and the after-kill visibility and match.</summary>
internal readonly record struct MssqlGuardedKillRow(
    int? Visible,
    int? Matched,
    int? KillError,
    int? VisibleAfter,
    int? MatchedAfter
);

/// <summary>The maintenance connection a termination runs on and the session it addresses.</summary>
internal readonly record struct MssqlAddressedSession(
    SqlConnection Connection,
    short SessionId,
    DateTime LoginTime
);

/// <summary>The four things the catalog can say about a session addressed by <c>session_id</c> and <c>login_time</c> (D-22).</summary>
internal enum MssqlSessionObservation
{
    /// <summary>No session has the id.</summary>
    Absent,

    /// <summary>A session has the id with another <c>login_time</c>: the addressed session is gone and the id was reused.</summary>
    SessionIdReused,

    /// <summary>The session is listed with the expected <c>login_time</c>.</summary>
    Present,

    /// <summary>The admin may not see other sessions (<c>VIEW SERVER STATE</c>), so nothing is proven.</summary>
    IdentityHidden,
}
