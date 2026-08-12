// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlLifecycleExceptionAggregation
{
    private InvalidOperationException _primaryException = null!;
    private ApplicationException _cleanupException = null!;

    [SetUp]
    public void Setup()
    {
        _primaryException = new("primary failure");
        _cleanupException = new("cleanup failure");
    }

    [Test]
    public void It_rethrows_the_primary_exception_without_wrapping_when_cleanup_succeeds()
    {
        Action act = () => MssqlLifecycleExceptionAggregator.Throw(_primaryException, []);

        act.Should().ThrowExactly<InvalidOperationException>().Which.Should().BeSameAs(_primaryException);
    }

    [Test]
    public void It_keeps_the_primary_exception_first_when_cleanup_also_fails()
    {
        Action act = () => MssqlLifecycleExceptionAggregator.Throw(_primaryException, [_cleanupException]);

        AggregateException exception = act.Should().ThrowExactly<AggregateException>().Which;
        exception.InnerExceptions.Should().Equal(_primaryException, _cleanupException);
    }
}

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlDatabaseLifecycleCoordinator
{
    private const int WorkerCount = 4;
    private const int CyclesPerWorker = 5;

    [SetUp]
    public void Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }
    }

    [Test]
    public async Task It_completes_parallel_create_and_drop_cycles_without_retries_or_leaks()
    {
        ConcurrentDictionary<string, byte> databasesNeedingCleanup = new(StringComparer.Ordinal);
        ConcurrentBag<string> databaseNames = [];
        var retryCountBefore = MssqlDatabaseLifecycleCoordinator.TransientConnectionRetryCount;

        try
        {
            await Task.WhenAll(
                Enumerable
                    .Range(0, WorkerCount)
                    .Select(async workerIndex =>
                    {
                        for (var cycle = 0; cycle < CyclesPerWorker; cycle++)
                        {
                            var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
                            databaseNames.Add(databaseName);
                            databasesNeedingCleanup[databaseName] = 0;

                            await MssqlTestDatabaseHelper.CreateDatabaseUnderLifecycleGateAsync(databaseName);
                            await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(databaseName);
                            databasesNeedingCleanup.TryRemove(databaseName, out _);
                        }
                    })
            );

            IReadOnlyList<MssqlRunOwnedDatabase> runOwnedDatabases =
                await MssqlTestDatabaseHelper.ReadRunOwnedDatabasesAsync();

            databasesNeedingCleanup.Should().BeEmpty();
            runOwnedDatabases.Select(database => database.Name).Should().NotIntersectWith(databaseNames);
            MssqlDatabaseLifecycleCoordinator.TransientConnectionRetryCount.Should().Be(retryCountBefore);
        }
        finally
        {
            foreach (var databaseName in databasesNeedingCleanup.Keys)
            {
                await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(databaseName);
            }
        }
    }

    [Test]
    public async Task It_retries_deadlock_victim_failures_from_lifecycle_operations()
    {
        var attemptCount = 0;
        var retryCountBefore = MssqlDatabaseLifecycleCoordinator.TransientConnectionRetryCount;

        await MssqlDatabaseLifecycleCoordinator.ExecuteAsync(_ =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                throw CreateSqlException(
                    1205,
                    "Transaction was deadlocked on lock resources and has been chosen as the deadlock victim."
                );
            }

            return Task.CompletedTask;
        });

        attemptCount.Should().Be(2);
        MssqlDatabaseLifecycleCoordinator.TransientConnectionRetryCount.Should().Be(retryCountBefore + 1);
    }

    [Test]
    public async Task It_waits_for_the_instance_application_lock_held_by_another_session()
    {
        SqlConnectionStringBuilder builder = new(Configuration.MssqlAdminConnectionString!)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        Task? createDatabaseTask = null;

        try
        {
            bool lifecycleOperationWaited;
            await using (SqlConnection competingSession = new(builder.ConnectionString))
            {
                await competingSession.OpenAsync();
                await ExecuteApplicationLockCommandAsync(
                    competingSession,
                    MssqlDatabaseLifecycleCoordinator.AcquireApplicationLockSql
                );
                createDatabaseTask = MssqlTestDatabaseHelper.CreateDatabaseUnderLifecycleGateAsync(
                    databaseName
                );
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                lifecycleOperationWaited = !createDatabaseTask.IsCompleted;
            }

            await createDatabaseTask.WaitAsync(TimeSpan.FromSeconds(10));
            createDatabaseTask = null;

            lifecycleOperationWaited.Should().BeTrue();
        }
        finally
        {
            if (createDatabaseTask is not null)
            {
                try
                {
                    await createDatabaseTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // The test operation already carries the causal failure; this await only bounds cleanup.
                }
            }

            await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(databaseName);
        }
    }

    private static async Task ExecuteApplicationLockCommandAsync(SqlConnection connection, string commandText)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 0;
        command.Parameters.Add(
            new SqlParameter("@resource", SqlDbType.NVarChar, 255)
            {
                Value = MssqlDatabaseLifecycleCoordinator.LockResource,
            }
        );

        var result = Convert.ToInt32(await command.ExecuteScalarAsync());
        result.Should().BeGreaterThanOrEqualTo(0);
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, message);

        var errorList = new List<object> { sqlError };
        var errorCollection = (SqlErrorCollection)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errorCollection, errorList);

        var sqlException = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, errorCollection);

        return sqlException;
    }
}
