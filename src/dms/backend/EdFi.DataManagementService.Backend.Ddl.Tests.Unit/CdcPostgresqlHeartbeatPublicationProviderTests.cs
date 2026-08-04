// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
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

    [Test]
    public void CdcProviderMetadata_should_observe_the_source_fingerprint_from_DataStoreIdentity()
    {
        _result
            .ObservedSourceFingerprint.Should()
            .Be(CdcProviderSetupContractTestData.PostgresqlSourceFingerprint);
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SourceFingerprint
                && observation.SafeArtifactName.Value == "dms.DataStoreIdentity"
                && observation.State == CdcProviderArtifactState.Matched
                && observation.SafeObservedValues["source_fingerprint_version"] == "dms-source-fingerprint-v1"
                && observation.SafeObservedValues["physical_source_fingerprint"]
                    == CdcProviderSetupContractTestData.PostgresqlSourceFingerprint.Value
                && observation.SafeObservedValues["provider_token"] == "postgresql"
                && !observation.SafeObservedValues.ContainsKey("source_identity")
            );
        _result.ManifestPayload!.Json.Should().Contain("\"observed_source_fingerprint\": {");
        _result
            .ManifestPayload.Json.Should()
            .Contain($"\"value\": \"{CdcProviderSetupContractTestData.PostgresqlSourceFingerprint.Value}\"");
        _result.ManifestPayload.Json.Should().NotContain(CdcProviderSetupContractTestData.SourceIdentity);
    }

    [Test]
    public async Task It_should_fail_closed_with_source_inventory_diagnostics_for_sparse_observed_ordinals()
    {
        var executor = new RecordingPostgresqlCdcExecutor(
            omittedSourceInventoryTableKind: CdcSourceTableKind.DocumentCache,
            omittedSourceInventoryColumnName: "ResourceName"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SOURCE_COLUMN_MISSING"
                && diagnostic.SafeName.Value == "dms.DocumentCache.ResourceName"
            );
        result
            .Diagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.Category == CdcProviderDiagnosticCategory.SetupPrincipalFailure
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("CREATE PUBLICATION"));
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

    [TestCase(
        "singleton_check",
        false,
        true,
        TestName = "It_should_fail_closed_when_the_singleton_constraint_contains_the_expected_fragment_but_allows_other_ids"
    )]
    [TestCase(
        "sequence_check",
        true,
        false,
        TestName = "It_should_fail_closed_when_the_sequence_constraint_contains_the_expected_fragment_but_allows_negative_sequences"
    )]
    public async Task It_should_fail_closed_for_malformed_heartbeat_constraints(
        string mismatchedShapeKey,
        bool singletonCheckMatches,
        bool sequenceCheckMatches
    )
    {
        var executor = new RecordingPostgresqlCdcExecutor(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            heartbeatSingletonCheckMatches: singletonCheckMatches,
            heartbeatSequenceCheckMatches: sequenceCheckMatches
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
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues[mismatchedShapeKey] == "mismatched"
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
            );
        executor.ExecutedSql.Should().BeEmpty();

        var heartbeatShapeSql = executor.QueriedSql.Single(sql =>
            sql.Contains("cdc:postgresql:heartbeat-shape")
        );
        heartbeatShapeSql.Should().Contain("pg_catalog.pg_get_expr");
        heartbeatShapeSql.Should().NotContain("LIKE");
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
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_WORK_TABLE_PUBLICATION_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_in_validate_only_when_an_existing_publication_publishes_all_tables()
    {
        var executor = new RecordingPostgresqlCdcExecutor(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            publicationPublishesAllTables: true
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
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["publishes_all_tables"] == "True"
                && !observation.SafeObservedValues["tables"].Contains("dms.DocumentProjectionWork")
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_WORK_TABLE_PUBLICATION_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
                && diagnostic.ObservedValue == "publishes_all_tables"
            );
        result.ManifestPayload!.Json.Should().Contain("\"publishes_all_tables\": \"True\"");
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_in_initial_rerun_when_an_existing_publication_publishes_all_tables()
    {
        var executor = new RecordingPostgresqlCdcExecutor(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            publicationPublishesAllTables: true
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
                && observation.SafeObservedValues["publishes_all_tables"] == "True"
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_WORK_TABLE_PUBLICATION_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("CREATE PUBLICATION"));
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
                && observation.SafeObservedValues["initial_slot_proof"] == "available"
                && observation.SafeObservedValues["database_matches_current"] == "True"
                && observation
                    .SafeObservedValues["initial_slot_proof_database_identity_token"]
                    .StartsWith("postgresql_database_identity_sha256:", StringComparison.Ordinal)
                && observation.SafeObservedValues["initial_slot_proof_restart_lsn"] == "0_16B6C50"
                && observation.SafeObservedValues["initial_slot_proof_confirmed_flush_lsn"] == "0_16B6C50"
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeArtifactName.Value == "dms_binding_slot"
                && observation.Classification == CdcProviderRetryContinuityClassification.None
                && observation
                    .SafeObservedValues["database_identity_token"]
                    .StartsWith("postgresql_database_identity_sha256:", StringComparison.Ordinal)
                && !observation.SafeObservedValues.ContainsKey("database")
                && !observation.SafeObservedValues.ContainsKey("expected_database")
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
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
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
    public async Task It_should_fail_closed_when_existing_initial_slot_has_no_same_workflow_proof()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_INITIAL_PROOF_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryUnknown
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_exact_match_existing_initial_slot_when_same_workflow_proof_matches()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(
            slotConfirmedFlushLsn: "0/16B6D00"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof(
                    retainedConfirmedFlushLsn: "0_16B6D00"
                )
            )
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

    [Test]
    public async Task It_should_fail_closed_when_proved_initial_slot_advanced_before_connector_registration()
    {
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(
            slotRestartLsn: "0/16B6D10",
            slotConfirmedFlushLsn: "0/16B6D10"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_ADVANCED_BEFORE_CONNECTOR_REGISTRATION"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
        executor.ExecutedSql.Should().BeEmpty();
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
    public async Task It_should_redact_database_identity_from_slot_manifest_and_diagnostics()
    {
        const string TenantDatabaseName = "tenant_EastHigh_2026";
        var executor = RecordingPostgresqlCdcExecutor.WithExistingProviderArtifacts(
            slotDatabase: TenantDatabaseName
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        var diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_POSTGRESQL_REPLICATION_SLOT_MISMATCH")
            .Which;
        diagnostic.ObservedValue.Should().NotBeNull();
        diagnostic.ObservedValue!.Should().NotContain(TenantDatabaseName);
        diagnostic.ObservedValue.Should().NotContain("dms_test");
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && observation.SafeObservedValues["database_matches_current"] == "False"
                && observation
                    .SafeObservedValues["database_identity_token"]
                    .StartsWith("postgresql_database_identity_sha256:", StringComparison.Ordinal)
                && !observation.SafeObservedValues.ContainsKey("database")
                && !observation.SafeObservedValues.ContainsKey("expected_database")
            );
        result.ManifestPayload!.Json.Should().NotContain(TenantDatabaseName);
        result.ManifestPayload.Json.Should().NotContain("dms_test");
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

[TestFixture]
public class Given_PostgresqlCdcPrincipalAccess_Initial_Setup
{
    [Test]
    public async Task It_should_grant_only_required_database_local_privileges_to_existing_connector_role()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeArtifactName.Value == "connector_principal"
                && observation.State == CdcProviderArtifactState.Created
            );

        var grantSql = executor.ExecutedSql.Single(sql =>
            sql.Contains("cdc:postgresql:grant-connector-access")
        );
        grantSql
            .Should()
            .Contain(
                "GRANT SELECT ON TABLE \"dms\".\"Document\", \"dms\".\"DocumentCache\", \"dms\".\"CdcHeartbeat\""
            );
        grantSql
            .Should()
            .Contain(
                "GRANT UPDATE (\"HeartbeatSequence\", \"HeartbeatAt\") ON TABLE \"dms\".\"CdcHeartbeat\""
            );
        grantSql.Should().NotContain("DocumentProjectionWork");
        grantSql.Should().NotContain("ALTER ROLE");

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

    [Test]
    public async Task It_should_not_add_replication_or_elevated_role_attributes()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants(connectorCanReplicate: false);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_ROLE_ATTRIBUTES_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
                && diagnostic.PrincipalKind == CdcPrincipalKind.ConnectorPrincipal
            );
        executor
            .ExecutedSql.Should()
            .NotContain(sql =>
                sql.Contains("cdc:postgresql:grant-connector-access") || sql.Contains("ALTER ROLE")
            );
    }

    [Test]
    public async Task It_should_reject_disallowed_elevated_connector_role_attributes()
    {
        var executor = ExistingArtifactsWithConnectorAccess(
            connectorDisallowedRoleAttributes: "SUPERUSER,pg_read_all_data"
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_ROLE_ATTRIBUTES_MISMATCH"
                && diagnostic.ObservedValue!.Contains("SUPERUSER")
                && diagnostic.ObservedValue.Contains("pg_read_all_data")
            );
        executor
            .ExecutedSql.Should()
            .NotContain(sql => sql.Contains("cdc:postgresql:grant-connector-access"));
    }

    [Test]
    public async Task It_should_reject_connector_access_to_document_projection_work()
    {
        var executor = ExistingArtifactsWithConnectorAccess(connectorWorkTablePrivileges: "SELECT");
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
                && diagnostic.ExpectedValue == "no-dms.DocumentProjectionWork-privileges"
            );
    }

    [Test]
    public async Task It_should_reject_extra_select_on_dms_owned_non_source_tables()
    {
        const string extraSelectTables =
            "auth.EducationOrganizationIdToEducationOrganizationId,edfi.School,tracked_changes_edfi.School";
        var executor = ExistingArtifactsWithConnectorAccess(connectorExtraDmsSelectTables: extraSelectTables);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
                && diagnostic.ObservedValue == extraSelectTables
            );
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeObservedValues["extra_dms_select_tables"] == extraSelectTables
            );

        var connectorAccessSql = executor.QueriedSql.Single(sql =>
            sql.Contains("cdc:postgresql:connector-principal-access")
        );
        connectorAccessSql.Should().Contain("dms_managed_table_inventory");
        connectorAccessSql
            .Should()
            .Contain("('auth', 'EducationOrganizationIdToEducationOrganizationId', 'authorization')");
        connectorAccessSql.Should().Contain("('edfi', 'School', 'resource')");
        connectorAccessSql.Should().Contain("('tracked_changes_edfi', 'School', 'tracked_change')");
        connectorAccessSql.Should().NotContain("ProjectEndpointName");
        connectorAccessSql.Should().NotContain("tracked\\_changes\\_%");
    }

    [Test]
    public async Task It_should_use_caller_supplied_shortened_dms_managed_table_inventory_for_extra_select_validation()
    {
        var rawPhysicalSchema = $"p{new string('a', 80)}";
        var shortenedPhysicalSchema = new PgsqlDialectRules().ShortenIdentifier(rawPhysicalSchema);
        var rawTrackedChangeSchema = $"tracked_changes_{rawPhysicalSchema}";
        var shortenedTrackedChangeSchema = new PgsqlDialectRules().ShortenIdentifier(rawTrackedChangeSchema);
        shortenedPhysicalSchema.Should().NotBe(rawPhysicalSchema);
        shortenedTrackedChangeSchema.Should().NotBe(rawTrackedChangeSchema);

        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var managedTableInventory = CdcProviderSetupContractTestData.BuildDmsManagedTableInventory(
            dialect,
            new DbTableName(new DbSchemaName(shortenedPhysicalSchema), "School"),
            new DbTableName(new DbSchemaName(shortenedTrackedChangeSchema), "School")
        );
        var extraSelectTables = $"{shortenedPhysicalSchema}.School,{shortenedTrackedChangeSchema}.School";
        var executor = ExistingArtifactsWithConnectorAccess(connectorExtraDmsSelectTables: extraSelectTables);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                dmsManagedTableInventory: managedTableInventory,
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.ObservedValue == extraSelectTables
            );

        var connectorAccessSql = executor.QueriedSql.Single(sql =>
            sql.Contains("cdc:postgresql:connector-principal-access")
        );
        connectorAccessSql.Should().Contain($"('{shortenedPhysicalSchema}', 'School', 'resource')");
        connectorAccessSql
            .Should()
            .Contain($"('{shortenedTrackedChangeSchema}', 'School', 'tracked_change')");
        connectorAccessSql.Should().NotContain(rawPhysicalSchema);
        connectorAccessSql.Should().NotContain(rawTrackedChangeSchema);
    }

    [TestCase("INSERT", "", "INSERT")]
    [TestCase("DELETE", "", "DELETE")]
    [TestCase("TRUNCATE", "", "TRUNCATE")]
    [TestCase("REFERENCES", "", "REFERENCES")]
    [TestCase("TRIGGER", "", "TRIGGER")]
    [TestCase("UPDATE", "", "UPDATE")]
    [TestCase("", "HeartbeatId", "UPDATE:HeartbeatId")]
    public async Task It_should_reject_forbidden_heartbeat_privileges(
        string heartbeatTablePrivileges,
        string heartbeatUnexpectedUpdateColumns,
        string expectedObservedPrivilege
    )
    {
        var executor = ExistingArtifactsWithConnectorAccess(
            connectorHeartbeatForbiddenTablePrivileges: heartbeatTablePrivileges,
            connectorHeartbeatUnexpectedUpdateColumns: heartbeatUnexpectedUpdateColumns
        );
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        var expectedDiagnosticCode = string.IsNullOrEmpty(heartbeatTablePrivileges)
            ? "CDC_POSTGRESQL_CONNECTOR_HEARTBEAT_UPDATE_GRANT_MISMATCH"
            : "CDC_POSTGRESQL_CONNECTOR_HEARTBEAT_GRANT_MISMATCH";
        var expectedDiagnosticObservedValue = string.IsNullOrEmpty(heartbeatTablePrivileges)
            ? heartbeatUnexpectedUpdateColumns
            : expectedObservedPrivilege;
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == expectedDiagnosticCode
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
                && diagnostic.ObservedValue!.Contains(expectedDiagnosticObservedValue)
            );
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeArtifactName.Value == "connector_principal"
                && observation
                    .SafeObservedValues["heartbeat_forbidden_privileges"]
                    .Contains(expectedObservedPrivilege)
            );
        result.GrantInventory.Should().Contain(grant => grant.SafeObjectName.Value == "dms.CdcHeartbeat");
        result.ManifestPayload!.Json.Should().Contain("dms.CdcHeartbeat");
        result.ManifestPayload.Json.Should().Contain(expectedObservedPrivilege);
    }

    [Test]
    public async Task It_should_fail_closed_when_optional_live_probe_reports_connector_boundary_failure()
    {
        var executor = ExistingArtifactsWithConnectorAccess();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(
                databaseExecutor: executor,
                postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof(),
                connectorPrincipalProbeFactory: new FailingConnectorPrincipalProbeFactory()
            )
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
    }

    private static RecordingPostgresqlCdcExecutor ExistingArtifactsWithoutConnectorGrants(
        bool connectorCanReplicate = true
    ) =>
        new(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            slotExists: true,
            connectorCanReplicate: connectorCanReplicate
        );

    private static RecordingPostgresqlCdcExecutor ExistingArtifactsWithConnectorAccess(
        string connectorDisallowedRoleAttributes = "",
        string connectorWorkTablePrivileges = "",
        string connectorHeartbeatForbiddenTablePrivileges = "",
        string connectorHeartbeatUnexpectedUpdateColumns = "",
        string connectorExtraDmsSelectTables = ""
    ) =>
        new(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            slotExists: true,
            connectorDisallowedRoleAttributes: connectorDisallowedRoleAttributes,
            connectorDatabaseConnect: true,
            connectorSchemaUsage: true,
            connectorDocumentSelect: true,
            connectorDocumentCacheSelect: true,
            connectorHeartbeatSelect: true,
            connectorHeartbeatSequenceUpdate: true,
            connectorHeartbeatAtUpdate: true,
            connectorHeartbeatForbiddenTablePrivileges: connectorHeartbeatForbiddenTablePrivileges,
            connectorHeartbeatUnexpectedUpdateColumns: connectorHeartbeatUnexpectedUpdateColumns,
            connectorWorkTablePrivileges: connectorWorkTablePrivileges,
            connectorExtraDmsSelectTables: connectorExtraDmsSelectTables
        );
}

