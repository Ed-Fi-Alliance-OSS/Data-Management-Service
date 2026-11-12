// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

/// <summary>
/// Runs the encoding guards against a real PostgreSQL 18 cluster initialized with a non-UTF-8
/// encoding, so template1 and every database created from it are not UTF-8.
/// </summary>
public abstract class PostgresqlNonUtf8ClusterFixture
{
    protected string DatabaseName { get; private set; } = null!;
    protected string NonUtf8AdminConnectionString { get; private set; } = null!;

    [OneTimeSetUp]
    public void RequireCluster()
    {
        // Checked once per fixture, so an ignored fixture runs no per-test setup or teardown.
        if (DatabaseConfiguration.PostgresNonUtf8AdminConnectionString is not { } connectionString)
        {
            // CI starts this cluster for these fixtures; a wiring typo there must not silently drop them.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Assert.Fail("PostgresNonUtf8Admin connection string is not configured in CI.");
            }
            Assert.Ignore("PostgresNonUtf8Admin connection string is not configured.");
            return;
        }
        NonUtf8AdminConnectionString = connectionString;
    }

    [SetUp]
    public void SetUpCluster()
    {
        DatabaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();

        // The fixture is meaningful only if the cluster really is PostgreSQL 18 with a non-UTF-8 template1.
        using var connection = new NpgsqlConnection(NonUtf8AdminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "SELECT current_setting('server_version_num')::integer, pg_encoding_to_char(encoding) FROM pg_database WHERE datname = 'template1'",
            connection
        );
        using var reader = command.ExecuteReader();
        reader.Read();
        reader.GetInt32(0).Should().BeGreaterThanOrEqualTo(180000);
        reader.GetString(1).Should().NotBe("UTF8");
        Template1Encoding = reader.GetString(1);
    }

    protected string Template1Encoding { get; private set; } = null!;

    [TearDown]
    public void TearDownCluster() =>
        ExecuteOnNonUtf8Cluster($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)");

    protected string NonUtf8TargetConnectionString =>
        new NpgsqlConnectionStringBuilder(NonUtf8AdminConnectionString)
        {
            Database = DatabaseName,
        }.ConnectionString;

    protected void ExecuteOnNonUtf8Cluster(string sql)
    {
        using var connection = new NpgsqlConnection(NonUtf8AdminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    protected bool DatabaseExistsOnNonUtf8Cluster()
    {
        using var connection = new NpgsqlConnection(NonUtf8AdminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", DatabaseName);
        return command.ExecuteScalar() is not null;
    }

    protected static string Message(string detected) =>
        $"{PostgresqlPlatformCompatibilityException.RequirementMessage} Detected: {detected}.";

    protected static void ShouldNotBeTheRawCreateFailure(string error) =>
        error
            .Should()
            .NotContain("22023")
            .And.NotContain("incompatible with the encoding of the template database");
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Direct_Provisioning_With_Create_Database_On_A_Cluster_Whose_Template1_Is_Not_UTF8
    : PostgresqlNonUtf8ClusterFixture
{
    private (int ExitCode, string Output, string Error) _result;

    [SetUp]
    public void SetUp() =>
        _result = ProvisionTestHelper.RunProvision(
            "pgsql",
            NonUtf8TargetConnectionString,
            createDatabase: true
        );

    [Test]
    public void It_fails() => _result.ExitCode.Should().Be(1);

    [Test]
    public void It_reports_the_compatibility_message_instead_of_the_raw_create_failure()
    {
        _result.Error.Should().Contain(Message($"template1 encoding {Template1Encoding}"));
        ShouldNotBeTheRawCreateFailure(_result.Error);
    }

    [Test]
    public void It_leaves_no_database_behind() => DatabaseExistsOnNonUtf8Cluster().Should().BeFalse();
}

[TestFixture(true)]
[TestFixture(false)]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Direct_Provisioning_Into_An_Existing_Non_UTF8_Database_On_PostgreSQL_18(
    bool createDatabase
) : PostgresqlNonUtf8ClusterFixture
{
    private (int ExitCode, string Output, string Error) _result;

    [SetUp]
    public void SetUp()
    {
        // A plain CREATE on this cluster inherits template1's non-UTF-8 encoding.
        ExecuteOnNonUtf8Cluster($"CREATE DATABASE \"{DatabaseName}\"");
        _result = ProvisionTestHelper.RunProvision(
            "pgsql",
            NonUtf8TargetConnectionString,
            createDatabase: createDatabase
        );
    }

    [Test]
    public void It_fails() => _result.ExitCode.Should().Be(1);

    [Test]
    public void It_reports_the_compatibility_message() =>
        _result.Error.Should().Contain(Message($"target database encoding {Template1Encoding}"));

    [Test]
    public void It_runs_no_DDL()
    {
        using var connection = new NpgsqlConnection(NonUtf8TargetConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT 1 FROM pg_namespace WHERE nspname = 'dms'", connection);
        command.ExecuteScalar().Should().BeNull();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Managed_Provisioning_On_A_Cluster_Whose_Template1_Is_Not_UTF8_Then_A_Compliant_Server
    : PostgresqlNonUtf8ClusterFixture
{
    private string _root = null!;
    private (int ExitCode, string Output, string Error) _rejected;
    private int _operationsAfterRejection;
    private (int ExitCode, string Output, string Error) _retry;
    private int _operationsAfterRetry;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "platform-managed-" + Guid.NewGuid().ToString("N"));
        _rejected = RunManaged(NonUtf8TargetConnectionString);
        _operationsAfterRejection = CountJournalOperations();

        // The operator moves the same target identity to a compliant PostgreSQL 18 UTF-8 server.
        _retry = RunManaged(PostgresTestDatabaseHelper.BuildConnectionString(DatabaseName));
        _operationsAfterRetry = CountJournalOperations();
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private (int ExitCode, string Output, string Error) RunManaged(string connectionString) =>
        CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            CliTestHelper.GetMinimalSchemaPath(),
            "--connection-string",
            connectionString,
            "--dialect",
            "pgsql",
            "--create-database",
            "--managed-state-path",
            _root,
            "--data-store-id",
            "42",
            "--instance-key",
            "datastore-42"
        );

    private int CountJournalOperations() => CountJournalOperationsAsync().GetAwaiter().GetResult();

    private async Task<int> CountJournalOperationsAsync()
    {
        CdcTargetIdentity target = new("local", "default", "42", "datastore-42", 1, CdcProvider.Postgresql);
        await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(10),
            default
        );
        try
        {
            return (await session.ReadAsync(target, default)).Operations.Length;
        }
        catch (CdcWorkflowStateException exception)
            when (exception.Failure == CdcWorkflowStateFailure.Missing)
        {
            return 0;
        }
    }

    [Test]
    public void It_rejects_the_non_UTF8_cluster_with_the_compatibility_message()
    {
        _rejected.ExitCode.Should().Be(1);
        _rejected.Error.Should().Be(Message($"template1 encoding {Template1Encoding}") + Environment.NewLine);
        _rejected.Output.Should().BeEmpty();
    }

    [Test]
    public void It_leaves_no_database_behind() => DatabaseExistsOnNonUtf8Cluster().Should().BeFalse();

    [Test]
    public void It_records_no_creation_intent() => _operationsAfterRejection.Should().Be(0);

    [Test]
    public void It_completes_the_retry_on_a_compliant_server_without_recovery()
    {
        _retry.ExitCode.Should().Be(0, _retry.Error);
        _retry.Error.Should().NotContain("interrupted");
        using var json = JsonDocument.Parse(_retry.Output);
        json.RootElement.GetProperty("creationReceipt")
            .GetProperty("outcome")
            .GetString()
            .Should()
            .Be("Created");
        _operationsAfterRetry.Should().Be(2);
    }
}
