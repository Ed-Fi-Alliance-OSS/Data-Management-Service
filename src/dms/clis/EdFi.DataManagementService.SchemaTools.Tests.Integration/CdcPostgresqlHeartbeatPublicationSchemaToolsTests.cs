// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_PostgresqlCdcHeartbeatPublication_Provider_Setup
{
    private const string PublicationName = "dms_binding_publication";
    private const string ReplicationSlotName = "dms_binding_slot";

    private string _databaseName = null!;
    private string _connectionString = null!;
    private string _connectorRoleName = null!;

    [SetUp]
    public void SetUp()
    {
        AssumePostgresqlLogicalReplicationAvailable();

        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);
        _connectorRoleName = $"cdc_connector_{_databaseName}";

        PostgresTestDatabaseHelper.CreateDatabase(_databaseName);
        CreateConnectorRole(_connectorRoleName);

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision("pgsql", _connectionString);
        exitCode
            .Should()
            .Be(0, $"ordinary PostgreSQL provisioning must succeed. Output: {output} Error: {error}");
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_databaseName))
        {
            DropReplicationSlotIfExists();
            PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
            DropConnectorRoleIfExists();
        }
    }

    [Test]
    public async Task It_should_create_heartbeat_replica_identity_and_exact_publication_only_when_opted_in()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        TableExists(connection, "CdcHeartbeat")
            .Should()
            .BeFalse("ordinary provisioning must not create CDC heartbeat");

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result
            .HeartbeatActionQuery!.Sql.Should()
            .Be(
                """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1, "HeartbeatAt" = now() WHERE "HeartbeatId" = 1"""
            );
        result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.Document
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );
        result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.DocumentCache
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );

        AssertHeartbeatTable(connection);
        AssertDocumentReplicaIdentityFull(connection);
        AssertPublication(connection);
        AssertDocumentCacheKeepsPrimaryKeyShape(connection);
    }

    [Test]
    [Category("CdcProviderArtifacts")]
    public async Task It_should_apply_PostgresqlCdcArtifacts_only_after_opt_in_setup()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var manifestOutputDirectory = Directory.CreateTempSubdirectory("postgresql-cdc-artifacts-");
        var manifestPath = Path.Combine(manifestOutputDirectory.FullName, "cdc-provider.pgsql.manifest.json");

        try
        {
            var effectiveSchemaHashBeforeSetup = ReadEffectiveSchemaHash(connection);

            AssertOrdinaryProvisioningOmitsProviderArtifacts(connection, manifestPath);

            var result = await RunSetupAsync(
                connection,
                CdcProviderSetupMode.InitialCreateOrExactMatch,
                artifactOutput: new CdcProviderArtifactOutputRequest(
                    IncludeManifestPayload: true,
                    ManifestOutputDirectoryPath: manifestOutputDirectory.FullName
                )
            );

            result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
            result.Diagnostics.Should().BeEmpty();
            ReadEffectiveSchemaHash(connection).Should().Be(effectiveSchemaHashBeforeSetup);

            AssertHeartbeatTable(connection);
            ExecuteNonQuery(connection, result.HeartbeatActionQuery!.Sql);
            ReadHeartbeatSnapshot(connection).Should().Be(new HeartbeatSnapshot(1, 1));
            AssertDocumentReplicaIdentityFull(connection);
            AssertPublication(connection);
            AssertDocumentCacheKeepsPrimaryKeyShape(connection);

            ReadReplicationSlotSnapshot(connection)
                .Should()
                .Match<ReplicationSlotSnapshot>(slot =>
                    slot.Plugin == "pgoutput"
                    && slot.SlotType == "logical"
                    && slot.Database == _databaseName
                    && !slot.Temporary
                    && !slot.Active
                    && slot.RestartLsn.Length > 0
                    && slot.ConfirmedFlushLsn.Length > 0
                    && slot.WalStatus != "lost"
                );

            result
                .SourceTableInventory.Select(table =>
                    $"{table.TableName.Schema.Value}.{table.TableName.Name}"
                )
                .Should()
                .BeEquivalentTo("dms.Document", "dms.DocumentCache", "dms.CdcHeartbeat")
                .And.NotContain("dms.DocumentProjectionWork");
            result
                .ExpectedMessageKeyColumns.Should()
                .Contain(key =>
                    key.TableKind == CdcSourceTableKind.Document
                    && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
                )
                .And.Contain(key =>
                    key.TableKind == CdcSourceTableKind.DocumentCache
                    && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
                );
            result
                .ProviderHistoryObservations.Should()
                .ContainSingle(observation =>
                    observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                    && observation.SafeArtifactName.Value == ReplicationSlotName
                    && observation.SafeObservedValues["plugin"] == "pgoutput"
                    && observation.SafeObservedValues["retained_position_gap_evaluation"]
                        == "not_evaluated_without_committed_offset"
                );
            result.ManifestPayload.Should().NotBeNull();
            result.ManifestPayload!.FileName.Value.Should().Be("cdc-provider.pgsql.manifest.json");
            result.ManifestPayload.Json.Should().Be(await File.ReadAllTextAsync(manifestPath));
            result.ManifestPayload.Json.Should().Contain("\"provider\": \"postgresql\"");
            result.ManifestPayload.Json.Should().Contain("\"artifact_name\": \"dms_binding_publication\"");
            result.ManifestPayload.Json.Should().Contain("\"artifact_name\": \"dms_binding_slot\"");
            result.ManifestPayload.Json.Should().NotContain(_connectionString);
            result.ManifestPayload.Json.Should().NotContain("EffectiveSchemaHash");
            result.ManifestPayload.Json.Should().NotContain("ResourceKeySeedHash");
            result.ManifestPayload.Json.Should().NotContain("RelationalMappingVersion");
            result.ManifestPayload.Json.Should().NotContain("DocumentProjectionWork");
        }
        finally
        {
            Directory.Delete(manifestOutputDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task PostgresqlCdcSlotHistory_should_create_validate_and_report_retained_history()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        ReadReplicationSlotSnapshot(connection)
            .Should()
            .BeNull("ordinary provisioning must not create slots");

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        setupResult.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        setupResult.Diagnostics.Should().BeEmpty();
        setupResult
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == ReplicationSlotName
                && observation.State == CdcProviderArtifactState.Created
            );
        setupResult
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == ReplicationSlotName
                && observation.SafeObservedValues["plugin"] == "pgoutput"
                && observation.SafeObservedValues["slot_type"] == "logical"
                && observation.Classification == CdcProviderRetryContinuityClassification.None
            );

        var setupSnapshot = ReadReplicationSlotSnapshot(connection);
        setupSnapshot.Should().NotBeNull();
        setupSnapshot!.Plugin.Should().Be("pgoutput");
        setupSnapshot.SlotType.Should().Be("logical");
        setupSnapshot.Database.Should().Be(_databaseName);
        setupSnapshot.Temporary.Should().BeFalse();
        setupSnapshot.Active.Should().BeFalse();
        setupSnapshot.TwoPhase.Should().BeOneOf("false", "unsupported");
        setupSnapshot.RestartLsn.Should().NotBeNullOrWhiteSpace();
        setupSnapshot.ConfirmedFlushLsn.Should().NotBeNullOrWhiteSpace();
        setupSnapshot.WalStatus.Should().NotBe("lost");
        setupSnapshot.InvalidationReason.Should().BeEmpty();

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        validateResult.Diagnostics.Should().BeEmpty();
        ReadReplicationSlotSnapshot(connection).Should().Be(setupSnapshot);
    }

    [Test]
    public async Task PostgresqlCdcProviderMetadata_should_report_source_fingerprint_publication_and_slot_history()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var expectedSourceIdentity = ReadDataStoreIdentity(connection);
        var expectedSourceFingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.Postgresql,
            expectedSourceIdentity
        );
        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.ObservedSourceFingerprint.Should().Be(expectedSourceFingerprint);
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SourceFingerprint
                && observation.SafeArtifactName.Value == "dms.DataStoreIdentity"
                && observation.State == CdcProviderArtifactState.Matched
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.SafeObservedValues["tables"]
                    == "dms.CdcHeartbeat,dms.Document,dms.DocumentCache"
                && observation.SafeObservedValues["publish"] == "True,True,True"
                && observation.SafeObservedValues["row_filters"] == "absent"
                && observation.SafeObservedValues["column_lists"] == "absent"
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeObservedValues["plugin"] == "pgoutput"
                && observation.SafeObservedValues["restart_lsn"].Length > 0
                && observation.SafeObservedValues["confirmed_flush_lsn"].Length > 0
                && observation.SafeObservedValues["retained_position_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
        result.ManifestPayload!.Json.Should().Contain(expectedSourceFingerprint.Value);
        result.ManifestPayload.Json.Should().NotContain(expectedSourceIdentity);
        result.ManifestPayload.Json.Should().NotContain(_connectionString);
    }

    [Test]
    [Category("PostgresqlCdcAccessRetry")]
    public async Task PostgresqlCdcAccessRetry_should_exact_match_existing_artifacts_in_validate_only_without_mutating_heartbeat()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult.Diagnostics.Should().BeEmpty();
        var beforeValidate = ReadHeartbeatSnapshot(connection);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        validateResult.Diagnostics.Should().BeEmpty();
        ReadHeartbeatSnapshot(connection).Should().Be(beforeValidate);
    }

    [Test]
    [Category("PostgresqlCdcAccessRetry")]
    public async Task PostgresqlCdcPrincipalAccess_PostgresqlCdcAccessRetry_should_grant_and_validate_connector_principal_boundaries()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        setupResult.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        setupResult.Diagnostics.Should().BeEmpty();
        setupResult
            .GrantInventory.Should()
            .Contain(grant =>
                grant.SafePrincipalName.Value == _connectorRoleName
                && grant.SafeObjectName.Value == "dms.Document"
                && grant.Privileges.SequenceEqual(new[] { "SELECT" })
            );
        setupResult
            .GrantInventory.Should()
            .Contain(grant =>
                grant.SafePrincipalName.Value == _connectorRoleName
                && grant.SafeObjectName.Value == "dms.CdcHeartbeat"
                && grant.Privileges.SequenceEqual(new[] { "UPDATE" })
                && grant
                    .Columns.Select(column => column.Value)
                    .SequenceEqual(new[] { "HeartbeatSequence", "HeartbeatAt" })
            );

        AssertConnectorPrincipalAccess(connection);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);
        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        validateResult.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task CdcWorkTableExclusion_should_exclude_projection_work_from_result_and_raw_pgoutput()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var documentId = InsertDocumentBeforeProviderSlot(connection);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result
            .SourceTableInventory.Select(table => $"{table.TableName.Schema.Value}.{table.TableName.Name}")
            .Should()
            .BeEquivalentTo("dms.Document", "dms.DocumentCache", "dms.CdcHeartbeat")
            .And.NotContain("dms.DocumentProjectionWork");
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.SafeObservedValues["tables"]
                    == "dms.CdcHeartbeat,dms.Document,dms.DocumentCache"
            );
        result
            .GrantInventory.Should()
            .NotContain(grant => grant.SafeObjectName.Value == "dms.DocumentProjectionWork");
        result.ManifestPayload!.Json.Should().NotContain("DocumentProjectionWork");
        AssertPublication(connection);

        ExecuteProjectionWorkDml(connection, documentId);

        CountPeekedPgoutputChanges(connection)
            .Should()
            .Be(0, "DocumentProjectionWork is not in the provider publication");

        ExecuteNonQuery(connection, result.HeartbeatActionQuery!.Sql);
        CountPeekedPgoutputChanges(connection)
            .Should()
            .BeGreaterThan(0, "the same slot should still observe published heartbeat changes");
    }

    [Test]
    public async Task PostgresqlCdcBindingAwareValidation_should_fail_before_creating_artifacts_when_source_fingerprint_mismatches()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
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
        TableExists(connection, "CdcHeartbeat").Should().BeFalse();
        PublicationExists(connection).Should().BeFalse();
        ReadReplicationSlotSnapshot(connection).Should().BeNull();
    }

    private CdcProviderSetupRequest BuildRequest(
        ICdcProviderDatabaseExecutor databaseExecutor,
        CdcProviderSetupMode mode,
        string boundSourceIdentity,
        CdcProviderArtifactOutputRequest? artifactOutput = null
    )
    {
        var emission = CdcSchemaToolsTestMetadata.BuildMinimalDdlEmission(SqlDialect.Pgsql);

        return new(
            provider: CdcProvider.Postgresql,
            mode: mode,
            boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                CdcProvider.Postgresql,
                boundSourceIdentity
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorRoleName)),
            artifactNames: CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(PublicationName),
                new CdcSafeName(ReplicationSlotName)
            ),
            artifactOutput: artifactOutput
                ?? new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: emission.CdcSourceInventory,
            dmsManagedTableInventory: emission.CdcDmsManagedTableInventory,
            databaseExecutor: databaseExecutor
        );
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        NpgsqlConnection connection,
        CdcProviderSetupMode mode,
        string? boundSourceIdentity = null,
        CdcProviderArtifactOutputRequest? artifactOutput = null
    )
    {
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            BuildRequest(
                executor,
                mode,
                boundSourceIdentity ?? ReadDataStoreIdentity(connection),
                artifactOutput
            )
        );
    }

    private static void CreateConnectorRole(string connectorRoleName)
    {
        using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE ROLE {QuoteIdentifier(connectorRoleName)} WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;";
        command.ExecuteNonQuery();
    }

    private static void AssumePostgresqlLogicalReplicationAvailable()
    {
        using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
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

    private static bool TableExists(NpgsqlConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'dms'
                AND table_name = @table_name
            );
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        return (bool)command.ExecuteScalar()!;
    }

    private void AssertOrdinaryProvisioningOmitsProviderArtifacts(
        NpgsqlConnection connection,
        string manifestPath
    )
    {
        TableExists(connection, "CdcHeartbeat")
            .Should()
            .BeFalse("ordinary provisioning must not create CDC heartbeat");
        PublicationExists(connection)
            .Should()
            .BeFalse("ordinary provisioning must not create CDC publications");
        ReadReplicationSlotSnapshot(connection)
            .Should()
            .BeNull("ordinary provisioning must not create CDC replication slots");
        File.Exists(manifestPath)
            .Should()
            .BeFalse("ordinary provisioning must not emit CDC provider manifests");

        HasTablePrivilege(connection, "\"dms\".\"Document\"", "SELECT").Should().BeFalse();
        HasTablePrivilege(connection, "\"dms\".\"DocumentCache\"", "SELECT").Should().BeFalse();
        HasTablePrivilege(connection, "\"dms\".\"DocumentProjectionWork\"", "SELECT").Should().BeFalse();
    }

    private static bool PublicationExists(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_publication
                WHERE pubname = @publication_name
            );
            """;
        command.Parameters.AddWithValue("publication_name", PublicationName);
        return (bool)command.ExecuteScalar()!;
    }

    private void AssertConnectorPrincipalAccess(NpgsqlConnection connection)
    {
        HasDatabasePrivilege(connection, "CONNECT").Should().BeTrue();
        HasSchemaPrivilege(connection, "dms", "USAGE").Should().BeTrue();

        HasTablePrivilege(connection, "\"dms\".\"Document\"", "SELECT").Should().BeTrue();
        HasTablePrivilege(connection, "\"dms\".\"DocumentCache\"", "SELECT").Should().BeTrue();
        HasTablePrivilege(connection, "\"dms\".\"CdcHeartbeat\"", "SELECT").Should().BeTrue();
        HasTablePrivilege(connection, "\"dms\".\"DocumentProjectionWork\"", "SELECT").Should().BeFalse();
        HasTablePrivilege(connection, "\"dms\".\"Document\"", "UPDATE").Should().BeFalse();
        HasTablePrivilege(connection, "\"dms\".\"DocumentCache\"", "UPDATE").Should().BeFalse();

        HasColumnPrivilege(connection, "\"dms\".\"CdcHeartbeat\"", "HeartbeatSequence", "UPDATE")
            .Should()
            .BeTrue();
        HasColumnPrivilege(connection, "\"dms\".\"CdcHeartbeat\"", "HeartbeatAt", "UPDATE").Should().BeTrue();
        HasColumnPrivilege(connection, "\"dms\".\"CdcHeartbeat\"", "HeartbeatId", "UPDATE")
            .Should()
            .BeFalse();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                role_info.rolcanlogin,
                role_info.rolreplication,
                role_info.rolsuper,
                role_info.rolcreatedb,
                role_info.rolcreaterole,
                role_info.rolbypassrls
            FROM pg_catalog.pg_roles role_info
            WHERE role_info.rolname = @role_name;
            """;
        command.Parameters.AddWithValue("role_name", _connectorRoleName);

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue();
        reader.GetBoolean(1).Should().BeTrue();
        reader.GetBoolean(2).Should().BeFalse();
        reader.GetBoolean(3).Should().BeFalse();
        reader.GetBoolean(4).Should().BeFalse();
        reader.GetBoolean(5).Should().BeFalse();
        reader.Read().Should().BeFalse();
    }

    private bool HasDatabasePrivilege(NpgsqlConnection connection, string privilege)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_catalog.has_database_privilege(@role_name, current_database(), @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)command.ExecuteScalar()!;
    }

    private bool HasSchemaPrivilege(NpgsqlConnection connection, string schemaName, string privilege)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.has_schema_privilege(@role_name, @schema_name, @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)command.ExecuteScalar()!;
    }

    private bool HasTablePrivilege(NpgsqlConnection connection, string tableName, string privilege)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.has_table_privilege(@role_name, @table_name, @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)command.ExecuteScalar()!;
    }

    private bool HasColumnPrivilege(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        string privilege
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_catalog.has_column_privilege(@role_name, @table_name, @column_name, @privilege);";
        command.Parameters.AddWithValue("role_name", _connectorRoleName);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_name", columnName);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)command.ExecuteScalar()!;
    }

    private static string ReadDataStoreIdentity(NpgsqlConnection connection) =>
        ExecuteScalarText(
            connection,
            """
            SELECT "SourceIdentity"::text
            FROM dms."DataStoreIdentity"
            WHERE "DataStoreIdentitySingletonId" = 1;
            """
        );

    private static string ReadEffectiveSchemaHash(NpgsqlConnection connection) =>
        ExecuteScalarText(
            connection,
            """
            SELECT "EffectiveSchemaHash"
            FROM dms."EffectiveSchema";
            """
        );

    private static void AssertHeartbeatTable(NpgsqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'dms'
                AND table_name = 'CdcHeartbeat'
                ORDER BY ordinal_position;
                """;

            using var reader = command.ExecuteReader();
            List<(string ColumnName, string DataType, string IsNullable)> columns = [];
            while (reader.Read())
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            columns
                .Should()
                .Equal(
                    ("HeartbeatId", "smallint", "NO"),
                    ("HeartbeatSequence", "bigint", "NO"),
                    ("HeartbeatAt", "timestamp with time zone", "NO")
                );
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT conname
                FROM pg_catalog.pg_constraint constraint_info
                INNER JOIN pg_catalog.pg_class table_info
                    ON table_info.oid = constraint_info.conrelid
                INNER JOIN pg_catalog.pg_namespace namespace_info
                    ON namespace_info.oid = table_info.relnamespace
                WHERE namespace_info.nspname = 'dms'
                AND table_info.relname = 'CdcHeartbeat'
                ORDER BY conname;
                """;

            using var reader = command.ExecuteReader();
            List<string> constraints = [];
            while (reader.Read())
            {
                constraints.Add(reader.GetString(0));
            }

            constraints
                .Should()
                .Contain(["CK_CdcHeartbeat_Sequence", "CK_CdcHeartbeat_Singleton", "PK_CdcHeartbeat"]);
        }

        ReadHeartbeatSnapshot(connection).Should().Be(new HeartbeatSnapshot(1, 0));
    }

    private static HeartbeatSnapshot ReadHeartbeatSnapshot(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "HeartbeatId", "HeartbeatSequence"
            FROM "dms"."CdcHeartbeat";
            """;

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        var snapshot = new HeartbeatSnapshot(reader.GetInt16(0), reader.GetInt64(1));
        reader.Read().Should().BeFalse();
        return snapshot;
    }

    private static long InsertDocumentBeforeProviderSlot(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@document_uuid, 1)
            RETURNING "DocumentId";
            """;
        command.Parameters.AddWithValue("document_uuid", Guid.NewGuid());

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ExecuteProjectionWorkDml(NpgsqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM dms."DocumentProjectionWork"
            WHERE "DocumentId" = @document_id;

            INSERT INTO dms."DocumentProjectionWork" (
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            )
            VALUES (@document_id, 1, now(), now());

            UPDATE dms."DocumentProjectionWork"
            SET "RequiredContentVersion" = "RequiredContentVersion" + 1,
                "LastEnqueuedAt" = now()
            WHERE "DocumentId" = @document_id;

            DELETE FROM dms."DocumentProjectionWork"
            WHERE "DocumentId" = @document_id;
            """;
        command.Parameters.AddWithValue("document_id", documentId);
        command.ExecuteNonQuery();
    }

    private static long CountPeekedPgoutputChanges(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_catalog.pg_logical_slot_peek_binary_changes(
                CAST(@slot_name AS name),
                NULL::pg_lsn,
                NULL::integer,
                'proto_version',
                '1',
                'publication_names',
                CAST(@publication_name AS text)
            );
            """;
        command.Parameters.AddWithValue("slot_name", ReplicationSlotName);
        command.Parameters.AddWithValue("publication_name", PublicationName);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ExecuteNonQuery(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertDocumentReplicaIdentityFull(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_info.relreplident
            FROM pg_catalog.pg_class table_info
            INNER JOIN pg_catalog.pg_namespace namespace_info
                ON namespace_info.oid = table_info.relnamespace
            WHERE namespace_info.nspname = 'dms'
            AND table_info.relname = 'Document';
            """;

        command.ExecuteScalar()!.ToString().Should().Be("f");
    }

    private static void AssertPublication(NpgsqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT pubinsert, pubupdate, pubdelete, pubtruncate, puballtables, pubviaroot
                FROM pg_catalog.pg_publication
                WHERE pubname = @publication_name;
                """;
            command.Parameters.AddWithValue("publication_name", PublicationName);

            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetBoolean(0).Should().BeTrue();
            reader.GetBoolean(1).Should().BeTrue();
            reader.GetBoolean(2).Should().BeTrue();
            reader.GetBoolean(3).Should().BeFalse();
            reader.GetBoolean(4).Should().BeFalse();
            reader.GetBoolean(5).Should().BeFalse();
            reader.Read().Should().BeFalse();
        }

        using (var command = connection.CreateCommand())
        {
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

            using var reader = command.ExecuteReader();
            List<string> tables = [];
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            tables.Should().Equal("dms.CdcHeartbeat", "dms.Document", "dms.DocumentCache");
            tables.Should().NotContain("dms.DocumentProjectionWork");
        }
    }

    private static void AssertDocumentCacheKeepsPrimaryKeyShape(NpgsqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT pg_catalog.array_agg(attribute_info.attname ORDER BY key_column.ordinality)
                FROM pg_catalog.pg_constraint constraint_info
                INNER JOIN pg_catalog.pg_class table_info
                    ON table_info.oid = constraint_info.conrelid
                INNER JOIN pg_catalog.pg_namespace namespace_info
                    ON namespace_info.oid = table_info.relnamespace
                INNER JOIN pg_catalog.unnest(constraint_info.conkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                    ON TRUE
                INNER JOIN pg_catalog.pg_attribute attribute_info
                    ON attribute_info.attrelid = table_info.oid
                    AND attribute_info.attnum = key_column.attnum
                WHERE namespace_info.nspname = 'dms'
                AND table_info.relname = 'DocumentCache'
                AND constraint_info.contype = 'p';
                """;

            ((string[])command.ExecuteScalar()!).Should().Equal("DocumentId");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM pg_catalog.pg_indexes
                WHERE schemaname = 'dms'
                AND tablename = 'DocumentCache'
                AND indexdef LIKE '%"DocumentUuid"%';
                """;

            Convert.ToInt64(command.ExecuteScalar()).Should().Be(0);
        }
    }

    private void DropReplicationSlotIfExists()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pg_catalog.pg_drop_replication_slot(slot.slot_name)
                FROM pg_catalog.pg_replication_slots slot
                WHERE slot.slot_name = @slot_name
                AND slot.active = false;
                """;
            command.Parameters.AddWithValue("slot_name", ReplicationSlotName);
            command.ExecuteNonQuery();
        }
        catch (PostgresException)
        {
            // The database drop helper still performs final cleanup for cases where the database no longer exists.
        }
        catch (NpgsqlException)
        {
            // The database drop helper still performs final cleanup for cases where the database no longer exists.
        }
    }

    private void DropConnectorRoleIfExists()
    {
        try
        {
            using var connection = new NpgsqlConnection(DatabaseConfiguration.PostgresAdminConnectionString);
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

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static ReplicationSlotSnapshot? ReadReplicationSlotSnapshot(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
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
        command.Parameters.AddWithValue("slot_name", ReplicationSlotName);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
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
        reader.Read().Should().BeFalse();
        return snapshot;
    }

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
