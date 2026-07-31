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
[Category("PostgresqlIntegration")]
public class Given_PostgresqlCdcHeartbeatPublication_Provider_Setup
{
    private const string PublicationName = "dms_binding_publication";
    private const string ReplicationSlotName = "dms_binding_slot";

    private string _databaseName = null!;
    private string _connectionString = null!;

    [SetUp]
    public void SetUp()
    {
        AssumePostgresqlLogicalReplicationAvailable();

        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);

        PostgresTestDatabaseHelper.CreateDatabase(_databaseName);

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
    public async Task It_should_exact_match_existing_artifacts_in_validate_only_without_mutating_heartbeat()
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

    private static CdcProviderSetupRequest BuildRequest(
        ICdcProviderDatabaseExecutor databaseExecutor,
        CdcProviderSetupMode mode
    ) =>
        new(
            provider: CdcProvider.Postgresql,
            mode: mode,
            boundPhysicalSourceFingerprint: new CdcSourceFingerprint(
                "dms-source-fingerprint-v1",
                "integration-source"
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("cdc_connector")),
            artifactNames: CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(PublicationName),
                new CdcSafeName(ReplicationSlotName)
            ),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
                SqlDialectFactory.Create(SqlDialect.Pgsql)
            ),
            databaseExecutor: databaseExecutor
        );

    private static async Task<CdcProviderSetupResult> RunSetupAsync(
        NpgsqlConnection connection,
        CdcProviderSetupMode mode
    )
    {
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(BuildRequest(executor, mode));
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
