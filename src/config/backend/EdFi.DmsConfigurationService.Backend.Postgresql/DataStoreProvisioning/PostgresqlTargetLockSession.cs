// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The PostgreSQL holder session (D-12): a dedicated unpooled connection to the maintenance database, named
/// <c>dmsprov:&lt;op&gt;</c>, that holds the target's session-level advisory lock for the operation's
/// lifetime. The provider-neutral base supplies the execution gate, the heartbeat schedule, loss declaration,
/// and disposal; this class supplies what only the connection knows: the lock key, the acquisition under
/// <c>lock_timeout</c>, the <c>SELECT 1</c> probe, the connection-failure classification, the broken-state
/// read, and the release (<c>pg_advisory_unlock</c>, then close). A session-level advisory lock cannot leave a
/// live session, so a probe that completes proves the lock is still held, and the lock is gone exactly when
/// the session is. The physical connection is never replaced or reopened.
/// </summary>
internal sealed class PostgresqlTargetLockSession : DmsTargetLockHolder
{
    /// <summary>The lock resource prefix of D-12; the key is derived from it and the lowercased target name.</summary>
    public const string LockResourcePrefix = "dms-provision-target:v1:";

    /// <summary>The <c>Diagnostic.Operation</c> of an acquisition that failed for an infrastructure reason.</summary>
    public const string AcquireStep = "AcquireTargetLock";

    private const string SessionQuery =
        "SELECT a.pid, a.backend_start FROM pg_catalog.pg_stat_activity a WHERE a.pid = pg_catalog.pg_backend_pid();";

    private const string SetLockTimeoutStatement =
        "SELECT pg_catalog.set_config('lock_timeout', @milliseconds, false);";

    private const string ResetLockTimeoutStatement =
        "SELECT pg_catalog.set_config('lock_timeout', '0', false);";

    private const string LockStatement = "SELECT pg_catalog.pg_advisory_lock(@key);";

    private const string UnlockStatement = "SELECT pg_catalog.pg_advisory_unlock(@key);";

    private const string ProbeStatement = "SELECT 1;";

    private readonly NpgsqlConnection _connection;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeProvider _timeProvider;

    private PostgresqlTargetLockSession(
        NpgsqlConnection connection,
        long key,
        DmsServerSessionId session,
        TimeSpan heartbeatInterval,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        ILogger logger
    )
        : base(session, heartbeatInterval, commandTimeout, timeProvider, logger)
    {
        _connection = connection;
        Key = key;
        _commandTimeout = commandTimeout;
        _timeProvider = timeProvider;
    }

    /// <summary>The 64-bit advisory lock key this session holds.</summary>
    public long Key { get; }

    /// <summary>The private connection, for the commands the engine issues inside <see cref="DmsTargetLockHolder.RunAsync{T}"/> only.</summary>
    public NpgsqlConnection Connection => _connection;

