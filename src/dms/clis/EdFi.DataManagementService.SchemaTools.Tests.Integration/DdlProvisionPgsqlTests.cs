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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Postgresql_DocumentProjectionLifecycle
{
    private string _databaseName = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PgsqlDocumentProjectionTestSupport.ProvisionFreshDatabase();
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_records_work_only_for_enabled_lifecycle_states_and_preserves_enqueue_timestamps()
    {
        using var connection = PgsqlDocumentProjectionTestSupport.OpenConnection(_databaseName);

        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Disabled");
        var disabledDocuments = PgsqlDocumentProjectionTestSupport.InsertDocuments(connection, 2);
        PgsqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, disabledDocuments.Select(d => d.DocumentId))
            .Should()
            .BeEmpty("Disabled lifecycle should not enqueue direct document inserts");

        PgsqlDocumentProjectionTestSupport.AdvanceDocumentContentVersions(
            connection,
            disabledDocuments.Select(d => d.DocumentId)
        );
        PgsqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, disabledDocuments.Select(d => d.DocumentId))
            .Should()
            .BeEmpty("Disabled lifecycle should not enqueue direct document updates");

        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Resetting");
        var resettingDocuments = PgsqlDocumentProjectionTestSupport.InsertDocuments(connection, 2);
        var resettingWork = PgsqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            resettingDocuments.Select(d => d.DocumentId)
        );
        PgsqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(resettingWork, resettingDocuments);
        PgsqlDocumentProjectionTestSupport.AssertSingleStatementInsertTimestamps(resettingWork);

        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Rebuilding");
        var resettingWorkBeforeUpdate = resettingWork.ToDictionary(row => row.DocumentId);
        var rebuiltDocuments = PgsqlDocumentProjectionTestSupport.AdvanceDocumentContentVersions(
            connection,
            resettingDocuments.Select(d => d.DocumentId)
        );
        var rebuiltWork = PgsqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            resettingDocuments.Select(d => d.DocumentId)
        );
        PgsqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(rebuiltWork, rebuiltDocuments);
        rebuiltWork
            .Select(row => row.LastEnqueuedAt)
            .Distinct()
            .Should()
            .ContainSingle("one statement_timestamp() should be shared by the update statement");
        foreach (var row in rebuiltWork)
        {
            row.FirstEnqueuedAt.Should()
                .Be(
                    resettingWorkBeforeUpdate[row.DocumentId].FirstEnqueuedAt,
                    "advancing work should preserve FirstEnqueuedAt"
                );
        }

        var workBeforeNonAdvancingUpdate = PgsqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [resettingDocuments[0].DocumentId])
            .Single();
        PgsqlDocumentProjectionTestSupport.LowerDocumentContentVersion(
            connection,
            resettingDocuments[0].DocumentId
        );
        PgsqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [resettingDocuments[0].DocumentId])
            .Single()
            .Should()
            .Be(workBeforeNonAdvancingUpdate, "non-advancing requirements should not touch work rows");

        PgsqlDocumentProjectionTestSupport.TouchDocumentWithoutContentVersionChange(
            connection,
            resettingDocuments[0].DocumentId
        );
        PgsqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [resettingDocuments[0].DocumentId])
            .Single()
            .Should()
            .Be(workBeforeNonAdvancingUpdate, "unchanged ContentVersion updates should not touch work rows");

        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");
        var trackingDocuments = PgsqlDocumentProjectionTestSupport.InsertDocuments(connection, 2);
        var trackingInsertWork = PgsqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            trackingDocuments.Select(d => d.DocumentId)
        );
        PgsqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(trackingInsertWork, trackingDocuments);
        PgsqlDocumentProjectionTestSupport.AssertSingleStatementInsertTimestamps(trackingInsertWork);

        var trackedDocuments = PgsqlDocumentProjectionTestSupport.AdvanceDocumentContentVersions(
            connection,
            trackingDocuments.Select(d => d.DocumentId)
        );
        var trackingUpdateWork = PgsqlDocumentProjectionTestSupport.ReadWorkRows(
            connection,
            trackingDocuments.Select(d => d.DocumentId)
        );
        PgsqlDocumentProjectionTestSupport.AssertWorkMatchesDocuments(trackingUpdateWork, trackedDocuments);
        trackingUpdateWork
            .Select(row => row.LastEnqueuedAt)
            .Distinct()
            .Should()
            .ContainSingle("one statement_timestamp() should be shared by the tracking update");
    }

    [Test]
    public void It_enqueues_when_a_generated_resource_stamp_advances_document_content_version()
    {
        using var connection = PgsqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");

        var documentId = PgsqlDocumentProjectionTestSupport.InsertWidget(connection, 1001, "before");
        var initialDocument = PgsqlDocumentProjectionTestSupport.ReadDocumentRow(connection, documentId);
        var initialWork = PgsqlDocumentProjectionTestSupport.ReadWorkRows(connection, [documentId]).Single();

        PgsqlDocumentProjectionTestSupport.UpdateWidgetName(connection, documentId, "after");

        var updatedDocument = PgsqlDocumentProjectionTestSupport.ReadDocumentRow(connection, documentId);
        updatedDocument
            .ContentVersion.Should()
            .BeGreaterThan(
                initialDocument.ContentVersion,
                "the generated resource stamp trigger should advance dms.Document.ContentVersion"
            );

        PgsqlDocumentProjectionTestSupport
            .ReadWidgetContentVersion(connection, documentId)
            .Should()
            .Be(
                updatedDocument.ContentVersion,
                "the generated resource table should mirror the stamped content version"
            );

        var updatedWork = PgsqlDocumentProjectionTestSupport.ReadWorkRows(connection, [documentId]).Single();
        updatedWork.RequiredContentVersion.Should().Be(updatedDocument.ContentVersion);
        updatedWork.FirstEnqueuedAt.Should().Be(initialWork.FirstEnqueuedAt);
        updatedWork.LastEnqueuedAt.Should().BeOnOrAfter(initialWork.LastEnqueuedAt);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_Postgresql_DocumentProjectionSafety
{
    private string _databaseName = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = PgsqlDocumentProjectionTestSupport.ProvisionFreshDatabase();
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
    }

    [Test]
    public void It_rolls_back_document_dml_when_lifecycle_singleton_is_missing()
    {
        using var connection = PgsqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        var initialDocumentCount = ProvisionTestHelper.GetDmsTableCount(connection, "pgsql", "Document");
        var initialWorkCount = ProvisionTestHelper.GetDmsTableCount(
            connection,
            "pgsql",
            "DocumentProjectionWork"
        );

        PgsqlDocumentProjectionTestSupport.ExecuteNonQuery(
            connection,
            """DELETE FROM dms."DocumentCacheState" WHERE "StateId" = 1"""
        );

        using var transaction = connection.BeginTransaction();
        using var command = PgsqlDocumentProjectionTestSupport.CreateInsertDocumentCommand(connection);
        command.Transaction = transaction;

        Action insertDocument = () => command.ExecuteNonQuery();
        var exception = insertDocument.Should().Throw<PostgresException>().Which;
        exception
            .MessageText.Should()
            .Contain("DocumentCacheState singleton row is missing", "the native diagnostic should be useful");
        transaction.Rollback();

        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "Document")
            .Should()
            .Be(initialDocumentCount, "the failed canonical document insert should roll back");
        ProvisionTestHelper
            .GetDmsTableCount(connection, "pgsql", "DocumentProjectionWork")
            .Should()
            .Be(initialWorkCount, "the failed enqueue should not leave work rows behind");
    }

    [Test]
    public void It_rolls_back_document_dml_when_enqueue_work_write_fails()
    {
        using var connection = PgsqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");
        PgsqlDocumentProjectionTestSupport.InstallForcedWorkFailureTrigger(connection);

        var initialDocumentCount = ProvisionTestHelper.GetDmsTableCount(connection, "pgsql", "Document");
        var initialWorkCount = ProvisionTestHelper.GetDmsTableCount(
            connection,
            "pgsql",
            "DocumentProjectionWork"
        );

        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = PgsqlDocumentProjectionTestSupport.CreateInsertDocumentCommand(connection);
            command.Transaction = transaction;

            Action insertDocument = () => command.ExecuteNonQuery();
            var exception = insertDocument.Should().Throw<PostgresException>().Which;
            exception
                .MessageText.Should()
                .Contain("forced DocumentProjectionWork failure", "the native diagnostic should be useful");
            transaction.Rollback();

            ProvisionTestHelper
                .GetDmsTableCount(connection, "pgsql", "Document")
                .Should()
                .Be(initialDocumentCount, "the failed canonical document insert should roll back");
            ProvisionTestHelper
                .GetDmsTableCount(connection, "pgsql", "DocumentProjectionWork")
                .Should()
                .Be(initialWorkCount, "the failed work-table write should roll back");
        }
        finally
        {
            PgsqlDocumentProjectionTestSupport.DropForcedWorkFailureTrigger(connection);
        }
    }

    [Test]
    public void It_enforces_cache_constraints_and_cascades_cache_and_work_rows()
    {
        using var connection = PgsqlDocumentProjectionTestSupport.OpenConnection(_databaseName);
        PgsqlDocumentProjectionTestSupport.SetLifecycle(connection, "Tracking");

        var documentId = PgsqlDocumentProjectionTestSupport
            .InsertDocuments(connection, 1)
            .Single()
            .DocumentId;
        PgsqlDocumentProjectionTestSupport.InsertDocumentCache(
            connection,
            documentId,
            """{"id":"matching"}"""
        );

        PgsqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                $"""UPDATE dms."DocumentCache" SET "DocumentUuid" = gen_random_uuid() WHERE "DocumentId" = {documentId}"""
            )
            .MessageText.Should()
            .Contain("DocumentUuid diverges");

        var invalidJsonDocumentId = PgsqlDocumentProjectionTestSupport
            .InsertDocuments(connection, 1)
            .Single()
            .DocumentId;
        PgsqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                PgsqlDocumentProjectionTestSupport.BuildDocumentCacheInsertSql(invalidJsonDocumentId, "[]")
            )
            .SqlState.Should()
            .Be(PostgresErrorCodes.CheckViolation, "DocumentCache.DocumentJson must be a JSON object");

        PgsqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                """
                INSERT INTO dms."DocumentCacheState" (
                    "StateId",
                    "ProjectionLifecycleState",
                    "CacheAheadRecoveryRequired"
                )
                VALUES (2, 'Disabled', FALSE)
                """
            )
            .SqlState.Should()
            .Be(PostgresErrorCodes.CheckViolation, "DocumentCacheState should enforce its singleton");

        PgsqlDocumentProjectionTestSupport
            .TryExecuteNonQuery(
                connection,
                """INSERT INTO dms."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity") VALUES (2, gen_random_uuid())"""
            )
            .SqlState.Should()
            .Be(PostgresErrorCodes.CheckViolation, "DataStoreIdentity should enforce its singleton");

        PgsqlDocumentProjectionTestSupport.ExecuteNonQuery(
            connection,
            $"""DELETE FROM dms."Document" WHERE "DocumentId" = {documentId}"""
        );

        PgsqlDocumentProjectionTestSupport
            .ReadDocumentCacheCount(connection, documentId)
            .Should()
            .Be(0, "DocumentCache should cascade when the owning document is deleted");
        PgsqlDocumentProjectionTestSupport
            .ReadWorkRows(connection, [documentId])
            .Should()
            .BeEmpty("DocumentProjectionWork should cascade when the owning document is deleted");
    }

    [Test]
    public void It_allows_restricted_document_writer_to_enqueue_without_work_table_permissions()
    {
        var roleName = $"dms_writer_{Guid.NewGuid():N}"[..30];
        const string password = "Dms_Writer_1!";

        try
        {
            PgsqlDocumentProjectionTestSupport.CreateRestrictedWriterRole(_databaseName, roleName, password);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            Assert.Ignore(
                "PostgreSQL admin connection cannot create test roles for restricted-writer coverage."
            );
        }

        try
        {
            using (var adminConnection = PgsqlDocumentProjectionTestSupport.OpenConnection(_databaseName))
            {
                PgsqlDocumentProjectionTestSupport.SetLifecycle(adminConnection, "Tracking");
            }

            var restrictedConnectionString = new NpgsqlConnectionStringBuilder(
                PostgresTestDatabaseHelper.BuildConnectionString(_databaseName)
            )
            {
                Username = roleName,
                Password = password,
            }.ConnectionString;

            var documentUuid = Guid.NewGuid();
            using (var restrictedConnection = new NpgsqlConnection(restrictedConnectionString))
            {
                restrictedConnection.Open();
                using (var command = restrictedConnection.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                        VALUES (@documentUuid, 1)
                        """;
                    command.Parameters.AddWithValue("documentUuid", documentUuid);
                    command.ExecuteNonQuery();
                }

                PgsqlDocumentProjectionTestSupport
                    .TryExecuteNonQuery(
                        restrictedConnection,
                        """
                        INSERT INTO dms."DocumentProjectionWork" (
                            "DocumentId",
                            "RequiredContentVersion",
                            "FirstEnqueuedAt",
                            "LastEnqueuedAt"
                        )
                        VALUES (0, 1, now(), now())
                        """
                    )
                    .SqlState.Should()
                    .Be(PostgresErrorCodes.InsufficientPrivilege);
                PgsqlDocumentProjectionTestSupport
                    .TryExecuteNonQuery(
                        restrictedConnection,
                        """UPDATE dms."DocumentProjectionWork" SET "RequiredContentVersion" = "RequiredContentVersion" WHERE FALSE"""
                    )
                    .SqlState.Should()
                    .Be(PostgresErrorCodes.InsufficientPrivilege);
                PgsqlDocumentProjectionTestSupport
                    .TryExecuteNonQuery(
                        restrictedConnection,
                        """DELETE FROM dms."DocumentProjectionWork" WHERE FALSE"""
                    )
                    .SqlState.Should()
                    .Be(PostgresErrorCodes.InsufficientPrivilege);
            }

            using var verificationConnection = PgsqlDocumentProjectionTestSupport.OpenConnection(
                _databaseName
            );
            PgsqlDocumentProjectionTestSupport
                .ReadWorkRequiredVersionForDocumentUuid(verificationConnection, documentUuid)
                .Should()
                .NotBeNull(
                    "the SECURITY DEFINER enqueue trigger should write work for the restricted writer"
                );
        }
        finally
        {
            PgsqlDocumentProjectionTestSupport.DropRoleIfExists(_databaseName, roleName);
        }
    }
}

internal sealed record PgsqlDocumentProjectionDocumentRow(long DocumentId, long ContentVersion);

internal sealed record PgsqlDocumentProjectionWorkRow(
    long DocumentId,
    long RequiredContentVersion,
    DateTime FirstEnqueuedAt,
    DateTime LastEnqueuedAt
);

internal static class PgsqlDocumentProjectionTestSupport
{
    internal static string ProvisionFreshDatabase()
    {
        var databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresTestDatabaseHelper.BuildConnectionString(databaseName);

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision(
            "pgsql",
            connectionString,
            createDatabase: true
        );
        exitCode.Should().Be(0, $"stdout: {output}\nstderr: {error}");

        return databaseName;
    }

    internal static NpgsqlConnection OpenConnection(string databaseName)
    {
        var connection = new NpgsqlConnection(PostgresTestDatabaseHelper.BuildConnectionString(databaseName));
        connection.Open();
        return connection;
    }

    internal static void SetLifecycle(NpgsqlConnection connection, string lifecycle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dms."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycle
            WHERE "StateId" = 1
            """;
        command.Parameters.AddWithValue("lifecycle", lifecycle);
        command.ExecuteNonQuery();
    }

    internal static List<PgsqlDocumentProjectionDocumentRow> InsertDocuments(
        NpgsqlConnection connection,
        int count
    )
    {
        using var command = CreateInsertDocumentCommand(connection, count);
        using var reader = command.ExecuteReader();

        var rows = new List<PgsqlDocumentProjectionDocumentRow>();
        while (reader.Read())
        {
            rows.Add(new PgsqlDocumentProjectionDocumentRow(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    internal static NpgsqlCommand CreateInsertDocumentCommand(NpgsqlConnection connection, int count = 1)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            WITH selected_resource AS (
                SELECT "ResourceKeyId"
                FROM dms."ResourceKey"
                ORDER BY "ResourceKeyId"
                LIMIT 1
            ),
            inserted_document AS (
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT gen_random_uuid(), selected_resource."ResourceKeyId"
                FROM selected_resource
                CROSS JOIN generate_series(1, @count)
                RETURNING "DocumentId", "ContentVersion"
            )
            SELECT "DocumentId", "ContentVersion"
            FROM inserted_document
            ORDER BY "DocumentId"
            """;
        command.Parameters.AddWithValue("count", count);
        return command;
    }

    internal static List<PgsqlDocumentProjectionDocumentRow> AdvanceDocumentContentVersions(
        NpgsqlConnection connection,
        IEnumerable<long> documentIds
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dms."Document"
            SET "ContentVersion" = nextval('dms."ChangeVersionSequence"'),
                "ContentLastModifiedAt" = now()
            WHERE "DocumentId" = ANY(@documentIds)
            RETURNING "DocumentId", "ContentVersion"
            """;
        command.Parameters.AddWithValue("documentIds", documentIds.ToArray());

        using var reader = command.ExecuteReader();
        var rows = new List<PgsqlDocumentProjectionDocumentRow>();
        while (reader.Read())
        {
            rows.Add(new PgsqlDocumentProjectionDocumentRow(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows.OrderBy(row => row.DocumentId).ToList();
    }

    internal static void LowerDocumentContentVersion(NpgsqlConnection connection, long documentId)
    {
        ExecuteNonQuery(
            connection,
            $"""
            UPDATE dms."Document"
            SET "ContentVersion" = "ContentVersion" - 1
            WHERE "DocumentId" = {documentId}
            """
        );
    }

    internal static void TouchDocumentWithoutContentVersionChange(
        NpgsqlConnection connection,
        long documentId
    )
    {
        ExecuteNonQuery(
            connection,
            $"""
            UPDATE dms."Document"
            SET "ResourceKeyId" = "ResourceKeyId"
            WHERE "DocumentId" = {documentId}
            """
        );
    }

    internal static List<PgsqlDocumentProjectionWorkRow> ReadWorkRows(
        NpgsqlConnection connection,
        IEnumerable<long> documentIds
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "DocumentId", "RequiredContentVersion", "FirstEnqueuedAt", "LastEnqueuedAt"
            FROM dms."DocumentProjectionWork"
            WHERE "DocumentId" = ANY(@documentIds)
            ORDER BY "DocumentId"
            """;
        command.Parameters.AddWithValue("documentIds", documentIds.ToArray());

        using var reader = command.ExecuteReader();
        var rows = new List<PgsqlDocumentProjectionWorkRow>();
        while (reader.Read())
        {
            rows.Add(
                new PgsqlDocumentProjectionWorkRow(
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
        IReadOnlyCollection<PgsqlDocumentProjectionWorkRow> workRows,
        IReadOnlyCollection<PgsqlDocumentProjectionDocumentRow> documentRows
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
        IReadOnlyCollection<PgsqlDocumentProjectionWorkRow> workRows
    )
    {
        workRows.Should().OnlyContain(row => row.FirstEnqueuedAt == row.LastEnqueuedAt);
        workRows
            .Select(row => row.FirstEnqueuedAt)
            .Distinct()
            .Should()
            .ContainSingle("one statement_timestamp() should be shared by all inserted work rows");
    }

    internal static long InsertWidget(NpgsqlConnection connection, int widgetId, string widgetName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH widget_resource AS (
                SELECT "ResourceKeyId"
                FROM dms."ResourceKey"
                WHERE "ProjectName" = 'TestProject'
                AND "ResourceName" = 'Widget'
                ORDER BY "ResourceKeyId"
                LIMIT 1
            ),
            inserted_document AS (
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                SELECT gen_random_uuid(), widget_resource."ResourceKeyId"
                FROM widget_resource
                RETURNING "DocumentId"
            ),
            inserted_widget AS (
                INSERT INTO testproject."Widget" ("DocumentId", "WidgetId", "WidgetName")
                SELECT "DocumentId", @widgetId, @widgetName
                FROM inserted_document
                RETURNING "DocumentId"
            )
            SELECT "DocumentId"
            FROM inserted_widget
            """;
        command.Parameters.AddWithValue("widgetId", widgetId);
        command.Parameters.AddWithValue("widgetName", widgetName);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void UpdateWidgetName(NpgsqlConnection connection, long documentId, string widgetName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE testproject."Widget"
            SET "WidgetName" = @widgetName
            WHERE "DocumentId" = @documentId
            """;
        command.Parameters.AddWithValue("widgetName", widgetName);
        command.Parameters.AddWithValue("documentId", documentId);
        command.ExecuteNonQuery();
    }

    internal static PgsqlDocumentProjectionDocumentRow ReadDocumentRow(
        NpgsqlConnection connection,
        long documentId
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "DocumentId", "ContentVersion"
            FROM dms."Document"
            WHERE "DocumentId" = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue("the document row should exist");
        return new PgsqlDocumentProjectionDocumentRow(reader.GetInt64(0), reader.GetInt64(1));
    }

    internal static long ReadWidgetContentVersion(NpgsqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ContentVersion"
            FROM testproject."Widget"
            WHERE "DocumentId" = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void InstallForcedWorkFailureTrigger(NpgsqlConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE OR REPLACE FUNCTION dms."TF_Test_ForceDocumentProjectionWorkFailure"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'forced DocumentProjectionWork failure';
            END;
            $$;

            CREATE TRIGGER "TR_Test_ForceDocumentProjectionWorkFailure"
            BEFORE INSERT OR UPDATE ON dms."DocumentProjectionWork"
            FOR EACH ROW
            EXECUTE FUNCTION dms."TF_Test_ForceDocumentProjectionWorkFailure"();
            """
        );
    }

    internal static void DropForcedWorkFailureTrigger(NpgsqlConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            DROP TRIGGER IF EXISTS "TR_Test_ForceDocumentProjectionWorkFailure"
            ON dms."DocumentProjectionWork";
            DROP FUNCTION IF EXISTS dms."TF_Test_ForceDocumentProjectionWorkFailure"();
            """
        );
    }

    internal static void InsertDocumentCache(
        NpgsqlConnection connection,
        long documentId,
        string documentJson
    )
    {
        ExecuteNonQuery(connection, BuildDocumentCacheInsertSql(documentId, documentJson));
    }

    internal static string BuildDocumentCacheInsertSql(long documentId, string documentJson)
    {
        return $$"""
            INSERT INTO dms."DocumentCache" (
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson"
            )
            SELECT
                document."DocumentId",
                document."DocumentUuid",
                resource_key."ProjectName",
                resource_key."ResourceName",
                resource_key."ResourceVersion",
                document."ContentVersion",
                'etag-' || document."ContentVersion"::text,
                document."ContentLastModifiedAt",
                '{{documentJson}}'::jsonb
            FROM dms."Document" document
            INNER JOIN dms."ResourceKey" resource_key
                ON resource_key."ResourceKeyId" = document."ResourceKeyId"
            WHERE document."DocumentId" = {{documentId}}
            """;
    }

    internal static long ReadDocumentCacheCount(NpgsqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dms."DocumentCache"
            WHERE "DocumentId" = @documentId
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void CreateRestrictedWriterRole(string databaseName, string roleName, string password)
    {
        using (
            var adminConnection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString)
        )
        {
            adminConnection.Open();
            using var command = adminConnection.CreateCommand();
            command.CommandText = $"""
                CREATE ROLE "{roleName}" LOGIN PASSWORD '{password}';
                GRANT CONNECT ON DATABASE "{databaseName}" TO "{roleName}";
                """;
            command.ExecuteNonQuery();
        }

        using (var databaseConnection = OpenConnection(databaseName))
        {
            using var command = databaseConnection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA dms TO "{roleName}";
                GRANT INSERT, UPDATE ON TABLE dms."Document" TO "{roleName}";
                GRANT USAGE, SELECT ON SEQUENCE dms."ChangeVersionSequence" TO "{roleName}";
                """;
            command.ExecuteNonQuery();
        }
    }

    internal static void DropRoleIfExists(string databaseName, string roleName)
    {
        NpgsqlConnection.ClearAllPools();

        try
        {
            using var databaseConnection = OpenConnection(databaseName);
            using var command = databaseConnection.CreateCommand();
            command.CommandText = $"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{roleName}') THEN
                        EXECUTE 'DROP OWNED BY "{roleName}"';
                    END IF;
                END $$;
                """;
            command.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            TestContext.Progress.WriteLine(
                $"Database {databaseName} no longer exists while cleaning up role {roleName}."
            );
        }

        using (
            var adminConnection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString)
        )
        {
            adminConnection.Open();
            using var command = adminConnection.CreateCommand();
            command.CommandText = $"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{roleName}') THEN
                        EXECUTE 'REVOKE CONNECT ON DATABASE "{databaseName}" FROM "{roleName}"';
                        EXECUTE 'DROP ROLE "{roleName}"';
                    END IF;
                END $$;
                """;
            command.ExecuteNonQuery();
        }
    }

    internal static long? ReadWorkRequiredVersionForDocumentUuid(
        NpgsqlConnection connection,
        Guid documentUuid
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work."RequiredContentVersion"
            FROM dms."Document" document
            INNER JOIN dms."DocumentProjectionWork" work
                ON work."DocumentId" = document."DocumentId"
            WHERE document."DocumentUuid" = @documentUuid
            """;
        command.Parameters.AddWithValue("documentUuid", documentUuid);
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value);
    }

    internal static void ExecuteNonQuery(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static PostgresException TryExecuteNonQuery(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Action action = () => command.ExecuteNonQuery();
        return action.Should().Throw<PostgresException>().Which;
    }
}
