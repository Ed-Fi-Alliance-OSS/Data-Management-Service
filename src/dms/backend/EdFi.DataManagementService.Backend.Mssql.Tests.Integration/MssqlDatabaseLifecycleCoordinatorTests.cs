// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Mssql.Tests.Integration.MssqlTeardownTestSupport;

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

/// <summary>
/// SQL Agent's CDC capture job holds a session in its database. The teardown fence must not be what
/// ends that session, so the drop path has to remove the database's CDC Agent jobs before the fence.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Cdc_Enabled_Database_Dropped_Under_The_Lifecycle_Gate(bool withCaptureInstance)
{
    private static readonly TimeSpan _captureSessionWaitLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _captureSessionPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly List<FenceObservation> _observations = [];
    private string _databaseName = null!;
    private Exception? _dropException;
    private string _teardownReport = "";
    private bool _databaseExistsAfterDrop;
    private bool _sqlAgentRunning;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server CDC teardown integration tests require a MssqlAdmin connection string."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        await MssqlTestDatabaseHelper.CreateDatabaseUnderLifecycleGateAsync(_databaseName);
        await EnableCdcAsync(_databaseName, withCaptureInstance);

        _sqlAgentRunning = await IsSqlAgentRunningAsync();
        if (withCaptureInstance && _sqlAgentRunning)
        {
            await WaitForCaptureSessionAsync();
        }

        MssqlTestDatabaseHelper.BeforeSourceFenceObservationHook = ObserveFenceAsync;
        try
        {
            (_teardownReport, _dropException) = await CaptureConsoleErrorAsync(() =>
                MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(_databaseName)
            );
        }
        finally
        {
            MssqlTestDatabaseHelper.BeforeSourceFenceObservationHook = null;
        }

        await using SqlConnection connection = await OpenConnectionAsync("master");
        _databaseExistsAfterDrop = await DatabaseExistsAsync(connection, _databaseName);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        MssqlTestDatabaseHelper.BeforeSourceFenceObservationHook = null;

        if (_databaseName is null)
        {
            return;
        }

        await using SqlConnection connection = await OpenConnectionAsync("master");
        if (await DatabaseExistsAsync(connection, _databaseName))
        {
            await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(_databaseName);
        }
    }

    [Test]
    public void It_observes_the_fence_while_the_database_exists()
    {
        _observations.Should().Contain(observation => observation.DatabaseExists, DescribeObservations());
    }

    [Test]
    public void It_drops_the_cdc_agent_jobs_before_the_fence()
    {
        _observations
            .Should()
            .OnlyContain(observation => observation.CdcJobTypes.Count == 0, DescribeObservations());
    }

    [Test]
    public void It_leaves_no_cdc_job_session_in_the_database_when_the_fence_runs()
    {
        if (withCaptureInstance && !_sqlAgentRunning)
        {
            Assert.Ignore("SQL Agent is not running, so no CDC capture session existed to observe.");
        }

        _observations
            .Should()
            .OnlyContain(observation => observation.JobStepSessions.Count == 0, DescribeObservations());
    }

    [Test]
    public void It_drops_the_database()
    {
        _dropException.Should().BeNull(DescribeObservations());
        _databaseExistsAfterDrop.Should().BeFalse(DescribeObservations());
    }

    [Test]
    public void It_reports_nothing_when_the_teardown_is_not_degraded()
    {
        _teardownReport.Should().BeEmpty(DescribeObservations());
    }

    private async Task ObserveFenceAsync(SqlConnection connection, string databaseName)
    {
        if (!string.Equals(databaseName, _databaseName, StringComparison.Ordinal))
        {
            return;
        }

        const string sql = """
            SELECT CASE WHEN DB_ID(@databaseName) IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END;

            -- A missing cdc_jobs (208) means no CDC jobs; see MssqlTestDatabaseHelper.DropCdcAgentJobsAsync.
            BEGIN TRY
                EXEC sys.sp_executesql
                    N'SELECT [job_type] FROM msdb.dbo.cdc_jobs WHERE [database_id] = DB_ID(@databaseName) ORDER BY [job_type];',
                    N'@databaseName sysname',
                    @databaseName = @databaseName;
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() <> 208
                    THROW;

                SELECT CAST(N'' AS nvarchar(20)) AS [job_type] WHERE 1 = 0;
            END CATCH;

            SELECT CONCAT([session_id], N' ', [program_name], N' ', [status])
            FROM sys.dm_exec_sessions
            WHERE [database_id] = DB_ID(@databaseName)
              AND [program_name] LIKE N'SQLAgent - TSQL JobStep%'
            ORDER BY [session_id];
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(
            new SqlParameter("@databaseName", SqlDbType.NVarChar, 128) { Value = databaseName }
        );

        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var databaseExists = reader.GetBoolean(0);
        await reader.NextResultAsync();
        List<string> jobTypes = await ReadStringsAsync(reader);
        await reader.NextResultAsync();
        List<string> sessions = await ReadStringsAsync(reader);

        _observations.Add(new(databaseExists, jobTypes, sessions));
    }

    private static async Task<bool> IsSqlAgentRunningAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync("master");
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.dm_server_services
            WHERE [servicename] LIKE N'SQL Server Agent%'
              AND [status_desc] = N'Running';
            """;

        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task WaitForCaptureSessionAsync()
    {
        const string sql = """
            SELECT COUNT(*)
            FROM sys.dm_exec_sessions
            WHERE [database_id] = DB_ID(@databaseName)
              AND [program_name] LIKE N'SQLAgent - TSQL JobStep%';
            """;

        await using SqlConnection connection = await OpenConnectionAsync("master");
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < _captureSessionWaitLimit)
        {
            await using (SqlCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.Add(
                    new SqlParameter("@databaseName", SqlDbType.NVarChar, 128) { Value = _databaseName }
                );

                if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
                {
                    return;
                }
            }

            await Task.Delay(_captureSessionPollInterval);
        }

        Assert.Fail(
            $"SQL Agent is running, but no CDC capture session appeared in '{_databaseName}' within "
                + $"{_captureSessionWaitLimit.TotalSeconds} s.{Environment.NewLine}"
                + await DescribeCaptureJobStateAsync(connection)
        );
    }

    private async Task<string> DescribeCaptureJobStateAsync(SqlConnection connection)
    {
        const string cdcJobsSql = """
            IF OBJECT_ID(N'msdb.dbo.cdc_jobs') IS NOT NULL
                EXEC sys.sp_executesql
                    N'SELECT [job_type], CONVERT(nvarchar(36), [job_id]) FROM msdb.dbo.cdc_jobs WHERE [database_id] = DB_ID(@databaseName);',
                    N'@databaseName sysname',
                    @databaseName = @databaseName;
            ELSE
                SELECT N'msdb.dbo.cdc_jobs does not exist', NULL;
            """;
        const string jobActivitySql = """
            SELECT jobs.[name], activity.[session_id], activity.[start_execution_date], activity.[stop_execution_date]
            FROM msdb.dbo.sysjobs jobs
            INNER JOIN msdb.dbo.sysjobactivity activity ON activity.[job_id] = jobs.[job_id]
            WHERE jobs.[name] IN (N'cdc.' + @databaseName + N'_capture', N'cdc.' + @databaseName + N'_cleanup');
            """;
        const string jobHistorySql = """
            SELECT TOP (10) jobs.[name], history.[step_id], history.[run_status], history.[run_date],
                history.[run_time], history.[message]
            FROM msdb.dbo.sysjobhistory history
            INNER JOIN msdb.dbo.sysjobs jobs ON jobs.[job_id] = history.[job_id]
            WHERE jobs.[name] IN (N'cdc.' + @databaseName + N'_capture', N'cdc.' + @databaseName + N'_cleanup')
            ORDER BY history.[instance_id] DESC;
            """;
        const string sessionsSql = """
            SELECT [session_id], [program_name], [login_name], [status]
            FROM sys.dm_exec_sessions
            WHERE [database_id] = DB_ID(@databaseName);
            """;

        StringBuilder description = new();
        foreach (
            (string title, string sql) in new[]
            {
                ("msdb.dbo.cdc_jobs", cdcJobsSql),
                ("msdb.dbo.sysjobactivity", jobActivitySql),
                ("msdb.dbo.sysjobhistory (latest first)", jobHistorySql),
                ("sessions in the database", sessionsSql),
            }
        )
        {
            description.AppendLine($"{title}:");
            foreach (var row in await ReadRowsAsTextAsync(connection, sql))
            {
                description.AppendLine($"  {row}");
            }
        }

        return description.ToString();
    }

    private async Task<List<string>> ReadRowsAsTextAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(
            new SqlParameter("@databaseName", SqlDbType.NVarChar, 128) { Value = _databaseName }
        );

        List<string> rows = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                string.Join(
                    " | ",
                    Enumerable
                        .Range(0, reader.FieldCount)
                        .Select(ordinal =>
                            reader.IsDBNull(ordinal)
                                ? "NULL"
                                : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
                        )
                )
            );
        }

        return rows;
    }

    private string DescribeObservations()
    {
        return _observations.Count == 0
            ? $"the fence observation hook never ran for '{_databaseName}'; drop exception: {_dropException}"
            : $"fence observations for '{_databaseName}' (with capture instance: {withCaptureInstance}):{Environment.NewLine}"
                + string.Join(
                    Environment.NewLine,
                    _observations.Select(
                        (observation, index) =>
                            $"  #{index + 1}: exists={observation.DatabaseExists}, "
                            + $"cdc jobs=[{string.Join(", ", observation.CdcJobTypes)}], "
                            + $"job-step sessions=[{string.Join("; ", observation.JobStepSessions)}]"
                    )
                )
                + $"{Environment.NewLine}drop exception: {_dropException?.ToString() ?? "none"}";
    }

    private static async Task<List<string>> ReadStringsAsync(SqlDataReader reader)
    {
        List<string> values = [];
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private sealed record FenceObservation(
        bool DatabaseExists,
        IReadOnlyList<string> CdcJobTypes,
        IReadOnlyList<string> JobStepSessions
    );
}

