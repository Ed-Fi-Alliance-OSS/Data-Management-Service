// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The SQL Server holder session (D-12): a dedicated unpooled connection to <c>master</c>, named
/// <c>dmsprov:&lt;op&gt;</c>, that holds the target's session-owned exclusive application lock for the
/// operation's lifetime. The provider-neutral base supplies the execution gate, the heartbeat schedule, loss
/// declaration, and disposal; this class supplies what only the connection knows: the lock resource, the
/// acquisition under <c>@LockTimeout</c>, the probe, the connection-failure classification, the closed-state
/// read, and the release (<c>sp_releaseapplock</c>, then close). An application lock lives in the database it
/// was taken in, so every holder takes it in <c>master</c>, and the lock, the probe, and the release are called
/// through <c>master.sys</c>: a holder command that changes the session's database context cannot make the
/// probe look in another database and report a held lock as lost. Unlike a PostgreSQL advisory lock, an
/// application lock can be released while its session lives, so the probe asks <c>APPLOCK_MODE</c> whether
/// this session still holds it exclusively, and a probe that answers no declares the holder lost. The physical
/// connection is never replaced or reopened: <see cref="MssqlAdminConnection"/> turns transparent reconnection
/// off.
/// </summary>
internal sealed class MssqlTargetLockSession : DmsTargetLockHolder
{
    /// <summary>The lock resource prefix of D-12; the resource is the prefix plus the lowercased target name.</summary>
    public const string LockResourcePrefix = "dms-provision-target:v1:";

    /// <summary>The <c>Diagnostic.Operation</c> of an acquisition that failed for an infrastructure reason.</summary>
    public const string AcquireStep = "AcquireTargetLock";

    /// <summary>The longest resource <c>sp_getapplock</c> accepts.</summary>
    public const int MaximumResourceLength = 255;

    /// <summary>The database principal the lock is taken for: <c>public</c>, which every user of <c>master</c> is a member of.</summary>
    private const string LockPrincipal = "public";

    private const string ExclusiveMode = "Exclusive";

    private const string SessionOwner = "Session";

    private const string GetAppLockProcedure = "master.sys.sp_getapplock";

    private const string ReleaseAppLockProcedure = "master.sys.sp_releaseapplock";

    private const string ExecuteInMasterProcedure = "master.sys.sp_executesql";

    private const string ProbeStatement =
        "SELECT CASE WHEN APPLOCK_MODE(N'public', @resource, N'Session') = N'Exclusive' THEN 1 ELSE 0 END;";

    private const string ProbeParameters = "@resource nvarchar(255)";

    private const string SessionQuery =
        "SELECT CONVERT(int, @@SPID), s.login_time FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID;";

    /// <summary><c>sp_getapplock</c>: granted at once, or granted after waiting.</summary>
    private const int Granted = 0;

    private const int GrantedAfterWaiting = 1;

    /// <summary><c>sp_getapplock</c>: the request timed out while another session held the resource.</summary>
    private const int LockRequestTimedOut = -1;

    private readonly SqlConnection _connection;
    private readonly int _commandTimeoutSeconds;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeProvider _timeProvider;

    private MssqlTargetLockSession(
        SqlConnection connection,
        string resource,
        DmsServerSessionId session,
        TimeSpan heartbeatInterval,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        ILogger logger
    )
        : base(session, heartbeatInterval, commandTimeout, timeProvider, logger)
    {
        _connection = connection;
        Resource = resource;
        _commandTimeout = commandTimeout;
        _commandTimeoutSeconds = MssqlAdminConnection.DriverTimeoutSeconds(commandTimeout);
        _timeProvider = timeProvider;
    }

    /// <summary>The application lock resource this session holds.</summary>
    public string Resource { get; }

    /// <summary>The private connection, for the commands the engine issues inside <see cref="DmsTargetLockHolder.RunAsync{T}"/> only.</summary>
    public SqlConnection Connection => _connection;

    /// <summary>
    /// The application lock resource of D-12: <see cref="LockResourcePrefix"/> plus the lowercased target name.
    /// <c>sp_getapplock</c> compares resources byte for byte whatever the collation, so the name is folded here
    /// and names that differ only in case contend. A target name is at most 63 characters, well within the
    /// 255-character limit.
    /// </summary>
    public static string LockResourceFor(DmsTargetDatabaseName name) =>
        LockResourcePrefix + name.Value.ToLowerInvariant();

    /// <summary>
    /// The readiness lock probe's resource (7.3 item 7): <see cref="LockResourcePrefix"/> plus <c>__probe__</c>,
    /// which no target resource can equal, because a target name starts with a letter.
    /// </summary>
    public const string ProbeResource = LockResourcePrefix + "__probe__";