    /// <summary>
    /// The advisory lock key of D-12: the first eight bytes of the SHA-256 of
    /// <see cref="LockResourcePrefix"/> plus the lowercased target name, read big-endian as a signed 64-bit
    /// integer. In <c>pg_locks</c> it appears as <c>classid</c> (the high 32 bits) and <c>objid</c> (the low
    /// 32 bits) with <c>objsubid = 1</c>, which is how the operator procedure (7.6) finds the holder.
    /// </summary>
    public static long LockKeyFor(DmsTargetDatabaseName name)
    {
        byte[] resource = Encoding.UTF8.GetBytes(LockResourcePrefix + name.Value.ToLowerInvariant());
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(resource, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    /// <summary>
    /// Opens the holder session and takes the lock, waiting at most <paramref name="acquireTimeout"/> on the
    /// server (<c>lock_timeout</c>, in force for the lock statement only). A lock another session still holds
    /// when the budget ends is <see cref="DmsTargetLockResult.Timeout"/>; that holder is never identified or
    /// terminated. Any other failure closes the connection and is <see cref="DmsTargetLockResult.Failure"/>;
    /// cancellation closes it and propagates. On success the heartbeat is started before the holder is returned.
    /// </summary>
    public static Task<DmsTargetLockResult> AcquireAsync(
        PostgresqlAdminConnection adminConnection,
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
    internal static async Task<DmsTargetLockResult> AcquireAsync(
        PostgresqlAdminConnection adminConnection,
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        TimeSpan acquireTimeout,
        TimeSpan heartbeatInterval,
        TimeProvider timeProvider,
        ILogger logger,
        Action<NpgsqlConnection>? connectionOpened,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(acquireTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        long key = LockKeyFor(name);
        NpgsqlConnection connection;
        try
        {
            connection = await adminConnection.OpenAsync(
                PostgresqlAdminConnection.MaintenanceDatabaseName,
                operation.HolderApplicationName,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(exception);
        }

        connectionOpened?.Invoke(connection);
        bool held = false;
        try
        {
            DmsServerSessionId session = await ReadSessionAsync(connection, cancellationToken);
            if (
                !await TryLockAsync(
                    connection,
                    key,
                    acquireTimeout,
                    adminConnection.CommandTimeout,
                    cancellationToken
                )
            )
            {
                return new DmsTargetLockResult.Timeout();
            }

            PostgresqlTargetLockSession holder = new(
                connection,
                key,
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
            return Failure(exception);
        }
        finally
        {
            if (!held)
            {
                await connection.DisposeAsync();
            }
        }
    }

    protected override async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ProbeStatement, _connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return true;
    }

    protected override bool IsConnectionFailure(Exception exception) =>
        PostgresqlErrorClassifier.IsConnectionFailure(exception);

    // FullState is a plain read of the connector's state and never throws.
    protected override bool IsConnectionBroken => (_connection.FullState & ConnectionState.Broken) != 0;

    /// <summary>Unlocks while the session is still believed alive, then closes the connection either way; the close alone releases a session-level lock.</summary>
    protected override async ValueTask ReleaseAsync()
    {
        try
        {
            if (!IsLost && !IsConnectionBroken)
            {
                using CancellationTokenSource deadline = new(_commandTimeout, _timeProvider);
                await using NpgsqlCommand command = new(UnlockStatement, _connection);
                command.Parameters.AddWithValue("key", Key);
                await command.ExecuteScalarAsync(deadline.Token);
            }
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private static DmsTargetLockResult Failure(Exception exception) =>
        new DmsTargetLockResult.Failure(PostgresqlErrorClassifier.Diagnostic(exception, AcquireStep));

    private static async Task<DmsServerSessionId> ReadSessionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(SessionQuery, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The holder session query returned no row.");
        }

        long pid = reader.GetInt32(0);
        DateTime backendStart = await reader.GetFieldValueAsync<DateTime>(1, cancellationToken);
        return new DmsServerSessionId(
            pid,
            new DateTimeOffset(DateTime.SpecifyKind(backendStart, DateTimeKind.Utc), TimeSpan.Zero)
        );
    }

    /// <summary>
    /// <c>lock_timeout</c> bounds the wait on the server and is reset once the lock is held, so later holder
    /// commands wait under their own deadlines. The driver's timeout on the lock statement is widened to the
    /// lock budget plus the command timeout: it stays a backstop for an unresponsive server rather than the
    /// first bound to fire.
    /// </summary>
    private static async Task<bool> TryLockAsync(
        NpgsqlConnection connection,
        long key,
        TimeSpan acquireTimeout,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using (NpgsqlCommand setTimeout = new(SetLockTimeoutStatement, connection))
        {
            setTimeout.Parameters.AddWithValue("milliseconds", MillisecondsText(acquireTimeout));
            await setTimeout.ExecuteScalarAsync(cancellationToken);
        }

        try
        {
            await using NpgsqlCommand lockCommand = new(LockStatement, connection)
            {
                CommandTimeout = PostgresqlAdminConnection.DriverTimeoutSeconds(
                    acquireTimeout + commandTimeout
                ),
            };
            lockCommand.Parameters.AddWithValue("key", key);
            await lockCommand.ExecuteScalarAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return false;
        }

        await using NpgsqlCommand resetTimeout = new(ResetLockTimeoutStatement, connection);
        await resetTimeout.ExecuteScalarAsync(cancellationToken);
        return true;
    }

    private static string MillisecondsText(TimeSpan timeout) =>
        Math.Max(1, (long)Math.Ceiling(timeout.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
}
