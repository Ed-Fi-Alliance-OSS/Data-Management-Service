// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_MssqlCdcHeartbeatDatabase_Initial_Setup
{
    private RecordingSqlServerCdcExecutor _executor = null!;
    private CdcProviderSetupResult _result = null!;

    [SetUp]
    public async Task SetUp()
    {
        _executor = new RecordingSqlServerCdcExecutor();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        _result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: _executor)
        );
    }

    [Test]
    public void It_should_enable_database_cdc_without_mutating_projection_prerequisites_or_jobs()
    {
        _result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        _result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        _executor.ExecutedSql.Should().ContainSingle(sql => sql.Contains("EXEC sys.sp_cdc_enable_db"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("READ_COMMITTED_SNAPSHOT"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_configure"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_add_job"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_change_job"));

        _result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["database_cdc_enabled"] == "True"
                && observation.SafeObservedValues["capture_instance_count"] == "3"
                && observation.SafeObservedValues["capture_job_present"] == "True"
                && observation.SafeObservedValues["cleanup_job_present"] == "True"
            );
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["database_cdc_enabled"] == "True"
                && observation.SafeObservedValues["capture_instance_count"] == "3"
                && observation.SafeObservedValues["capture_job_present"] == "True"
                && observation.SafeObservedValues["cleanup_job_present"] == "True"
            );

        using var manifestDocument = JsonDocument.Parse(_result.ManifestPayload!.Json);
        manifestDocument
            .RootElement.GetProperty("provider_artifacts")
            .EnumerateArray()
            .Should()
            .ContainSingle(artifact =>
                artifact.GetProperty("artifact_kind").GetString() == "provider_history"
                && artifact.GetProperty("artifact_name").GetString() == "sqlserver_database_cdc"
                && artifact.GetProperty("state").GetString() == "created"
                && artifact.GetProperty("observed_values").GetProperty("capture_instance_count").GetString()
                    == "3"
                && artifact.GetProperty("observed_values").GetProperty("capture_job_present").GetString()
                    == "True"
                && artifact.GetProperty("observed_values").GetProperty("cleanup_job_present").GetString()
                    == "True"
            );
    }

    [Test]
    public void It_should_create_the_opt_in_heartbeat_table_and_singleton()
    {
        _result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.State == CdcProviderArtifactState.Created
            );

        var createSql = _executor.ExecutedSql.Single(sql =>
            sql.Contains("cdc:sqlserver:create-heartbeat-table")
        );
        createSql.Should().Contain("CREATE TABLE [dms].[CdcHeartbeat]");
        createSql.Should().Contain("[HeartbeatId] smallint NOT NULL");
        createSql.Should().Contain("[HeartbeatSequence] bigint NOT NULL");
        createSql.Should().Contain("[HeartbeatAt] datetime2(7) NOT NULL");
        createSql.Should().Contain("CONSTRAINT [PK_CdcHeartbeat] PRIMARY KEY CLUSTERED ([HeartbeatId])");
        createSql.Should().Contain("CONSTRAINT [CK_CdcHeartbeat_Singleton] CHECK ([HeartbeatId] = 1)");
        createSql.Should().Contain("CONSTRAINT [CK_CdcHeartbeat_Sequence] CHECK ([HeartbeatSequence] >= 0)");
        createSql.Should().Contain("VALUES (1, 0, sysutcdatetime())");
    }

    [Test]
    public void It_should_return_generated_heartbeat_action_query_and_document_uuid_message_keys()
    {
        _result
            .HeartbeatActionQuery!.Sql.Should()
            .Be(
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1"
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
            .Be(CdcProviderSetupContractTestData.SqlServerSourceFingerprint);
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SourceFingerprint
                && observation.SafeArtifactName.Value == "dms.DataStoreIdentity"
                && observation.State == CdcProviderArtifactState.Matched
                && observation.SafeObservedValues["source_fingerprint_version"] == "dms-source-fingerprint-v1"
                && observation.SafeObservedValues["physical_source_fingerprint"]
                    == CdcProviderSetupContractTestData.SqlServerSourceFingerprint.Value
                && observation.SafeObservedValues["provider_token"] == "sqlserver"
                && !observation.SafeObservedValues.ContainsKey("source_identity")
            );
        _result.ManifestPayload!.Json.Should().Contain("\"observed_source_fingerprint\": {");
        _result
            .ManifestPayload.Json.Should()
            .Contain($"\"value\": \"{CdcProviderSetupContractTestData.SqlServerSourceFingerprint.Value}\"");
        _result.ManifestPayload.Json.Should().NotContain(CdcProviderSetupContractTestData.SourceIdentity);
    }

    [Test]
    public void MssqlCdcCaptureInstances_should_create_binding_derived_capture_instances_for_the_three_fixed_sources()
    {
        var enableCaptureSql = _executor
            .ExecutedSql.Where(sql => sql.Contains("cdc:sqlserver:enable-capture-instance"))
            .ToArray();

        _executor.ExecutedSql.Should().ContainSingle(sql => sql.Contains("cdc:sqlserver:create-gating-role"));
        enableCaptureSql.Should().HaveCount(3);
        enableCaptureSql.Should().OnlyContain(sql => !sql.Contains("DocumentProjectionWork"));
        enableCaptureSql
            .Should()
            .Contain(sql =>
                sql.Contains("@source_schema = N'dms'")
                && sql.Contains("@source_name = N'DocumentCache'")
                && sql.Contains("@capture_instance = N'dms_binding_document_cache'")
                && sql.Contains("@supports_net_changes = 0")
                && sql.Contains("@role_name = N'dms_binding_gate'")
                && sql.Contains("@index_name = NULL")
                && sql.Contains("@filegroup_name = NULL")
                && sql.Contains("@allow_partition_switch = 0")
                && sql.Contains(
                    "@captured_column_list = N'[DocumentId], [DocumentUuid], [ProjectName], [ResourceName], [ResourceVersion], [ContentVersion], [StreamEtag], [LastModifiedAt], [DocumentJson], [ComputedAt]'"
                )
            );
        enableCaptureSql
            .Should()
            .Contain(sql =>
                sql.Contains("@source_name = N'Document'")
                && sql.Contains("@capture_instance = N'dms_binding_document'")
                && sql.Contains(
                    "@captured_column_list = N'[DocumentId], [DocumentUuid], [ResourceKeyId], [CreatedByOwnershipTokenId], [ContentVersion], [IdentityVersion], [ContentLastModifiedAt], [IdentityLastModifiedAt], [CreatedAt]'"
                )
            );
        enableCaptureSql
            .Should()
            .Contain(sql =>
                sql.Contains("@source_name = N'CdcHeartbeat'")
                && sql.Contains("@capture_instance = N'dms_binding_cdc_heartbeat'")
                && sql.Contains(
                    "@captured_column_list = N'[HeartbeatId], [HeartbeatSequence], [HeartbeatAt]'"
                )
            );

        _result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation => observation.State == CdcProviderArtifactState.Created);
        _result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document_cache"
                && observation.SafeObservedValues["source_object"] == "dms.DocumentCache"
                && observation.SafeObservedValues["role_name"] == "dms_binding_gate"
                && observation.SafeObservedValues["supports_net_changes"] == "False"
                && observation.SafeObservedValues["source_index"] == "PK_DocumentCache"
                && observation.SafeObservedValues["expected_source_index"]
                    == "none_or_source_primary_key.PK_DocumentCache"
                && observation.SafeObservedValues["partition_switch"] == "True"
                && observation.SafeObservedValues["expected_partition_switch"]
                    == "disabled_when_source_partitioned"
                && observation.SafeObservedValues["source_is_partitioned"] == "False"
                && observation.SafeObservedValues["captured_column_count"] == "10"
            );
        _result
            .ProviderHistoryObservations.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_cdc_heartbeat"
                && observation.SafeObservedValues["heartbeat_capture_visible"] == "True"
            );
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == "dms_binding_gate"
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["gating_role_exists"] == "True"
                && observation.SafeObservedValues["gating_role_is_normal_role"] == "True"
                && observation.SafeObservedValues["gating_role_direct_members"] == "connector_principal"
                && observation.SafeObservedValues["gating_role_parent_roles"] == "none"
                && observation.SafeObservedValues["gating_role_owned_objects"] == "none"
                && observation.SafeObservedValues["gating_role_explicit_permissions"] == "none"
                && observation.SafeObservedValues["expected_capture_instances_using_role"] == "3"
                && observation.SafeObservedValues["unexpected_capture_instances_using_role"] == "none"
            );
        _result.ManifestPayload!.Json.Should().Contain("\"artifact_kind\": \"sqlserver_gating_role\"");
        _result.ManifestPayload.Json.Should().Contain("\"artifact_name\": \"dms_binding_gate\"");
    }

    [Test]
    public void MssqlCdcCaptureInstances_should_emit_per_capture_retained_lsn_metadata()
    {
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["retained_min_lsn"] == ""
                && observation.SafeObservedValues["retained_max_lsn"] == ""
            );
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document_cache"
                && observation.SafeObservedValues["retained_min_lsn"] == "0x00000000000000000001"
                && observation.SafeObservedValues["retained_max_lsn"] == "0x00000000000000000010"
                && observation.SafeObservedValues["retained_lsn_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document"
                && observation.SafeObservedValues["retained_min_lsn"] == "0x00000000000000000002"
                && observation.SafeObservedValues["retained_max_lsn"] == "0x00000000000000000010"
            );
        _result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_cdc_heartbeat"
                && observation.SafeObservedValues["retained_min_lsn"] == "0x00000000000000000003"
                && observation.SafeObservedValues["retained_max_lsn"] == "0x00000000000000000010"
            );
        _result
            .ProviderHistoryObservations.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation =>
                observation.SafeObservedValues["retained_min_lsn"].StartsWith("0x", StringComparison.Ordinal)
                && observation
                    .SafeObservedValues["retained_max_lsn"]
                    .StartsWith("0x", StringComparison.Ordinal)
                && observation.SafeObservedValues["retained_lsn_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_exact_match_provider_normal_source_index_and_nonpartitioned_partition_switch()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances:
            [
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.DocumentCache,
                    indexName: "PK_DocumentCache",
                    partitionSwitch: true
                ),
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.Document,
                    indexName: "PK_Document",
                    partitionSwitch: true
                ),
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.CdcHeartbeat,
                    indexName: "PK_CdcHeartbeat",
                    partitionSwitch: true
                ),
            ]
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation =>
                observation.State == CdcProviderArtifactState.Matched
                && observation.SafeObservedValues["partition_switch"] == "True"
                && observation.SafeObservedValues["source_is_partitioned"] == "False"
            );
    }
}

