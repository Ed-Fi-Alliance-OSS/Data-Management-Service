// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CdcProviderAccessRetry")]
public class Given_PostgresqlCdcProviderAccessRetry
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const string PublicationName = "dms_binding_publication";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorRoleName = null!;
    private string _replicationSlotName = null!;

    [SetUp]
    public async Task SetUp()
    {
        AssumePostgresqlLogicalReplicationAvailable();

        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorRoleName = $"cdc_connector_{_database.DatabaseName}";
        _replicationSlotName = $"dms_binding_slot_{_database.DatabaseName}";

        CreateConnectorRole(_connectorRoleName, canReplicate: true);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await DropReplicationSlotIfExistsAsync();
            await _database.DisposeAsync();
        }

        DropConnectorRoleIfExists();
    }

    [Test]
    public async Task It_should_create_connector_access_and_pass_live_boundary_probe_on_initial_setup()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            connectorPrincipalProbeFactory: new PostgresqlConnectorPrincipalBoundaryProbeFactory(
                _database.ConnectionString
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.State == CdcProviderArtifactState.Created
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.State == CdcProviderArtifactState.Created
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == _replicationSlotName
                && observation.State == CdcProviderArtifactState.Created
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeArtifactName.Value == _connectorRoleName
                && observation.State == CdcProviderArtifactState.Created
            );

        AssertRequiredGrantInventory(result);
        await AssertConnectorPrincipalAccessAsync(connection);
    }

    [Test]
    public async Task It_should_fail_closed_when_optional_live_probe_reports_connector_boundary_failure()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            connectorPrincipalProbeFactory: new FailingPostgresqlConnectorPrincipalProbeFactory()
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        var diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_PROBE_BOUNDARY_FAILURE"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            )
            .Which;
        diagnostic.ProviderErrorClass.Should().BeNull();
        await AssertConnectorPrincipalAccessAsync(connection);
    }

    [Test]
    public async Task It_should_exact_match_rerun_and_validate_only_without_mutating_source_fingerprint_or_slot_history()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult.Diagnostics.Should().BeEmpty();

        var sourceIdentity = await ReadDataStoreIdentityAsync(connection);
        var effectiveSchemaHash = await ReadEffectiveSchemaHashAsync(connection);
        var heartbeatSnapshot = await ReadHeartbeatSnapshotAsync(connection);
        var slotSnapshot = await ReadReplicationSlotSnapshotAsync(connection);
        slotSnapshot.Should().NotBeNull();

        var rerunResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        rerunResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        rerunResult.Diagnostics.Should().BeEmpty();
        rerunResult
            .ArtifactInventory.Should()
            .OnlyContain(observation => observation.State == CdcProviderArtifactState.Matched);
        rerunResult
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == _replicationSlotName
                && observation.State == CdcProviderArtifactState.Matched
            );

        (await ReadDataStoreIdentityAsync(connection)).Should().Be(sourceIdentity);
        (await ReadEffectiveSchemaHashAsync(connection)).Should().Be(effectiveSchemaHash);
        (await ReadHeartbeatSnapshotAsync(connection)).Should().Be(heartbeatSnapshot);
        (await ReadReplicationSlotSnapshotAsync(connection)).Should().Be(slotSnapshot);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        validateResult.Diagnostics.Should().BeEmpty();
        (await ReadDataStoreIdentityAsync(connection)).Should().Be(sourceIdentity);
        (await ReadEffectiveSchemaHashAsync(connection)).Should().Be(effectiveSchemaHash);
        (await ReadHeartbeatSnapshotAsync(connection)).Should().Be(heartbeatSnapshot);
        (await ReadReplicationSlotSnapshotAsync(connection)).Should().Be(slotSnapshot);
    }

    [Test]
    public async Task It_should_report_missing_required_artifacts_in_validate_only_without_creating_them()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
            );
        (await TableExistsAsync(connection, "CdcHeartbeat")).Should().BeFalse();
        (await PublicationExistsAsync(connection)).Should().BeFalse();
        (await ReadReplicationSlotSnapshotAsync(connection)).Should().BeNull();
    }

    [Test]
    public async Task It_should_report_missing_slot_in_validate_only_without_recreating_retained_history()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await CreateProviderArtifactsThroughPublicationAsync(connection);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
            );
        (await ReadReplicationSlotSnapshotAsync(connection)).Should().BeNull();
    }

    [Test]
    public async Task It_should_retry_partial_initial_setup_by_exact_matching_existing_slot_and_creating_missing_grants()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        SetConnectorRoleReplication(canReplicate: false);

        var failedSetupResult = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch
        );

        failedSetupResult.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        failedSetupResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_ROLE_ATTRIBUTES_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            );
        var createdSlot = await ReadReplicationSlotSnapshotAsync(connection);
        createdSlot.Should().NotBeNull();

        SetConnectorRoleReplication(canReplicate: true);

        var retryResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        retryResult.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        retryResult.Diagnostics.Should().BeEmpty();
        retryResult
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == _replicationSlotName
                && observation.State == CdcProviderArtifactState.Matched
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeArtifactName.Value == _connectorRoleName
                && observation.State == CdcProviderArtifactState.Created
            );
        (await ReadReplicationSlotSnapshotAsync(connection)).Should().Be(createdSlot);
        await AssertConnectorPrincipalAccessAsync(connection);
    }

    [Test]
    public async Task It_should_fail_closed_on_mismatched_grants_without_removing_them()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult.Diagnostics.Should().BeEmpty();

        await ExecuteNonQueryAsync(
            connection,
            $"GRANT SELECT ON TABLE \"dms\".\"DocumentProjectionWork\" TO {QuoteIdentifier(_connectorRoleName)};"
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        validateResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
            )
            .And.Contain(diagnostic => diagnostic.Code == "CDC_BINDING_WORK_TABLE_GRANT_FORBIDDEN");
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"DocumentProjectionWork\"", "SELECT"))
            .Should()
            .BeTrue("validation reports the mismatch without destructive cleanup");
    }

    [Test]
    public async Task It_should_fail_closed_on_elevated_connector_role_without_downgrading_it()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        SetConnectorRoleCreatedb(canCreateDatabase: true);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_ROLE_ATTRIBUTES_MISMATCH"
                && diagnostic.ObservedValue!.Contains("CREATEDB")
            );
        (await ConnectorRoleCanCreateDatabaseAsync()).Should().BeTrue();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"Document\"", "SELECT"))
            .Should()
            .BeFalse("setup must not grant access when connector role attributes are unsafe");
    }

    [Test]
    public async Task It_should_fail_closed_on_work_table_publication_membership_without_removing_it()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult.Diagnostics.Should().BeEmpty();

        await ExecuteNonQueryAsync(
            connection,
            $"ALTER PUBLICATION {QuoteIdentifier(PublicationName)} ADD TABLE \"dms\".\"DocumentProjectionWork\";"
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        validateResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_WORK_TABLE_PUBLICATION_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
            );
        (await PublicationTablesAsync(connection))
            .Should()
            .Contain(
                "dms.DocumentProjectionWork",
                "validation reports but does not repair mismatched capture"
            );
    }

    [Test]
    public async Task It_should_fail_source_fingerprint_mismatch_before_creating_provider_artifacts()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            boundSourceIdentity: "11111111-1111-1111-1111-111111111111"
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_SOURCE_FINGERPRINT_MISMATCH");
        (await TableExistsAsync(connection, "CdcHeartbeat")).Should().BeFalse();
        (await PublicationExistsAsync(connection)).Should().BeFalse();
        (await ReadReplicationSlotSnapshotAsync(connection)).Should().BeNull();
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        NpgsqlConnection connection,
        CdcProviderSetupMode mode,
        string? boundSourceIdentity = null,
        ICdcConnectorPrincipalProbeFactory? connectorPrincipalProbeFactory = null
    )
    {
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: CdcProvider.Postgresql,
                mode: mode,
                boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                    CdcProvider.Postgresql,
                    boundSourceIdentity ?? await ReadDataStoreIdentityAsync(connection)
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorRoleName)),
                artifactNames: CdcProviderArtifactNames.ForPostgresql(
                    new CdcSafeName(PublicationName),
                    new CdcSafeName(_replicationSlotName)
                ),
                artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
                expectedSourceInventory: CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
                    SqlDialectFactory.Create(SqlDialect.Pgsql)
                ),
                connectorPrincipalProbeFactory: connectorPrincipalProbeFactory,
                databaseExecutor: executor
            )
        );
    }

    private static void AssertRequiredGrantInventory(CdcProviderSetupResult result)
    {
        result
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafeObjectName.Value == "dms.Document"
                && grant.Privileges.SequenceEqual(new[] { "SELECT" })
            );
        result
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafeObjectName.Value == "dms.DocumentCache"
                && grant.Privileges.SequenceEqual(new[] { "SELECT" })
            );
        result
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafeObjectName.Value == "dms.CdcHeartbeat"
                && grant.Privileges.SequenceEqual(new[] { "UPDATE" })
                && grant
                    .Columns.Select(column => column.Value)
                    .SequenceEqual(new[] { "HeartbeatSequence", "HeartbeatAt" })
            );
        result
            .GrantInventory.Should()
            .NotContain(grant => grant.SafeObjectName.Value == "dms.DocumentProjectionWork");
    }

    private async Task AssertConnectorPrincipalAccessAsync(NpgsqlConnection connection)
    {
        (await HasDatabasePrivilegeAsync(connection, "CONNECT")).Should().BeTrue();
        (await HasSchemaPrivilegeAsync(connection, "dms", "USAGE")).Should().BeTrue();

        (await HasTablePrivilegeAsync(connection, "\"dms\".\"Document\"", "SELECT")).Should().BeTrue();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"DocumentCache\"", "SELECT")).Should().BeTrue();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"CdcHeartbeat\"", "SELECT")).Should().BeTrue();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"DocumentProjectionWork\"", "SELECT"))
            .Should()
            .BeFalse();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"Document\"", "UPDATE")).Should().BeFalse();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"DocumentCache\"", "UPDATE")).Should().BeFalse();

        (await HasColumnPrivilegeAsync(connection, "\"dms\".\"CdcHeartbeat\"", "HeartbeatSequence", "UPDATE"))
            .Should()
            .BeTrue();
        (await HasColumnPrivilegeAsync(connection, "\"dms\".\"CdcHeartbeat\"", "HeartbeatAt", "UPDATE"))
            .Should()
            .BeTrue();
        (await HasColumnPrivilegeAsync(connection, "\"dms\".\"CdcHeartbeat\"", "HeartbeatId", "UPDATE"))
            .Should()
            .BeFalse();
    }

    private static async Task CreateProviderArtifactsThroughPublicationAsync(NpgsqlConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE TABLE "dms"."CdcHeartbeat"
            (
                "HeartbeatId" smallint NOT NULL,
                "HeartbeatSequence" bigint NOT NULL,
                "HeartbeatAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_CdcHeartbeat" PRIMARY KEY ("HeartbeatId"),
                CONSTRAINT "CK_CdcHeartbeat_Singleton" CHECK ("HeartbeatId" = 1),
                CONSTRAINT "CK_CdcHeartbeat_Sequence" CHECK ("HeartbeatSequence" >= 0)
            );

            INSERT INTO "dms"."CdcHeartbeat" ("HeartbeatId", "HeartbeatSequence", "HeartbeatAt")
            VALUES (1, 0, now());

            ALTER TABLE "dms"."Document" REPLICA IDENTITY FULL;

            CREATE PUBLICATION {QuoteIdentifier(PublicationName)}
            FOR TABLE "dms"."DocumentCache", "dms"."Document", "dms"."CdcHeartbeat"
            WITH (publish = 'insert, update, delete', publish_via_partition_root = false);
            """
        );
    }

    private static void AssumePostgresqlLogicalReplicationAvailable()
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        var walLevel = ExecuteScalarText(connection, "SHOW wal_level;");
        var maxReplicationSlots = int.Parse(ExecuteScalarText(connection, "SHOW max_replication_slots;"));

        if (!string.Equals(walLevel, "logical", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                $"PostgreSQL logical replication tests require wal_level=logical; observed wal_level={walLevel}."
            );
        }

        if (maxReplicationSlots < 1)
        {
            Assert.Ignore(
                $"PostgreSQL logical replication tests require max_replication_slots >= 1; observed {maxReplicationSlots}."
            );
        }
    }

    private static string ExecuteScalarText(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!.ToString()!;
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadDataStoreIdentityAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "SourceIdentity"::text
            FROM dms."DataStoreIdentity"
            WHERE "DataStoreIdentitySingletonId" = 1;
            """;
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<string> ReadEffectiveSchemaHashAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "EffectiveSchemaHash"
            FROM dms."EffectiveSchema";
            """;
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<HeartbeatSnapshot> ReadHeartbeatSnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "HeartbeatId", "HeartbeatSequence"
            FROM "dms"."CdcHeartbeat";
            """;

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var snapshot = new HeartbeatSnapshot(reader.GetInt16(0), reader.GetInt64(1));
        (await reader.ReadAsync()).Should().BeFalse();
        return snapshot;
    }

    private async Task<ReplicationSlotSnapshot?> ReadReplicationSlotSnapshotAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                slot.plugin,
                slot.slot_type,
                slot.database,
                slot.temporary,
                slot.active,
                COALESCE(to_jsonb(slot)->>'two_phase', 'unsupported') AS two_phase,
                COALESCE(slot.restart_lsn::text, '') AS restart_lsn,
                COALESCE(slot.confirmed_flush_lsn::text, '') AS confirmed_flush_lsn,
                COALESCE(to_jsonb(slot)->>'wal_status', 'unavailable') AS wal_status,
                COALESCE(to_jsonb(slot)->>'invalidation_reason', '') AS invalidation_reason
            FROM pg_catalog.pg_replication_slots slot
            WHERE slot.slot_name = @slot_name;
            """;
        command.Parameters.AddWithValue("slot_name", _replicationSlotName);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var snapshot = new ReplicationSlotSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9)
        );
        (await reader.ReadAsync()).Should().BeFalse();
        return snapshot;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'dms'
                AND table_name = @table_name
            );
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> PublicationExistsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_publication
                WHERE pubname = @publication_name
            );
            """;
        command.Parameters.AddWithValue("publication_name", PublicationName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<string>> PublicationTablesAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace_info.nspname || '.' || table_info.relname
            FROM pg_catalog.pg_publication_rel publication_table
            INNER JOIN pg_catalog.pg_publication publication
                ON publication.oid = publication_table.prpubid
            INNER JOIN pg_catalog.pg_class table_info
                ON table_info.oid = publication_table.prrelid
            INNER JOIN pg_catalog.pg_namespace namespace_info
                ON namespace_info.oid = table_info.relnamespace
            WHERE publication.pubname = @publication_name
            ORDER BY namespace_info.nspname, table_info.relname;
            """;
        command.Parameters.AddWithValue("publication_name", PublicationName);

        await using var reader = await command.ExecuteReaderAsync();
        List<string> tables = [];
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private async Task<bool> HasDatabasePrivilegeAsync(NpgsqlConnection connection, string privilege)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_catalog.has_database_privilege(@role_name, current_database(), @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> HasSchemaPrivilegeAsync(
        NpgsqlConnection connection,
        string schemaName,
        string privilege
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.has_schema_privilege(@role_name, @schema_name, @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> HasTablePrivilegeAsync(
        NpgsqlConnection connection,
        string tableName,
        string privilege
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.has_table_privilege(@role_name, @table_name, @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> HasColumnPrivilegeAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        string privilege
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_catalog.has_column_privilege(@role_name, @table_name, @column_name, @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_name", columnName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> ConnectorRoleCanCreateDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rolcreatedb
            FROM pg_catalog.pg_roles
            WHERE rolname = @role_name;
            """;
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task DropReplicationSlotIfExistsAsync()
    {
        if (string.IsNullOrWhiteSpace(_replicationSlotName))
        {
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pg_catalog.pg_drop_replication_slot(slot.slot_name)
                FROM pg_catalog.pg_replication_slots slot
                WHERE slot.slot_name = @slot_name
                AND slot.active = false;
                """;
            command.Parameters.AddWithValue("slot_name", _replicationSlotName);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            // Database cleanup owns dependent objects; slot cleanup is best-effort in teardown.
        }
        catch (NpgsqlException)
        {
            // Database cleanup owns dependent objects; slot cleanup is best-effort in teardown.
        }
    }

    private void DropConnectorRoleIfExists()
    {
        if (string.IsNullOrWhiteSpace(_connectorRoleName))
        {
            return;
        }

        try
        {
            using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"DROP ROLE IF EXISTS {QuoteIdentifier(_connectorRoleName)};";
            command.ExecuteNonQuery();
        }
        catch (PostgresException)
        {
            // Database cleanup owns dependent objects; role cleanup is best-effort in teardown.
        }
        catch (NpgsqlException)
        {
            // Database cleanup owns dependent objects; role cleanup is best-effort in teardown.
        }
    }

    private static void CreateConnectorRole(string connectorRoleName, bool canReplicate)
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE ROLE {QuoteIdentifier(connectorRoleName)} WITH LOGIN {(canReplicate ? "REPLICATION" : "NOREPLICATION")} NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;";
        command.ExecuteNonQuery();
    }

    private void SetConnectorRoleReplication(bool canReplicate)
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER ROLE {QuoteIdentifier(_connectorRoleName)} {(canReplicate ? "REPLICATION" : "NOREPLICATION")};";
        command.ExecuteNonQuery();
    }

    private void SetConnectorRoleCreatedb(bool canCreateDatabase)
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER ROLE {QuoteIdentifier(_connectorRoleName)} {(canCreateDatabase ? "CREATEDB" : "NOCREATEDB")};";
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record HeartbeatSnapshot(short HeartbeatId, long HeartbeatSequence);

    private sealed record ReplicationSlotSnapshot(
        string Plugin,
        string SlotType,
        string Database,
        bool Temporary,
        bool Active,
        string TwoPhase,
        string RestartLsn,
        string ConfirmedFlushLsn,
        string WalStatus,
        string InvalidationReason
    );
}

internal sealed class PostgresqlConnectorPrincipalBoundaryProbeFactory(string connectionString)
    : ICdcConnectorPrincipalProbeFactory
{
    public async Task<CdcConnectorPrincipalProbeResult> ProbeAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    )
    {
        List<string> failures = [];

        foreach (
            var sql in new[]
            {
                """SELECT 1 FROM "dms"."Document" LIMIT 0;""",
                """SELECT 1 FROM "dms"."DocumentCache" LIMIT 0;""",
                """SELECT 1 FROM "dms"."CdcHeartbeat" LIMIT 0;""",
                """
                    UPDATE "dms"."CdcHeartbeat"
                    SET "HeartbeatSequence" = "HeartbeatSequence"
                    WHERE "HeartbeatId" = 1;
                    """,
            }
        )
        {
            var failure = await TryRunAsConnectorAsync(
                request.ConnectorPrincipal.SafePrincipalName.Value,
                sql,
                expectPrivilegeFailure: false,
                cancellationToken
            );
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        foreach (
            var sql in new[]
            {
                """SELECT 1 FROM "dms"."DocumentProjectionWork" LIMIT 0;""",
                """UPDATE "dms"."Document" SET "ResourceKeyId" = "ResourceKeyId" WHERE false;""",
                """UPDATE "dms"."DocumentCache" SET "DocumentUuid" = "DocumentUuid" WHERE false;""",
                """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatId" = "HeartbeatId" WHERE false;""",
            }
        )
        {
            var failure = await TryRunAsConnectorAsync(
                request.ConnectorPrincipal.SafePrincipalName.Value,
                sql,
                expectPrivilegeFailure: true,
                cancellationToken
            );
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count == 0)
        {
            return new CdcConnectorPrincipalProbeResult();
        }

        return new CdcConnectorPrincipalProbeResult(
            GrantInventory: [],
            Diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_POSTGRESQL_CONNECTOR_PROBE_BOUNDARY_FAILURE",
                    Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.Grant,
                    SafeName: request.ConnectorPrincipal.SafePrincipalName,
                    ExpectedValue: "rolled-back-boundary-probe-success",
                    ObservedValue: string.Join(";", failures),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );
    }

    private async Task<string?> TryRunAsConnectorAsync(
        string connectorRoleName,
        string sql,
        bool expectPrivilegeFailure,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SET LOCAL ROLE {QuoteIdentifier(connectorRoleName)};
                {sql}
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);

            return expectPrivilegeFailure ? $"expected-privilege-failure:{TrimSql(sql)}" : null;
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return expectPrivilegeFailure ? null : $"unexpected-privilege-failure:{TrimSql(sql)}";
        }
        catch (NpgsqlException exception)
        {
            return $"{exception.GetType().Name}:{TrimSql(sql)}";
        }
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string TrimSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

internal sealed class FailingPostgresqlConnectorPrincipalProbeFactory : ICdcConnectorPrincipalProbeFactory
{
    public Task<CdcConnectorPrincipalProbeResult> ProbeAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            new CdcConnectorPrincipalProbeResult(
                GrantInventory: [],
                Diagnostics:
                [
                    new CdcProviderDiagnostic(
                        Code: "CDC_POSTGRESQL_CONNECTOR_PROBE_BOUNDARY_FAILURE",
                        Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                        Severity: CdcProviderDiagnosticSeverity.Error,
                        PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                        ArtifactKind: CdcProviderArtifactKind.Grant,
                        SafeName: request.ConnectorPrincipal.SafePrincipalName,
                        ExpectedValue: "rolled-back-boundary-probe-success",
                        ObservedValue: "probe-failed",
                        ProviderErrorClass: null,
                        Classification: CdcProviderRetryContinuityClassification.FailClosed
                    ),
                ]
            )
        );
}
