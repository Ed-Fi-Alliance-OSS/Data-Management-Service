// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql;

/// <summary>
/// PostgreSQL session advisory lock per Application aggregate, held on a dedicated connection
/// for the lifetime of the returned handle. The connection is held across the owning workflow's
/// identity-provider calls, so the hold duration is bounded by those calls, not by the acquire
/// timeout. Session termination releases the lock unconditionally, so a crashed instance cannot
/// leak it.
/// </summary>
internal sealed class PostgresqlApplicationLockManager : IApplicationLockManager
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IOptions<DatabaseOptions> _databaseOptions;
    private readonly IOptions<ApplicationLockOptions> _lockOptions;
    private readonly ILogger<PostgresqlApplicationLockManager> _logger;
    private readonly Func<NpgsqlConnection, long, Task> _unlockAsync;

    public PostgresqlApplicationLockManager(
        IOptions<DatabaseOptions> databaseOptions,
        IOptions<ApplicationLockOptions> lockOptions,
        ILogger<PostgresqlApplicationLockManager> logger
    )
        : this(databaseOptions, lockOptions, logger, UnlockAsync) { }

    // Test seam: lets integration tests force a release failure and observe the session used.
    internal PostgresqlApplicationLockManager(
        IOptions<DatabaseOptions> databaseOptions,
        IOptions<ApplicationLockOptions> lockOptions,
        ILogger<PostgresqlApplicationLockManager> logger,
        Func<NpgsqlConnection, long, Task> unlockAsync
    )
    {
        _databaseOptions = databaseOptions;
        _lockOptions = lockOptions;
        _logger = logger;
        _unlockAsync = unlockAsync;
    }

    public async Task<ApplicationLockResult> AcquireAsync(
        int applicationId,
        CancellationToken cancellationToken
    )
    {
        long key = ComputeLockKey(applicationId);
        NpgsqlConnection? connection = null;
        try
        {
            connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);

            TimeSpan timeout = _lockOptions.Value.AcquireTimeout;
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key);", connection);
                command.Parameters.AddWithValue("key", key);
                bool acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
                if (acquired)
                {
                    _logger.LogDebug(
                        "Acquired the application lock for Application {ApplicationId} after waiting {LockWaitMilliseconds} ms",
                        applicationId,
                        elapsed.ElapsedMilliseconds
                    );
                    var handle = new Handle(this, connection, key, applicationId);
                    connection = null;
                    return new ApplicationLockResult.Acquired(handle);
                }

                TimeSpan remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Timeout();
                }

                await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken);

                // The deadline is re-checked before every retry so a holder releasing after the
                // deadline cannot hand the lock to a caller whose wait already expired.
                if (elapsed.Elapsed >= timeout)
                {
                    return Timeout();
                }
            }

            ApplicationLockResult.FailureTimeout Timeout()
            {
                _logger.LogWarning(
                    "Timed out acquiring the application lock for Application {ApplicationId} after {LockWaitMilliseconds} ms",
                    applicationId,
                    elapsed.ElapsedMilliseconds
                );
                return new ApplicationLockResult.FailureTimeout();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The application lock acquisition was cancelled.",
                ex,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to acquire the application lock for Application {ApplicationId}",
                applicationId
            );
            return new ApplicationLockResult.FailureUnknown(ex.Message);
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Deterministic advisory-lock key: the first 8 bytes, read big-endian, of the SHA-256 hash
    /// of the canonical resource string. The namespace prefix keeps future advisory-lock uses
    /// from colliding with application locks.
    /// </summary>
    internal static long ComputeLockKey(int applicationId)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"dmscs:application:{applicationId}")
            )
        );
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    internal static async Task UnlockAsync(NpgsqlConnection connection, long key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", connection);
        command.Parameters.AddWithValue("key", key);
        bool released = (bool)(await command.ExecuteScalarAsync())!;
        if (!released)
        {
            throw new InvalidOperationException(
                "pg_advisory_unlock reported that the application lock was not held by this session."
            );
        }
    }

    private sealed class Handle(
        PostgresqlApplicationLockManager manager,
        NpgsqlConnection connection,
        long key,
        int applicationId
    ) : IAsyncDisposable
    {
        private readonly Stopwatch _held = Stopwatch.StartNew();
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            bool evict = false;
            try
            {
                await manager._unlockAsync(connection, key);
                manager._logger.LogDebug(
                    "Released the application lock for Application {ApplicationId} after holding it {LockHoldMilliseconds} ms",
                    applicationId,
                    _held.ElapsedMilliseconds
                );
            }
            catch (Exception ex)
            {
                evict = true;
                manager._logger.LogError(
                    ex,
                    "Failed to release the application lock for Application {ApplicationId} after holding it {LockHoldMilliseconds} ms; evicting the connection so the lock cannot leak into the pool",
                    applicationId,
                    _held.ElapsedMilliseconds
                );
            }

            if (evict)
            {
                try
                {
                    NpgsqlConnection.ClearPool(connection);
                }
                catch (Exception ex)
                {
                    manager._logger.LogError(ex, "Failed to evict the application lock connection pool");
                }
            }

            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                manager._logger.LogError(ex, "Failed to dispose the application lock connection");
            }
        }
    }
}
