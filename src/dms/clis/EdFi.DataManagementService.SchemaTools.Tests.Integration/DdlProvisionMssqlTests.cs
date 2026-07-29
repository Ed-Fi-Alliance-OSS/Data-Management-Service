// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.SchemaTools.Introspection;
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
    public void It_initializes_document_cache_mutable_singletons()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper.AssertDocumentCacheStateSeeded(connection, "mssql");
    }

    [Test]
    public void It_rejects_invalid_document_cache_lifecycle_values()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper.AssertDocumentCacheLifecycleRejectsInvalidValues(connection, "mssql");
    }

    [Test]
    public void It_configures_lifecycle_collation_and_same_owner_enqueue_trigger()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.collation_name
                FROM sys.columns c
                INNER JOIN sys.tables t ON t.object_id = c.object_id
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = N'dms'
                AND t.name = N'DocumentCacheState'
                AND c.name = N'ProjectionLifecycleState'
                """;
            command.ExecuteScalar().Should().Be("Latin1_General_100_BIN2");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    OBJECT_SCHEMA_NAME(tr.object_id) AS trigger_schema,
                    SCHEMA_NAME(parent_schema.schema_id) AS parent_schema,
                    module.execute_as_principal_id,
                    module.definition
                FROM sys.triggers tr
                INNER JOIN sys.tables parent_table ON parent_table.object_id = tr.parent_id
                INNER JOIN sys.schemas parent_schema ON parent_schema.schema_id = parent_table.schema_id
                INNER JOIN sys.sql_modules module ON module.object_id = tr.object_id
                WHERE tr.name = N'TR_Document_EnqueueProjectionWork'
                """;
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue("the enqueue trigger should exist");
            reader.GetString(0).Should().Be("dms");
            reader.GetString(1).Should().Be("dms");
            reader.IsDBNull(2).Should().BeTrue("the trigger should not use EXECUTE AS");
            reader.GetString(3).Should().NotContain("EXECUTE AS");
        }
    }

    [Test]
    public void It_creates_the_uuidv5_function()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(N'dms.uuidv5', N'FN');";
        var result = command.ExecuteScalar();
        result.Should().NotBeNull("the dms.uuidv5 function should exist after provisioning");
        result.Should().NotBe(DBNull.Value, "the dms.uuidv5 function should exist after provisioning");
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
    private string? _firstManifestJson;
    private string? _secondManifestJson;
    private ProvisionTestHelper.DocumentCacheMutableStateSnapshot? _beforeRerun;
    private ProvisionTestHelper.DocumentCacheMutableStateSnapshot? _afterRerun;

    [OneTimeSetUp]
    public void OneTimeSetUp()
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
            CliTestHelper.GetAuthoritativeSchemaPaths(),
            createDatabase: true
        );

        // Introspect after first run
        var schemaAllowlist = ProvisionTestHelper.DiscoverProvisionedSchemasMssql(connectionString);
        var introspector = new MssqlSchemaIntrospector();
        var firstManifest = introspector.Introspect(connectionString, schemaAllowlist);
        _firstManifestJson = ProvisionedSchemaManifestEmitter.Emit(firstManifest);

        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            ProvisionTestHelper.InsertRowsThatMustSurviveRerun(connection, "mssql");
            _beforeRerun = ProvisionTestHelper.ReadDocumentCacheMutableStateSnapshot(connection, "mssql");
        }

        // Second provisioning run (idempotent rerun)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            CliTestHelper.GetAuthoritativeSchemaPaths(),
            createDatabase: true
        );

        // Introspect after second run (rediscover schemas to catch accidental new schemas)
        var secondSchemaAllowlist = ProvisionTestHelper.DiscoverProvisionedSchemasMssql(connectionString);
        var secondManifest = introspector.Introspect(connectionString, secondSchemaAllowlist);
        _secondManifestJson = ProvisionedSchemaManifestEmitter.Emit(secondManifest);

        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            _afterRerun = ProvisionTestHelper.ReadDocumentCacheMutableStateSnapshot(connection, "mssql");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
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
    public void It_produces_identical_schema_manifests_on_both_runs()
    {
        _secondManifestJson
            .Should()
            .Be(
                _firstManifestJson,
                "the schema manifest after the second provisioning should be identical to the first"
            );
    }

    [Test]
    public void It_preserves_document_cache_mutable_rows_on_second_run()
    {
        _afterRerun.Should().Be(_beforeRerun);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Bounded_E18_Preflight_Guards
{
    [TestCase(
        """DROP TABLE [dms].[DataStoreIdentity];""",
        "DataStoreIdentity] is missing",
        TestName = "It_rejects_missing_DataStoreIdentity_table"
    )]
    [TestCase(
        """DELETE FROM [dms].[DataStoreIdentity] WHERE [DataStoreIdentitySingletonId] = 1;""",
        "DataStoreIdentity singleton row is missing",
        TestName = "It_rejects_missing_DataStoreIdentity_singleton"
    )]
    [TestCase(
        """UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = '00000000-0000-0000-0000-000000000000' WHERE [DataStoreIdentitySingletonId] = 1;""",
        "SourceIdentity must not be the zero UUID",
        TestName = "It_rejects_zero_SourceIdentity"
    )]
    [TestCase(
        """DROP TABLE [dms].[DocumentCacheState];""",
        "DocumentCacheState] is missing",
        TestName = "It_rejects_missing_DocumentCacheState_table"
    )]
    [TestCase(
        """DELETE FROM [dms].[DocumentCacheState] WHERE [StateId] = 1;""",
        "DocumentCacheState singleton row is missing",
        TestName = "It_rejects_missing_DocumentCacheState_singleton"
    )]
    [TestCase(
        """
            ALTER TABLE [dms].[DocumentCacheState] DROP CONSTRAINT [CK_DocumentCacheState_Lifecycle];
            UPDATE [dms].[DocumentCacheState] SET [ProjectionLifecycleState] = 'Broken' WHERE [StateId] = 1;
            """,
        "ProjectionLifecycleState has unsupported value",
        TestName = "It_rejects_invalid_DocumentCacheState_lifecycle"
    )]
    [TestCase(
        """ALTER TABLE [dms].[DocumentCache] ADD [Etag] nvarchar(64) NULL;""",
        "Known legacy DocumentCache artifact",
        TestName = "It_rejects_legacy_DocumentCache_Etag"
    )]
    [TestCase(
        """CREATE UNIQUE INDEX [UX_DocumentCache_DocumentUuid] ON [dms].[DocumentCache] ([DocumentUuid]);""",
        "UX_DocumentCache_DocumentUuid",
        TestName = "It_rejects_legacy_DocumentCache_uuid_index"
    )]
    [TestCase(
        """CREATE INDEX [IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt] ON [dms].[DocumentCache] ([ProjectName], [ResourceName], [LastModifiedAt]);""",
        "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt",
        TestName = "It_rejects_legacy_DocumentCache_scan_index"
    )]
    public void It_rejects_incompatible_completed_state_before_rerun(string mutationSql, string expectedError)
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(databaseName);

        try
        {
            var (firstExitCode, firstOutput, firstError) = ProvisionTestHelper.RunProvision(
                "mssql",
                connectionString,
                createDatabase: true
            );
            firstExitCode.Should().Be(0, $"stdout: {firstOutput}\nstderr: {firstError}");

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = mutationSql;
                command.ExecuteNonQuery();
            }

            var (secondExitCode, secondOutput, secondError) = ProvisionTestHelper.RunProvision(
                "mssql",
                connectionString
            );

            secondExitCode.Should().NotBe(0, $"stdout: {secondOutput}\nstderr: {secondError}");
            secondError.Should().Contain(expectedError);
        }
        finally
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
        }
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_Compatible_Interrupted_Initial_Apply
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
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE SCHEMA [dms];";
            command.ExecuteNonQuery();

            command.CommandText =
                "CREATE SEQUENCE [dms].[ChangeVersionSequence] AS bigint START WITH 1 INCREMENT BY 1;";
            command.ExecuteNonQuery();
        }

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
    public void It_completes_successfully()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_creates_and_seeds_document_cache_state()
    {
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper.AssertCoreTablesExist(connection);
        ProvisionTestHelper.AssertDocumentCacheStateSeeded(connection, "mssql");
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
        var schemaPathA = CliTestHelper.GetMinimalSchemaPath();
        (_firstExitCode, _firstOutput, _) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            [schemaPathA],
            createDatabase: true
        );

        // Second provisioning run with schema B (alternate minimal)
        var schemaPathB = CliTestHelper.GetAlternateMinimalSchemaPath();
        (_secondExitCode, _, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            [schemaPathB]
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
        _secondError
            .Should()
            .Contain(
                "EffectiveSchemaHash: stored=",
                "stderr should report the stored and expected EffectiveSchemaHash values"
            );
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
        var hashB = ProvisionTestHelper.ExtractHashFromOutput(_secondError);
        hashB.Should().NotBeNullOrEmpty("should be able to extract hash from second run output");

        _secondError
            .Should()
            .Contain($"expected={hashB!}", "stderr should include the hash produced by the current schema");
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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_ResourceKey_Tampered_After_Provisioning_Mssql
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );

        // Tamper with a ResourceKey row
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE [dms].[ResourceKey]
                SET [ProjectName] = 'TamperedProject'
                WHERE [ResourceKeyId] = (SELECT MIN([ResourceKeyId]) FROM [dms].[ResourceKey])
                """;
            var rowsAffected = command.ExecuteNonQuery();
            if (rowsAffected == 0)
            {
                throw new InvalidOperationException("Test setup failed: no ResourceKey rows to tamper with");
            }
        }

        // Second provisioning run (should detect tampering)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString
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
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM [dms].[ResourceKey]
            WHERE [ProjectName] = 'TamperedProject'
            """;
        var count = Convert.ToInt64(command.ExecuteScalar());
        count.Should().Be(1, "preflight should stop before DDL execution, leaving tampered row intact");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_SchemaComponent_Tampered_After_Provisioning_Mssql
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );

        // Tamper with a SchemaComponent row
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE [dms].[SchemaComponent]
                SET [ProjectName] = 'TamperedProject'
                WHERE [ProjectEndpointName] = (SELECT MIN([ProjectEndpointName]) FROM [dms].[SchemaComponent])
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
            "mssql",
            connectionString
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
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM [dms].[SchemaComponent]
            WHERE [ProjectName] = 'TamperedProject'
            """;
        var count = Convert.ToInt64(command.ExecuteScalar());
        count.Should().Be(1, "preflight should stop before DDL execution, leaving tampered row intact");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_ResourceKey_Table_Dropped_After_Provisioning
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );

        // Drop FK constraints that reference ResourceKey, then drop the table
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE [dms].[Descriptor] DROP CONSTRAINT [FK_Descriptor_ResourceKey];
                ALTER TABLE [dms].[Document] DROP CONSTRAINT [FK_Document_ResourceKey];
                ALTER TABLE [dms].[ReferentialIdentity] DROP CONSTRAINT [FK_ReferentialIdentity_ResourceKey];
                DROP TABLE [dms].[ResourceKey];
                """;
            command.ExecuteNonQuery();
        }

        // Second provisioning run — should detect the missing table
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString
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
[Category("MssqlIntegration")]
public class Given_Mssql_SchemaComponent_Table_Dropped_After_Provisioning
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );

        // Drop the SchemaComponent table (no inbound FKs reference it)
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE [dms].[SchemaComponent];";
            command.ExecuteNonQuery();
        }

        // Second provisioning run — should detect the missing table
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString
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
[Category("MssqlIntegration")]
public class Given_Mssql_EffectiveSchema_Table_Exists_But_Singleton_Row_Missing
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run — creates tables and seeds data
        (_firstExitCode, _firstOutput, _firstError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );

        // Delete the singleton row to simulate partial/corrupt state
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM [dms].[EffectiveSchema] WHERE [EffectiveSchemaSingletonId] = 1";
            command.ExecuteNonQuery();
        }

        // Second provisioning run — should detect the missing row
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString
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
        using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "EffectiveSchema")
            .Should()
            .Be(0, "preflight should stop before DDL execution, leaving the table empty");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_DocumentProjectionLifecycle
{
    private string _databaseName = null!;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlDocumentProjectionTestSupport.ProvisionFreshDatabase();
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
    public void It_records_work_only_for_enabled_lifecycle_states_and_preserves_enqueue_timestamps()
    {
        using var connection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName);

        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Disabled");
        var disabledDocuments = MssqlDocumentProjectionTestSupport.InsertDocuments(connection, 2);
        MssqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, disabledDocuments.Select(d => d.DocumentId))
            .Should()
            .BeEmpty("Disabled lifecycle should not enqueue direct document inserts");

        MssqlDocumentProjectionTestSupport.AdvanceDocumentContentVersions(
            connection,
            disabledDocuments.Select(d => d.DocumentId)
        );
        MssqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, disabledDocuments.Select(d => d.DocumentId))
            .Should()
            .BeEmpty("Disabled lifecycle should not enqueue direct document updates");

        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Resetting");
        var resettingDocuments = MssqlDocumentProjectionTestSupport.InsertDocuments(connection, 2);
        var resettingWork = MssqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            resettingDocuments.Select(d => d.DocumentId)
        );
        MssqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(resettingWork, resettingDocuments);
        MssqlDocumentProjectionTestSupport.AssertSingleStatementInsertTimestamps(resettingWork);

        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Rebuilding");
        var resettingWorkBeforeUpdate = resettingWork.ToDictionary(row => row.DocumentId);
        var rebuiltDocuments = MssqlDocumentProjectionTestSupport.AdvanceDocumentContentVersions(
            connection,
            resettingDocuments.Select(d => d.DocumentId)
        );
        var rebuiltWork = MssqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            resettingDocuments.Select(d => d.DocumentId)
        );
        MssqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(rebuiltWork, rebuiltDocuments);
        rebuiltWork
            .Select(row => row.LastEnqueuedAt)
            .Distinct()
            .Should()
            .ContainSingle("one SYSUTCDATETIME() value should be shared by the update statement");
        foreach (var row in rebuiltWork)
        {
            row.FirstEnqueuedAt.Should()
                .Be(
                    resettingWorkBeforeUpdate[row.DocumentId].FirstEnqueuedAt,
                    "advancing work should preserve FirstEnqueuedAt"
                );
        }

        var workBeforeNonAdvancingUpdate = MssqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [resettingDocuments[0].DocumentId])
            .Single();
        MssqlDocumentProjectionTestSupport.LowerDocumentContentVersion(
            connection,
            resettingDocuments[0].DocumentId
        );
        MssqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [resettingDocuments[0].DocumentId])
            .Single()
            .Should()
            .Be(workBeforeNonAdvancingUpdate, "non-advancing requirements should not touch work rows");

        MssqlDocumentProjectionTestSupport.TouchDocumentWithoutContentVersionChange(
            connection,
            resettingDocuments[0].DocumentId
        );
        MssqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [resettingDocuments[0].DocumentId])
            .Single()
            .Should()
            .Be(workBeforeNonAdvancingUpdate, "unchanged ContentVersion updates should not touch work rows");

        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");
        var trackingDocuments = MssqlDocumentProjectionTestSupport.InsertDocuments(connection, 2);
        var trackingInsertWork = MssqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            trackingDocuments.Select(d => d.DocumentId)
        );
        MssqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(trackingInsertWork, trackingDocuments);
        MssqlDocumentProjectionTestSupport.AssertSingleStatementInsertTimestamps(trackingInsertWork);

        var trackedDocuments = MssqlDocumentProjectionTestSupport.AdvanceDocumentContentVersions(
            connection,
            trackingDocuments.Select(d => d.DocumentId)
        );
        var trackingUpdateWork = MssqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            trackingDocuments.Select(d => d.DocumentId)
        );
        MssqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(trackingUpdateWork, trackedDocuments);
        trackingUpdateWork
            .Select(row => row.LastEnqueuedAt)
            .Distinct()
            .Should()
            .ContainSingle("one SYSUTCDATETIME() value should be shared by the tracking update");
    }

    [Test]
    public void It_enqueues_when_a_generated_resource_stamp_advances_document_content_version()
    {
        MssqlDocumentProjectionTestSupport.AssertNestedTriggersEnabled();

        using var connection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        MssqlDocumentProjectionTestSupport.AssertWidgetStampTriggerDoesNotReadServerConfiguration(connection);
        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");

        var documentId = MssqlDocumentProjectionTestSupport.InsertWidget(connection, 1001, "before");
        var initialDocument = MssqlDocumentProjectionTestSupport.ReadDocumentRow(connection, documentId);
        var initialWork = MssqlDocumentProjectionTestSupport.ReadWorkRows(connection, [documentId]).Single();

        MssqlDocumentProjectionTestSupport.UpdateWidgetName(connection, documentId, "after");

        var updatedDocument = MssqlDocumentProjectionTestSupport.ReadDocumentRow(connection, documentId);
        updatedDocument
            .ContentVersion.Should()
            .BeGreaterThan(
                initialDocument.ContentVersion,
                "the generated resource stamp trigger should advance dms.Document.ContentVersion"
            );

        MssqlDocumentProjectionTestSupport
            .ReadWidgetContentVersion(connection, documentId)
            .Should()
            .Be(
                updatedDocument.ContentVersion,
                "the generated resource table should mirror the stamped content version"
            );

        var updatedWork = MssqlDocumentProjectionTestSupport.ReadWorkRows(connection, [documentId]).Single();
        updatedWork.RequiredContentVersion.Should().Be(updatedDocument.ContentVersion);
        updatedWork.FirstEnqueuedAt.Should().Be(initialWork.FirstEnqueuedAt);
        updatedWork.LastEnqueuedAt.Should().BeOnOrAfter(initialWork.LastEnqueuedAt);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_Mssql_DocumentProjectionSafety
{
    private string _databaseName = null!;

    [SetUp]
    public void SetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _databaseName = MssqlDocumentProjectionTestSupport.ProvisionFreshDatabase();
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
    public void It_rolls_back_document_dml_when_lifecycle_singleton_is_missing()
    {
        using var connection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        var initialDocumentCount = ProvisionTestHelper.GetDmsTableCount(connection, "mssql", "Document");
        var initialWorkCount = ProvisionTestHelper.GetDmsTableCount(
            connection,
            "mssql",
            "DocumentProjectionWork"
        );

        MssqlDocumentProjectionTestSupport.ExecuteNonQuery(
            connection,
            "DELETE FROM [dms].[DocumentCacheState] WHERE [StateId] = 1"
        );

        using var transaction = connection.BeginTransaction();
        using var command = MssqlDocumentProjectionTestSupport.CreateInsertDocumentCommand(connection);
        command.Transaction = transaction;

        Action insertDocument = () => command.ExecuteNonQuery();
        var exception = insertDocument.Should().Throw<SqlException>().Which;
        exception
            .Message.Should()
            .Contain("DocumentCacheState singleton row is missing", "the native diagnostic should be useful");
        transaction.Rollback();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "Document")
            .Should()
            .Be(initialDocumentCount, "the failed canonical document insert should roll back");
        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "DocumentProjectionWork")
            .Should()
            .Be(initialWorkCount, "the failed enqueue should not leave work rows behind");
    }

    [Test]
    public void It_rolls_back_document_dml_when_lifecycle_state_is_padded_disabled()
    {
        using var connection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        var initialDocumentCount = ProvisionTestHelper.GetDmsTableCount(connection, "mssql", "Document");
        var initialWorkCount = ProvisionTestHelper.GetDmsTableCount(
            connection,
            "mssql",
            "DocumentProjectionWork"
        );

        MssqlDocumentProjectionTestSupport.BypassLifecycleConstraintAndSetLifecycle(connection, "Disabled ");

        using var transaction = connection.BeginTransaction();
        using var command = MssqlDocumentProjectionTestSupport.CreateInsertDocumentCommand(connection);
        command.Transaction = transaction;

        Action insertDocument = () => command.ExecuteNonQuery();
        var exception = insertDocument.Should().Throw<SqlException>().Which;
        exception
            .Message.Should()
            .Contain(
                "ProjectionLifecycleState has unsupported value",
                "padded lifecycle values must not be treated as exact Disabled"
            );
        transaction.Rollback();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "Document")
            .Should()
            .Be(initialDocumentCount, "the failed canonical document insert should roll back");
        ProvisionTestHelper
            .GetDmsTableCount(connection, "mssql", "DocumentProjectionWork")
            .Should()
            .Be(initialWorkCount, "the failed enqueue should not leave work rows behind");
    }

    [Test]
    public void It_rolls_back_document_dml_when_enqueue_work_write_fails()
    {
        using var connection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");
        MssqlDocumentProjectionTestSupport.InstallForcedWorkFailureConstraint(connection);

        var initialDocumentCount = ProvisionTestHelper.GetDmsTableCount(connection, "mssql", "Document");
        var initialWorkCount = ProvisionTestHelper.GetDmsTableCount(
            connection,
            "mssql",
            "DocumentProjectionWork"
        );

        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = MssqlDocumentProjectionTestSupport.CreateInsertDocumentCommand(connection);
            command.Transaction = transaction;

            Action insertDocument = () => command.ExecuteNonQuery();
            var exception = insertDocument.Should().Throw<SqlException>().Which;
            exception
                .Message.Should()
                .Contain(
                    "CK_Test_ForceDocumentProjectionWorkFailure",
                    "the native diagnostic should identify the forced work-table failure"
                );
            transaction.Rollback();

            ProvisionTestHelper
                .GetDmsTableCount(connection, "mssql", "Document")
                .Should()
                .Be(initialDocumentCount, "the failed canonical document insert should roll back");
            ProvisionTestHelper
                .GetDmsTableCount(connection, "mssql", "DocumentProjectionWork")
                .Should()
                .Be(initialWorkCount, "the failed work-table write should roll back");
        }
        finally
        {
            MssqlDocumentProjectionTestSupport.DropForcedWorkFailureConstraint(connection);
        }
    }

    [Test]
    public void It_enforces_cache_constraints_and_cascades_cache_and_work_rows()
    {
        using var connection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        MssqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");

        var documentId = MssqlDocumentProjectionTestSupport
            .InsertDocuments(connection, 1)
            .Single()
            .DocumentId;
        MssqlDocumentProjectionTestSupport.InsertDocumentCache(
            connection,
            documentId,
            """{"id":"matching"}"""
        );

        MssqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                $"UPDATE [dms].[DocumentCache] SET [DocumentUuid] = NEWID() WHERE [DocumentId] = {documentId}"
            )
            .Message.Should()
            .Contain("DocumentUuid diverges");

        var invalidJsonDocumentId = MssqlDocumentProjectionTestSupport
            .InsertDocuments(connection, 1)
            .Single()
            .DocumentId;
        MssqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                MssqlDocumentProjectionTestSupport.BuildDocumentCacheInsertSql(invalidJsonDocumentId, "[]")
            )
            .Message.Should()
            .Contain("CK_DocumentCache_IsJsonObject", "DocumentCache.DocumentJson must be a JSON object");

        MssqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                """
                INSERT INTO [dms].[DocumentCacheState] (
                    [StateId],
                    [ProjectionLifecycleState],
                    [CacheAheadRecoveryRequired]
                )
                VALUES (2, 'Disabled', 0)
                """
            )
            .Message.Should()
            .Contain("CK_DocumentCacheState_Singleton", "DocumentCacheState should enforce its singleton");

        MssqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                "INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity]) VALUES (2, NEWID())"
            )
            .Message.Should()
            .Contain("CK_DataStoreIdentity_Singleton", "DataStoreIdentity should enforce its singleton");

        MssqlDocumentProjectionTestSupport.ExecuteNonQuery(
            connection,
            $"DELETE FROM [dms].[Document] WHERE [DocumentId] = {documentId}"
        );

        MssqlDocumentProjectionTestSupport
            .ReadDocumentCacheCount(connection, documentId)
            .Should()
            .Be(0, "DocumentCache should cascade when the owning document is deleted");
        MssqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [documentId])
            .Should()
            .BeEmpty("DocumentProjectionWork should cascade when the owning document is deleted");
    }

    [Test]
    public void It_allows_restricted_document_writer_to_enqueue_without_work_table_permissions()
    {
        var loginName = $"dms_writer_{Guid.NewGuid():N}"[..30];
        const string password = "Dms_Writer_1!";

        try
        {
            MssqlDocumentProjectionTestSupport.CreateRestrictedWriterLogin(
                _databaseName,
                loginName,
                password
            );

            using (var adminConnection = MssqlDocumentProjectionTestSupport.OpenConnection(_databaseName))
            {
                MssqlDocumentProjectionTestSupport.SetLifecycle(adminConnection, "Tracking");
            }

            var restrictedConnectionString = new SqlConnectionStringBuilder(
                MssqlTestDatabaseHelper.BuildConnectionString(_databaseName)
            )
            {
                UserID = loginName,
                Password = password,
                IntegratedSecurity = false,
            }.ConnectionString;

            var documentUuid = Guid.NewGuid();
            using (var restrictedConnection = new SqlConnection(restrictedConnectionString))
            {
                restrictedConnection.Open();
                using (var command = restrictedConnection.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                        VALUES (@documentUuid, 1)
                        """;
                    command.Parameters.AddWithValue("documentUuid", documentUuid);
                    command.ExecuteNonQuery();
                }

                MssqlDocumentProjectionTestSupport
                    .TryExecuteNonQuery(
                        restrictedConnection,
                        """
                        INSERT INTO [dms].[DocumentProjectionWork] (
                            [DocumentId],
                            [RequiredContentVersion],
                            [FirstEnqueuedAt],
                            [LastEnqueuedAt]
                        )
                        VALUES (0, 1, SYSUTCDATETIME(), SYSUTCDATETIME())
                        """
                    )
                    .Message.Should()
                    .Contain("INSERT permission was denied");
                MssqlDocumentProjectionTestSupport
                    .TryExecuteNonQuery(
                        restrictedConnection,
                        "UPDATE [dms].[DocumentProjectionWork] SET [RequiredContentVersion] = [RequiredContentVersion] WHERE 1 = 0"
                    )
                    .Message.Should()
                    .Contain("UPDATE permission was denied");
                MssqlDocumentProjectionTestSupport
                    .TryExecuteNonQuery(
                        restrictedConnection,
                        "DELETE FROM [dms].[DocumentProjectionWork] WHERE 1 = 0"
                    )
                    .Message.Should()
                    .Contain("DELETE permission was denied");
            }

            using var verificationConnection = MssqlDocumentProjectionTestSupport.OpenConnection(
                _databaseName
            );
            MssqlDocumentProjectionTestSupport
                .ReadWorkRequiredVersionForDocumentUuid(verificationConnection, documentUuid)
                .Should()
                .NotBeNull("the same-owner enqueue trigger should write work for the restricted writer");
        }
        finally
        {
            MssqlDocumentProjectionTestSupport.DropLoginIfExists(_databaseName, loginName);
        }
    }
}

internal sealed record MssqlDocumentProjectionDocumentRow(long DocumentId, long ContentVersion);

internal sealed record MssqlDocumentProjectionWorkRow(
    long DocumentId,
    long RequiredContentVersion,
    DateTime FirstEnqueuedAt,
    DateTime LastEnqueuedAt
);

internal static class MssqlDocumentProjectionTestSupport
{
    internal static string ProvisionFreshDatabase()
    {
        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(databaseName);

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision(
            "mssql",
            connectionString,
            createDatabase: true
        );
        exitCode.Should().Be(0, $"stdout: {output}\nstderr: {error}");

        return databaseName;
    }

    internal static SqlConnection OpenConnection(string databaseName)
    {
        var connection = new SqlConnection(MssqlTestDatabaseHelper.BuildConnectionString(databaseName));
        connection.Open();
        return connection;
    }

    internal static void AssertNestedTriggersEnabled()
    {
        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST([value_in_use] AS int)
            FROM sys.configurations
            WHERE [name] = N'nested triggers'
            """;
        Convert
            .ToInt32(command.ExecuteScalar())
            .Should()
            .Be(1, "SQL Server nested triggers must be enabled for generated resource stamps to enqueue");
    }

    internal static void AssertWidgetStampTriggerDoesNotReadServerConfiguration(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT module.[definition]
            FROM sys.triggers trigger_info
            INNER JOIN sys.tables parent_table ON parent_table.[object_id] = trigger_info.[parent_id]
            INNER JOIN sys.schemas parent_schema ON parent_schema.[schema_id] = parent_table.[schema_id]
            INNER JOIN sys.sql_modules module ON module.[object_id] = trigger_info.[object_id]
            WHERE parent_schema.[name] = N'testproject'
            AND parent_table.[name] = N'Widget'
            AND trigger_info.[name] = N'TR_Widget_Stamp'
            """;
        var definition = (string?)command.ExecuteScalar();
        definition.Should().NotBeNull("the generated Widget stamp trigger should exist");
        definition.Should().NotContain("sys.configurations");
    }

    internal static void SetLifecycle(SqlConnection connection, string lifecycle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycle
            WHERE [StateId] = 1
            """;
        command.Parameters.AddWithValue("lifecycle", lifecycle);
        command.ExecuteNonQuery();
    }

    internal static void BypassLifecycleConstraintAndSetLifecycle(SqlConnection connection, string lifecycle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE [dms].[DocumentCacheState] NOCHECK CONSTRAINT [CK_DocumentCacheState_Lifecycle];

            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycle
            WHERE [StateId] = 1
            """;
        command.Parameters.Add("lifecycle", SqlDbType.VarChar, 16).Value = lifecycle;
        command.ExecuteNonQuery();
    }

    internal static List<MssqlDocumentProjectionDocumentRow> InsertDocuments(
        SqlConnection connection,
        int count
    )
    {
        using var command = CreateInsertDocumentCommand(connection, count);
        using var reader = command.ExecuteReader();

        var rows = new List<MssqlDocumentProjectionDocumentRow>();
        while (reader.Read())
        {
            rows.Add(new MssqlDocumentProjectionDocumentRow(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    internal static SqlCommand CreateInsertDocumentCommand(SqlConnection connection, int count = 1)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @inserted_document TABLE (
                [DocumentId] bigint NOT NULL,
                [ContentVersion] bigint NOT NULL
            );

            WITH selected_resource AS (
                SELECT TOP (1) [ResourceKeyId]
                FROM [dms].[ResourceKey]
                ORDER BY [ResourceKeyId]
            ),
            numbers AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [Number]
                FROM sys.all_objects
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT inserted.[DocumentId], inserted.[ContentVersion]
            INTO @inserted_document
            SELECT NEWID(), selected_resource.[ResourceKeyId]
            FROM selected_resource
            CROSS JOIN numbers;

            SELECT [DocumentId], [ContentVersion]
            FROM @inserted_document
            ORDER BY [DocumentId];
            """;
        command.Parameters.AddWithValue("count", count);
        return command;
    }

    internal static List<MssqlDocumentProjectionDocumentRow> AdvanceDocumentContentVersions(
        SqlConnection connection,
        IEnumerable<long> documentIds
    )
    {
        using var command = connection.CreateCommand();
        var documentIdValues = AddDocumentIdParameters(command, documentIds);
        command.CommandText = $$"""
            DECLARE @document_ids TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
            INSERT INTO @document_ids ([DocumentId])
            VALUES {{documentIdValues}};

            DECLARE @updated_document TABLE (
                [DocumentId] bigint NOT NULL,
                [ContentVersion] bigint NOT NULL
            );

            UPDATE document
            SET [ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
                [ContentLastModifiedAt] = SYSUTCDATETIME()
            OUTPUT inserted.[DocumentId], inserted.[ContentVersion]
            INTO @updated_document
            FROM [dms].[Document] document
            INNER JOIN @document_ids ids
                ON ids.[DocumentId] = document.[DocumentId];

            SELECT [DocumentId], [ContentVersion]
            FROM @updated_document
            ORDER BY [DocumentId];
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<MssqlDocumentProjectionDocumentRow>();
        while (reader.Read())
        {
            rows.Add(new MssqlDocumentProjectionDocumentRow(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    internal static void LowerDocumentContentVersion(SqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ContentVersion] = [ContentVersion] - 1
            WHERE [DocumentId] = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        command.ExecuteNonQuery();
    }

    internal static void TouchDocumentWithoutContentVersionChange(SqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ResourceKeyId] = [ResourceKeyId]
            WHERE [DocumentId] = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        command.ExecuteNonQuery();
    }

    internal static List<MssqlDocumentProjectionWorkRow> ReadWorkRows(
        SqlConnection connection,
        IEnumerable<long> documentIds
    )
    {
        using var command = connection.CreateCommand();
        var ids = documentIds.ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var documentIdValues = AddDocumentIdParameters(command, ids);
        command.CommandText = $$"""
            DECLARE @document_ids TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY);
            INSERT INTO @document_ids ([DocumentId])
            VALUES {{documentIdValues}};

            SELECT work.[DocumentId], work.[RequiredContentVersion], work.[FirstEnqueuedAt], work.[LastEnqueuedAt]
            FROM [dms].[DocumentProjectionWork] work
            INNER JOIN @document_ids ids
                ON ids.[DocumentId] = work.[DocumentId]
            ORDER BY work.[DocumentId];
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<MssqlDocumentProjectionWorkRow>();
        while (reader.Read())
        {
            rows.Add(
                new MssqlDocumentProjectionWorkRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetDateTime(2),
                    reader.GetDateTime(3)
                )
            );
        }

        return rows;
    }

    internal static void AssertWorkMatchesDocuments(
        IReadOnlyCollection<MssqlDocumentProjectionWorkRow> workRows,
        IReadOnlyCollection<MssqlDocumentProjectionDocumentRow> documentRows
    )
    {
        workRows.Should().HaveCount(documentRows.Count);
        var documentsById = documentRows.ToDictionary(row => row.DocumentId);
        foreach (var workRow in workRows)
        {
            workRow.RequiredContentVersion.Should().Be(documentsById[workRow.DocumentId].ContentVersion);
        }
    }

    internal static void AssertSingleStatementInsertTimestamps(
        IReadOnlyCollection<MssqlDocumentProjectionWorkRow> workRows
    )
    {
        workRows.Should().OnlyContain(row => row.FirstEnqueuedAt == row.LastEnqueuedAt);
        workRows
            .Select(row => row.FirstEnqueuedAt)
            .Distinct()
            .Should()
            .ContainSingle("one SYSUTCDATETIME() value should be shared by all inserted work rows");
    }

    internal static long InsertWidget(SqlConnection connection, int widgetId, string widgetName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @inserted_document TABLE ([DocumentId] bigint NOT NULL);

            WITH widget_resource AS (
                SELECT TOP (1) [ResourceKeyId]
                FROM [dms].[ResourceKey]
                WHERE [ProjectName] = N'TestProject'
                AND [ResourceName] = N'Widget'
                ORDER BY [ResourceKeyId]
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT inserted.[DocumentId]
            INTO @inserted_document
            SELECT NEWID(), widget_resource.[ResourceKeyId]
            FROM widget_resource;

            INSERT INTO [testproject].[Widget] ([DocumentId], [WidgetId], [WidgetName])
            SELECT [DocumentId], @widgetId, @widgetName
            FROM @inserted_document;

            SELECT [DocumentId]
            FROM @inserted_document;
            """;
        command.Parameters.AddWithValue("widgetId", widgetId);
        command.Parameters.AddWithValue("widgetName", widgetName);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void UpdateWidgetName(SqlConnection connection, long documentId, string widgetName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [testproject].[Widget]
            SET [WidgetName] = @widgetName
            WHERE [DocumentId] = @documentId
            """;
        command.Parameters.AddWithValue("widgetName", widgetName);
        command.Parameters.AddWithValue("documentId", documentId);
        command.ExecuteNonQuery();
    }

    internal static MssqlDocumentProjectionDocumentRow ReadDocumentRow(
        SqlConnection connection,
        long documentId
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [DocumentId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue("the document row should exist");
        return new MssqlDocumentProjectionDocumentRow(reader.GetInt64(0), reader.GetInt64(1));
    }

    internal static long ReadWidgetContentVersion(SqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [ContentVersion]
            FROM [testproject].[Widget]
            WHERE [DocumentId] = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void InstallForcedWorkFailureConstraint(SqlConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            ALTER TABLE [dms].[DocumentProjectionWork]
            ADD CONSTRAINT [CK_Test_ForceDocumentProjectionWorkFailure]
            CHECK ([RequiredContentVersion] < 0);
            """
        );
    }

    internal static void DropForcedWorkFailureConstraint(SqlConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            IF EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE [name] = N'CK_Test_ForceDocumentProjectionWorkFailure'
                AND [parent_object_id] = OBJECT_ID(N'dms.DocumentProjectionWork')
            )
            ALTER TABLE [dms].[DocumentProjectionWork]
            DROP CONSTRAINT [CK_Test_ForceDocumentProjectionWorkFailure];
            """
        );
    }

    internal static void InsertDocumentCache(SqlConnection connection, long documentId, string documentJson)
    {
        ExecuteNonQuery(connection, BuildDocumentCacheInsertSql(documentId, documentJson));
    }

    internal static string BuildDocumentCacheInsertSql(long documentId, string documentJson)
    {
        var escapedJson = documentJson.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            INSERT INTO [dms].[DocumentCache] (
                [DocumentId],
                [DocumentUuid],
                [ProjectName],
                [ResourceName],
                [ResourceVersion],
                [ContentVersion],
                [StreamEtag],
                [LastModifiedAt],
                [DocumentJson]
            )
            SELECT
                document.[DocumentId],
                document.[DocumentUuid],
                resource_key.[ProjectName],
                resource_key.[ResourceName],
                resource_key.[ResourceVersion],
                document.[ContentVersion],
                CONCAT(N'etag-', CONVERT(varchar(20), document.[ContentVersion])),
                document.[ContentLastModifiedAt],
                N'{{escapedJson}}'
            FROM [dms].[Document] document
            INNER JOIN [dms].[ResourceKey] resource_key
                ON resource_key.[ResourceKeyId] = document.[ResourceKeyId]
            WHERE document.[DocumentId] = {{documentId}}
            """;
    }

    internal static long ReadDocumentCacheCount(SqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM [dms].[DocumentCache]
            WHERE [DocumentId] = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void CreateRestrictedWriterLogin(string databaseName, string loginName, string password)
    {
        using (var adminConnection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!))
        {
            adminConnection.Open();
            using var command = adminConnection.CreateCommand();
            command.CommandText = $$"""
                CREATE LOGIN {{QuoteIdentifier(loginName)}} WITH PASSWORD = '{{EscapeSqlLiteral(password)}}',
                    CHECK_POLICY = OFF,
                    CHECK_EXPIRATION = OFF;
                """;
            command.ExecuteNonQuery();
        }

        using (var databaseConnection = OpenConnection(databaseName))
        {
            using var command = databaseConnection.CreateCommand();
            command.CommandText = $$"""
                CREATE USER {{QuoteIdentifier(loginName)}} FOR LOGIN {{QuoteIdentifier(loginName)}};
                GRANT INSERT, UPDATE ON OBJECT::[dms].[Document] TO {{QuoteIdentifier(loginName)}};
                """;
            command.ExecuteNonQuery();
        }
    }

    internal static void DropLoginIfExists(string databaseName, string loginName)
    {
        SqlConnection.ClearAllPools();

        try
        {
            using var databaseConnection = OpenConnection(databaseName);
            using var command = databaseConnection.CreateCommand();
            command.CommandText = $$"""
                IF USER_ID(N'{{EscapeSqlLiteral(loginName)}}') IS NOT NULL
                    DROP USER {{QuoteIdentifier(loginName)}};
                """;
            command.ExecuteNonQuery();
        }
        catch (SqlException ex) when (ex.Number is 4060 or 911)
        {
            TestContext.Progress.WriteLine(
                $"Database {databaseName} no longer exists while cleaning up login {loginName}."
            );
        }

        using (var adminConnection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!))
        {
            adminConnection.Open();
            using var command = adminConnection.CreateCommand();
            command.CommandText = $$"""
                IF SUSER_ID(N'{{EscapeSqlLiteral(loginName)}}') IS NOT NULL
                    DROP LOGIN {{QuoteIdentifier(loginName)}};
                """;
            command.ExecuteNonQuery();
        }
    }

    internal static long? ReadWorkRequiredVersionForDocumentUuid(SqlConnection connection, Guid documentUuid)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work.[RequiredContentVersion]
            FROM [dms].[Document] document
            INNER JOIN [dms].[DocumentProjectionWork] work
                ON work.[DocumentId] = document.[DocumentId]
            WHERE document.[DocumentUuid] = @documentUuid
            """;
        command.Parameters.AddWithValue("documentUuid", documentUuid);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value);
    }

    internal static void ExecuteNonQuery(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static SqlException TryExecuteNonQuery(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Action action = () => command.ExecuteNonQuery();
        return action.Should().Throw<SqlException>().Which;
    }

    private static string AddDocumentIdParameters(SqlCommand command, IEnumerable<long> documentIds)
    {
        var ids = documentIds.ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("At least one document id is required.", nameof(documentIds));
        }

        var values = new List<string>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            var parameterName = $"documentId{i}";
            command.Parameters.AddWithValue(parameterName, ids[i]);
            values.Add($"(@{parameterName})");
        }

        return string.Join(", ", values);
    }

    private static string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