[TestFixture]
public class Given_PostgresqlCdcPrincipalAccess_ValidateOnly
{
    [Test]
    public async Task It_should_report_missing_required_grants_without_creating_them()
    {
        var executor = new RecordingPostgresqlCdcExecutor(
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            documentReplicaIdentityFull: true,
            publicationExists: true,
            slotExists: true
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
                diagnostic.Code == "CDC_POSTGRESQL_CONNECTOR_REQUIRED_GRANTS_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            );
        executor
            .ExecutedSql.Should()
            .NotContain(sql => sql.Contains("cdc:postgresql:grant-connector-access"));
    }
}

internal sealed class FailingConnectorPrincipalProbeFactory : ICdcConnectorPrincipalProbeFactory
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

internal sealed class RecordingPostgresqlCdcExecutor : ICdcProviderDatabaseExecutor
{
    private const string CurrentDatabaseName = "dms_test";

    private bool _heartbeatTableExists;
    private bool _heartbeatSingletonExists;
    private bool _documentReplicaIdentityFull;
    private bool _publicationExists;
    private readonly bool _publicationCapturesWorkTable;
    private readonly bool _publicationPublishesAllTables;
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
    private readonly bool _connectorRoleExists;
    private readonly bool _connectorCanLogin;
    private readonly bool _connectorCanReplicate;
    private readonly string _connectorDisallowedRoleAttributes;
    private readonly string _connectorOwnership;
    private bool _connectorDatabaseConnect;
    private bool _connectorSchemaUsage;
    private bool _connectorDocumentSelect;
    private bool _connectorDocumentCacheSelect;
    private bool _connectorHeartbeatSelect;
    private bool _connectorHeartbeatSequenceUpdate;
    private bool _connectorHeartbeatAtUpdate;
    private readonly bool _connectorHeartbeatIdUpdate;
    private readonly string _connectorHeartbeatForbiddenTablePrivileges;
    private readonly string _connectorHeartbeatUnexpectedUpdateColumns;
    private readonly string _connectorDocumentWritePrivileges;
    private readonly string _connectorDocumentCacheWritePrivileges;
    private readonly string _connectorWorkTablePrivileges;
    private readonly string _connectorExtraDmsSelectTables;
    private readonly string _sourceIdentity;
    private readonly CdcSourceTableKind? _omittedSourceInventoryTableKind;
    private readonly string _omittedSourceInventoryColumnName;
    private readonly bool? _heartbeatPrimaryKeyMatches;
    private readonly bool? _heartbeatSingletonCheckMatches;
    private readonly bool? _heartbeatSequenceCheckMatches;