[TestFixture]
public class Given_MssqlCdcHeartbeatDatabase_ValidateOnly
{
    [Test]
    public async Task It_should_not_enable_database_cdc_when_missing()
    {
        var executor = new RecordingSqlServerCdcExecutor();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Missing
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING");
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_exact_match_existing_database_cdc_heartbeat_and_capture_instances_without_writes()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected()
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation => observation.State == CdcProviderArtifactState.Matched);
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == "dms_binding_gate"
                && observation.State == CdcProviderArtifactState.Matched
                && observation.SafeObservedValues["expected_capture_instances_using_role"] == "3"
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_when_singleton_check_constraint_has_wrong_operator()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            heartbeatSingletonCheckDefinition: "([HeartbeatId]<=(1))"
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        AssertHeartbeatConstraintMismatch(result, "singleton_check");
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_when_sequence_check_constraint_has_wrong_operator()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            heartbeatSequenceCheckDefinition: "([HeartbeatSequence]<=(0))"
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        AssertHeartbeatConstraintMismatch(result, "sequence_check");
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_when_existing_table_cdc_has_missing_required_jobs()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(captureInstanceCount: 3);
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_DATABASE_CDC_JOBS_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryUnknown
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_report_disabled_stopped_or_failed_jobs_without_repairing_them()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureJobEnabled: "False",
            captureJobRunning: "False",
            captureJobLastRunStatus: "0",
            captureInstances: SqlServerCaptureInstanceTestData.Expected()
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CDC_JOB_DISABLED"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CAPTURE_JOB_NOT_RUNNING"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CDC_JOB_LAST_RUN_FAILED"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_mark_final_provider_history_refresh_mismatched_when_job_metadata_becomes_unavailable()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            dropJobsDuringFinalProviderMetadataRefresh: true
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_DATABASE_CDC_JOBS_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["capture_instance_count"] == "3"
                && observation.SafeObservedValues["capture_job_present"] == "False"
                && observation.SafeObservedValues["cleanup_job_present"] == "False"
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["capture_job_present"] == "False"
                && observation.Classification == CdcProviderRetryContinuityClassification.SourceHistoryUnknown
            );

        using var manifestDocument = JsonDocument.Parse(result.ManifestPayload!.Json);
        manifestDocument
            .RootElement.GetProperty("provider_artifacts")
            .EnumerateArray()
            .Should()
            .ContainSingle(artifact =>
                artifact.GetProperty("artifact_kind").GetString() == "provider_history"
                && artifact.GetProperty("state").GetString() == "mismatched"
                && artifact.GetProperty("observed_values").GetProperty("capture_job_present").GetString()
                    == "False"
            );
    }

    [Test]
    public async Task It_should_replace_provider_history_artifact_when_final_metadata_refresh_is_unavailable()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            failFinalProviderMetadataRefresh: true
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_PROVIDER_METADATA_UNAVAILABLE"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                && diagnostic.ProviderErrorClass == nameof(InvalidOperationException)
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Unavailable
                && observation.SafeObservedValues["history"] == "unavailable"
                && observation.SafeObservedValues["provider_error_class"] == nameof(InvalidOperationException)
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["history"] == "unavailable"
                && observation.Classification == CdcProviderRetryContinuityClassification.SourceHistoryUnknown
            );

        using var manifestDocument = JsonDocument.Parse(result.ManifestPayload!.Json);
        manifestDocument
            .RootElement.GetProperty("provider_artifacts")
            .EnumerateArray()
            .Should()
            .ContainSingle(artifact =>
                artifact.GetProperty("artifact_kind").GetString() == "provider_history"
                && artifact.GetProperty("state").GetString() == "unavailable"
                && artifact.GetProperty("observed_values").GetProperty("history").GetString() == "unavailable"
            );
    }

    [Test]
    public async Task It_should_report_projection_prerequisite_diagnostics_without_mutation()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            readCommittedSnapshotOn: false,
            nestedTriggersValue: "0",
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected()
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_READ_COMMITTED_SNAPSHOT_OFF"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_NESTED_TRIGGERS_NOT_ENABLED"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_capture_instances_are_missing()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document"
                && observation.State == CdcProviderArtifactState.Missing
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_reject_dirty_existing_gating_role_before_creating_missing_capture_instances()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            connectorAccess: new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal", "extra_reader"],
                GatingRoleExplicitPermissions = ["cdc.unexpected_CT.SELECT"],
                GatingRoleOwnedObjects = ["schema:dms"],
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == "dms_binding_gate"
                && diagnostic.ObservedValue!.Contains("members:connector_principal,extra_reader")
                && diagnostic.ObservedValue.Contains("permissions:cdc.unexpected_CT.SELECT")
                && diagnostic.ObservedValue.Contains("ownership:schema_dms")
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == "dms_binding_gate"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["gating_role_direct_members"]
                    == "connector_principal,extra_reader"
                && observation.SafeObservedValues["gating_role_explicit_permissions"]
                    == "cdc.unexpected_CT.SELECT"
                && observation.SafeObservedValues["gating_role_owned_objects"] == "schema_dms"
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:create-gating-role"));
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_enable_table"));
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_use_clean_existing_gating_role_to_create_missing_capture_instances()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            connectorAccess: new RecordingSqlServerConnectorAccess { GatingRoleExists = true }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:create-gating-role"));
        executor
            .ExecutedSql.Where(sql => sql.Contains("cdc:sqlserver:enable-capture-instance"))
            .Should()
            .HaveCount(3);
        executor
            .ExecutedSql.Should()
            .ContainSingle(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_capture_instance_metadata_mismatches()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances:
            [
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.DocumentCache,
                    supportsNetChanges: true
                ),
                RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.Document),
                RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.CdcHeartbeat),
            ]
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document_cache"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["supports_net_changes"] == "True"
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_initial_setup_capture_instance_has_drop_pending()
    {
        await AssertPendingDropCaptureInstanceFailsClosedAsync(
            CdcProviderSetupMode.InitialCreateOrExactMatch
        );
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_validate_only_capture_instance_has_drop_pending()
    {
        await AssertPendingDropCaptureInstanceFailsClosedAsync(CdcProviderSetupMode.ValidateOnly);
    }

    private static async Task AssertPendingDropCaptureInstanceFailsClosedAsync(CdcProviderSetupMode mode)
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances:
            [
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.DocumentCache,
                    hasDropPending: true
                ),
                RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.Document),
                RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.CdcHeartbeat),
            ]
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(mode: mode, databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document_cache"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["has_drop_pending"] == "True"
                && observation.SafeObservedValues["expected_has_drop_pending"] == "False"
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CAPTURE_INSTANCE_DROP_PENDING"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && diagnostic.SafeName.Value == "dms_binding_document_cache"
                && diagnostic.ExpectedValue == "has_drop_pending=False"
                && diagnostic.ObservedValue == "has_drop_pending=True"
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_disable_table"));
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_enable_table"));
    }

    private static void AssertHeartbeatConstraintMismatch(
        CdcProviderSetupResult result,
        string mismatchedCheckKey
    )
    {
        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.SafeArtifactName.Value == "dms.CdcHeartbeat"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["primary_key"] == "matched"
                && observation.SafeObservedValues[mismatchedCheckKey] == "mismatched"
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && diagnostic.SafeName.Value == "dms.CdcHeartbeat"
            );
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_capture_instance_uses_source_index_or_partition_switch()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances:
            [
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.DocumentCache,
                    indexName: "IX_DocumentCache_Manual"
                ),
                RecordingSqlServerCaptureInstance.Expected(
                    CdcSourceTableKind.Document,
                    partitionSwitch: true,
                    sourceIsPartitioned: true
                ),
                RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.CdcHeartbeat),
            ]
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document_cache"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["source_index"] == "IX_DocumentCache_Manual"
                && observation.SafeObservedValues["expected_source_index"]
                    == "none_or_source_primary_key.PK_DocumentCache"
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["partition_switch"] == "True"
                && observation.SafeObservedValues["expected_partition_switch"]
                    == "disabled_when_source_partitioned"
                && observation.SafeObservedValues["source_is_partitioned"] == "True"
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_document_projection_work_is_captured()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData
                .Expected()
                .Append(RecordingSqlServerCaptureInstance.WorkTable())
                .ToArray()
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_WORK_TABLE_CAPTURE_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            );
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_document_projection_work"
                && observation.State == CdcProviderArtifactState.Mismatched
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task MssqlCdcCaptureInstances_should_fail_closed_when_extra_dms_schema_capture_instance_is_present()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData
                .Expected()
                .Append(RecordingSqlServerCaptureInstance.UnexpectedDmsTable())
                .Append(RecordingSqlServerCaptureInstance.NonDmsTable())
                .ToArray()
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_UNEXPECTED_DMS_CAPTURE_INSTANCE"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ValidationMismatch
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && diagnostic.SafeName.Value == "dms_unexpected_descriptor"
                && diagnostic.ExpectedValue == "only-dms.DocumentCache-dms.Document-dms.CdcHeartbeat-captured"
                && diagnostic.ObservedValue == "dms.Descriptor_capture_dms_unexpected_descriptor"
            );
        result.Diagnostics.Should().NotContain(diagnostic => diagnostic.SafeName.Value == "edfi_school_cdc");
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_unexpected_descriptor"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["source_object"] == "dms.Descriptor"
                && observation.SafeObservedValues["role_name"] == "other_cdc_gate"
            )
            .And.NotContain(observation => observation.SafeArtifactName.Value == "edfi_school_cdc");
        result
            .ProviderHistoryObservations.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_unexpected_descriptor"
                && observation.SafeObservedValues["source_object"] == "dms.Descriptor"
                && observation.Classification == CdcProviderRetryContinuityClassification.FailClosed
            )
            .And.NotContain(observation => observation.SafeArtifactName.Value == "edfi_school_cdc");
        result.ManifestPayload!.Json.Should().Contain("dms_unexpected_descriptor");
        result.ManifestPayload.Json.Should().Contain("dms.Descriptor");
        result.ManifestPayload.Json.Should().NotContain("edfi_school_cdc");
        executor.ExecutedSql.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_MssqlCdcPrincipalAccess_Initial_Setup
{
    [Test]
    public async Task It_should_create_the_gating_role_membership_and_grant_only_required_privileges_to_existing_connector_user()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeArtifactName.Value == "connector_principal"
                && observation.State == CdcProviderArtifactState.Created
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == "dms_binding_gate"
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["gating_role_direct_members"] == "connector_principal"
                && observation.SafeObservedValues["expected_capture_instances_using_role"] == "3"
            );

        var grantSql = executor.ExecutedSql.Single(sql =>
            sql.Contains("cdc:sqlserver:grant-connector-access")
        );
        grantSql.Should().Contain("CREATE ROLE [dms_binding_gate]");
        grantSql.Should().Contain("ALTER ROLE [dms_binding_gate] ADD MEMBER [connector_principal]");
        grantSql.Should().Contain("GRANT SELECT ON OBJECT::[dms].[Document] TO [connector_principal]");
        grantSql.Should().Contain("GRANT SELECT ON OBJECT::[dms].[DocumentCache] TO [connector_principal]");
        grantSql.Should().Contain("GRANT SELECT ON OBJECT::[dms].[CdcHeartbeat] TO [connector_principal]");
        grantSql
            .Should()
            .Contain(
                "GRANT UPDATE ([HeartbeatSequence], [HeartbeatAt]) ON OBJECT::[dms].[CdcHeartbeat] TO [connector_principal]"
            );
        grantSql.Should().NotContain("DocumentProjectionWork");
        grantSql.Should().NotContain("CREATE LOGIN");
        grantSql.Should().NotContain("ALTER LOGIN");

        result
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafeObjectName.Value == "role.dms_binding_gate"
                && grant.Privileges.SequenceEqual(new[] { "MEMBER" })
            );
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
    public async Task It_should_not_create_connector_logins_or_users_when_connector_principal_is_missing()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants(
            new RecordingSqlServerConnectorAccess { ConnectorExists = false }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_USER_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            );
        executor
            .ExecutedSql.Should()
            .NotContain(sql =>
                sql.Contains("cdc:sqlserver:grant-connector-access")
                || sql.Contains("CREATE LOGIN")
                || sql.Contains("CREATE USER")
            );
    }

    [Test]
    public async Task It_should_reject_disallowed_elevated_connector_memberships()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants(
            new RecordingSqlServerConnectorAccess
            {
                DisallowedDatabaseRoles = ["db_owner"],
                DisallowedServerRoles = ["sysadmin"],
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_ELEVATED_MEMBERSHIP_MISMATCH"
                && diagnostic.ObservedValue!.Contains("db_owner")
                && diagnostic.ObservedValue.Contains("sysadmin")
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }

    [Test]
    public async Task It_should_reject_unexpected_custom_database_role_membership_and_required_grants_still_missing()
    {
        var executor = ExistingArtifactsWithConnectorAccess(
            new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal"],
                DatabaseConnect = true,
                DisallowedDatabaseRoles = ["custom_cdc_reader"],
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_ELEVATED_MEMBERSHIP_MISMATCH"
                && diagnostic.ObservedValue!.Contains("custom_cdc_reader")
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_REQUIRED_GRANTS_MISSING"
                && diagnostic.ObservedValue!.Contains("SELECT_dms.Document")
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }

    [Test]
    public async Task It_should_reject_gating_role_extra_members_permissions_and_ownership()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants(
            new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal", "extra_reader"],
                GatingRoleExplicitPermissions = ["dms.Document.SELECT"],
                GatingRoleOwnedObjects = ["schema:dms"],
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == "dms_binding_gate"
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == "dms_binding_gate"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["gating_role_direct_members"]
                    == "connector_principal,extra_reader"
                && observation.SafeObservedValues["gating_role_owned_objects"] == "schema_dms"
                && observation.SafeObservedValues["gating_role_explicit_permissions"] == "dms.Document.SELECT"
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }

    [Test]
    public async Task It_should_reject_gating_role_select_on_unexpected_cdc_schema_object()
    {
        var executor = ExistingArtifactsWithConnectorAccess(
            new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal"],
                GatingRoleExplicitPermissions = ["cdc.unexpected_CT.SELECT"],
                DatabaseConnect = true,
                DocumentSelect = true,
                DocumentCacheSelect = true,
                HeartbeatSelect = true,
                HeartbeatSequenceUpdate = true,
                HeartbeatAtUpdate = true,
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == "dms_binding_gate"
                && diagnostic.ObservedValue!.Contains("permissions:cdc.unexpected_CT.SELECT")
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == "dms_binding_gate"
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["gating_role_explicit_permissions"]
                    == "cdc.unexpected_CT.SELECT"
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }

    [Test]
    public async Task It_should_reject_inherited_or_public_forbidden_connector_permissions()
    {
        var executor = ExistingArtifactsWithConnectorAccess(
            new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal"],
                DatabaseConnect = true,
                DocumentSelect = true,
                DocumentCacheSelect = true,
                HeartbeatSelect = true,
                HeartbeatSequenceUpdate = true,
                HeartbeatAtUpdate = true,
                DisallowedDatabaseRoles = ["custom_writer"],
                DocumentWritePrivileges = ["UPDATE.via.role.custom_writer"],
                DocumentCacheWritePrivileges = ["DELETE.via.public"],
                HeartbeatWritePrivileges = ["INSERT.via.public", "DELETE.via.public"],
                WorkTablePrivileges = ["SELECT.via.public"],
                ExtraDmsSelectTables = ["ResourceKey.via.role.custom_writer"],
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_SOURCE_WRITE_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains("UPDATE.via.role.custom_writer")
                && diagnostic.ObservedValue.Contains("DELETE.via.public")
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_HEARTBEAT_UPDATE_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains("INSERT.via.public")
                && diagnostic.ObservedValue.Contains("DELETE.via.public")
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
                && diagnostic.ObservedValue == "SELECT.via.public"
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.ObservedValue == "ResourceKey.via.role.custom_writer"
            );
        result
            .GrantInventory.Should()
            .Contain(grant =>
                grant.SafeObjectName.Value == "dms.Document"
                && grant.Privileges.Contains("UPDATE")
                && !grant.Privileges.Contains("UPDATE.via.role.custom_writer")
            );
    }

    [Test]
    public async Task It_should_reject_connector_access_to_document_projection_work()
    {
        var executor = ExistingArtifactsWithConnectorAccess(
            new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal"],
                DatabaseConnect = true,
                DocumentSelect = true,
                DocumentCacheSelect = true,
                HeartbeatSelect = true,
                HeartbeatSequenceUpdate = true,
                HeartbeatAtUpdate = true,
                WorkTablePrivileges = ["SELECT"],
            }
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
                && diagnostic.ExpectedValue == "no-dms.DocumentProjectionWork-privileges"
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_optional_live_probe_reports_connector_boundary_failure()
    {
        var executor = ExistingArtifactsWithConnectorAccess(RecordingSqlServerConnectorAccess.Exact());
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                databaseExecutor: executor,
                connectorPrincipalProbeFactory: new FailingSqlServerConnectorPrincipalProbeFactory()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        var diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_PROBE_BOUNDARY_FAILURE"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            )
            .Which;
        diagnostic.ProviderErrorClass.Should().BeNull();
    }

    private static RecordingSqlServerCdcExecutor ExistingArtifactsWithoutConnectorGrants(
        RecordingSqlServerConnectorAccess? connectorAccess = null
    ) =>
        RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            connectorAccess: connectorAccess ?? RecordingSqlServerConnectorAccess.MissingGrants()
        );

    private static RecordingSqlServerCdcExecutor ExistingArtifactsWithConnectorAccess(
        RecordingSqlServerConnectorAccess connectorAccess
    ) =>
        RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            connectorAccess: connectorAccess
        );
}

[TestFixture]
public class Given_MssqlCdcPrincipalAccess_ValidateOnly
{
    [Test]
    public async Task It_should_report_missing_required_grants_without_creating_them()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureInstances: SqlServerCaptureInstanceTestData.Expected(),
            connectorAccess: RecordingSqlServerConnectorAccess.MissingGrants()
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_REQUIRED_GRANTS_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            );
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
    }
}

