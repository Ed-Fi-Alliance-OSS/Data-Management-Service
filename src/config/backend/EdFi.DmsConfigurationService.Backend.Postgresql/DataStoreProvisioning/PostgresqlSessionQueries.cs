// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// Session termination and lookup on PostgreSQL (D-22). A session is addressed by its <c>pid</c> together
/// with its <c>backend_start</c>, so a pid the operating system has reused since the id was captured never
/// matches; a <c>psql</c> replay session is found by the <c>application_name</c> the runner set and the
/// database it replayed into (D-7b). The termination is one guarded statement: the catalog row is matched
/// and <c>pg_terminate_backend(pid, timeout)</c> is applied to it in the same snapshot, so no signal is sent
/// when the session is already gone or the pid now belongs to another backend. The server itself waits up
/// to the deadline for the process to exit; the verdict is then read from the catalog, never inferred from
/// the function's result alone. Every statement here must run in its own transaction: inside an explicit
/// transaction <c>pg_stat_activity</c> is a snapshot taken at first use and would never observe the exit.
/// The start time of another role's session is visible only to a superuser or a member of
/// <c>pg_read_all_stats</c>; an admin that cannot read it cannot identify the session, which is reported as
/// a privilege failure, never as absence.
/// </summary>
internal static class PostgresqlSessionQueries
{
    /// <summary>The <c>Diagnostic.Operation</c> of a termination the server refused.</summary>
    public const string TerminateSessionStep = "TerminateSession";

    /// <summary>The <c>Diagnostic.Operation</c> of a session whose start time the admin may not read (<c>pg_read_all_stats</c>).</summary>
    public const string IdentifySessionStep = "IdentifySession";

    /// <summary>
    /// Matches the session by pid and start time and terminates it in the same statement, waiting on the
    /// server up to <c>@timeout</c> milliseconds. No row: the pid is not listed. <c>matched</c> null: the pid
    /// is listed but its start time is hidden from this admin. <c>matched</c> false: the pid now belongs to
    /// another backend; nothing is signalled. <c>matched</c> true: <c>process_ended</c> is <c>true</c> when the
    /// process ended within the wait and <c>false</c> when it did not or the signal could not be delivered.
    /// </summary>
    public const string GuardedTerminate =
        "SELECT a.backend_start = @start AS matched, CASE WHEN a.backend_start = @start THEN pg_catalog.pg_terminate_backend(a.pid, @timeout) END AS process_ended FROM pg_catalog.pg_stat_activity a WHERE a.pid = @pid;";

    /// <summary>
    /// The session's state after a signal, with the same three-way reading of the start time as the guard:
    /// no row when the pid is not listed; <c>matched</c> null when the pid is listed but its start time is
    /// hidden from this admin, which can happen between statements (a revoked <c>pg_read_all_stats</c>)
    /// and is never read as absence; false when the pid now belongs to another backend; true when the
    /// session is still there.
    /// </summary>
    public const string SessionObservation =
        "SELECT a.backend_start = @start AS matched FROM pg_catalog.pg_stat_activity a WHERE a.pid = @pid;";

    public const string SessionsByApplicationName =
        "SELECT a.pid, a.backend_start FROM pg_catalog.pg_stat_activity a WHERE a.datname = @database AND a.application_name = @application ORDER BY a.backend_start, a.pid;";

    /// <summary>The longest deadline a termination accepts; the setting's range (4.3) is narrower.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromHours(1);

    /// <summary>How often the catalog is re-read while a session the server reported ended is still listed.</summary>
    public static readonly TimeSpan VerificationInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Validates a termination request before any connection is opened and yields the backend pid: the id
    /// must be a positive 32-bit pid and the deadline positive and at most <see cref="MaximumDeadline"/>.
    /// </summary>
    public static int ValidateRequest(DmsServerSessionId session, TimeSpan deadline)
    {
        if (session.Id is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(session),
                "A PostgreSQL session id is a positive 32-bit backend pid."
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deadline, MaximumDeadline);
        return (int)session.Id;
    }

    /// <summary>The server-side wait in whole milliseconds, rounded up and never below one.</summary>
    public static long TimeoutMillisecondsOf(TimeSpan deadline) =>
        Math.Max(1, (long)Math.Ceiling(deadline.TotalMilliseconds));

    /// <summary>
    /// The D-22 protocol on <paramref name="connection"/>, which may be the holder session or a fresh
    /// maintenance session. SQLSTATE <c>42501</c> is <see cref="DmsSessionTerminationFailure.PrivilegeMissing"/>
    /// whether the session belongs to a superuser the admin is not or the admin lacks
    /// <c>pg_signal_backend</c>: the server raises the same state for both and only the state is read. A
    /// listed session whose start time the admin may not read is the same failure under
    /// <see cref="IdentifySessionStep"/>, with no exception behind it. A session still listed after the
    /// server's wait ended is <see cref="DmsSessionTerminationFailure.TimedOut"/>.
    /// Every other exception, including cancellation while the server waits, propagates: an exception is not
    /// a proven-gone verdict.
    /// </summary>
    public static async Task<DmsSessionTermination> TerminateAsync(
        NpgsqlConnection connection,
        DmsServerSessionId session,
        TimeSpan deadline,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(timeProvider);
        int pid = ValidateRequest(session, deadline);
        DateTime startedAt = session.StartedAt.UtcDateTime;
        long started = timeProvider.GetTimestamp();

        bool? matched;
        bool? processEnded;
        try
        {
            await using NpgsqlCommand terminate = new(GuardedTerminate, connection);
            terminate.CommandTimeout = PostgresqlAdminConnection.DriverTimeoutSeconds(
                deadline + commandTimeout
            );
            terminate.Parameters.AddWithValue("pid", pid);
            terminate.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, startedAt);
            terminate.Parameters.AddWithValue("timeout", TimeoutMillisecondsOf(deadline));
            await using NpgsqlDataReader reader = await terminate.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new DmsSessionTermination.AlreadyAbsent();
            }

