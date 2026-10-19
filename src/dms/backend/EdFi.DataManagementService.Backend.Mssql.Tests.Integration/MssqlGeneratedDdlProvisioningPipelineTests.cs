// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Mssql.Tests.Integration.MssqlGeneratedDdlProvisioningProbe;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture(false)]
[TestFixture(true)]
[Explicit("Runs controlled SQL Server command-timeout probes outside normal CI shards.")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlProvisioningPipeline(bool throwingWriter)
{
    private Exception _exception = null!;
    private string _diagnostic = "";
    private int _admittedAfterFailure;
    private int _admittedAfterReacquiring;
    private bool _databaseExists;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        RequireSqlServer();
        using StringWriter writer = throwingWriter ? new CaptureThenThrowWriter() : new StringWriter();
        var gate = new MssqlGeneratedDdlProvisioningGate("1");
        var resource = $"DMS-1330:{Guid.NewGuid():N}";
        _exception = await RunBlockedProvisioningAsync(resource, BuildBlockedBatch(resource), gate, writer);
        _diagnostic = writer.ToString();
        _admittedAfterFailure = gate.AdmittedCount;
        _databaseExists = await DoesDatabaseExistAsync(GetDiagnosticDatabaseName(_diagnostic));
        await using var nextLease = await gate.AcquireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(90));
        _admittedAfterReacquiring = gate.AdmittedCount;
    }

    [Test]
    public void It_preserves_the_real_provider_timeout_and_stack()
    {
        var providerException = PrimaryException(_exception).Should().BeOfType<SqlException>().Subject;
        providerException.Number.Should().Be(-2);
        providerException.StackTrace.Should().Contain("ExecuteGeneratedDdlBatchAsync");
    }

    [Test]
    public void It_reports_the_provisioning_context_even_if_the_writer_throws()
    {
        _diagnostic
            .Should()
            .Contain("MSSQL generated-DDL provisioning timeout: possible provisioning contention;")
            .And.Contain("no overlap observed in sampled admitted operations")
            .And.Contain("batch=1/1")
            .And.Contain("strategy=direct")
            .And.Contain("commandTimeoutSeconds=5")
            .And.Contain("limit=1")
            .And.Contain("admittedAtBatchStart=1")
            .And.Contain("admittedAtTimeout=1");
    }

    [Test]
    public void It_attaches_the_same_diagnostic_to_the_original_provider_exception() =>
        PrimaryException(_exception)
            .Data["MssqlGeneratedDdlProvisioningDiagnostic"]
            .Should()
            .Be(_diagnostic.TrimEnd());

    [Test]
    public void It_releases_production_admission_after_failure()
    {
        _admittedAfterFailure.Should().Be(0);
        _admittedAfterReacquiring.Should().Be(1);
    }

    [Test]
    public void It_drops_the_failed_database() => _databaseExists.Should().BeFalse();
}

[TestFixture]
[Explicit("Runs a controlled standalone SQL Server command-timeout probe outside normal CI shards.")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlProvisioningPipeline_With_Standalone_Ddl
{
    private Exception _exception = null!;
    private int _remainingSchemas;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        RequireSqlServer();
        var resource = $"DMS-1330:{Guid.NewGuid():N}";
        await using var blocker = await AcquireBlockerAsync(resource);
        await using var database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        var sql =
            $"CREATE SCHEMA dms1330_probe;\nGO\nCREATE TABLE dms1330_probe.probe (id int NOT NULL);\nGO\n{BuildBlockedBatch(resource)}";
        var applyTask = database.ApplyGeneratedDdlAsync(sql, 5, fixtureSignature: "standalone");
        try
        {
            await applyTask.WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
        finally
        {
            await blocker.CloseAsync();
            try
            {
                await applyTask;
            }
            catch
            {
                // Preserve the observed failure while draining the operation before disposal.
            }
        }
        _remainingSchemas = await database.ExecuteScalarOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM sys.schemas WHERE name = N'dms1330_probe';"
        );
    }

    [Test]
    public void It_preserves_the_standalone_provider_timeout() =>
        PrimaryException(_exception).Should().BeOfType<SqlException>().Which.Number.Should().Be(-2);

    [Test]
    public void It_does_not_attach_a_provisioning_diagnostic_to_standalone_ddl() =>
        PrimaryException(_exception)
            .Data.Contains("MssqlGeneratedDdlProvisioningDiagnostic")
            .Should()
            .BeFalse();

    [Test]
    public void It_rolls_back_all_standalone_batches() => _remainingSchemas.Should().Be(0);
}

[TestFixture]
[Category(MssqlCiShards.Shard4)]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlProvisioningPipeline_With_A_Non_Timeout_Failure
{
    private Exception _exception = null!;
    private string _diagnostic = "";
    private int _admittedAfterFailure;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        RequireSqlServer();
        using var writer = new StringWriter();
        var gate = new MssqlGeneratedDdlProvisioningGate("1");
        try
        {
            await using var database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
                "THROW 50000, 'injected failure', 1;",
                30,
                CreateContext("non-timeout"),
                () => gate,
                writer
            );
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
        _diagnostic = writer.ToString();
        _admittedAfterFailure = gate.AdmittedCount;
    }

    [Test]
    public void It_preserves_non_timeout_sql_failure_classification() =>
        PrimaryException(_exception).Should().BeOfType<SqlException>().Which.Number.Should().Be(50000);

    [Test]
    public void It_does_not_report_provisioning_contention()
    {
        _diagnostic.Should().BeEmpty();
        PrimaryException(_exception)
            .Data.Contains("MssqlGeneratedDdlProvisioningDiagnostic")
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_releases_production_admission() => _admittedAfterFailure.Should().Be(0);
}

internal static class MssqlGeneratedDdlProvisioningProbe
{
    public static void RequireSqlServer()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }
    }

    public static async Task<Exception> RunBlockedProvisioningAsync(
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

    public static MssqlProvisioningTimingContext CreateContext(string fixture) =>
        new(
            fixture,
            MssqlProvisioningTimingRecorder.ComputeGeneratedDdlHash(fixture),
            MssqlProvisioningTimingRecorder.DirectLeaseStrategy,
            nameof(RunBlockedProvisioningAsync),
            "probe.cs",
            1
        );

    public static string BuildBlockedBatch(string resource) =>
        $"""
            USE [master];
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock @Resource = N'{resource}', @LockMode = N'Exclusive',
                @LockOwner = N'Session', @LockTimeout = -1, @DbPrincipal = N'public';
            IF @lockResult < 0 THROW 50001, 'application lock request failed', 1;
            """;

    public static async Task<SqlConnection> AcquireBlockerAsync(string resource)
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

    public static async Task<bool> DoesDatabaseExistAsync(string databaseName)
    {
        await using var connection = new SqlConnection(Configuration.MssqlAdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.databases WHERE name = @name) THEN 1 ELSE 0 END);";
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = databaseName });
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public static string GetDiagnosticDatabaseName(string diagnostic)
    {
        var marker = " database=";
        var start = diagnostic.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = diagnostic.IndexOf(' ', start);
        return diagnostic[start..end];
    }

    public static Exception PrimaryException(Exception exception) =>
        exception is AggregateException aggregate ? aggregate.InnerExceptions[0] : exception;

    public sealed class CaptureThenThrowWriter : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            throw new IOException("captured diagnostic writer failure");
        }
    }
}
