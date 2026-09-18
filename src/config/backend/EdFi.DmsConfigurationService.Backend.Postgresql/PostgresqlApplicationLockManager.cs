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
/// PostgreSQL session advisory locks per Application aggregate, held on one dedicated connection
/// per acquisition for the lifetime of the returned handle. A lock set is acquired on a single
/// session, in ascending application id order and within one shared
/// <see cref="ApplicationLockOptions.AcquireTimeout"/> contention budget, so a workflow spanning
/// many applications holds one connection and waits for one timeout. The connection is held across the owning workflow's identity-provider calls,
/// so the hold duration is bounded by those calls, not by the acquire timeout; lock sessions
/// therefore come from their own bounded pool (<see cref="ApplicationLockConnectionPool"/>) and
/// never occupy the repositories' pool. Session termination releases the locks unconditionally,
/// so a crashed instance cannot leak them.
/// </summary>
internal sealed class PostgresqlApplicationLockManager : IApplicationLockManager
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _lockConnectionString;
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
        _lockConnectionString = BuildLockConnectionString(databaseOptions.Value.DatabaseConnection);
        _lockOptions = lockOptions;
        _logger = logger;
        _unlockAsync = unlockAsync;
    }

    public Task<ApplicationLockResult> AcquireAsync(int applicationId, CancellationToken cancellationToken) =>
        AcquireOrderedAsync([applicationId], cancellationToken);

    public Task<ApplicationLockResult> AcquireAllAsync(
        IReadOnlyCollection<int> applicationIds,
        CancellationToken cancellationToken
    ) =>
        AcquireOrderedAsync(ApplicationLockConnectionPool.OrderedDistinct(applicationIds), cancellationToken);

    /// <summary>
    /// The lock sessions' connection string: the configured database connection with the pool
    /// renamed and bounded, so the same string, and therefore the same pool, is used for every
    /// acquisition in the process.
    /// </summary>
    internal static string BuildLockConnectionString(string databaseConnection) =>
        new NpgsqlConnectionStringBuilder(databaseConnection)
        {
            ApplicationName = ApplicationLockConnectionPool.ApplicationName,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = ApplicationLockConnectionPool.MaxPoolSize,
        }.ConnectionString;

    /// <summary>
    /// Acquires every key on one session, all of them inside one shared contention budget. Any
    /// outcome other than the whole set being held (a timeout, a failure, a cancellation)
    /// releases the keys already taken and closes the session before the result leaves this
    /// method.
    /// </summary>
    private async Task<ApplicationLockResult> AcquireOrderedAsync(
        int[] applicationIds,
        CancellationToken cancellationToken
    )
    {
        NpgsqlConnection? connection = null;
        List<long> heldKeys = [];
        try
        {
            connection = new NpgsqlConnection(_lockConnectionString);
            await connection.OpenAsync(cancellationToken);

            // One contention budget for the whole attempt. It starts once the session is open,
            // so establishing the connection is not charged to it, and it is never restarted for
            // a later key: without that, a set of N keys could spend N AcquireTimeout windows
            // contending while the keys taken earlier stayed held. Releasing the set is outside
            // the budget too. This bounds contention, not the request.
            TimeSpan budget = _lockOptions.Value.AcquireTimeout;
            var spent = Stopwatch.StartNew();

            foreach (int applicationId in applicationIds)
            {
                long key = ComputeLockKey(applicationId);
                if (
                    !await TryAcquireWithinBudgetAsync(
                        connection,
                        key,
                        applicationId,
                        budget,
                        spent,
                        cancellationToken
                    )
                )
                {
                    return new ApplicationLockResult.FailureTimeout();
                }

                heldKeys.Add(key);
            }

            var handle = new Handle(this, connection, [.. heldKeys], applicationIds);
            connection = null;
            return new ApplicationLockResult.Acquired(handle);
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
                "Failed to acquire the application lock for Applications {ApplicationIds}",
                Describe(applicationIds)
            );
            return new ApplicationLockResult.FailureUnknown(ex.Message);
        }
        finally
        {
            if (connection is not null)
            {
                // The set was not acquired in full: whatever this session already holds is
                // released through the same path a handle uses, evicting the session if a
                // release fails, so a partial acquisition can never ride a pooled connection.
                await new Handle(this, connection, [.. heldKeys], applicationIds).DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Takes one key within whatever is left of the attempt's shared contention budget.
    /// <paramref name="spent"/> is what the attempt has already consumed, including on the keys
    /// taken before this one, so a later key never opens a fresh window; polling delays are
    /// clipped to the remaining budget for the same reason.
    /// </summary>
    private async Task<bool> TryAcquireWithinBudgetAsync(
        NpgsqlConnection connection,
        long key,
        int applicationId,
        TimeSpan budget,
        Stopwatch spent,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            // Checked before the first attempt as well as before every retry: a budget already
            // exhausted by an earlier key times out here rather than waiting again, and a holder
            // releasing after the deadline cannot hand the lock to a caller whose wait expired.
            if (spent.Elapsed >= budget)
            {
                return Timeout();
            }

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key);", connection);
            command.Parameters.AddWithValue("key", key);
            bool acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (acquired)
            {
                _logger.LogDebug(
                    "Acquired the application lock for Application {ApplicationId} {LockWaitMilliseconds} ms into the acquisition budget",
                    applicationId,
                    spent.ElapsedMilliseconds
                );
                return true;
            }

            TimeSpan remaining = budget - spent.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return Timeout();
            }

            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken);
        }

        bool Timeout()
        {
            _logger.LogWarning(
                "Timed out acquiring the application lock for Application {ApplicationId} after spending {LockWaitMilliseconds} ms of the acquisition budget",
                applicationId,
                spent.ElapsedMilliseconds
            );
            return false;
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

    private static string Describe(int[] applicationIds) =>
        string.Join(",", applicationIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Owns one lock session and the keys it holds. Disposal releases every key, attempting each
    /// even after one fails, and evicts the session from the pool when any release failed so a
    /// still-held lock cannot be handed to the next borrower of the connection.
    /// </summary>
    private sealed class Handle(
        PostgresqlApplicationLockManager manager,
        NpgsqlConnection connection,
        long[] keys,
        int[] applicationIds
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
            foreach (long key in keys)
            {
                try
                {
                    await manager._unlockAsync(connection, key);
                }
                catch (Exception ex)
                {
                    evict = true;
                    manager._logger.LogError(
                        ex,
                        "Failed to release an application lock for Applications {ApplicationIds} after holding it {LockHoldMilliseconds} ms; evicting the connection so the lock cannot leak into the pool",
                        Describe(applicationIds),
                        _held.ElapsedMilliseconds
                    );
                }
            }

            if (!evict && keys.Length > 0)
            {
                manager._logger.LogDebug(
                    "Released the application locks for Applications {ApplicationIds} after holding them {LockHoldMilliseconds} ms",
                    Describe(applicationIds),
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
