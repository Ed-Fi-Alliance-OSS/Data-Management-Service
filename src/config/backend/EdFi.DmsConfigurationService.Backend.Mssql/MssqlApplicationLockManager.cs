// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// SQL Server session-owned application lock per Application aggregate, held on a dedicated
/// connection for the lifetime of the returned handle. The connection is held across the owning
/// workflow's identity-provider calls, so the hold duration is bounded by those calls, not by
/// the acquire timeout. Session termination releases the lock unconditionally, so a crashed
/// instance cannot leak it.
/// </summary>
internal sealed class MssqlApplicationLockManager : IApplicationLockManager
{
    private readonly IOptions<DatabaseOptions> _databaseOptions;
    private readonly IOptions<ApplicationLockOptions> _lockOptions;
    private readonly ILogger<MssqlApplicationLockManager> _logger;
    private readonly Func<SqlConnection, string, Task> _unlockAsync;

    public MssqlApplicationLockManager(
        IOptions<DatabaseOptions> databaseOptions,
        IOptions<ApplicationLockOptions> lockOptions,
        ILogger<MssqlApplicationLockManager> logger
    )
        : this(databaseOptions, lockOptions, logger, UnlockAsync) { }

    // Test seam: lets integration tests force a release failure and observe the session used.
    internal MssqlApplicationLockManager(
        IOptions<DatabaseOptions> databaseOptions,
        IOptions<ApplicationLockOptions> lockOptions,
        ILogger<MssqlApplicationLockManager> logger,
        Func<SqlConnection, string, Task> unlockAsync
    )
    {
        _databaseOptions = databaseOptions;
        _lockOptions = lockOptions;
        _logger = logger;
        _unlockAsync = unlockAsync;
    }

    public async Task<ApplicationLockResult> AcquireAsync(
        long applicationId,
        CancellationToken cancellationToken
    )
    {
        string resource = ComputeLockResource(applicationId);
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(_databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);

            TimeSpan timeout = _lockOptions.Value.AcquireTimeout;
            var elapsed = Stopwatch.StartNew();
            using var command = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                // sp_getapplock itself waits up to @LockTimeout; the command timeout only needs
                // to outlast that wait.
                CommandTimeout = (int)timeout.TotalSeconds + 30,
            };
            command.Parameters.AddWithValue("@Resource", resource);
            command.Parameters.AddWithValue("@LockMode", "Exclusive");
            command.Parameters.AddWithValue("@LockOwner", "Session");
            command.Parameters.AddWithValue("@LockTimeout", (int)timeout.TotalMilliseconds);
            SqlParameter returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
            returnValue.Direction = ParameterDirection.ReturnValue;

            await command.ExecuteNonQueryAsync(cancellationToken);

            int status = (int)returnValue.Value;
            if (status >= 0)
            {
                _logger.LogDebug(
                    "Acquired the application lock for Application {ApplicationId} after waiting {LockWaitMilliseconds} ms",
                    applicationId,
                    elapsed.ElapsedMilliseconds
                );
                var handle = new Handle(this, connection, resource, applicationId);
                connection = null;
                return new ApplicationLockResult.Acquired(handle);
            }

            ApplicationLockResult failure = ClassifyFailedLockStatus(status, cancellationToken);
            if (failure is ApplicationLockResult.FailureTimeout)
            {
                _logger.LogWarning(
                    "Timed out acquiring the application lock for Application {ApplicationId} after {LockWaitMilliseconds} ms",
                    applicationId,
                    elapsed.ElapsedMilliseconds
                );
            }

            return failure;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // SqlClient can surface a cancelled server-side lock wait as SqlException; a
            // requested cancellation always propagates as cancellation.
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
    /// Deterministic session-lock resource name; the namespace prefix keeps future application
    /// locks from colliding with other uses.
    /// </summary>
    internal static string ComputeLockResource(long applicationId) =>
        string.Create(CultureInfo.InvariantCulture, $"dmscs:application:{applicationId}");

    /// <summary>
    /// Maps a non-granted sp_getapplock return status: -1 is a lock wait timeout; -2 is a
    /// cancelled request, which propagates as cancellation when the caller requested it; every
    /// other status is an infrastructure failure.
    /// </summary>
    internal static ApplicationLockResult ClassifyFailedLockStatus(
        int status,
        CancellationToken cancellationToken
    )
    {
        if (status == -1)
        {
            return new ApplicationLockResult.FailureTimeout();
        }

        if (status == -2)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new ApplicationLockResult.FailureUnknown(
            $"sp_getapplock returned {status} for the application lock"
        );
    }

    internal static void ThrowIfReleaseFailed(int status)
    {
        if (status < 0)
        {
            throw new InvalidOperationException(
                $"sp_releaseapplock reported failure {status} releasing the application lock."
            );
        }
    }

    internal static async Task UnlockAsync(SqlConnection connection, string resource)
    {
        using var command = new SqlCommand("sp_releaseapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockOwner", "Session");
        SqlParameter returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync();

        ThrowIfReleaseFailed((int)returnValue.Value);
    }

    private sealed class Handle(
        MssqlApplicationLockManager manager,
        SqlConnection connection,
        string resource,
        long applicationId
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
                await manager._unlockAsync(connection, resource);
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
                    SqlConnection.ClearPool(connection);
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
