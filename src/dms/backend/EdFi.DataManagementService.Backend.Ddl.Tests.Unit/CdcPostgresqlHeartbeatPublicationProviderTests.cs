// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_PostgresqlCdcHeartbeatPublication_Initial_Setup
{
    private RecordingPostgresqlCdcExecutor _executor = null!;
    private CdcProviderSetupResult _result = null!;

    [SetUp]
    public async Task SetUp()
    {
        _executor = new RecordingPostgresqlCdcExecutor();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        _result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: _executor)
        );
    }

    [Test]
    public void It_should_create_the_opt_in_heartbeat_table_and_singleton()
    {
        _result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        _result.Diagnostics.Should().BeEmpty();
        _result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.State == CdcProviderArtifactState.Created
            );
        _executor
            .ExecutedSql.Should()
            .Contain(sql => sql.Contains("CREATE TABLE IF NOT EXISTS \"dms\".\"CdcHeartbeat\""));
        _executor.ExecutedSql.Should().Contain(sql => sql.Contains("INSERT INTO \"dms\".\"CdcHeartbeat\""));
    }

    [Test]
    public void It_should_set_document_replica_identity_full_without_changing_document_cache_key_shape()
    {
        _result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicaIdentity
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["replica_identity"] == "FULL"
            );
        _executor
            .ExecutedSql.Should()
            .ContainSingle(sql => sql.Contains("ALTER TABLE \"dms\".\"Document\" REPLICA IDENTITY FULL"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("DocumentCache_DocumentUuid"));
    }

    [Test]
    public void It_should_create_a_binding_derived_publication_for_the_three_fixed_sources_only()
    {
        var publicationSql = _executor.ExecutedSql.Single(sql =>
            sql.Contains("CREATE PUBLICATION \"dms_binding_publication\"")
        );

        publicationSql
            .Should()
            .Contain("FOR TABLE \"dms\".\"DocumentCache\", \"dms\".\"Document\", \"dms\".\"CdcHeartbeat\"");
        publicationSql.Should().Contain("publish = 'insert, update, delete'");
        publicationSql.Should().Contain("publish_via_partition_root = false");
        publicationSql.Should().NotContain("DocumentProjectionWork");

        _result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.State == CdcProviderArtifactState.Created
            );
    }

    [Test]
    public void It_should_return_generated_heartbeat_action_query_and_document_uuid_message_keys()
    {
        _result
            .HeartbeatActionQuery!.Sql.Should()
            .Be(
                """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1, "HeartbeatAt" = now() WHERE "HeartbeatId" = 1"""
            );
        _result.HeartbeatActionQuery.Sha256Hash.Should().HaveLength(64);

        _result.ExpectedMessageKeyColumns.Should().HaveCount(2);
        _result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.Document
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );
        _result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.DocumentCache
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );
        _result
            .ExpectedMessageKeyColumns.Should()
            .NotContain(key => key.TableKind == CdcSourceTableKind.CdcHeartbeat);
    }
}

