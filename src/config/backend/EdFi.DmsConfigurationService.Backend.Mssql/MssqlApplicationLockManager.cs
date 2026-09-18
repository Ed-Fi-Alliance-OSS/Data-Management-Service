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
/// SQL Server session-owned application locks per Application aggregate, held on one dedicated
/// connection per acquisition for the lifetime of the returned handle. A lock set is acquired on
/// a single session, in ascending application id order, so a workflow spanning many
/// applications holds one connection. The connection is held across the owning workflow's
/// identity-provider calls, so the hold duration is bounded by those calls, not by the acquire
/// timeout; lock sessions therefore come from their own bounded pool
/// (<see cref="ApplicationLockConnectionPool"/>) and never occupy the repositories' pool.
/// Session termination releases the locks unconditionally, so a crashed instance cannot leak
/// them.
/// </summary>
internal sealed class MssqlApplicationLockManager : IApplicationLockManager
{
    private readonly string _lockConnectionString;
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
        new SqlConnectionStringBuilder(databaseConnection)
        {
            ApplicationName = ApplicationLockConnectionPool.ApplicationName,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = ApplicationLockConnectionPool.MaxPoolSize,
        }.ConnectionString;

    /// <summary>
    /// Acquires every resource on one session, each within its own acquire window. Any outcome
    /// other than the whole set being held (a timeout, a failure, a cancellation) releases the
    /// resources already taken and closes the session before the result leaves this method.
    /// </summary>
    private async Task<ApplicationLockResult> AcquireOrderedAsync(
        int[] applicationIds,
        CancellationToken cancellationToken
    )
    {
        SqlConnection? connection = null;
        List<string> heldResources = [];
        try
        {
            connection = new SqlConnection(_lockConnectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (int applicationId in applicationIds)
            {
                string resource = ComputeLockResource(applicationId);
                if (
                    await TryGetApplockAsync(connection, resource, applicationId, cancellationToken) is
                    { } failure
                )
                {
                    return failure;
                }

                heldResources.Add(resource);
            }

            var handle = new Handle(this, connection, [.. heldResources], applicationIds);
            connection = null;
            return new ApplicationLockResult.Acquired(handle);
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
                await new Handle(this, connection, [.. heldResources], applicationIds).DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Returns null once the resource is held by this session; otherwise the classified failure.
    /// </summary>
    private async Task<ApplicationLockResult?> TryGetApplockAsync(
        SqlConnection connection,
        string resource,
        int applicationId,
        CancellationToken cancellationToken
    )
    {
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
            return null;
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

    /// <summary>
    /// Deterministic session-lock resource name; the namespace prefix keeps future application
    /// locks from colliding with other uses.
    /// </summary>
    internal static string ComputeLockResource(int applicationId) =>
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

    private static string Describe(int[] applicationIds) =>
        string.Join(",", applicationIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Owns one lock session and the resources it holds. Disposal releases every resource,
    /// attempting each even after one fails, and evicts the session from the pool when any
    /// release failed so a still-held lock cannot be handed to the next borrower of the
    /// connection.
    /// </summary>
    private sealed class Handle(
        MssqlApplicationLockManager manager,
        SqlConnection connection,
        string[] resources,
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
            foreach (string resource in resources)
            {
                try
                {
                    await manager._unlockAsync(connection, resource);
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

            if (!evict && resources.Length > 0)
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