internal sealed class FailingSqlServerConnectorPrincipalProbeFactory : ICdcConnectorPrincipalProbeFactory
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
                        Code: "CDC_SQLSERVER_CONNECTOR_PROBE_BOUNDARY_FAILURE",
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

internal static class SqlServerCaptureInstanceTestData
{
    internal static IReadOnlyList<RecordingSqlServerCaptureInstance> Expected() =>
        [
            RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.DocumentCache),
            RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.Document),
            RecordingSqlServerCaptureInstance.Expected(CdcSourceTableKind.CdcHeartbeat),
        ];
}

internal sealed class RecordingSqlServerConnectorAccess
{
    public bool ConnectorExists { get; init; } = true;

    public bool ConnectorIsDatabasePrincipal { get; init; } = true;

    public bool GatingRoleExists { get; set; }

    public bool GatingRoleIsNormalRole { get; set; } = true;

    public IReadOnlyList<string> GatingRoleDirectMembers { get; set; } = [];

    public IReadOnlyList<string> GatingRoleParentRoles { get; init; } = [];

    public IReadOnlyList<string> GatingRoleOwnedObjects { get; init; } = [];

    public IReadOnlyList<string> GatingRoleExplicitPermissions { get; init; } = [];

    public IReadOnlyList<string> DisallowedDatabaseRoles { get; init; } = [];

