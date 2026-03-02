// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Fresh_Mssql_Database_Provisioned_With_Create_Database_Flag
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        var fixturePath = CliTestHelper.GetMinimalFixturePath();

        (_exitCode, _output, _error) = CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            fixturePath,
            "--connection-string",
            connectionString,
            "--dialect",
            "mssql",
            "--create-database"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_exit_code_0()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_prints_provisioning_complete_message()
    {
        _output.Should().Contain("Provisioning complete");
    }

    [Test]
    public void It_creates_the_database()
    {
        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("name", _databaseName);

        var result = command.ExecuteScalar();
        result.Should().NotBeNull("the database should exist in sys.databases");
    }

    [Test]
    public void It_creates_the_dms_schema()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sys.schemas WHERE name = 'dms';";

        var result = command.ExecuteScalar();
        result.Should().NotBeNull("the dms schema should exist");
    }

    [Test]
    public void It_creates_core_tables()
    {
        var expectedTables = new[]
        {
            "Document",
            "ResourceKey",
            "Descriptor",
            "ReferentialIdentity",
            "EffectiveSchema",
            "SchemaComponent",
            "DocumentCache",
            "DocumentChangeEvent",
        };

        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        foreach (var table in expectedTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = @table;";
            command.Parameters.AddWithValue("table", table);

            var result = command.ExecuteScalar();
            result.Should().NotBeNull($"table dms.[{table}] should exist");
        }
    }

    [Test]
    public void It_seeds_effective_schema_row()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.EffectiveSchema;";

        var count = (int)command.ExecuteScalar()!;
        count.Should().Be(1, "there should be exactly one EffectiveSchema row");

        using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText = "SELECT EffectiveSchemaHash FROM dms.EffectiveSchema;";

        var hash = (string)hashCommand.ExecuteScalar()!;
        hash.Should().NotBeNullOrEmpty("the effective schema hash should be non-empty");
    }

    [Test]
    public void It_seeds_schema_component_rows()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.SchemaComponent;";

        var count = (int)command.ExecuteScalar()!;
        count.Should().BeGreaterThan(0, "there should be at least one SchemaComponent row");
    }

    [Test]
    public void It_seeds_resource_key_rows()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.ResourceKey;";

        var count = (int)command.ExecuteScalar()!;
        // The minimal fixture defines Widget and Gadget
        count.Should().BeGreaterThanOrEqualTo(2, "ResourceKey should have rows for Widget and Gadget");
    }

    [Test]
    public void It_configures_read_committed_snapshot_isolation()
    {
        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("name", _databaseName);

        var result = command.ExecuteScalar();
        Convert.ToBoolean(result).Should().BeTrue("RCSI should be enabled on newly created databases");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Provisioning_Rerun_On_Same_Database
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;
    private int _firstResourceKeyCount;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        var fixturePath = CliTestHelper.GetMinimalFixturePath();

        // First provisioning run
        (_firstExitCode, _, _) = CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            fixturePath,
            "--connection-string",
            connectionString,
            "--dialect",
            "mssql",
            "--create-database"
        );

        // Capture resource key count after first run
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.ResourceKey;";
        _firstResourceKeyCount = (int)command.ExecuteScalar()!;

        // Second provisioning run (idempotent rerun)
        (_secondExitCode, _secondOutput, _secondError) = CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            fixturePath,
            "--connection-string",
            connectionString,
            "--dialect",
            "mssql",
            "--create-database"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_exit_code_0_on_first_run()
    {
        _firstExitCode.Should().Be(0);
    }

    [Test]
    public void It_returns_exit_code_0_on_second_run()
    {
        _secondExitCode.Should().Be(0, $"stdout: {_secondOutput}\nstderr: {_secondError}");
    }

    [Test]
    public void It_still_has_exactly_one_effective_schema_row()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.EffectiveSchema;";

        var count = (int)command.ExecuteScalar()!;
        count.Should().Be(1, "rerun should not duplicate the EffectiveSchema row");
    }

    [Test]
    public void It_has_the_same_resource_key_count()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.ResourceKey;";

        var count = (int)command.ExecuteScalar()!;
        count.Should().Be(_firstResourceKeyCount, "rerun should not duplicate ResourceKey rows");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Provisioning_Without_Create_Database_Against_Existing_Empty_Db
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);

        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        var fixturePath = CliTestHelper.GetMinimalFixturePath();

        (_exitCode, _output, _error) = CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            fixturePath,
            "--connection-string",
            connectionString,
            "--dialect",
            "mssql"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_exit_code_0()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_creates_core_tables()
    {
        var expectedTables = new[]
        {
            "Document",
            "ResourceKey",
            "Descriptor",
            "ReferentialIdentity",
            "EffectiveSchema",
            "SchemaComponent",
            "DocumentCache",
            "DocumentChangeEvent",
        };

        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        foreach (var table in expectedTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = @table;";
            command.Parameters.AddWithValue("table", table);

            var result = command.ExecuteScalar();
            result.Should().NotBeNull($"table dms.[{table}] should exist");
        }
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Provisioning_Without_Create_Database_Against_Missing_Db
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        var fixturePath = CliTestHelper.GetMinimalFixturePath();

        // Do NOT create the database — run without --create-database
        (_exitCode, _output, _error) = CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            fixturePath,
            "--connection-string",
            connectionString,
            "--dialect",
            "mssql"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (MssqlTestDatabaseHelper.IsConfigured())
        {
            // Database may not exist, but clean up just in case
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_nonzero_exit_code()
    {
        _exitCode.Should().NotBe(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_prints_an_error_message()
    {
        var combinedOutput = _output + _error;
        combinedOutput.Should().NotBeNullOrEmpty("an error message should be printed");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Create_Database_Flag_With_Existing_Database
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);

        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        var fixturePath = CliTestHelper.GetMinimalFixturePath();

        (_exitCode, _output, _error) = CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            fixturePath,
            "--connection-string",
            connectionString,
            "--dialect",
            "mssql",
            "--create-database"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_exit_code_0()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_seeds_effective_schema_row()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dms.EffectiveSchema;";

        var count = (int)command.ExecuteScalar()!;
        count.Should().Be(1, "there should be exactly one EffectiveSchema row");
    }
}
