// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Explicit("Runs controlled SQL Server command-timeout probes outside normal CI shards.")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlProvisioningPipeline
{
    [SetUp]
    public void Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }
    }

    [Test]
    public async Task It_reports_a_real_provisioning_command_timeout_and_drops_the_failed_database()
    {
        var writer = new CaptureWriter();
        var gate = new MssqlGeneratedDdlProvisioningGate("1");
        var resource = $"DMS-1330:{Guid.NewGuid():N}";
        var sql = BuildBlockedBatch(resource);

        var exception = await RunBlockedProvisioningAsync(resource, sql, gate, writer);
        var providerException = PrimaryException(exception).Should().BeOfType<SqlException>().Subject;
        providerException.Number.Should().Be(-2);
        providerException.StackTrace.Should().Contain("ExecuteGeneratedDdlBatchAsync");
        gate.AdmittedCount.Should().Be(0);
        var diagnostic = writer.ToString();
        diagnostic
            .Should()
            .Contain("MSSQL generated-DDL provisioning timeout: suspected provisioning contention.");
        diagnostic
            .Should()
            .Contain("batch=1/1")
            .And.Contain("strategy=direct")
            .And.Contain("commandTimeoutSeconds=5")
            .And.Contain("limit=1")
            .And.Contain("admittedAtBatchStart=1")
            .And.Contain("admittedAtTimeout=1");
        var databaseName = GetDiagnosticDatabaseName(diagnostic);
        (await DoesDatabaseExistAsync(databaseName)).Should().BeFalse();
    }

    [Test]
    public async Task It_preserves_a_real_timeout_when_the_output_writer_throws()
    {
        var writer = new CaptureThenThrowWriter();
        var gate = new MssqlGeneratedDdlProvisioningGate("1");
        var resource = $"DMS-1330:{Guid.NewGuid():N}";

        var exception = await RunBlockedProvisioningAsync(
            resource,
            BuildBlockedBatch(resource),
            gate,
            writer
        );
        PrimaryException(exception).Should().BeOfType<SqlException>().Which.Number.Should().Be(-2);
        writer.ToString().Should().Contain("MSSQL generated-DDL provisioning timeout");
        gate.AdmittedCount.Should().Be(0);
        (await DoesDatabaseExistAsync(GetDiagnosticDatabaseName(writer.ToString()))).Should().BeFalse();
    }

    [Test]
    public async Task It_rolls_back_standalone_ddl_without_a_provisioning_diagnostic()
    {
        var context = CreateContext("standalone");
        var resource = $"DMS-1330:{Guid.NewGuid():N}";
        await using var blocker = await AcquireBlockerAsync(resource);
        var database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        var writer = new StringWriter();
        var sql =
            $"CREATE SCHEMA dms1330_probe;\nGO\nCREATE TABLE dms1330_probe.probe (id int NOT NULL);\nGO\n{BuildBlockedBatch(resource)}";

        var applyTask = database.ApplyGeneratedDdlAsync(sql, 5, context, writer);
        try
        {
            Func<Task> act = () => applyTask.WaitAsync(TimeSpan.FromSeconds(90));
            var exception = await act.Should().ThrowAsync<SqlException>();
            exception.Which.Number.Should().Be(-2);
            writer.ToString().Should().BeEmpty();
            (
                await database.ExecuteScalarOrDefaultAsync<int>(
                    "SELECT COUNT(*) FROM sys.schemas WHERE name = N'dms1330_probe';"
                )
            )
                .Should()
                .Be(0);
        }
        finally
        {
            try
            {
                await blocker.CloseAsync();
                try
                {
                    await applyTask;
                }
                catch
                {
                    // The assertion above observes the operation failure; drain it before disposing.
                }
            }
            finally
            {
                await database.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task It_preserves_non_timeout_sql_failure_classification()
    {
        var writer = new StringWriter();
        var gate = new MssqlGeneratedDdlProvisioningGate("1");
        var sql = "THROW 50000, 'injected failure', 1;";
        Func<Task> act = () =>
            MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
                sql,
                30,
                CreateContext("non-timeout"),
                () => gate,
                writer
            );

        var exception = await act.Should().ThrowAsync<Exception>();
        PrimaryException(exception.Which).Should().BeOfType<SqlException>().Which.Number.Should().Be(50000);
        writer.ToString().Should().BeEmpty();
        gate.AdmittedCount.Should().Be(0);
    }

    private static async Task<Exception> RunBlockedProvisioningAsync(
        string resource,
        string sql,
        MssqlGeneratedDdlProvisioningGate gate,
        TextWriter writer
    )
    {
        await using var blocker = await AcquireBlockerAsync(resource);
        var provisioningTask = MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            sql,
            5,
            CreateContext("timeout-probe"),
            () => gate,
            writer
        );
        try
        {
            await provisioningTask.WaitAsync(TimeSpan.FromSeconds(90));
            throw new AssertionException("The blocked generated-DDL command unexpectedly succeeded.");
        }
        catch (Exception exception) when (exception is not AssertionException)
        {
            return exception;
        }
        finally
        {
            await blocker.CloseAsync();
            try
            {
                await provisioningTask;
            }
            catch
            {
                // Observe a pending failure after releasing the blocker, including watchdog paths.
            }

            if (provisioningTask.IsCompletedSuccessfully)
            {
                await provisioningTask.Result.DisposeAsync();
            }
        }
    }

    private static MssqlProvisioningTimingContext CreateContext(string fixture) =>
        new(
            fixture,
            MssqlProvisioningTimingRecorder.ComputeGeneratedDdlHash(fixture),
            MssqlProvisioningTimingRecorder.DirectLeaseStrategy,
            nameof(RunBlockedProvisioningAsync),
            "probe.cs",
            1
        );

    private static string BuildBlockedBatch(string resource) =>
        $"""
            USE [master];
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock @Resource = N'{resource}', @LockMode = N'Exclusive',
                @LockOwner = N'Session', @LockTimeout = -1, @DbPrincipal = N'public';
            IF @lockResult < 0 THROW 50001, 'application lock request failed', 1;
            """;

    private static async Task<SqlConnection> AcquireBlockerAsync(string resource)
    {
        var builder = new SqlConnectionStringBuilder(Configuration.MssqlAdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @lockResult int;
                EXEC @lockResult = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
                    @LockOwner = N'Session', @LockTimeout = 0, @DbPrincipal = N'public';
                SELECT @lockResult;
                """;
            command.Parameters.Add(
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource }
            );
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().BeGreaterThanOrEqualTo(0);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> DoesDatabaseExistAsync(string databaseName)
    {
        await using var connection = new SqlConnection(Configuration.MssqlAdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.databases WHERE name = @name) THEN 1 ELSE 0 END);";
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = databaseName });
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static string GetDiagnosticDatabaseName(string diagnostic)
    {
        var marker = " database=";
        var start = diagnostic.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = diagnostic.IndexOf(' ', start);
        return diagnostic[start..end];
    }

    private static Exception PrimaryException(Exception exception) =>
        exception is AggregateException aggregate ? aggregate.InnerExceptions[0] : exception;

    private sealed class CaptureWriter : StringWriter { }

    private sealed class CaptureThenThrowWriter : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            throw new IOException("captured diagnostic writer failure");
        }
    }
}