    /// <summary>
    /// Opens the holder session and takes the lock, waiting at most <paramref name="acquireTimeout"/> on the
    /// server (<c>@LockTimeout</c>, which bounds this request only). A lock another session still holds when the
    /// budget ends is <see cref="DmsTargetLockResult.Timeout"/>; that holder is never identified or terminated.
    /// Any other failure closes the connection and is <see cref="DmsTargetLockResult.Failure"/>; cancellation
    /// closes it and propagates as <see cref="OperationCanceledException"/>, including when SqlClient reports
    /// the cancelled request as a <see cref="SqlException"/>. On success the heartbeat is started before the
    /// holder is returned.
    /// </summary>
    public static Task<DmsTargetLockResult> AcquireAsync(
        MssqlAdminConnection adminConnection,
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        TimeSpan acquireTimeout,
        TimeSpan heartbeatInterval,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken
    ) =>
        AcquireAsync(
            adminConnection,
            name,
            operation,
            acquireTimeout,
            heartbeatInterval,
            timeProvider,
            logger,
            connectionOpened: null,
            cancellationToken
        );

    /// <summary>
    /// The acquisition with a test seam: <paramref name="connectionOpened"/> receives the holder connection as
    /// soon as it is open, before the lock is attempted, so a test can keep a reference and prove the
    /// connection closed on every path that returns without the lock.
    /// </summary>
    internal static Task<DmsTargetLockResult> AcquireAsync(
        MssqlAdminConnection adminConnection,
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        TimeSpan acquireTimeout,
        TimeSpan heartbeatInterval,
        TimeProvider timeProvider,
        ILogger logger,
        Action<SqlConnection>? connectionOpened,
        CancellationToken cancellationToken
    ) =>
        AcquireResourceAsync(
            adminConnection,
            LockResourceFor(name),
            operation,
            acquireTimeout,
            heartbeatInterval,
            timeProvider,
            logger,
            connectionOpened,
            cancellationToken
        );

    /// <summary>The acquisition for an already built resource.</summary>
    internal static async Task<DmsTargetLockResult> AcquireResourceAsync(
        MssqlAdminConnection adminConnection,
        string resource,
        DmsOperationId operation,
        TimeSpan acquireTimeout,
        TimeSpan heartbeatInterval,
        TimeProvider timeProvider,
        ILogger logger,
        Action<SqlConnection>? connectionOpened,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resource.Length, MaximumResourceLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(acquireTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            acquireTimeout,
            MssqlDmsEngineSettings.MaximumLockTimeout
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        SqlConnection connection;
        try
        {
            connection = await adminConnection.OpenAsync(
                MssqlAdminConnection.MaintenanceDatabaseName,
                operation.HolderApplicationName,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailureOrCancellation(exception, cancellationToken);
        }

        connectionOpened?.Invoke(connection);
        bool held = false;
        try
        {
            DmsServerSessionId session = await ReadSessionAsync(
                connection,
                adminConnection.CommandTimeoutSeconds,
                cancellationToken
            );
            int result = await RequestLockAsync(
                connection,
                resource,
                acquireTimeout,
                adminConnection.CommandTimeout,
                cancellationToken
            );
            if (result == LockRequestTimedOut)
            {
                return new DmsTargetLockResult.Timeout();
            }

            if (result is not (Granted or GrantedAfterWaiting))
            {
                // Cancelled by the server, chosen as a deadlock victim, or refused (-999): not held.
                return new DmsTargetLockResult.Failure(DmsProvisioningDiagnostic.ForOperation(AcquireStep));
            }

            MssqlTargetLockSession holder = new(
                connection,
                resource,
                session,
                heartbeatInterval,
                adminConnection.CommandTimeout,
                timeProvider,
                logger
            );
            held = true;
            holder.StartHeartbeat();
            return new DmsTargetLockResult.Acquired(holder);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailureOrCancellation(exception, cancellationToken);
        }
        finally
        {
            if (!held)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Runs the heartbeat's probe once, now, through the execution gate under <c>CommandTimeout</c>, with the
    /// heartbeat's loss rules: a connection failure, or an answer that the lock is no longer held, declares the
    /// holder lost. The readiness lock probe (7.3 item 7) uses it to prove one heartbeat on a fresh holder
    /// without waiting for the timer.
    /// </summary>
    internal async Task<bool> ProbeOnceAsync(CancellationToken cancellationToken)
    {
        bool held = await RunAsync(ProbeAsync, _commandTimeout, cancellationToken);
        if (!held)
        {
            DeclareLost();
        }

        return held;
    }

    /// <summary>
    /// On the private connection, inside the gate: whether this session still holds the resource exclusively,
    /// asked in <c>master</c>. A request SqlClient reports as failed because the probe's own deadline or loss
    /// cancelled it is surfaced as a cancellation, which the base treats as a slow probe, not loss.
    /// </summary>
    protected override async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        await using SqlCommand command = new(ExecuteInMasterProcedure, _connection);
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@stmt", SqlDbType.NVarChar, -1) { Value = ProbeStatement });
        command.Parameters.Add(
            new SqlParameter("@params", SqlDbType.NVarChar, -1) { Value = ProbeParameters }
        );
        command.Parameters.Add(ResourceParameter("@resource", Resource));
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken) is 1;
        }
        catch (SqlException exception)
            when (cancellationToken.IsCancellationRequested
                && !MssqlErrorClassifier.IsConnectionFailure(exception)
            )
        {
            throw new OperationCanceledException(
                "The holder probe was cancelled.",
                exception,
                cancellationToken
            );
        }
    }

    protected override bool IsConnectionFailure(Exception exception) =>
        MssqlErrorClassifier.IsConnectionFailure(exception);

    /// <summary>
    /// SqlClient closes a connection whose session failed (a fatal severity or a transport error) and never
    /// reports <see cref="ConnectionState.Broken"/>, so any state other than open is the broken state here.
    /// Reading the state never throws.
    /// </summary>
    protected override bool IsConnectionBroken => _connection.State != ConnectionState.Open;

    /// <summary>Releases the lock while the session is still believed alive, then closes the connection either way; the close alone releases a session-owned lock.</summary>
    protected override async ValueTask ReleaseAsync()
    {
        try
        {
            if (!IsLost && !IsConnectionBroken)
            {
                using CancellationTokenSource deadline = new(_commandTimeout, _timeProvider);
                await using SqlCommand command = LockProcedure(
                    ReleaseAppLockProcedure,
                    _connection,
                    Resource
                );
                command.CommandTimeout = _commandTimeoutSeconds;
                SqlParameter returnValue = ReturnValue(command);
                await command.ExecuteNonQueryAsync(deadline.Token);
                if (returnValue.Value is not Granted)
                {
                    throw new InvalidOperationException("sp_releaseapplock did not release the resource.");
                }
            }
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private static DmsTargetLockResult FailureOrCancellation(
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The target lock acquisition was cancelled.",
                exception,
                cancellationToken
            );
        }

        return new DmsTargetLockResult.Failure(MssqlErrorClassifier.Diagnostic(exception, AcquireStep));
    }

    private static async Task<DmsServerSessionId> ReadSessionAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(SessionQuery, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The holder session query returned no row.");
        }

        int spid = reader.GetInt32(0);
        DateTime loginTime = await reader.GetFieldValueAsync<DateTime>(1, cancellationToken);
        return new DmsServerSessionId(
            spid,
            new DateTimeOffset(DateTime.SpecifyKind(loginTime, DateTimeKind.Utc), TimeSpan.Zero)
        );
    }

