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
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const string PublicationName = "dms_binding_publication";
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
            await DropReplicationSlotIfExistsAsync();
            await _database.DisposeAsync();
        }

        DropConnectorRoleIfExists();
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

    private async Task DropReplicationSlotIfExistsAsync()
    {
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
            command.Parameters.AddWithValue("slot_name", ReplicationSlotName);
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

    private static void CreateConnectorRole(string connectorRoleName)
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE ROLE {QuoteIdentifier(connectorRoleName)} WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;";
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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
