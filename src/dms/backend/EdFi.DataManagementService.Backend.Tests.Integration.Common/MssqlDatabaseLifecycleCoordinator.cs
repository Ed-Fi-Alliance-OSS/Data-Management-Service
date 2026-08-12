// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

/// <summary>
/// Serializes SQL Server catalog-mutating test database lifecycle operations. The in-process gate
/// avoids tying up a connection while another NUnit worker owns the gate; the session application
/// lock coordinates independent test processes using the same SQL Server instance.
/// </summary>
internal static class MssqlDatabaseLifecycleCoordinator
{
    internal const string LockResource = "EdFi.DMS.Tests.DatabaseLifecycle.v1";

    internal const string AcquireApplicationLockSql = """
        DECLARE @result int;

        EXEC @result = sys.sp_getapplock
            @Resource = @resource,
            @LockMode = N'Exclusive',
            @LockOwner = N'Session',
            @LockTimeout = -1,
            @DbPrincipal = N'public';

        SELECT @result;
        """;

    internal const string ReleaseApplicationLockSql = """
        DECLARE @result int;

        EXEC @result = sys.sp_releaseapplock
            @Resource = @resource,
            @LockOwner = N'Session',
            @DbPrincipal = N'public';

        SELECT @result;
        """;

    internal const string SetLowDeadlockPrioritySql = "SET DEADLOCK_PRIORITY LOW;";

    private const int MaxAttempts = 3;
    private const int DeadlockVictimErrorNumber = 1205;
    private static readonly TimeSpan[] _retryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
    ];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceGates = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static long _transientConnectionRetryCount;

    internal static long TransientConnectionRetryCount =>
        Interlocked.Read(ref _transientConnectionRetryCount);

    public static async Task ExecuteAsync(
        Func<SqlConnection, Task> operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        MssqlLifecycleConnectionTarget target = ResolveConnectionTarget();
        SemaphoreSlim instanceGate = _instanceGates.GetOrAdd(target.InstanceKey, static _ => new(1, 1));

        await instanceGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await ExecuteAttemptAsync(target.ConnectionString, operation, cancellationToken);
                    return;
                }
                catch (Exception exception)
                    when (attempt < MaxAttempts && IsTransientLifecycleFailure(GetPrimaryException(exception))
                    )
                {
                    Interlocked.Increment(ref _transientConnectionRetryCount);
                    await Task.Delay(_retryDelays[attempt - 1], cancellationToken);
                }
            }

            throw new InvalidOperationException("The SQL Server lifecycle retry loop ended unexpectedly.");
        }
        finally
        {
            instanceGate.Release();
        }
    }

    private static async Task ExecuteAttemptAsync(
        string connectionString,
        Func<SqlConnection, Task> operation,
        CancellationToken cancellationToken
    )
    {
        SqlConnection connection = new(connectionString);
        Exception? primaryException = null;
        List<Exception> cleanupExceptions = [];
        var applicationLockAcquired = false;

        try
        {
            await connection.OpenAsync(cancellationToken);
            await SetLowDeadlockPriorityAsync(connection, cancellationToken);
            await AcquireApplicationLockAsync(connection, cancellationToken);
            applicationLockAcquired = true;
            await operation(connection);
        }
        catch (Exception exception)
        {
            primaryException = exception;
        }

        if (applicationLockAcquired)
        {
            try
            {
                await ReleaseApplicationLockAsync(connection, CancellationToken.None);
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);

                try
                {
                    SqlConnection.ClearPool(connection);
                }
                catch (Exception clearPoolException)
                {
                    cleanupExceptions.Add(clearPoolException);
                }
            }
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
        }

        if (primaryException is not null)
        {
            MssqlLifecycleExceptionAggregator.Throw(primaryException, cleanupExceptions);
        }

        if (cleanupExceptions.Count != 0)
        {
            MssqlLifecycleExceptionAggregator.Throw(cleanupExceptions);
        }
    }

    private static async Task AcquireApplicationLockAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = CreateApplicationLockCommand(
            connection,
            AcquireApplicationLockSql,
            commandTimeoutSeconds: 0
        );
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

        if (result < 0)
        {
            throw new InvalidOperationException(
                $"SQL Server test database lifecycle lock acquisition failed with sp_getapplock result {result}."
            );
        }
    }

    private static async Task SetLowDeadlockPriorityAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = SetLowDeadlockPrioritySql;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReleaseApplicationLockAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = CreateApplicationLockCommand(
            connection,
            ReleaseApplicationLockSql,
            commandTimeoutSeconds: 30
        );
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

        if (result < 0)
        {
            throw new InvalidOperationException(
                $"SQL Server test database lifecycle lock release failed with sp_releaseapplock result {result}."
            );
        }
    }

    private static SqlCommand CreateApplicationLockCommand(
        SqlConnection connection,
        string commandText,
        int commandTimeoutSeconds
    )
    {
        SqlCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(
            new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource }
        );
        return command;
    }

    private static MssqlLifecycleConnectionTarget ResolveConnectionTarget()
    {
        var configuredConnectionString = BaselineDatabaseConfiguration.MssqlAdminConnectionString;
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        SqlConnectionStringBuilder builder = new(configuredConnectionString) { InitialCatalog = "master" };
        var instanceKey = builder.DataSource.Trim();

        if (string.IsNullOrWhiteSpace(instanceKey))
        {
            throw new InvalidOperationException(
                "The SQL Server integration test admin connection string must specify a data source."
            );
        }

        return new(builder.ConnectionString, instanceKey);
    }

    private static Exception GetPrimaryException(Exception exception)
    {
        return exception is AggregateException { InnerExceptions.Count: > 0 } aggregateException
            ? GetPrimaryException(aggregateException.InnerExceptions[0])
            : exception;
    }

    private static bool IsTransientLifecycleFailure(Exception exception)
    {
        if (exception is not SqlException sqlException)
        {
            return false;
        }

        foreach (SqlError error in sqlException.Errors)
        {
            if (
                error.Class >= 20
                || error.Number
                    is -1
                        or 2
                        or 20
                        or 53
                        or 64
                        or 233
                        or 258
                        or DeadlockVictimErrorNumber
                        or 10053
                        or 10054
                        or 10060
                        or 40197
                        or 40501
                        or 40613
                        or 49918
                        or 49919
                        or 49920
            )
            {
                return true;
            }
        }

        return false;
    }

    private sealed record MssqlLifecycleConnectionTarget(string ConnectionString, string InstanceKey);
}