            matched = await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetBoolean(0);
            processEnded = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetBoolean(1);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return new DmsSessionTermination.NotTerminated(
                DmsSessionTerminationFailure.PrivilegeMissing,
                PostgresqlErrorClassifier.Diagnostic(exception, TerminateSessionStep)
            );
        }

        if (matched is null)
        {
            return new DmsSessionTermination.NotTerminated(
                DmsSessionTerminationFailure.PrivilegeMissing,
                DmsProvisioningDiagnostic.ForOperation(IdentifySessionStep)
            );
        }

        if (processEnded is null)
        {
            return new DmsSessionTermination.AlreadyAbsent();
        }

        return await VerifyAsync(
            connection,
            pid,
            startedAt,
            processEnded.Value,
            started,
            deadline,
            timeProvider,
            cancellationToken
        );
    }

    /// <summary>
    /// The catalog is the proof. A process the server saw end has left <c>pg_stat_activity</c>, which one
    /// read confirms; one still there after the server's wait has spent the deadline. A reported end that is
    /// still listed is re-read while the deadline remains. An identity that cannot be read any more is not
    /// proof of anything.
    /// </summary>
    private static async Task<DmsSessionTermination> VerifyAsync(
        NpgsqlConnection connection,
        int pid,
        DateTime startedAt,
        bool processEnded,
        long started,
        TimeSpan deadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            switch (await ObserveAsync(connection, pid, startedAt, cancellationToken))
            {
                case PostgresqlSessionObservation.Absent:
                case PostgresqlSessionObservation.PidReused:
                    return new DmsSessionTermination.Terminated();
                case PostgresqlSessionObservation.IdentityHidden:
                    return new DmsSessionTermination.NotTerminated(
                        DmsSessionTerminationFailure.PrivilegeMissing,
                        DmsProvisioningDiagnostic.ForOperation(IdentifySessionStep)
                    );
                case PostgresqlSessionObservation.Present:
                    if (!processEnded || timeProvider.GetElapsedTime(started) >= deadline)
                    {
                        return new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut);
                    }

                    await Task.Delay(VerificationInterval, timeProvider, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        "The session observation is not one of the four states."
                    );
            }
        }
    }

    /// <summary>What the catalog says about the session right now; a hidden start time is reported as such, never as absence.</summary>
    public static Task<PostgresqlSessionObservation> ObserveAsync(
        NpgsqlConnection connection,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ObserveAsync(
            connection,
            ValidateRequest(session, MaximumDeadline),
            session.StartedAt.UtcDateTime,
            cancellationToken
        );
    }

    /// <summary>
    /// The sessions on <paramref name="database"/> carrying <paramref name="applicationName"/>, oldest first:
    /// how a <c>psql</c> replay session (<c>PGAPPNAME = dms-provision:&lt;op&gt;</c>) is addressed (D-7b).
    /// </summary>
    public static async Task<IReadOnlyList<DmsServerSessionId>> FindByApplicationNameAsync(
        NpgsqlConnection connection,
        DmsDatabaseName database,
        string applicationName,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        List<DmsServerSessionId> sessions = [];
        await using NpgsqlCommand command = new(SessionsByApplicationName, connection);
        command.Parameters.AddWithValue("database", database.Value);
        command.Parameters.AddWithValue("application", applicationName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long pid = reader.GetInt32(0);
            DateTime backendStart = await reader.GetFieldValueAsync<DateTime>(1, cancellationToken);
            sessions.Add(
                new DmsServerSessionId(
                    pid,
                    new DateTimeOffset(DateTime.SpecifyKind(backendStart, DateTimeKind.Utc), TimeSpan.Zero)
                )
            );
        }

        return sessions;
    }

    private static async Task<PostgresqlSessionObservation> ObserveAsync(
        NpgsqlConnection connection,
        int pid,
        DateTime startedAt,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(SessionObservation, connection);
        command.Parameters.AddWithValue("pid", pid);
        command.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, startedAt);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return PostgresqlSessionObservation.Absent;
        }

        if (await reader.IsDBNullAsync(0, cancellationToken))
        {
            return PostgresqlSessionObservation.IdentityHidden;
        }

        return reader.GetBoolean(0)
            ? PostgresqlSessionObservation.Present
            : PostgresqlSessionObservation.PidReused;
    }
}

/// <summary>The four things the catalog can say about a session addressed by pid and start time (D-22).</summary>
internal enum PostgresqlSessionObservation
{
    /// <summary>No backend has the pid.</summary>
    Absent,

    /// <summary>A backend has the pid, with a readable start time that differs: the session is gone and the pid was reused.</summary>
    PidReused,

    /// <summary>The session is listed with the expected start time.</summary>
    Present,

    /// <summary>A backend has the pid but its start time is hidden from this admin (<c>pg_read_all_stats</c>), so nothing is proven.</summary>
    IdentityHidden,
}
