// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_A_Fresh_Database_Provisioned_With_Create_Database_Flag
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
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
        using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name;";
        command.Parameters.AddWithValue("name", _databaseName);

        var result = command.ExecuteScalar();
        result.Should().NotBeNull("the database should exist in pg_database");
    }

    [Test]
    public void It_creates_the_dms_schema()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'dms';";

        var result = command.ExecuteScalar();
        result.Should().NotBeNull("the dms schema should exist");
    }

    [Test]
    public void It_creates_core_tables()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertCoreTablesExist(connection);
    }

    [Test]
    public void It_seeds_effective_schema_row()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertEffectiveSchemaSeeded(connection, "pgsql");
    }

    [Test]
    public void It_seeds_schema_component_rows()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertSchemaComponentsSeeded(connection, "pgsql");
    }

    [Test]
    public void It_seeds_resource_key_rows()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertResourceKeysSeeded(connection, "pgsql", 2);
    }

    [Test]
    public void It_creates_the_change_version_sequence()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM pg_sequences WHERE schemaname = 'dms' AND sequencename = 'ChangeVersionSequence';";

        var result = command.ExecuteScalar();
        result.Should().NotBeNull("the ChangeVersionSequence should exist");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_Provisioning_Rerun_On_Same_Database
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
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _, _) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );

        // Capture resource key count after first run
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        _firstResourceKeyCount = ProvisionTestHelper.GetDmsTableCount(connection, "pgsql", "ResourceKey");

        // Second provisioning run (idempotent rerun)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
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
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "EffectiveSchema")
            .Should()
            .Be(1, "rerun should not duplicate the EffectiveSchema row");
    }

    [Test]
    public void It_has_the_same_resource_key_count()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "ResourceKey")
            .Should()
            .Be(_firstResourceKeyCount, "rerun should not duplicate ResourceKey rows");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_Provisioning_Without_Create_Database_Against_Existing_Empty_Db
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        PostgresTestDatabaseHelper.CreateDatabase(_databaseName);

        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision("pgsql", connectionString);
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_returns_exit_code_0()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_creates_core_tables()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();
        ProvisionTestHelper.AssertCoreTablesExist(connection);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_Provisioning_Without_Create_Database_Against_Missing_Db
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // Do NOT create the database — run without --create-database
        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision("pgsql", connectionString);
    }

    [TearDown]
    public void TearDown()
    {
        // Database may not exist, but clean up just in case
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
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
public class Given_Create_Database_Flag_With_Existing_Database
{
    private string _databaseName = null!;
    private int _exitCode;
    private string _output = null!;
    private string _error = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        PostgresTestDatabaseHelper.CreateDatabase(_databaseName);

        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_returns_exit_code_0()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_seeds_effective_schema_row()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "EffectiveSchema")
            .Should()
            .Be(1, "there should be exactly one EffectiveSchema row");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_Schema_Hash_Mismatch_On_Provisioning
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private int _secondExitCode;
    private string _secondError = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run with schema A (minimal)
        var fixturePathA = CliTestHelper.GetMinimalFixturePath();
        (_firstExitCode, _firstOutput, _) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            fixturePathA,
            createDatabase: true
        );

        // Second provisioning run with schema B (alternate minimal)
        var fixturePathB = CliTestHelper.GetAlternateMinimalFixturePath();
        (_secondExitCode, _, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            fixturePathB
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
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
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "EffectiveSchema")
            .Should()
            .Be(1, "the preflight check should prevent any additional rows");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_ResourceKey_Tampered_After_Provisioning
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private string _firstError = null!;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;
    private const string TamperedProjectName = "TamperedProject";

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );

        // Tamper with a ResourceKey row
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dms."ResourceKey"
                SET "ProjectName" = 'TamperedProject'
                WHERE "ResourceKeyId" = (SELECT MIN("ResourceKeyId") FROM dms."ResourceKey")
                """;
            var rowsAffected = command.ExecuteNonQuery();
            if (rowsAffected == 0)
            {
                throw new InvalidOperationException("Test setup failed: no ResourceKey rows to tamper with");
            }
        }

        // Second provisioning run (should detect tampering)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_succeeds_on_first_provisioning()
    {
        _firstExitCode.Should().Be(0, $"stdout: {_firstOutput}\nstderr: {_firstError}");
    }

    [Test]
    public void It_returns_nonzero_exit_code_on_tampered_rerun()
    {
        _secondExitCode.Should().NotBe(0, "provisioning with tampered ResourceKey should fail");
    }

    [Test]
    public void It_reports_seed_data_mismatch_in_stderr()
    {
        _secondError.Should().Contain("ResourceKey", "stderr should mention the affected table");
    }

    [Test]
    public void It_includes_row_level_diff_in_stderr()
    {
        _secondError.Should().Contain("ProjectName", "stderr should identify the tampered column");
    }

    [Test]
    public void It_includes_the_tampered_value_in_stderr()
    {
        _secondError.Should().Contain(TamperedProjectName, "stderr should show the tampered value");
    }

    [Test]
    public void It_still_has_the_tampered_row_in_database()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM dms."ResourceKey"
            WHERE "ProjectName" = 'TamperedProject'
            """;
        var count = Convert.ToInt64(command.ExecuteScalar());
        count.Should().Be(1, "preflight should stop before DDL execution, leaving tampered row intact");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_SchemaComponent_Tampered_After_Provisioning
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private string _firstError = null!;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;
    private const string TamperedProjectName = "TamperedProject";

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );

        // Tamper with a SchemaComponent row
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dms."SchemaComponent"
                SET "ProjectName" = 'TamperedProject'
                WHERE "ProjectEndpointName" = (SELECT MIN("ProjectEndpointName") FROM dms."SchemaComponent")
                """;
            var rowsAffected = command.ExecuteNonQuery();
            if (rowsAffected == 0)
            {
                throw new InvalidOperationException(
                    "Test setup failed: no SchemaComponent rows to tamper with"
                );
            }
        }

        // Second provisioning run (should detect tampering)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_succeeds_on_first_provisioning()
    {
        _firstExitCode.Should().Be(0, $"stdout: {_firstOutput}\nstderr: {_firstError}");
    }

    [Test]
    public void It_returns_nonzero_exit_code_on_tampered_rerun()
    {
        _secondExitCode.Should().NotBe(0, "provisioning with tampered SchemaComponent should fail");
    }

    [Test]
    public void It_reports_seed_data_mismatch_in_stderr()
    {
        _secondError.Should().Contain("SchemaComponent", "stderr should mention the affected table");
    }

    [Test]
    public void It_includes_row_level_diff_in_stderr()
    {
        _secondError.Should().Contain("ProjectName", "stderr should identify the tampered column");
    }

    [Test]
    public void It_includes_the_tampered_value_in_stderr()
    {
        _secondError.Should().Contain(TamperedProjectName, "stderr should show the tampered value");
    }

    [Test]
    public void It_still_has_the_tampered_row_in_database()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM dms."SchemaComponent"
            WHERE "ProjectName" = 'TamperedProject'
            """;
        var count = Convert.ToInt64(command.ExecuteScalar());
        count.Should().Be(1, "preflight should stop before DDL execution, leaving tampered row intact");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_ResourceKey_Table_Dropped_After_Provisioning
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private string _firstError = null!;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );

        // Drop the ResourceKey table (CASCADE removes dependent FKs from Document, etc.)
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """DROP TABLE dms."ResourceKey" CASCADE""";
            command.ExecuteNonQuery();
        }

        // Second provisioning run — should detect the missing table
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_succeeds_on_first_provisioning()
    {
        _firstExitCode.Should().Be(0, $"stdout: {_firstOutput}\nstderr: {_firstError}");
    }

    [Test]
    public void It_returns_nonzero_exit_code()
    {
        _secondExitCode.Should().NotBe(0, $"stdout: {_secondOutput}\nstderr: {_secondError}");
    }

    [Test]
    public void It_reports_missing_seed_table_in_stderr()
    {
        _secondError.Should().Contain("required seed table(s) are missing");
    }

    [Test]
    public void It_names_the_missing_table_in_stderr()
    {
        _secondError.Should().Contain("ResourceKey");
    }

    [Test]
    public void It_recommends_drop_and_recreate()
    {
        _secondError.Should().Contain("Drop and recreate");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_SchemaComponent_Table_Dropped_After_Provisioning
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private string _firstError = null!;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );

        // Drop the SchemaComponent table (no inbound FKs, simple drop suffices)
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE dms.\"SchemaComponent\"";
            command.ExecuteNonQuery();
        }

        // Second provisioning run — should detect the missing table
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_succeeds_on_first_provisioning()
    {
        _firstExitCode.Should().Be(0, $"stdout: {_firstOutput}\nstderr: {_firstError}");
    }

    [Test]
    public void It_returns_nonzero_exit_code()
    {
        _secondExitCode.Should().NotBe(0, $"stdout: {_secondOutput}\nstderr: {_secondError}");
    }

    [Test]
    public void It_reports_missing_seed_table_in_stderr()
    {
        _secondError.Should().Contain("required seed table(s) are missing");
    }

    [Test]
    public void It_names_the_missing_table_in_stderr()
    {
        _secondError.Should().Contain("SchemaComponent");
    }

    [Test]
    public void It_recommends_drop_and_recreate()
    {
        _secondError.Should().Contain("Drop and recreate");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
public class Given_EffectiveSchema_Table_Exists_But_Singleton_Row_Missing
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private string _firstOutput = null!;
    private string _firstError = null!;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run — creates tables and seeds data
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );

        // Delete the singleton row to simulate partial/corrupt state
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """DELETE FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1""";
            command.ExecuteNonQuery();
        }

        // Second provisioning run — should detect the missing row
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString
        );
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_succeeds_on_first_provisioning()
    {
        _firstExitCode.Should().Be(0, $"stdout: {_firstOutput}\nstderr: {_firstError}");
    }

    [Test]
    public void It_returns_nonzero_exit_code()
    {
        _secondExitCode.Should().NotBe(0, $"stdout: {_secondOutput}\nstderr: {_secondError}");
    }

    [Test]
    public void It_reports_partial_provisioning_state_in_stderr()
    {
        _secondError.Should().Contain("partial or corrupt provisioning state");
    }

    [Test]
    public void It_recommends_drop_and_recreate()
    {
        _secondError.Should().Contain("Drop and recreate");
    }

    [Test]
    public void It_does_not_reinsert_the_singleton_row()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "EffectiveSchema")
            .Should()
            .Be(0, "preflight should stop before DDL execution, leaving the table empty");
    }
}