    public IReadOnlyList<string> DisallowedServerRoles { get; init; } = [];

    public IReadOnlyList<string> Ownership { get; init; } = [];

    public bool DatabaseConnect { get; set; }

    public bool DocumentSelect { get; set; }

    public bool DocumentCacheSelect { get; set; }

    public bool HeartbeatSelect { get; set; }

    public bool HeartbeatSequenceUpdate { get; set; }

    public bool HeartbeatAtUpdate { get; set; }

    public bool HeartbeatIdUpdate { get; init; }

    public IReadOnlyList<string> HeartbeatWritePrivileges { get; init; } = [];

    public IReadOnlyList<string> DocumentWritePrivileges { get; init; } = [];

    public IReadOnlyList<string> DocumentCacheWritePrivileges { get; init; } = [];

    public IReadOnlyList<string> WorkTablePrivileges { get; init; } = [];

    public IReadOnlyList<string> ExtraDmsSelectTables { get; init; } = [];

    public static RecordingSqlServerConnectorAccess MissingGrants() => new();

    public static RecordingSqlServerConnectorAccess Exact() =>
        new()
        {
            GatingRoleExists = true,
            GatingRoleDirectMembers = ["connector_principal"],
            DatabaseConnect = true,
            DocumentSelect = true,
            DocumentCacheSelect = true,
            HeartbeatSelect = true,
            HeartbeatSequenceUpdate = true,
            HeartbeatAtUpdate = true,
        };
}