    /// <summary>
    /// <c>sp_getapplock</c> with <c>@LockTimeout</c> = the budget, which bounds this request only and leaves
    /// the session's <c>LOCK_TIMEOUT</c> untouched, so later holder commands wait under their own deadlines. The
    /// driver's timeout is widened to the budget plus the command timeout: it stays a backstop for an
    /// unresponsive server rather than the first bound to fire.
    /// </summary>
    private static async Task<int> RequestLockAsync(
        SqlConnection connection,
        string resource,
        TimeSpan acquireTimeout,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = LockProcedure(GetAppLockProcedure, connection, resource);
        command.CommandTimeout = MssqlAdminConnection.DriverTimeoutSeconds(acquireTimeout + commandTimeout);
        command.Parameters.Add(
            new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = ExclusiveMode }
        );
        command.Parameters.Add(
            new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = MillisecondsOf(acquireTimeout) }
        );
        SqlParameter returnValue = ReturnValue(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return returnValue.Value is int result
            ? result
            : throw new InvalidOperationException("sp_getapplock returned no status.");
    }

    /// <summary>A call of an application-lock procedure for <paramref name="resource"/>, owned by the session, for <c>public</c>.</summary>
    private static SqlCommand LockProcedure(string procedure, SqlConnection connection, string resource)
    {
        SqlCommand command = new(procedure, connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(ResourceParameter("@Resource", resource));
        command.Parameters.Add(
            new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = SessionOwner }
        );
        command.Parameters.Add(
            new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 128) { Value = LockPrincipal }
        );
        return command;
    }

    private static SqlParameter ResourceParameter(string parameterName, string resource) =>
        new(parameterName, SqlDbType.NVarChar, MaximumResourceLength) { Value = resource };

    private static SqlParameter ReturnValue(SqlCommand command) =>
        command.Parameters.Add(
            new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue }
        );

    private static int MillisecondsOf(TimeSpan timeout) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds)));
}
