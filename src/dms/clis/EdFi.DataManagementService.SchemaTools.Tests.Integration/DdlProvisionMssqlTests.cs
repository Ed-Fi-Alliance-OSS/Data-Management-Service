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

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
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
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertCoreTablesExist(connection);
    }

    [Test]
    public void It_seeds_effective_schema_row()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertEffectiveSchemaSeeded(connection, "mssql");
    }

    [Test]
    public void It_seeds_schema_component_rows()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertSchemaComponentsSeeded(connection, "mssql");
    }

    [Test]
    public void It_seeds_resource_key_rows()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertResourceKeysSeeded(connection, "mssql", 2);
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
    private long _firstResourceKeyCount;

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

        // First provisioning run
        (_firstExitCode, _, _) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );

        // Capture resource key count after first run
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        _firstResourceKeyCount = ProvisionTestHelper.GetDmsTableCount(connection, "mssql", "ResourceKey");

        // Second provisioning run (idempotent rerun)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
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

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "EffectiveSchema")
            .Should()
            .Be(1, "rerun should not duplicate the EffectiveSchema row");
    }

    [Test]
    public void It_has_the_same_resource_key_count()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "ResourceKey")
            .Should()
            .Be(_firstResourceKeyCount, "rerun should not duplicate ResourceKey rows");
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

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision("mssql", connectionString);
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
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertCoreTablesExist(connection);
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

        // Do NOT create the database — run without --create-database
        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision("mssql", connectionString);
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

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
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

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "EffectiveSchema")
            .Should()
            .Be(1, "there should be exactly one EffectiveSchema row");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Schema_Hash_Mismatch_On_Provisioning
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private int _secondExitCode;
    private string _secondError = null!;

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

        // First provisioning run with schema A (minimal)
        var fixturePathA = CliTestHelper.GetMinimalFixturePath();
        (_firstExitCode, _firstOutput, _) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            fixturePathA,
            createDatabase: true
        );

        // Second provisioning run with schema B (alternate minimal)
        var fixturePathB = CliTestHelper.GetAlternateMinimalFixturePath();
        (_secondExitCode, _, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            fixturePathB
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
    public void It_succeeds_on_first_provisioning()
    {
        _firstExitCode.Should().Be(0, $"first provisioning should succeed; stdout: {_firstOutput}");
    }

    [Test]
    public void It_returns_nonzero_exit_code_on_mismatch()
    {
        _secondExitCode.Should().NotBe(0, "provisioning with a different schema should fail");
    }

    [Test]
    public void It_reports_schema_hash_mismatch_in_stderr()
    {
        _secondError.Should().Contain("Schema hash mismatch");
    }

    [Test]
    public void It_includes_the_stored_hash_in_error_output()
    {
        var hashA = ProvisionTestHelper.ExtractHashFromOutput(_firstOutput);
        hashA.Should().NotBeNullOrEmpty("should be able to extract hash from first run output");

        _secondError.Should().Contain(hashA!, "stderr should include the hash stored in the database");
    }

    [Test]
    public void It_includes_the_expected_hash_in_error_output()
    {
        _secondError.Should().Contain("but the current schema produces hash");
    }

    [Test]
    public void It_does_not_create_additional_effective_schema_rows()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "EffectiveSchema")
            .Should()
            .Be(1, "the preflight check should prevent any additional rows");
    }
}