internal sealed class RecordingSqlServerCdcExecutor : ICdcProviderDatabaseExecutor
{
    private const string ExpectedHeartbeatSingletonCheckDefinition = "([HeartbeatId]=(1))";
    private const string ExpectedHeartbeatSequenceCheckDefinition = "([HeartbeatSequence]>=(0))";

    private bool _databaseCdcEnabled;
    private bool _heartbeatTableExists;
    private bool _heartbeatSingletonExists;
    private string _heartbeatSingletonCheckDefinition;
    private string _heartbeatSequenceCheckDefinition;
    private readonly bool _readCommittedSnapshotOn;
    private readonly string _nestedTriggersValue;
    private int _captureInstanceCount;
    private bool _captureJobPresent;
    private bool _cleanupJobPresent;
    private readonly string _captureJobEnabled;
    private readonly string _captureJobRunning;
    private readonly string _captureJobLastRunStatus;
    private readonly string _cleanupJobEnabled;
    private readonly string _cleanupJobRunning;
    private readonly string _cleanupJobLastRunStatus;
    private readonly bool _failFinalProviderMetadataRefresh;
    private readonly bool _dropJobsDuringFinalProviderMetadataRefresh;
    private readonly Dictionary<string, RecordingSqlServerCaptureInstance> _captureInstances;
    private readonly RecordingSqlServerConnectorAccess _connectorAccess;
    private readonly string _sourceIdentity;
    private int _databaseCdcStateQueryCount;

    public RecordingSqlServerCdcExecutor(
        bool databaseCdcEnabled = false,
        bool heartbeatTableExists = false,
        bool heartbeatSingletonExists = false,
        string heartbeatSingletonCheckDefinition = ExpectedHeartbeatSingletonCheckDefinition,
        string heartbeatSequenceCheckDefinition = ExpectedHeartbeatSequenceCheckDefinition,
        bool readCommittedSnapshotOn = true,
        string nestedTriggersValue = "1",
        int captureInstanceCount = 0,
        bool captureJobPresent = false,
        bool cleanupJobPresent = false,
        string captureJobEnabled = "True",
        string captureJobRunning = "True",
        string captureJobLastRunStatus = "",
        string cleanupJobEnabled = "True",
        string cleanupJobRunning = "False",
        string cleanupJobLastRunStatus = "",
        bool failFinalProviderMetadataRefresh = false,
        bool dropJobsDuringFinalProviderMetadataRefresh = false,
        IReadOnlyList<RecordingSqlServerCaptureInstance>? captureInstances = null,
        RecordingSqlServerConnectorAccess? connectorAccess = null,
        string sourceIdentity = CdcProviderSetupContractTestData.SourceIdentity
    )
    {
        _databaseCdcEnabled = databaseCdcEnabled;
        _heartbeatTableExists = heartbeatTableExists;
        _heartbeatSingletonExists = heartbeatSingletonExists;
        _heartbeatSingletonCheckDefinition = heartbeatSingletonCheckDefinition;
        _heartbeatSequenceCheckDefinition = heartbeatSequenceCheckDefinition;
        _readCommittedSnapshotOn = readCommittedSnapshotOn;
        _nestedTriggersValue = nestedTriggersValue;
        _captureInstances = (captureInstances ?? []).ToDictionary(
            capture => capture.CaptureInstanceName.Value,
            StringComparer.Ordinal
        );
        _captureInstanceCount = Math.Max(captureInstanceCount, _captureInstances.Count);
        _captureJobPresent = captureJobPresent;
        _cleanupJobPresent = cleanupJobPresent;
        _captureJobEnabled = captureJobEnabled;
        _captureJobRunning = captureJobRunning;
        _captureJobLastRunStatus = captureJobLastRunStatus;
        _cleanupJobEnabled = cleanupJobEnabled;
        _cleanupJobRunning = cleanupJobRunning;
        _cleanupJobLastRunStatus = cleanupJobLastRunStatus;
        _failFinalProviderMetadataRefresh = failFinalProviderMetadataRefresh;
        _dropJobsDuringFinalProviderMetadataRefresh = dropJobsDuringFinalProviderMetadataRefresh;
        _connectorAccess = connectorAccess ?? RecordingSqlServerConnectorAccess.MissingGrants();
        _sourceIdentity = sourceIdentity;
    }