    public RecordingPostgresqlCdcExecutor(
        bool heartbeatTableExists = false,
        bool heartbeatSingletonExists = false,
        bool? heartbeatPrimaryKeyMatches = null,
        bool? heartbeatSingletonCheckMatches = null,
        bool? heartbeatSequenceCheckMatches = null,
        bool documentReplicaIdentityFull = false,
        bool publicationExists = false,
        bool publicationCapturesWorkTable = false,
        bool publicationPublishesAllTables = false,
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
        string slotInvalidationReason = "",
        bool connectorRoleExists = true,
        bool connectorCanLogin = true,
        bool connectorCanReplicate = true,
        string connectorDisallowedRoleAttributes = "",
        string connectorOwnership = "",
        bool connectorDatabaseConnect = false,
        bool connectorSchemaUsage = false,
        bool connectorDocumentSelect = false,
        bool connectorDocumentCacheSelect = false,
        bool connectorHeartbeatSelect = false,
        bool connectorHeartbeatSequenceUpdate = false,
        bool connectorHeartbeatAtUpdate = false,
        bool connectorHeartbeatIdUpdate = false,
        string connectorHeartbeatForbiddenTablePrivileges = "",
        string connectorHeartbeatUnexpectedUpdateColumns = "",
        string connectorDocumentWritePrivileges = "",
        string connectorDocumentCacheWritePrivileges = "",
        string connectorWorkTablePrivileges = "",
        string connectorExtraDmsSelectTables = "",
        string sourceIdentity = CdcProviderSetupContractTestData.SourceIdentity,
        CdcSourceTableKind? omittedSourceInventoryTableKind = null,
        string omittedSourceInventoryColumnName = ""
    )
    {
        _heartbeatTableExists = heartbeatTableExists;
        _heartbeatSingletonExists = heartbeatSingletonExists;
        _heartbeatPrimaryKeyMatches = heartbeatPrimaryKeyMatches;
        _heartbeatSingletonCheckMatches = heartbeatSingletonCheckMatches;
        _heartbeatSequenceCheckMatches = heartbeatSequenceCheckMatches;
        _documentReplicaIdentityFull = documentReplicaIdentityFull;
        _publicationExists = publicationExists;
        _publicationCapturesWorkTable = publicationCapturesWorkTable;
        _publicationPublishesAllTables = publicationPublishesAllTables;
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
        _connectorRoleExists = connectorRoleExists;
        _connectorCanLogin = connectorCanLogin;
        _connectorCanReplicate = connectorCanReplicate;
        _connectorDisallowedRoleAttributes = connectorDisallowedRoleAttributes;
        _connectorOwnership = connectorOwnership;
        _connectorDatabaseConnect = connectorDatabaseConnect;
        _connectorSchemaUsage = connectorSchemaUsage;
        _connectorDocumentSelect = connectorDocumentSelect;
        _connectorDocumentCacheSelect = connectorDocumentCacheSelect;
        _connectorHeartbeatSelect = connectorHeartbeatSelect;
        _connectorHeartbeatSequenceUpdate = connectorHeartbeatSequenceUpdate;
        _connectorHeartbeatAtUpdate = connectorHeartbeatAtUpdate;
        _connectorHeartbeatIdUpdate = connectorHeartbeatIdUpdate;
        _connectorHeartbeatForbiddenTablePrivileges = connectorHeartbeatForbiddenTablePrivileges;
        _connectorHeartbeatUnexpectedUpdateColumns =
            connectorHeartbeatIdUpdate
            || connectorHeartbeatForbiddenTablePrivileges
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("UPDATE", StringComparer.Ordinal)
                ? Csv(connectorHeartbeatUnexpectedUpdateColumns, "HeartbeatId")
                : connectorHeartbeatUnexpectedUpdateColumns;
        _connectorDocumentWritePrivileges = connectorDocumentWritePrivileges;
        _connectorDocumentCacheWritePrivileges = connectorDocumentCacheWritePrivileges;
        _connectorWorkTablePrivileges = connectorWorkTablePrivileges;
        _connectorExtraDmsSelectTables = connectorExtraDmsSelectTables;
        _sourceIdentity = sourceIdentity;
        _omittedSourceInventoryTableKind = omittedSourceInventoryTableKind;
        _omittedSourceInventoryColumnName = omittedSourceInventoryColumnName;
    }