/// <summary>
/// The teardown waits a bounded time for SQL Agent to end a dropped CDC job's session. A job-step session
/// still connected at the limit is reported (database, session and program) before the fence ends it, and
/// the drop still completes. The stand-in session carries the program-name prefix the helper uses to
/// recognize SQL Agent job steps; it works with SQL Agent on or off, because enabling a capture instance
/// writes the database's <c>msdb.dbo.cdc_jobs</c> rows either way.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Cdc_Job_Step_Session_That_Outlasts_The_Teardown_Wait
{
    private const string StandInProgramName = "SQLAgent - TSQL JobStep (DMS teardown stand-in)";

    private string _databaseName = null!;
    private short _standInSessionId;
    private string _teardownReport = "";
    private Exception? _dropException;
    private bool _databaseExistsAfterDrop;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server CDC teardown integration tests require a MssqlAdmin connection string."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        await MssqlTestDatabaseHelper.CreateDatabaseUnderLifecycleGateAsync(_databaseName);
        await EnableCdcAsync(_databaseName, withCaptureInstance: true);

        // The fence ends this session; disposing the broken, unpooled connection afterwards is harmless.
        await using SqlConnection standIn = await OpenConnectionAsync(_databaseName, StandInProgramName);
        _standInSessionId = await ReadSessionIdAsync(standIn);

        (_teardownReport, _dropException) = await CaptureConsoleErrorAsync(() =>
            MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(_databaseName)
        );

        await using SqlConnection connection = await OpenConnectionAsync("master");
        _databaseExistsAfterDrop = await DatabaseExistsAsync(connection, _databaseName);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_databaseName is null)
        {
            return;
        }

        await using SqlConnection connection = await OpenConnectionAsync("master");
        if (await DatabaseExistsAsync(connection, _databaseName))
        {
            await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(_databaseName);
        }
    }

    [Test]
    public void It_reports_the_job_step_session_left_after_the_wait()
    {
        _teardownReport
            .Should()
            .Contain($"'{_databaseName}'")
            .And.Contain("after its CDC jobs were dropped")
            .And.Contain($"{_standInSessionId} '{StandInProgramName}'");
    }

    [Test]
    public void It_still_drops_the_database()
    {
        _dropException.Should().BeNull(_teardownReport);
        _databaseExistsAfterDrop.Should().BeFalse(_teardownReport);
    }
}