    public List<string> ExecutedSql { get; } = [];

    public static RecordingSqlServerCdcExecutor WithExistingHeartbeatDatabase(
        bool readCommittedSnapshotOn = true,
        string nestedTriggersValue = "1",
        int captureInstanceCount = 0,
        bool captureJobPresent = false,
        bool cleanupJobPresent = false,
        string captureJobEnabled = "True",
        string captureJobRunning = "True",
        string captureJobLastRunStatus = "",
        string cleanupJobEnabled = "True",
        string cleanupJobRunning = "False",
        string cleanupJobLastRunStatus = "",
        bool failFinalProviderMetadataRefresh = false,
        bool dropJobsDuringFinalProviderMetadataRefresh = false,
        IReadOnlyList<RecordingSqlServerCaptureInstance>? captureInstances = null,
        RecordingSqlServerConnectorAccess? connectorAccess = null,
        string heartbeatSingletonCheckDefinition = ExpectedHeartbeatSingletonCheckDefinition,
        string heartbeatSequenceCheckDefinition = ExpectedHeartbeatSequenceCheckDefinition
    ) =>
        new(
            databaseCdcEnabled: true,
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            heartbeatSingletonCheckDefinition: heartbeatSingletonCheckDefinition,
            heartbeatSequenceCheckDefinition: heartbeatSequenceCheckDefinition,
            readCommittedSnapshotOn: readCommittedSnapshotOn,
            nestedTriggersValue: nestedTriggersValue,
            captureInstanceCount: captureInstanceCount,
            captureJobPresent: captureJobPresent,
            cleanupJobPresent: cleanupJobPresent,
            captureJobEnabled: captureJobEnabled,
            captureJobRunning: captureJobRunning,
            captureJobLastRunStatus: captureJobLastRunStatus,
            cleanupJobEnabled: cleanupJobEnabled,
            cleanupJobRunning: cleanupJobRunning,
            cleanupJobLastRunStatus: cleanupJobLastRunStatus,
            failFinalProviderMetadataRefresh: failFinalProviderMetadataRefresh,
            dropJobsDuringFinalProviderMetadataRefresh: dropJobsDuringFinalProviderMetadataRefresh,
            captureInstances: captureInstances,
            connectorAccess: connectorAccess ?? RecordingSqlServerConnectorAccess.Exact()
        );

    public Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        ExecutedSql.Add(sql);

        if (sql.Contains("cdc:sqlserver:enable-database-cdc"))
        {
            _databaseCdcEnabled = true;
        }

        if (sql.Contains("cdc:sqlserver:create-heartbeat-table"))
        {
            _heartbeatTableExists = true;
            _heartbeatSingletonExists = true;
            _heartbeatSingletonCheckDefinition = ExpectedHeartbeatSingletonCheckDefinition;
            _heartbeatSequenceCheckDefinition = ExpectedHeartbeatSequenceCheckDefinition;
        }

        if (sql.Contains("INSERT INTO [dms].[CdcHeartbeat]"))
        {
            _heartbeatSingletonExists = true;
        }

        if (sql.Contains("cdc:sqlserver:enable-capture-instance"))
        {
            var captureInstance = RecordingSqlServerCaptureInstance.FromEnableSql(sql);
            _captureInstances[captureInstance.CaptureInstanceName.Value] = captureInstance;
            _captureInstanceCount = Math.Max(_captureInstanceCount, _captureInstances.Count);
            _captureJobPresent = true;
            _cleanupJobPresent = true;
        }

        if (sql.Contains("cdc:sqlserver:create-gating-role"))
        {
            _connectorAccess.GatingRoleExists = true;
            _connectorAccess.GatingRoleIsNormalRole = true;
        }

        if (sql.Contains("cdc:sqlserver:grant-connector-access"))
        {
            _connectorAccess.GatingRoleExists = true;
            _connectorAccess.GatingRoleIsNormalRole = true;
            _connectorAccess.GatingRoleDirectMembers = ["connector_principal"];
            _connectorAccess.DatabaseConnect = true;
            _connectorAccess.DocumentSelect = true;
            _connectorAccess.DocumentCacheSelect = true;
            _connectorAccess.HeartbeatSelect = true;
            _connectorAccess.HeartbeatSequenceUpdate = true;
            _connectorAccess.HeartbeatAtUpdate = true;
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
            var text when text.Contains("cdc:sqlserver:source-fingerprint") =>
            [
                Row(("source_identity", _sourceIdentity)),
            ],
            var text when text.Contains("cdc:sqlserver:database-cdc-state") => DatabaseCdcStateRows(),
            var text when text.Contains("cdc:sqlserver:capture-instance-count") =>
            [
                Row(("capture_instance_count", _captureInstanceCount.ToString())),
            ],
            var text when text.Contains("cdc:sqlserver:help-jobs") => JobHelpRows(),
            var text when text.Contains("cdc:sqlserver:job-runtime") => JobRuntimeRows(),
            var text when text.Contains("cdc:sqlserver:retained-lsn") =>
            [
                Row(("lsn_row_count", "0"), ("min_lsn", ""), ("max_lsn", "")),
            ],
            var text when text.Contains("cdc:sqlserver:table-exists") =>
            [
                Row(("table_exists", _heartbeatTableExists.ToString())),
            ],
            var text when text.Contains("cdc:sqlserver:heartbeat-shape") =>
            [
                Row(
                    ("primary_key_matches", _heartbeatTableExists.ToString()),
                    (
                        "singleton_check_matches",
                        (
                            _heartbeatTableExists
                            && MatchesExpectedCheckDefinition(
                                _heartbeatSingletonCheckDefinition,
                                ExpectedHeartbeatSingletonCheckDefinition
                            )
                        ).ToString()
                    ),
                    (
                        "sequence_check_matches",
                        (
                            _heartbeatTableExists
                            && MatchesExpectedCheckDefinition(
                                _heartbeatSequenceCheckDefinition,
                                ExpectedHeartbeatSequenceCheckDefinition
                            )
                        ).ToString()
                    )
                ),
            ],
            var text when text.Contains("cdc:sqlserver:heartbeat-singleton") =>
            [
                Row(
                    ("row_count", _heartbeatSingletonExists ? "1" : "0"),
                    ("singleton_row_count", _heartbeatSingletonExists ? "1" : "0"),
                    ("extra_row_count", "0"),
                    ("heartbeat_sequence", _heartbeatSingletonExists ? "0" : "-1")
                ),
            ],
            var text when text.Contains("cdc:sqlserver:source-inventory") => SourceInventoryRows(),
            var text when text.Contains("cdc:sqlserver:capture-instances") => CaptureInstanceRows(),
            var text when text.Contains("cdc:sqlserver:gating-role-pre-capture") =>
            [
                ConnectorPrincipalAccessRow(),
            ],
            var text when text.Contains("cdc:sqlserver:connector-principal-access") =>
            [
                ConnectorPrincipalAccessRow(),
            ],
            _ => throw new InvalidOperationException($"Unexpected SQL Server CDC query: {sql}"),
        };