    public List<string> ExecutedSql { get; } = [];

    public List<string> QueriedSql { get; } = [];

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
            slotInvalidationReason: slotInvalidationReason,
            connectorDatabaseConnect: true,
            connectorSchemaUsage: true,
            connectorDocumentSelect: true,
            connectorDocumentCacheSelect: true,
            connectorHeartbeatSelect: true,
            connectorHeartbeatSequenceUpdate: true,
            connectorHeartbeatAtUpdate: true
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

        if (sql.Contains("cdc:postgresql:grant-connector-access"))
        {
            _connectorDatabaseConnect = true;
            _connectorSchemaUsage = true;
            _connectorDocumentSelect = true;
            _connectorDocumentCacheSelect = true;
            _connectorHeartbeatSelect = true;
            _connectorHeartbeatSequenceUpdate = true;
            _connectorHeartbeatAtUpdate = true;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        QueriedSql.Add(sql);

        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = sql switch
        {
            var text when text.Contains("cdc:postgresql:source-fingerprint") =>
            [
                Row(("source_identity", _sourceIdentity)),
            ],
            var text when text.Contains("cdc:postgresql:table-exists") =>
            [
                Row(("table_exists", _heartbeatTableExists.ToString())),
            ],
            var text when text.Contains("cdc:postgresql:heartbeat-shape") =>
            [
                Row(
                    ("primary_key_matches", HeartbeatShapeMatches(_heartbeatPrimaryKeyMatches).ToString()),
                    (
                        "singleton_check_matches",
                        HeartbeatShapeMatches(_heartbeatSingletonCheckMatches).ToString()
                    ),
                    (
                        "sequence_check_matches",
                        HeartbeatShapeMatches(_heartbeatSequenceCheckMatches).ToString()
                    )
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
                        ("publishes_all_tables", _publicationPublishesAllTables.ToString()),
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
            var text when text.Contains("cdc:postgresql:connector-principal-access") =>
            [
                Row(
                    ("role_exists", _connectorRoleExists.ToString()),
                    ("can_login", _connectorCanLogin.ToString()),
                    ("can_replicate", _connectorCanReplicate.ToString()),
                    ("disallowed_role_attributes", _connectorDisallowedRoleAttributes),
                    ("ownership", _connectorOwnership),
                    ("database_connect", _connectorDatabaseConnect.ToString()),
                    ("schema_usage", _connectorSchemaUsage.ToString()),
                    ("document_select", _connectorDocumentSelect.ToString()),
                    ("document_cache_select", _connectorDocumentCacheSelect.ToString()),
                    ("heartbeat_select", _connectorHeartbeatSelect.ToString()),
                    ("heartbeat_sequence_update", _connectorHeartbeatSequenceUpdate.ToString()),
                    ("heartbeat_at_update", _connectorHeartbeatAtUpdate.ToString()),
                    ("heartbeat_id_update", _connectorHeartbeatIdUpdate.ToString()),
                    ("heartbeat_forbidden_table_privileges", _connectorHeartbeatForbiddenTablePrivileges),
                    ("heartbeat_unexpected_update_columns", _connectorHeartbeatUnexpectedUpdateColumns),
                    ("document_write_privileges", _connectorDocumentWritePrivileges),
                    ("document_cache_write_privileges", _connectorDocumentCacheWritePrivileges),
                    ("work_table_privileges", _connectorWorkTablePrivileges),
                    ("extra_dms_select_tables", _connectorExtraDmsSelectTables)
                ),
            ],
            _ => throw new InvalidOperationException($"Unexpected PostgreSQL CDC query: {sql}"),
        };

        return Task.FromResult(rows);
    }

    private bool HeartbeatShapeMatches(bool? explicitShapeMatch) =>
        explicitShapeMatch ?? _heartbeatTableExists;

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
                table
                    .Columns.Where(column =>
                        _omittedSourceInventoryTableKind != table.TableKind
                        || !string.Equals(
                            _omittedSourceInventoryColumnName,
                            column.ColumnName.Value,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(column =>
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

    private static string Csv(params string[] values) =>
        string.Join(
            ",",
            values
                .SelectMany(value =>
                    value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                )
                .Distinct(StringComparer.Ordinal)
        );

    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);
}
