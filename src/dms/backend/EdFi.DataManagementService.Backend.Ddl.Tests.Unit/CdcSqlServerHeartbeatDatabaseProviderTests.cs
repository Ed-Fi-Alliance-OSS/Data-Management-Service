// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
                && observation.SafeObservedValues["captured_column_count"] == "10"
            );
        _result
            .ProviderHistoryObservations.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_cdc_heartbeat"
                && observation.SafeObservedValues["heartbeat_capture_visible"] == "True"
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
    public async Task It_should_reject_gating_role_extra_members_permissions_and_ownership()
    {
        var executor = ExistingArtifactsWithoutConnectorGrants(
            new RecordingSqlServerConnectorAccess
            {
                GatingRoleExists = true,
                GatingRoleDirectMembers = ["connector_principal", "extra_reader"],
                GatingRoleExplicitPermissions = ["SELECT"],
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
        executor.ExecutedSql.Should().NotContain(sql => sql.Contains("cdc:sqlserver:grant-connector-access"));
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
    private bool _databaseCdcEnabled;
    private bool _heartbeatTableExists;
    private bool _heartbeatSingletonExists;
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
    private readonly Dictionary<string, RecordingSqlServerCaptureInstance> _captureInstances;
    private readonly RecordingSqlServerConnectorAccess _connectorAccess;
    private readonly string _sourceIdentity;

    public RecordingSqlServerCdcExecutor(
        bool databaseCdcEnabled = false,
        bool heartbeatTableExists = false,
        bool heartbeatSingletonExists = false,
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
        IReadOnlyList<RecordingSqlServerCaptureInstance>? captureInstances = null,
        RecordingSqlServerConnectorAccess? connectorAccess = null,
        string sourceIdentity = CdcProviderSetupContractTestData.SourceIdentity
    )
    {
        _databaseCdcEnabled = databaseCdcEnabled;
        _heartbeatTableExists = heartbeatTableExists;
        _heartbeatSingletonExists = heartbeatSingletonExists;
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
        IReadOnlyList<RecordingSqlServerCaptureInstance>? captureInstances = null,
        RecordingSqlServerConnectorAccess? connectorAccess = null
    ) =>
        new(
            databaseCdcEnabled: true,
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
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
            var text when text.Contains("cdc:sqlserver:database-cdc-state") =>
            [
                Row(
                    ("is_cdc_enabled", _databaseCdcEnabled.ToString()),
                    ("read_committed_snapshot_on", _readCommittedSnapshotOn.ToString()),
                    ("nested_triggers_value", _nestedTriggersValue)
                ),
            ],
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
                    ("singleton_check_matches", _heartbeatTableExists.ToString()),
                    ("sequence_check_matches", _heartbeatTableExists.ToString())
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
            var text when text.Contains("cdc:sqlserver:connector-principal-access") =>
            [
                ConnectorPrincipalAccessRow(),
            ],
            _ => throw new InvalidOperationException($"Unexpected SQL Server CDC query: {sql}"),
        };

        return Task.FromResult(rows);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> JobHelpRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];

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
            var capture in _captureInstances.Values.OrderBy(capture => capture.CaptureInstanceName.Value)
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
                            ("filegroup_name", capture.FilegroupName),
                            ("partition_switch", capture.PartitionSwitch.ToString()),
                            ("source_is_partitioned", capture.SourceIsPartitioned.ToString()),
                            ("change_table", $"cdc.{capture.CaptureInstanceName.Value}_CT"),
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
    string FilegroupName = "",
    bool PartitionSwitch = true,
    bool SourceIsPartitioned = false
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
        bool partitionSwitch = true,
        bool sourceIsPartitioned = false
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
            indexName ?? ExpectedPrimaryKeyName(tableKind),
            filegroupName,
            partitionSwitch,
            sourceIsPartitioned
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
            ExtractSqlLiteral(sql, "@role_name = N'")
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

    private static string ExpectedPrimaryKeyName(CdcSourceTableKind tableKind) =>
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
}
