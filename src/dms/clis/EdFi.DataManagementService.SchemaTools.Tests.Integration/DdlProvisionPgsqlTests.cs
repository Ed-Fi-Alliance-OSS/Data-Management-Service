// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaTools.Introspection;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
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
    public void It_initializes_document_cache_mutable_singletons()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper.AssertDocumentCacheStateSeeded(connection, "pgsql");
    }

    [Test]
    public void It_rejects_invalid_document_cache_lifecycle_values()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper.AssertDocumentCacheLifecycleRejectsInvalidValues(connection, "pgsql");
    }

    [Test]
    public void It_configures_enqueue_function_security()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM pg_catalog.pg_proc p
                INNER JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'dms'
                AND p.proname IN ('TF_Document_EnqueueProjectionInsert', 'TF_Document_EnqueueProjectionUpdate')
                AND p.prosecdef
                AND p.proowner = 'edfi_dms_enqueue_owner'::pg_catalog.regrole
                AND COALESCE(p.proconfig, ARRAY[]::text[]) @> ARRAY['search_path=pg_catalog']::text[]
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.aclexplode(COALESCE(p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                    WHERE acl.grantee = 0
                    AND acl.privilege_type = 'EXECUTE'
                )
                """;
            Convert
                .ToInt64(command.ExecuteScalar())
                .Should()
                .Be(
                    2,
                    "both enqueue functions should be SECURITY DEFINER with the locked owner and no PUBLIC execute"
                );
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    pg_catalog.has_schema_privilege(
                        'edfi_dms_enqueue_owner',
                        'dms',
                        'USAGE'
                    )
                    AND pg_catalog.has_table_privilege(
                        'edfi_dms_enqueue_owner',
                        '"dms"."DocumentCacheState"',
                        'SELECT'
                    )
                    AND pg_catalog.has_table_privilege(
                        'edfi_dms_enqueue_owner',
                        '"dms"."DocumentProjectionWork"',
                        'SELECT, INSERT, UPDATE'
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_class table_info
                        INNER JOIN pg_catalog.pg_namespace namespace_info
                            ON namespace_info.oid = table_info.relnamespace
                        CROSS JOIN LATERAL pg_catalog.aclexplode(
                            COALESCE(table_info.relacl, pg_catalog.acldefault('r', table_info.relowner))
                        ) acl
                        WHERE namespace_info.nspname = 'dms'
                        AND table_info.relname = 'DocumentProjectionWork'
                        AND acl.grantee = 0
                        AND acl.privilege_type IN ('INSERT', 'UPDATE', 'DELETE')
                    )
                """;
            ((bool)command.ExecuteScalar()!)
                .Should()
                .BeTrue(
                    "the enqueue owner should have only the local privileges needed by the definer functions"
                );
        }
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
[Category("PostgresqlIntegration")]
public class Given_Provisioning_Rerun_On_Same_Database
{
    private string _databaseName = null!;
    private int _firstExitCode;
    private int _secondExitCode;
    private string _secondOutput = null!;
    private string _secondError = null!;
    private string _firstManifestJson = null!;
    private string _secondManifestJson = null!;
    private ProvisionTestHelper.DocumentCacheMutableStateSnapshot _beforeRerun = null!;
    private ProvisionTestHelper.DocumentCacheMutableStateSnapshot _afterRerun = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        // First provisioning run
        (_firstExitCode, _, _) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            CliTestHelper.GetAuthoritativeSchemaPaths(),
            createDatabase: true
        );

        // Introspect after first run
        var schemaAllowlist = ProvisionTestHelper.DiscoverProvisionedSchemasPgsql(connectionString);
        var introspector = new PgsqlSchemaIntrospector();
        var firstManifest = introspector.Introspect(connectionString, schemaAllowlist);
        _firstManifestJson = ProvisionedSchemaManifestEmitter.Emit(firstManifest);

        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            ProvisionTestHelper.InsertRowsThatMustSurviveRerun(connection, "pgsql");
            _beforeRerun = ProvisionTestHelper.ReadDocumentCacheMutableStateSnapshot(connection, "pgsql");
        }

        // Second provisioning run (idempotent rerun)
        (_secondExitCode, _secondOutput, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            CliTestHelper.GetAuthoritativeSchemaPaths(),
            createDatabase: true
        );

        // Introspect after second run (rediscover schemas to catch accidental new schemas)
        var secondSchemaAllowlist = ProvisionTestHelper.DiscoverProvisionedSchemasPgsql(connectionString);
        var secondManifest = introspector.Introspect(connectionString, secondSchemaAllowlist);
        _secondManifestJson = ProvisionedSchemaManifestEmitter.Emit(secondManifest);

        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            _afterRerun = ProvisionTestHelper.ReadDocumentCacheMutableStateSnapshot(connection, "pgsql");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
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
[Category("PostgresqlIntegration")]
public class Given_Postgresql_Bounded_E18_Preflight_Guards
{
    [TestCase(
        """DROP TABLE dms."DataStoreIdentity";""",
        "DataStoreIdentity",
        TestName = "It_rejects_missing_DataStoreIdentity_table"
    )]
    [TestCase(
        """DELETE FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1;""",
        "DataStoreIdentity singleton row is missing",
        TestName = "It_rejects_missing_DataStoreIdentity_singleton"
    )]
    [TestCase(
        """UPDATE dms."DataStoreIdentity" SET "SourceIdentity" = '00000000-0000-0000-0000-000000000000' WHERE "DataStoreIdentitySingletonId" = 1;""",
        "SourceIdentity must not be the zero UUID",
        TestName = "It_rejects_zero_SourceIdentity"
    )]
    [TestCase(
        """DROP TABLE dms."DocumentCacheState";""",
        "DocumentCacheState",
        TestName = "It_rejects_missing_DocumentCacheState_table"
    )]
    [TestCase(
        """DELETE FROM dms."DocumentCacheState" WHERE "StateId" = 1;""",
        "DocumentCacheState singleton row is missing",
        TestName = "It_rejects_missing_DocumentCacheState_singleton"
    )]
    [TestCase(
        """
            ALTER TABLE dms."DocumentCacheState" DROP CONSTRAINT "CK_DocumentCacheState_Lifecycle";
            UPDATE dms."DocumentCacheState" SET "ProjectionLifecycleState" = 'Broken' WHERE "StateId" = 1;
            """,
        "ProjectionLifecycleState has unsupported value",
        TestName = "It_rejects_invalid_DocumentCacheState_lifecycle"
    )]
    [TestCase(
        """ALTER TABLE dms."DocumentCache" ADD COLUMN "Etag" character varying(64);""",
        "Known legacy DocumentCache artifact",
        TestName = "It_rejects_legacy_DocumentCache_Etag"
    )]
    [TestCase(
        """CREATE UNIQUE INDEX "UX_DocumentCache_DocumentUuid" ON dms."DocumentCache" ("DocumentUuid");""",
        "UX_DocumentCache_DocumentUuid",
        TestName = "It_rejects_legacy_DocumentCache_uuid_index"
    )]
    [TestCase(
        """CREATE INDEX "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt" ON dms."DocumentCache" ("ProjectName", "ResourceName", "LastModifiedAt");""",
        "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt",
        TestName = "It_rejects_legacy_DocumentCache_scan_index"
    )]
    public void It_rejects_incompatible_completed_state_before_rerun(string mutationSql, string expectedError)
    {
        var databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(databaseName);

        try
        {
            var (firstExitCode, firstOutput, firstError) = ProvisionTestHelper.RunProvision(
                "pgsql",
                connectionString,
                createDatabase: true
            );
            firstExitCode.Should().Be(0, $"stdout: {firstOutput}\nstderr: {firstError}");

            using (var connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = mutationSql;
                command.ExecuteNonQuery();
            }

            var (secondExitCode, secondOutput, secondError) = ProvisionTestHelper.RunProvision(
                "pgsql",
                connectionString
            );

            secondExitCode.Should().NotBe(0, $"stdout: {secondOutput}\nstderr: {secondError}");
            secondError.Should().Contain(expectedError);
        }
        finally
        {
            PostgresTestDatabaseHelper.DropDatabaseIfExists(databaseName);
        }
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Postgresql_Compatible_Interrupted_Initial_Apply
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
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE SCHEMA dms;
                CREATE SEQUENCE dms."ChangeVersionSequence" AS bigint START WITH 1 INCREMENT BY 1;
                """;
            command.ExecuteNonQuery();
        }

        (_exitCode, _output, _error) = ProvisionTestHelper.RunProvision("pgsql", connectionString);
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_completes_successfully()
    {
        _exitCode.Should().Be(0, $"stdout: {_output}\nstderr: {_error}");
    }

    [Test]
    public void It_creates_and_seeds_document_cache_state()
    {
        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        ProvisionTestHelper.AssertCoreTablesExist(connection);
        ProvisionTestHelper.AssertDocumentCacheStateSeeded(connection, "pgsql");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Postgresql_Unsafe_Enqueue_Owner_Prerequisite
{
    private string _databaseName = null!;
    private string _roleName = null!;
    private const string Password = "Dms_Preflight_1!";

    [SetUp]
    public void SetUp()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        _roleName = $"dms_preflight_{Guid.NewGuid():N}"[..30];
        PostgresTestDatabaseHelper.CreateDatabase(_databaseName);

        using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'edfi_dms_enqueue_owner') THEN
                    CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                END IF;
            END $$;

            CREATE ROLE "{_roleName}" LOGIN PASSWORD '{Password}';
            GRANT CONNECT ON DATABASE "{_databaseName}" TO "{_roleName}";
            GRANT "edfi_dms_enqueue_owner" TO "{_roleName}" WITH SET TRUE, INHERIT TRUE, ADMIN FALSE;
            """;
        try
        {
            command.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            Assert.Ignore(
                "PostgreSQL admin connection cannot create test roles for owner-prerequisite coverage."
            );
        }
    }