[TestFixture]
public class Given_PostgresqlCdcHeartbeatPublication_ValidateOnly
{
    [Test]
    public async Task It_should_not_create_or_change_missing_provider_artifacts()
    {
        var executor = new RecordingPostgresqlCdcExecutor();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING")
            .Which.ArtifactKind.Should()
            .Be(CdcProviderArtifactKind.HeartbeatTable);
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_when_an_existing_publication_captures_the_work_table()
    {
        var executor = new RecordingPostgresqlCdcExecutor(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            publicationCapturesWorkTable: true
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["tables"].Contains("dms.DocumentProjectionWork")
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH");
        executor.ExecutedSql.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_PostgresqlCdcSlotHistory_Initial_Setup
{
    [Test]
    public async Task It_should_create_one_permanent_pgoutput_slot_and_return_retained_history_observation()
    {
        var executor = new RecordingPostgresqlCdcExecutor();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        executor
            .ExecutedSql.Should()
            .ContainSingle(sql => sql.Contains("pg_create_logical_replication_slot"));
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["plugin"] == "pgoutput"
                && observation.SafeObservedValues["slot_type"] == "logical"
                && observation.SafeObservedValues["temporary"] == "False"
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == "dms_binding_slot"
                && observation.Classification == CdcProviderRetryContinuityClassification.None
                && observation.SafeObservedValues["restart_lsn"] == "0_16B6C50"
                && observation.SafeObservedValues["confirmed_flush_lsn"] == "0_16B6C50"
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_an_existing_initial_slot_is_active()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(slotActive: true);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_HISTORY_UNPROVABLE"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryUnknown
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_return_retained_positions_without_classifying_offset_gaps_when_no_committed_offset_is_supplied()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(
            slotConfirmedFlushLsn: "0/16B6D00"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result.Diagnostics.Should().BeEmpty();
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeObservedValues["confirmed_flush_lsn"] == "0_16B6D00"
                && observation.SafeObservedValues["retained_position_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
    }
}

[TestFixture]
public class Given_PostgresqlCdcSlotHistory_ValidateOnly
{
    [Test]
    public async Task It_should_report_active_slot_as_observation_without_creating_or_repairing()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(slotActive: true);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result.Diagnostics.Should().BeEmpty();
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeObservedValues["active"] == "True"
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_report_missing_slot_as_source_history_loss()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(slotExists: false);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.State == CdcProviderArtifactState.Missing
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_when_slot_shape_does_not_exact_match()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(
            slotPlugin: "test_decoding"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ValidationMismatch
            );
    }

    [Test]
    public async Task It_should_map_lost_wal_or_invalidated_history_to_source_history_lost()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(
            slotWalStatus: "lost",
            slotInvalidationReason: "wal_removed"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_HISTORY_LOST"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
    }
}

internal sealed class RecordingPostgresqlCdcExecutor : ICdcProviderDatabaseExecutor
{
    private const string CurrentDatabaseName = "dms_test";

    private bool _heartbeatTableExists;
    private bool _heartbeatSingletonExists;
    private bool _documentReplicaIdentityFull;
    private bool _publicationExists;
    private readonly bool _publicationCapturesWorkTable;
    private bool _slotExists;
    private readonly string _slotPlugin;
    private readonly string _slotType;
    private readonly string _slotDatabase;
    private readonly bool _slotTemporary;
    private readonly bool _slotActive;
    private readonly string _slotTwoPhase;
    private readonly string _slotRestartLsn;
    private readonly string _slotConfirmedFlushLsn;
    private readonly string _slotWalStatus;
    private readonly string _slotInvalidationReason;

    public RecordingPostgresqlCdcExecutor(
        bool heartbeatTableExists = false,
        bool heartbeatSingletonExists = false,
        bool documentReplicaIdentityFull = false,
        bool publicationExists = false,
        bool publicationCapturesWorkTable = false,
        bool slotExists = false,
        string slotPlugin = "pgoutput",
        string slotType = "logical",
        string slotDatabase = CurrentDatabaseName,
        bool slotTemporary = false,
        bool slotActive = false,
        string slotTwoPhase = "false",
        string slotRestartLsn = "0/16B6C50",
        string slotConfirmedFlushLsn = "0/16B6C50",
        string slotWalStatus = "reserved",
        string slotInvalidationReason = ""
    )
    {
        _heartbeatTableExists = heartbeatTableExists;
        _heartbeatSingletonExists = heartbeatSingletonExists;
        _documentReplicaIdentityFull = documentReplicaIdentityFull;
        _publicationExists = publicationExists;
        _publicationCapturesWorkTable = publicationCapturesWorkTable;
        _slotExists = slotExists;
        _slotPlugin = slotPlugin;
        _slotType = slotType;
        _slotDatabase = slotDatabase;
        _slotTemporary = slotTemporary;
        _slotActive = slotActive;
        _slotTwoPhase = slotTwoPhase;
        _slotRestartLsn = slotRestartLsn;
        _slotConfirmedFlushLsn = slotConfirmedFlushLsn;
        _slotWalStatus = slotWalStatus;
        _slotInvalidationReason = slotInvalidationReason;
    }

    public List<string> ExecutedSql { get; } = [];

    public static RecordingPostgresqlCdcExecutor WithExistingProviderArtifacts(
        bool slotExists = true,
        string slotPlugin = "pgoutput",
        string slotType = "logical",
        string slotDatabase = CurrentDatabaseName,
        bool slotTemporary = false,
        bool slotActive = false,
        string slotTwoPhase = "false",
        string slotRestartLsn = "0/16B6C50",
        string slotConfirmedFlushLsn = "0/16B6C50",
        string slotWalStatus = "reserved",
        string slotInvalidationReason = ""
    ) =>
        new(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            slotExists: slotExists,
            slotPlugin: slotPlugin,
            slotType: slotType,
            slotDatabase: slotDatabase,
            slotTemporary: slotTemporary,
            slotActive: slotActive,
            slotTwoPhase: slotTwoPhase,
            slotRestartLsn: slotRestartLsn,
            slotConfirmedFlushLsn: slotConfirmedFlushLsn,
            slotWalStatus: slotWalStatus,
            slotInvalidationReason: slotInvalidationReason
        );

    public Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        ExecutedSql.Add(sql);

        if (sql.Contains("CREATE TABLE IF NOT EXISTS \"dms\".\"CdcHeartbeat\""))
        {
            _heartbeatTableExists = true;
            _heartbeatSingletonExists = true;
        }

        if (sql.Contains("INSERT INTO \"dms\".\"CdcHeartbeat\""))
        {
            _heartbeatSingletonExists = true;
        }

        if (sql.Contains("ALTER TABLE \"dms\".\"Document\" REPLICA IDENTITY FULL"))
        {
            _documentReplicaIdentityFull = true;
        }

        if (sql.Contains("CREATE PUBLICATION \"dms_binding_publication\""))
        {
            _publicationExists = true;
        }

        if (sql.Contains("pg_create_logical_replication_slot"))
        {
            _slotExists = true;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = sql switch
        {
            var text when text.Contains("cdc:postgresql:table-exists") =>
            [
                Row(("table_exists", _heartbeatTableExists.ToString())),
            ],
            var text when text.Contains("cdc:postgresql:heartbeat-shape") =>
            [
                Row(
                    ("primary_key_matches", _heartbeatTableExists.ToString()),
                    ("singleton_check_matches", _heartbeatTableExists.ToString()),
                    ("sequence_check_matches", _heartbeatTableExists.ToString())
                ),
            ],
            var text when text.Contains("cdc:postgresql:heartbeat-singleton") =>
            [
                Row(
                    ("row_count", _heartbeatSingletonExists ? "1" : "0"),
                    ("singleton_row_count", _heartbeatSingletonExists ? "1" : "0"),
                    ("extra_row_count", "0"),
                    ("heartbeat_sequence", _heartbeatSingletonExists ? "0" : "-1")
                ),
            ],
            var text when text.Contains("cdc:postgresql:source-inventory") => SourceInventoryRows(),
            var text when text.Contains("cdc:postgresql:document-replica-identity") =>
            [
                Row(("relreplident", _documentReplicaIdentityFull ? "f" : "d")),
            ],
            var text when text.Contains("cdc:postgresql:server-version") =>
            [
                Row(("server_version_num", "160000")),
            ],
            var text when text.Contains("cdc:postgresql:publication-properties") => _publicationExists
                ?
                [
                    Row(
                        ("publishes_insert", "true"),
                        ("publishes_update", "true"),
                        ("publishes_delete", "true"),
                        ("publishes_truncate", "false"),
                        ("publishes_all_tables", "false"),
                        ("publish_via_partition_root", "false")
                    ),
                ]
                : [],
            var text when text.Contains("cdc:postgresql:publication-tables") => _publicationExists
                ? PublicationTableRows()
                : [],
            var text when text.Contains("cdc:postgresql:replication-slot") => _slotExists
                ?
                [
                    Row(
                        ("slot_name", "dms_binding_slot"),
                        ("plugin", _slotPlugin),
                        ("slot_type", _slotType),
                        ("database", _slotDatabase),
                        ("expected_database", CurrentDatabaseName),
                        ("temporary", _slotTemporary.ToString()),
                        ("active", _slotActive.ToString()),
                        ("two_phase", _slotTwoPhase),
                        ("restart_lsn", _slotRestartLsn),
                        ("confirmed_flush_lsn", _slotConfirmedFlushLsn),
                        ("wal_status", _slotWalStatus),
                        ("invalidation_reason", _slotInvalidationReason)
                    ),
                ]
                : [],
            _ => throw new InvalidOperationException($"Unexpected PostgreSQL CDC query: {sql}"),
        };

        return Task.FromResult(rows);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> SourceInventoryRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];
        foreach (var table in CdcProviderSetupContractTestData.BuildRequiredSourceInventory())
        {
            if (table.TableKind == CdcSourceTableKind.CdcHeartbeat && !_heartbeatTableExists)
            {
                continue;
            }

            rows.AddRange(
                table.Columns.Select(column =>
                    Row(
                        ("table_schema", table.TableName.Schema.Value),
                        ("table_name", table.TableName.Name),
                        ("column_name", column.ColumnName.Value),
                        ("ordinal", column.Ordinal.ToString()),
                        ("provider_data_type", column.ProviderDataType),
                        ("is_nullable", column.IsNullable.ToString())
                    )
                )
            );
        }

        return rows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> PublicationTableRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows =
        [
            PublicationTableRow("dms", "CdcHeartbeat"),
            PublicationTableRow("dms", "Document"),
            PublicationTableRow("dms", "DocumentCache"),
        ];

        if (_publicationCapturesWorkTable)
        {
            rows.Add(PublicationTableRow("dms", "DocumentProjectionWork"));
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, string?> PublicationTableRow(
        string schemaName,
        string tableName
    ) =>
        Row(
            ("schema_name", schemaName),
            ("table_name", tableName),
            ("publishes_all_columns", "true"),
            ("row_filter_absent", "true")
        );

    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);
}
