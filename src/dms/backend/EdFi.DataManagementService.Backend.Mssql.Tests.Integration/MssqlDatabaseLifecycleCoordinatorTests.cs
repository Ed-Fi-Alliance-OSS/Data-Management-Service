// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
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
    public async Task It_waits_for_the_instance_application_lock_held_by_another_session()
    {
        SqlConnectionStringBuilder builder = new(Configuration.MssqlAdminConnectionString!)
        {
            InitialCatalog = "master",
        };
        SqlConnection competingSession = new(builder.ConnectionString);

        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        Task? createDatabaseTask = null;

        try
        {
            await competingSession.OpenAsync();
            await ExecuteApplicationLockCommandAsync(
                competingSession,
                MssqlDatabaseLifecycleCoordinator.AcquireApplicationLockSql
            );
            bool lifecycleOperationWaited;
            try
            {
                createDatabaseTask = MssqlTestDatabaseHelper.CreateDatabaseUnderLifecycleGateAsync(
                    databaseName
                );
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                lifecycleOperationWaited = !createDatabaseTask.IsCompleted;
            }
            finally
            {
                await competingSession.DisposeAsync();
            }

            await createDatabaseTask.WaitAsync(TimeSpan.FromSeconds(10));
            createDatabaseTask = null;

            lifecycleOperationWaited.Should().BeTrue();
        }
        finally
        {
            await competingSession.DisposeAsync();

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
}
