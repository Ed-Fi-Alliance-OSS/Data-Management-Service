// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CdcProviderArtifacts")]
public class Given_PostgresqlCdcProviderArtifacts
{
    internal const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    internal const string PublicationName = "dms_binding_publication";
    private const string ReplicationSlotName = "dms_binding_slot";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorRoleName = null!;

    [SetUp]
    public async Task SetUp()
    {
        AssumePostgresqlLogicalReplicationAvailable();

        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorRoleName = $"cdc_connector_{_database.DatabaseName}";

        CreateConnectorRole(_connectorRoleName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await DropReplicationSlotIfExistsAsync(_database.ConnectionString, ReplicationSlotName);
            await _database.DisposeAsync();
        }

        DropConnectorRoleIfExists(_connectorRoleName);
    }

    [Test]
    public async Task It_should_apply_PostgresqlCdcArtifacts_to_generated_ddl_database_only_after_opt_in()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var manifestOutputDirectory = Directory.CreateTempSubdirectory("postgresql-cdc-artifacts-");
        var manifestPath = Path.Combine(manifestOutputDirectory.FullName, "cdc-provider.pgsql.manifest.json");

        try
        {
            var effectiveSchemaHashBeforeSetup = await ReadEffectiveSchemaHashAsync(connection);

            await AssertOrdinaryProvisioningOmitsProviderArtifactsAsync(connection, manifestPath);

            var result = await RunSetupAsync(
                connection,
                new CdcProviderArtifactOutputRequest(
                    IncludeManifestPayload: true,
                    ManifestOutputDirectoryPath: manifestOutputDirectory.FullName
                )
            );

            result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
            result.InitialReplicationSlotProof.Should().NotBeNull();
            result.InitialReplicationSlotProof!.ReplicationSlotName.Value.Should().Be(ReplicationSlotName);
            result
                .InitialReplicationSlotProof.SourceFingerprint.Should()
                .Be(result.ObservedSourceFingerprint);
            result
                .InitialReplicationSlotProof.DatabaseIdentityToken.Value.Should()
                .StartWith("postgresql_database_identity_sha256:");
            result.Diagnostics.Should().BeEmpty();
            (await ReadEffectiveSchemaHashAsync(connection)).Should().Be(effectiveSchemaHashBeforeSetup);

            await AssertHeartbeatTableAsync(connection);
            await ExecuteNonQueryAsync(connection, result.HeartbeatActionQuery!.Sql);
            (await ReadHeartbeatSnapshotAsync(connection)).Should().Be(new HeartbeatSnapshot(1, 1));
            await AssertDocumentReplicaIdentityFullAsync(connection);
            await AssertPublicationAsync(connection);
            await AssertDocumentCacheKeepsPrimaryKeyShapeAsync(connection);

            var slot = await ReadReplicationSlotSnapshotAsync(connection);
            slot.Should().NotBeNull();
            slot!.Plugin.Should().Be("pgoutput");
            slot.SlotType.Should().Be("logical");
            slot.Database.Should().Be(_database.DatabaseName);
            slot.Temporary.Should().BeFalse();
            slot.Active.Should().BeFalse();
            slot.RestartLsn.Should().NotBeNullOrWhiteSpace();
            slot.ConfirmedFlushLsn.Should().NotBeNullOrWhiteSpace();
            slot.WalStatus.Should().NotBe("lost");

            result
                .SourceTableInventory.Select(table =>
                    $"{table.TableName.Schema.Value}.{table.TableName.Name}"
                )
                .Should()
                .BeEquivalentTo("dms.Document", "dms.DocumentCache", "dms.CdcHeartbeat")
                .And.NotContain("dms.DocumentProjectionWork");
            result
                .ArtifactInventory.Should()
                .Contain(observation =>
                    observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                    && observation.SafeArtifactName.Value == PublicationName
                    && observation.SafeObservedValues["tables"]
                        == "dms.CdcHeartbeat,dms.Document,dms.DocumentCache"
                )
                .And.Contain(observation =>
                    observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                    && observation.SafeArtifactName.Value == ReplicationSlotName
                    && observation.State == CdcProviderArtifactState.Created
                );
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
            result.ManifestPayload.Json.Should().NotContain(_database.ConnectionString);
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
    public async Task It_should_emit_raw_pgoutput_only_for_provider_source_records()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var documentId = await InsertDocumentBeforeProviderSlotAsync(connection);

        var result = await RunSetupAsync(connection, new CdcProviderArtifactOutputRequest(false));

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();

        await ExecuteProjectionWorkDmlAsync(connection, documentId);
        (await CountPeekedPgoutputChangesAsync(connection))
            .Should()
            .Be(0, "DocumentProjectionWork is not in the provider publication");

        await ExecuteNonQueryAsync(connection, result.HeartbeatActionQuery!.Sql);
        (await CountPeekedPgoutputChangesAsync(connection))
            .Should()
            .BeGreaterThan(0, "the same provider slot should still observe published heartbeat changes");
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        NpgsqlConnection connection,
        CdcProviderArtifactOutputRequest artifactOutput
    )
    {
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: CdcProvider.Postgresql,
                mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
                boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                    CdcProvider.Postgresql,
                    await ReadDataStoreIdentityAsync(connection)
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorRoleName)),
                artifactNames: CdcProviderArtifactNames.ForPostgresql(
                    new CdcSafeName(PublicationName),
                    new CdcSafeName(ReplicationSlotName)
                ),
                artifactOutput: artifactOutput,
                expectedSourceInventory: _fixture.CdcSourceInventory,
                dmsManagedTableInventory: _fixture.CdcDmsManagedTableInventory,
                databaseExecutor: executor
            )
        );
    }

    private async Task AssertOrdinaryProvisioningOmitsProviderArtifactsAsync(
        NpgsqlConnection connection,
        string manifestPath
    )
    {
        (await TableExistsAsync(connection, "CdcHeartbeat"))
            .Should()
            .BeFalse("ordinary provisioning must not create CDC heartbeat");
        (await PublicationExistsAsync(connection))
            .Should()
            .BeFalse("ordinary provisioning must not create CDC publications");
        (await ReadReplicationSlotSnapshotAsync(connection))
            .Should()
            .BeNull("ordinary provisioning must not create CDC replication slots");
        File.Exists(manifestPath)
            .Should()
            .BeFalse("ordinary provisioning must not emit CDC provider manifests");

        (await HasTablePrivilegeAsync(connection, "\"dms\".\"Document\"", "SELECT")).Should().BeFalse();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"DocumentCache\"", "SELECT")).Should().BeFalse();
        (await HasTablePrivilegeAsync(connection, "\"dms\".\"DocumentProjectionWork\"", "SELECT"))
            .Should()
            .BeFalse();
    }

    internal static void AssumePostgresqlLogicalReplicationAvailable()
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

    internal static async Task<string> ReadDataStoreIdentityAsync(NpgsqlConnection connection)
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

    private static async Task AssertHeartbeatTableAsync(NpgsqlConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'dms'
                AND table_name = 'CdcHeartbeat'
                ORDER BY ordinal_position;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            List<(string ColumnName, string DataType, string IsNullable)> columns = [];
            while (await reader.ReadAsync())
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

        (await ReadHeartbeatSnapshotAsync(connection)).Should().Be(new HeartbeatSnapshot(1, 0));
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

    private static async Task AssertDocumentReplicaIdentityFullAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_info.relreplident
            FROM pg_catalog.pg_class table_info
            INNER JOIN pg_catalog.pg_namespace namespace_info
                ON namespace_info.oid = table_info.relnamespace
            WHERE namespace_info.nspname = 'dms'
            AND table_info.relname = 'Document';
            """;

        (await command.ExecuteScalarAsync())!.ToString().Should().Be("f");
    }

    private static async Task AssertPublicationAsync(NpgsqlConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT pubinsert, pubupdate, pubdelete, pubtruncate, puballtables, pubviaroot
                FROM pg_catalog.pg_publication
                WHERE pubname = @publication_name;
                """;
            command.Parameters.AddWithValue("publication_name", PublicationName);

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetBoolean(0).Should().BeTrue();
            reader.GetBoolean(1).Should().BeTrue();
            reader.GetBoolean(2).Should().BeTrue();
            reader.GetBoolean(3).Should().BeFalse();
            reader.GetBoolean(4).Should().BeFalse();
            reader.GetBoolean(5).Should().BeFalse();
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using (var command = connection.CreateCommand())
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

            await using var reader = await command.ExecuteReaderAsync();
            List<string> tables = [];
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            tables.Should().Equal("dms.CdcHeartbeat", "dms.Document", "dms.DocumentCache");
            tables.Should().NotContain("dms.DocumentProjectionWork");
        }
    }

    private static async Task AssertDocumentCacheKeepsPrimaryKeyShapeAsync(NpgsqlConnection connection)
    {
        await using (var command = connection.CreateCommand())
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

            ((string[])(await command.ExecuteScalarAsync())!).Should().Equal("DocumentId");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM pg_catalog.pg_indexes
                WHERE schemaname = 'dms'
                AND tablename = 'DocumentCache'
                AND indexdef LIKE '%"DocumentUuid"%';
                """;

            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
        }
    }

    internal static async Task DropReplicationSlotIfExistsAsync(
        string databaseConnectionString,
        string replicationSlotName
    )
    {
        try
        {
            await using var connection = new NpgsqlConnection(databaseConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pg_catalog.pg_drop_replication_slot(slot.slot_name)
                FROM pg_catalog.pg_replication_slots slot
                WHERE slot.slot_name = @slot_name
                AND slot.active = false;
                """;
            command.Parameters.AddWithValue("slot_name", replicationSlotName);
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

    internal static void DropConnectorRoleIfExists(string? connectorRoleName)
    {
        if (string.IsNullOrWhiteSpace(connectorRoleName))
        {
            return;
        }

        try
        {
            using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"DROP ROLE IF EXISTS {QuoteIdentifier(connectorRoleName)};";
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

    internal static void CreateConnectorRole(string connectorRoleName)
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE ROLE {QuoteIdentifier(connectorRoleName)} WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;";
        command.ExecuteNonQuery();
    }

    internal static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertDocumentBeforeProviderSlotAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@document_uuid, 1)
            RETURNING "DocumentId";
            """;
        command.Parameters.AddWithValue("document_uuid", Guid.NewGuid());

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteProjectionWorkDmlAsync(NpgsqlConnection connection, long documentId)
    {
        await using var command = connection.CreateCommand();
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

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountPeekedPgoutputChangesAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
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

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static async Task<ReplicationSlotSnapshot?> ReadReplicationSlotSnapshotAsync(
        NpgsqlConnection connection
    )
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
        command.Parameters.AddWithValue("slot_name", ReplicationSlotName);

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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CdcProviderArtifacts")]
public class Given_PostgresqlCdcProviderArtifacts_With_A_Legacy_CreatedAt_Column
{
    private static readonly string[] _sixDocumentColumns =
    [
        "DocumentId",
        "DocumentUuid",
        "ResourceKeyId",
        "CreatedByOwnershipTokenId",
        "ContentVersion",
        "ContentLastModifiedAt",
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorRoleName = null!;
    private string _replicationSlotName = null!;
    private CdcProviderSetupResult _sixColumnValidation = null!;
    private CdcProviderSetupResult _legacyColumnValidation = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Given_PostgresqlCdcProviderArtifacts.AssumePostgresqlLogicalReplicationAvailable();

        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            Given_PostgresqlCdcProviderArtifacts.FixtureRelativePath
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorRoleName = $"cdc_connector_{_database.DatabaseName}";
        // Replication slot names are cluster-wide; a per-database name keeps this fixture's slot, which lives
        // until OneTimeTearDown, from colliding with the per-test slot of Given_PostgresqlCdcProviderArtifacts.
        _replicationSlotName = $"dms_binding_slot_{_database.DatabaseName}";
        Given_PostgresqlCdcProviderArtifacts.CreateConnectorRole(_connectorRoleName);

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        // Create every CDC prerequisite against the six-column table, then confirm it validates cleanly,
        // so the legacy column is the only difference in the second validation.
        await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        _sixColumnValidation = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        await Given_PostgresqlCdcProviderArtifacts.ExecuteNonQueryAsync(
            connection,
            """
            ALTER TABLE dms."Document" ADD COLUMN "CreatedAt" timestamp with time zone NOT NULL DEFAULT now();
            """
        );

        _legacyColumnValidation = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await Given_PostgresqlCdcProviderArtifacts.DropReplicationSlotIfExistsAsync(
                _database.ConnectionString,
                _replicationSlotName
            );
            await _database.DisposeAsync();
        }

        Given_PostgresqlCdcProviderArtifacts.DropConnectorRoleIfExists(_connectorRoleName);
    }

    [Test]
    public void It_expects_a_six_column_Document_source_without_CreatedAt()
    {
        DocumentColumnNames(_fixture.CdcSourceInventory).Should().Equal(_sixDocumentColumns);
    }

    [Test]
    public void It_validates_the_six_column_database_as_an_exact_match()
    {
        _sixColumnValidation
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.ExactMatch, DescribeDiagnostics(_sixColumnValidation));
        _sixColumnValidation.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_fails_validation_once_the_Document_table_carries_CreatedAt()
    {
        _legacyColumnValidation.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
    }

    [Test]
    public void It_reports_the_unexpected_CreatedAt_source_column_as_the_only_error()
    {
        var diagnostic = _legacyColumnValidation
            .Diagnostics.Where(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error)
            .Should()
            .ContainSingle(DescribeDiagnostics(_legacyColumnValidation))
            .Which;

        diagnostic.Code.Should().Be("CDC_SOURCE_COLUMN_UNEXPECTED");
        diagnostic.SafeName.Value.Should().Be("dms.Document.CreatedAt");
        diagnostic.ArtifactKind.Should().Be(CdcProviderArtifactKind.SourceColumn);
        diagnostic.Severity.Should().Be(CdcProviderDiagnosticSeverity.Error);
        diagnostic.Classification.Should().Be(CdcProviderRetryContinuityClassification.FailClosed);
        diagnostic.ExpectedValue.Should().Be("absent");
        diagnostic.ObservedValue.Should().Be("present");
    }

    [Test]
    public void It_reports_the_same_non_error_diagnostics_as_the_six_column_validation()
    {
        NonErrorDiagnosticCodes(_legacyColumnValidation)
            .Should()
            .Equal(NonErrorDiagnosticCodes(_sixColumnValidation));
    }

    [Test]
    public void It_reads_CreatedAt_from_the_live_source_inventory()
    {
        string[] legacyDocumentColumns = [.. _sixDocumentColumns, "CreatedAt"];
        DocumentColumnNames(_legacyColumnValidation.SourceTableInventory)
            .Should()
            .Equal(legacyDocumentColumns);
    }

    [Test]
    public void It_stops_at_the_source_inventory_step()
    {
        CdcProviderArtifactKind[] laterStepArtifactKinds =
        [
            CdcProviderArtifactKind.PostgresqlReplicaIdentity,
            CdcProviderArtifactKind.PostgresqlPublication,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
        ];

        _sixColumnValidation
            .ArtifactInventory.Select(observation => observation.ArtifactKind)
            .Should()
            .Contain(laterStepArtifactKinds);
        _sixColumnValidation.GrantInventory.Should().NotBeEmpty();

        _legacyColumnValidation
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.State == CdcProviderArtifactState.Matched
            );
        _legacyColumnValidation
            .ArtifactInventory.Select(observation => observation.ArtifactKind)
            .Should()
            .NotContain(laterStepArtifactKinds);
        _legacyColumnValidation.GrantInventory.Should().BeEmpty();
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        NpgsqlConnection connection,
        CdcProviderSetupMode mode
    )
    {
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: CdcProvider.Postgresql,
                mode: mode,
                boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                    CdcProvider.Postgresql,
                    await Given_PostgresqlCdcProviderArtifacts.ReadDataStoreIdentityAsync(connection)
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorRoleName)),
                artifactNames: CdcProviderArtifactNames.ForPostgresql(
                    new CdcSafeName(Given_PostgresqlCdcProviderArtifacts.PublicationName),
                    new CdcSafeName(_replicationSlotName)
                ),
                artifactOutput: new CdcProviderArtifactOutputRequest(false),
                expectedSourceInventory: _fixture.CdcSourceInventory,
                dmsManagedTableInventory: _fixture.CdcDmsManagedTableInventory,
                databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(connection)
            )
        );
    }

    private static string[] DocumentColumnNames(IEnumerable<CdcSourceTableInventory> inventory) =>
        inventory
            .Single(table => table.TableKind == CdcSourceTableKind.Document)
            .Columns.OrderBy(column => column.Ordinal)
            .Select(column => column.ColumnName.Value)
            .ToArray();

    private static string[] NonErrorDiagnosticCodes(CdcProviderSetupResult result) =>
        result
            .Diagnostics.Where(diagnostic => diagnostic.Severity != CdcProviderDiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Code)
            .ToArray();

    private static string DescribeDiagnostics(CdcProviderSetupResult result) =>
        string.Join(
            "; ",
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.ArtifactKind}:{diagnostic.SafeName.Value}:{diagnostic.ExpectedValue}->{diagnostic.ObservedValue}"
            )
        );
}