        return Task.FromResult(rows);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> DatabaseCdcStateRows()
    {
        _databaseCdcStateQueryCount++;

        if (_failFinalProviderMetadataRefresh && IsFinalProviderMetadataRefresh)
        {
            throw new InvalidOperationException("Final SQL Server CDC provider metadata refresh failed.");
        }

        return
        [
            Row(
                ("is_cdc_enabled", _databaseCdcEnabled.ToString()),
                ("read_committed_snapshot_on", _readCommittedSnapshotOn.ToString()),
                ("nested_triggers_value", _nestedTriggersValue)
            ),
        ];
    }

    private bool IsFinalProviderMetadataRefresh => _databaseCdcStateQueryCount > 1;

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> JobHelpRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];

        if (_dropJobsDuringFinalProviderMetadataRefresh && IsFinalProviderMetadataRefresh)
        {
            return rows;
        }

        if (_captureJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "capture"),
                    ("job_name", "cdc.dms_test_capture"),
                    ("maxtrans", "500"),
                    ("maxscans", "10"),
                    ("continuous", "1"),
                    ("pollinginterval", "5"),
                    ("retention", ""),
                    ("threshold", "")
                )
            );
        }

        if (_cleanupJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "cleanup"),
                    ("job_name", "cdc.dms_test_cleanup"),
                    ("maxtrans", ""),
                    ("maxscans", ""),
                    ("continuous", ""),
                    ("pollinginterval", ""),
                    ("retention", "4320"),
                    ("threshold", "5000")
                )
            );
        }

        return rows;
    }

    private IReadOnlyDictionary<string, string?> ConnectorPrincipalAccessRow() =>
        Row(
            ("connector_exists", _connectorAccess.ConnectorExists.ToString()),
            ("connector_is_database_principal", _connectorAccess.ConnectorIsDatabasePrincipal.ToString()),
            ("gating_role_exists", _connectorAccess.GatingRoleExists.ToString()),
            ("gating_role_is_normal_role", _connectorAccess.GatingRoleIsNormalRole.ToString()),
            (
                "gating_role_member",
                _connectorAccess
                    .GatingRoleDirectMembers.SequenceEqual(["connector_principal"], StringComparer.Ordinal)
                    .ToString()
            ),
            ("gating_role_direct_members", Csv(_connectorAccess.GatingRoleDirectMembers)),
            ("gating_role_parent_roles", Csv(_connectorAccess.GatingRoleParentRoles)),
            ("gating_role_owned_objects", Csv(_connectorAccess.GatingRoleOwnedObjects)),
            ("gating_role_explicit_permissions", Csv(_connectorAccess.GatingRoleExplicitPermissions)),
            ("expected_capture_instances_using_role", ExpectedCaptureInstancesUsingGatingRole().ToString()),
            ("unexpected_capture_instances_using_role", Csv(UnexpectedCaptureInstancesUsingGatingRole())),
            ("disallowed_database_roles", Csv(_connectorAccess.DisallowedDatabaseRoles)),
            ("disallowed_server_roles", Csv(_connectorAccess.DisallowedServerRoles)),
            ("ownership", Csv(_connectorAccess.Ownership)),
            ("database_connect", _connectorAccess.DatabaseConnect.ToString()),
            ("document_select", _connectorAccess.DocumentSelect.ToString()),
            ("document_cache_select", _connectorAccess.DocumentCacheSelect.ToString()),
            ("heartbeat_select", _connectorAccess.HeartbeatSelect.ToString()),
            ("heartbeat_sequence_update", _connectorAccess.HeartbeatSequenceUpdate.ToString()),
            ("heartbeat_at_update", _connectorAccess.HeartbeatAtUpdate.ToString()),
            ("heartbeat_id_update", _connectorAccess.HeartbeatIdUpdate.ToString()),
            ("document_write_privileges", Csv(_connectorAccess.DocumentWritePrivileges)),
            ("document_cache_write_privileges", Csv(_connectorAccess.DocumentCacheWritePrivileges)),
            ("heartbeat_write_privileges", Csv(_connectorAccess.HeartbeatWritePrivileges)),
            ("work_table_privileges", Csv(_connectorAccess.WorkTablePrivileges)),
            ("extra_dms_select_tables", Csv(_connectorAccess.ExtraDmsSelectTables))
        );

    private int ExpectedCaptureInstancesUsingGatingRole()
    {
        var expected = SqlServerCaptureInstanceTestData
            .Expected()
            .Select(capture => capture.CaptureInstanceName.Value)
            .ToHashSet(StringComparer.Ordinal);

        return _captureInstances.Values.Count(capture =>
            capture.GatingRoleName.Value == "dms_binding_gate"
            && expected.Contains(capture.CaptureInstanceName.Value)
        );
    }

    private IReadOnlyList<string> UnexpectedCaptureInstancesUsingGatingRole()
    {
        var expected = SqlServerCaptureInstanceTestData
            .Expected()
            .Select(capture => capture.CaptureInstanceName.Value)
            .ToHashSet(StringComparer.Ordinal);

        return _captureInstances
            .Values.Where(capture =>
                capture.GatingRoleName.Value == "dms_binding_gate"
                && !expected.Contains(capture.CaptureInstanceName.Value)
            )
            .Select(capture => capture.CaptureInstanceName.Value)
            .ToArray();
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> JobRuntimeRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];

        if (_dropJobsDuringFinalProviderMetadataRefresh && IsFinalProviderMetadataRefresh)
        {
            return rows;
        }

        if (_captureJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "capture"),
                    ("job_name", "cdc.dms_test_capture"),
                    ("job_id", "capture-job"),
                    ("enabled", _captureJobEnabled),
                    ("running", _captureJobRunning),
                    ("last_run_status", _captureJobLastRunStatus)
                )
            );
        }

        if (_cleanupJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "cleanup"),
                    ("job_name", "cdc.dms_test_cleanup"),
                    ("job_id", "cleanup-job"),
                    ("enabled", _cleanupJobEnabled),
                    ("running", _cleanupJobRunning),
                    ("last_run_status", _cleanupJobLastRunStatus)
                )
            );
        }

        return rows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> SourceInventoryRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];
        foreach (var table in CdcProviderSetupContractTestData.BuildSqlServerRequiredSourceInventory())
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

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> CaptureInstanceRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];
        foreach (
            var capture in _captureInstances
                .Values.Where(capture =>
                    string.Equals(
                        capture.SourceTable.Schema.Value,
                        DmsTableNames.DmsSchema.Value,
                        StringComparison.Ordinal
                    )
                )
                .OrderBy(capture => capture.CaptureInstanceName.Value)
        )
        {
            rows.AddRange(
                capture.CapturedColumns.Select(
                    (column, index) =>
                        Row(
                            ("capture_instance", capture.CaptureInstanceName.Value),
                            ("source_schema", capture.SourceTable.Schema.Value),
                            ("source_name", capture.SourceTable.Name),
                            ("table_kind", capture.TableKindToken),
                            ("expected_capture_instance_for_source", ""),
                            ("expected_source_schema", ""),
                            ("expected_source_name", ""),
                            ("role_name", capture.GatingRoleName.Value),
                            ("supports_net_changes", capture.SupportsNetChanges.ToString()),
                            ("has_drop_pending", capture.HasDropPending.ToString()),
                            ("index_name", capture.IndexName),
                            ("source_primary_key_name", capture.SourcePrimaryKeyName),
                            ("filegroup_name", capture.FilegroupName),
                            ("partition_switch", capture.PartitionSwitch.ToString()),
                            ("source_is_partitioned", capture.SourceIsPartitioned.ToString()),
                            ("change_table", $"cdc.{capture.CaptureInstanceName.Value}_CT"),
                            ("retained_min_lsn", capture.RetainedMinLsn),
                            ("retained_max_lsn", capture.RetainedMaxLsn),
                            ("column_name", column),
                            ("column_ordinal", (index + 1).ToString())
                        )
                )
            );
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);

    private static bool MatchesExpectedCheckDefinition(
        string observedDefinition,
        string expectedDefinition
    ) => StripSqlWhitespace(observedDefinition).Equals(expectedDefinition, StringComparison.Ordinal);

    private static string StripSqlWhitespace(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static string Csv(IEnumerable<string> values) => string.Join(",", values);
}