    [TearDown]
    public void TearDown()
    {
        using (var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{_roleName}') THEN
                        EXECUTE 'REVOKE CONNECT ON DATABASE "{_databaseName}" FROM "{_roleName}"';
                        EXECUTE 'REVOKE "edfi_dms_enqueue_owner" FROM "{_roleName}"';
                        EXECUTE 'DROP OWNED BY "{_roleName}"';
                        EXECUTE 'DROP ROLE "{_roleName}"';
                    END IF;
                END $$;
                """;
            command.ExecuteNonQuery();
        }

        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_fails_before_mutation_with_prerequisite_diagnostic()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        )
        {
            Username = _roleName,
            Password = Password,
        };

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision("pgsql", builder.ConnectionString);

        exitCode.Should().NotBe(0, $"stdout: {output}\nstderr: {error}");
        error.Should().Contain("Provider provisioning prerequisite check failed");
        error.Should().Contain("unsafe direct membership in edfi_dms_enqueue_owner");

        using var connection = new NpgsqlConnection(
            PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
        );
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'dms'
            AND table_name = 'EffectiveSchema'
            """;
        command.ExecuteScalar().Should().BeNull("preflight should fail before any DMS table is created");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
        var schemaPathA = CliTestHelper.GetMinimalSchemaPath();
        (_firstExitCode, _firstOutput, _) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            [schemaPathA],
            createDatabase: true
        );

        // Second provisioning run with schema B (alternate minimal)
        var schemaPathB = CliTestHelper.GetAlternateMinimalSchemaPath();
        (_secondExitCode, _, _secondError) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            [schemaPathB]
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
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
[Category("PostgresqlIntegration")]
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