/// <summary>
/// Models the tail of the CI failure: a database left in single-user mode with a session still in it makes
/// the fence fail with 5064. The failure must name the session that holds the database, and reach the
/// caller unchanged so that the deadlock and transient retries still classify it.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Database_Held_In_Single_User_Mode_At_The_Teardown_Fence
{
    private const string HolderProgramName = "DMS teardown single-user holder";

    private string _databaseName = null!;
    private SqlConnection? _holder;
    private short _holderSessionId;
    private string _teardownReport = "";
    private Exception? _dropException;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server teardown integration tests require a MssqlAdmin connection string."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        await MssqlTestDatabaseHelper.CreateDatabaseUnderLifecycleGateAsync(_databaseName);

        _holder = await OpenConnectionAsync(_databaseName, HolderProgramName);
        _holderSessionId = await ReadSessionIdAsync(_holder);
        await ExecuteNonQueryAsync(_holder, "ALTER DATABASE CURRENT SET SINGLE_USER;");

        (_teardownReport, _dropException) = await CaptureConsoleErrorAsync(() =>
            MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(_databaseName)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_holder is not null)
        {
            await _holder.DisposeAsync();
        }

        if (_databaseName is null)
        {
            return;
        }

        await using SqlConnection connection = await OpenConnectionAsync("master");
        if (await DatabaseExistsAsync(connection, _databaseName))
        {
            await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(_databaseName);
        }
    }

    [Test]
    public void It_fails_the_drop_with_the_unchanged_sql_server_error()
    {
        _dropException
            .Should()
            .BeOfType<SqlException>(_teardownReport)
            .Which.Errors.Cast<SqlError>()
            .Select(error => error.Number)
            .Should()
            .Contain(5064, _teardownReport);
    }

    [Test]
    public void It_reports_the_session_that_holds_the_database()
    {
        _teardownReport
            .Should()
            .Contain($"'{_databaseName}' teardown: teardown failed with SQL Server error 5064")
            .And.Contain($"{_holderSessionId} '{HolderProgramName}'");
    }
}