internal sealed record RecordingSqlServerCaptureInstance(
    string TableKindToken,
    DbTableName SourceTable,
    CdcSafeName CaptureInstanceName,
    CdcSafeName GatingRoleName,
    IReadOnlyList<string> CapturedColumns,
    bool SupportsNetChanges = false,
    bool HasDropPending = false,
    string IndexName = "",
    string SourcePrimaryKeyName = "",
    string FilegroupName = "",
    bool PartitionSwitch = false,
    bool SourceIsPartitioned = false,
    string RetainedMinLsn = "",
    string RetainedMaxLsn = ""
)
{
    public static RecordingSqlServerCaptureInstance Expected(
        CdcSourceTableKind tableKind,
        string? captureInstanceName = null,
        string gatingRoleName = "dms_binding_gate",
        IReadOnlyList<string>? capturedColumns = null,
        bool supportsNetChanges = false,
        bool hasDropPending = false,
        string? indexName = null,
        string filegroupName = "",
        bool partitionSwitch = false,
        bool sourceIsPartitioned = false,
        string? retainedMinLsn = null,
        string? retainedMaxLsn = null
    )
    {
        var table = CdcProviderSetupContractTestData
            .BuildSqlServerRequiredSourceInventory()
            .Single(table => table.TableKind == tableKind);

        return new RecordingSqlServerCaptureInstance(
            SourceTableKindToken(tableKind),
            table.TableName,
            new CdcSafeName(captureInstanceName ?? DefaultCaptureInstanceName(tableKind)),
            new CdcSafeName(gatingRoleName),
            capturedColumns ?? table.Columns.Select(column => column.ColumnName.Value).ToArray(),
            supportsNetChanges,
            hasDropPending,
            indexName ?? "",
            DefaultPrimaryKeyName(tableKind),
            filegroupName,
            partitionSwitch,
            sourceIsPartitioned,
            retainedMinLsn ?? DefaultRetainedMinLsn(tableKind),
            retainedMaxLsn ?? "0x00000000000000000010"
        );
    }

    public static RecordingSqlServerCaptureInstance WorkTable() =>
        new(
            "document_projection_work",
            DmsTableNames.DocumentProjectionWork,
            new CdcSafeName("dms_binding_document_projection_work"),
            new CdcSafeName("dms_binding_gate"),
            ["DocumentId", "RequiredContentVersion"],
            IndexName: "PK_DocumentProjectionWork"
        );

    public static RecordingSqlServerCaptureInstance UnexpectedDmsTable() =>
        new(
            "unexpected",
            DmsTableNames.Descriptor,
            new CdcSafeName("dms_unexpected_descriptor"),
            new CdcSafeName("other_cdc_gate"),
            ["DocumentId", "Namespace", "CodeValue"]
        );

    public static RecordingSqlServerCaptureInstance NonDmsTable() =>
        new(
            "unexpected",
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new CdcSafeName("edfi_school_cdc"),
            new CdcSafeName("other_cdc_gate"),
            ["SchoolId", "NameOfInstitution"]
        );

    public static RecordingSqlServerCaptureInstance FromEnableSql(string sql)
    {
        var tableKind = sql switch
        {
            var text when text.Contains("@source_name = N'DocumentCache'") =>
                CdcSourceTableKind.DocumentCache,
            var text when text.Contains("@source_name = N'Document'") => CdcSourceTableKind.Document,
            var text when text.Contains("@source_name = N'CdcHeartbeat'") => CdcSourceTableKind.CdcHeartbeat,
            _ => throw new InvalidOperationException($"Could not identify CDC source table from SQL: {sql}"),
        };

        return Expected(
            tableKind,
            ExtractSqlLiteral(sql, "@capture_instance = N'"),
            ExtractSqlLiteral(sql, "@role_name = N'"),
            indexName: DefaultPrimaryKeyName(tableKind),
            partitionSwitch: true
        );
    }

    private static string ExtractSqlLiteral(string sql, string marker)
    {
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Expected marker '{marker}' in SQL.");
        }

        start += marker.Length;
        var end = sql.IndexOf('\'', start);
        if (end < 0)
        {
            throw new InvalidOperationException($"Expected SQL literal terminator after marker '{marker}'.");
        }

        return sql[start..end];
    }

    private static string DefaultCaptureInstanceName(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.Document => "dms_binding_document",
            CdcSourceTableKind.DocumentCache => "dms_binding_document_cache",
            CdcSourceTableKind.CdcHeartbeat => "dms_binding_cdc_heartbeat",
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static string SourceTableKindToken(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.Document => "document",
            CdcSourceTableKind.DocumentCache => "document_cache",
            CdcSourceTableKind.CdcHeartbeat => "cdc_heartbeat",
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static string DefaultRetainedMinLsn(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.DocumentCache => "0x00000000000000000001",
            CdcSourceTableKind.Document => "0x00000000000000000002",
            CdcSourceTableKind.CdcHeartbeat => "0x00000000000000000003",
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static string DefaultPrimaryKeyName(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.Document => "PK_Document",
            CdcSourceTableKind.DocumentCache => "PK_DocumentCache",
            CdcSourceTableKind.CdcHeartbeat => "PK_CdcHeartbeat",
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };
}