internal static class MssqlTeardownTestSupport
{
    /// <summary>
    /// Runs the action with <see cref="Console.Error"/>, where the teardown helper writes its reports,
    /// captured. Only <c>[NonParallelizable]</c> fixtures may call it, because the writer is process-wide.
    /// </summary>
    public static async Task<(string Output, Exception? Exception)> CaptureConsoleErrorAsync(
        Func<Task> action
    )
    {
        TextWriter original = Console.Error;
        StringWriter captured = new(CultureInfo.InvariantCulture);
        Console.SetError(captured);
        try
        {
            await action();
            return (captured.ToString(), null);
        }
        catch (Exception exception)
        {
            return (captured.ToString(), exception);
        }
        finally
        {
            Console.SetError(original);
            // Still reaches the test output.
            await original.WriteAsync(captured.ToString());
        }
    }

    public static async Task EnableCdcAsync(string databaseName, bool withCaptureInstance)
    {
        await using SqlConnection connection = await OpenConnectionAsync(databaseName);
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE [dbo].[CdcTeardownProbe]
            (
                [ProbeId] int NOT NULL CONSTRAINT [PK_CdcTeardownProbe] PRIMARY KEY CLUSTERED,
                [ProbeValue] int NOT NULL
            );

            EXEC sys.sp_cdc_enable_db;
            """
        );

        if (withCaptureInstance)
        {
            // The first capture instance creates and starts the database's capture and cleanup jobs.
            await ExecuteNonQueryAsync(
                connection,
                """
                EXEC sys.sp_cdc_enable_table
                    @source_schema = N'dbo',
                    @source_name = N'CdcTeardownProbe',
                    @role_name = NULL,
                    @supports_net_changes = 0;
                """
            );
        }
    }

    public static async Task<bool> DatabaseExistsAsync(SqlConnection connection, string databaseName)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@databaseName) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.Add(
            new SqlParameter("@databaseName", SqlDbType.NVarChar, 128) { Value = databaseName }
        );

        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    public static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<short> ReadSessionIdAsync(SqlConnection connection)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@@SPID AS smallint);";
        return (short)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Unpooled, so that no idle connection from a fixture stays in the database for the fence to end.
    /// </summary>
    public static async Task<SqlConnection> OpenConnectionAsync(
        string databaseName,
        string? applicationName = null
    )
    {
        SqlConnectionStringBuilder builder = new(MssqlTestDatabaseHelper.BuildConnectionString(databaseName))
        {
            Pooling = false,
        };
        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
        }

        SqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
