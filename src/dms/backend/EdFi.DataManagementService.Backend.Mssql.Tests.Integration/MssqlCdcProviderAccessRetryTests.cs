// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("CdcProviderAccessRetry")]
public class Given_MssqlCdcProviderAccessRetry
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const string ConnectorPassword = "EdFi_Dms1!";
    private const string GatingRoleName = "dms_binding_gate";
    private const string DocumentCaptureInstanceName = "dms_binding_document";
    private const string DocumentCacheCaptureInstanceName = "dms_binding_document_cache";
    private const string HeartbeatCaptureInstanceName = "dms_binding_cdc_heartbeat";
    private const string WorkTableCaptureInstanceName = "dms_binding_projection_work";
    private const string WrongDocumentCaptureInstanceName = "dms_unexpected_document";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorPrincipalName = null!;

    [SetUp]
    public async Task SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server CDC access/retry integration tests require a MssqlAdmin connection string."
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorPrincipalName = $"cdc_connector_{Guid.NewGuid():N}";

        CreateConnectorLoginAndUser(_database.DatabaseName, _connectorPrincipalName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_connectorPrincipalName))
        {
            DropConnectorLoginIfExists(_connectorPrincipalName);
        }
    }

    [Test]
    public async Task It_should_create_connector_access_and_pass_live_boundary_probe_on_initial_setup()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            connectorPrincipalProbeFactory: new SqlServerConnectorPrincipalBoundaryProbeFactory(
                _database.ConnectionString,
                _connectorPrincipalName,
                ConnectorPassword
            )
        );

        result
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Created
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.SafeArtifactName.Value == "dms.CdcHeartbeat"
                && observation.State == CdcProviderArtifactState.Created
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.SafeArtifactName.Value == _connectorPrincipalName
                && observation.State == CdcProviderArtifactState.Created
            )
            .And.Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["gating_role_direct_members"] == _connectorPrincipalName
                && observation.SafeObservedValues["expected_capture_instances_using_role"] == "3"
            );
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation => observation.State == CdcProviderArtifactState.Created);

        AssertRequiredGrantInventory(result);
        await AssertConnectorPrincipalAccessAsync(connection);
    }

    [Test]
    public async Task It_should_fail_closed_when_optional_live_probe_reports_connector_boundary_failure()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            connectorPrincipalProbeFactory: new FailingSqlServerConnectorPrincipalProbeFactory()
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_PROBE_BOUNDARY_FAILURE"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            );
        await AssertConnectorPrincipalAccessAsync(connection);
    }

    [TestCase("GRANT SELECT ON SCHEMA::[dms] TO public;")]
    [TestCase("GRANT SELECT TO public;")]
    public async Task It_should_fail_closed_when_public_broad_select_reaches_forbidden_dms_tables(
        string grantSql
    )
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, grantSql);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
                && diagnostic.ObservedValue == "SELECT.via.public"
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains(".via.public", StringComparison.Ordinal)
            );
        result
            .GrantInventory.Should()
            .Contain(grant =>
                grant.SafeObjectName.Value == "dms.DocumentProjectionWork"
                && grant.Privileges.SequenceEqual(new[] { "SELECT" })
            );
        result
            .GrantInventory.Should()
            .NotContain(grant =>
                grant.SafeObjectName.Value == "dms.Document"
                && grant.Privileges.SequenceEqual(new[] { "SELECT" })
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_public_schema_update_reaches_cdc_sources()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "GRANT UPDATE ON SCHEMA::[dms] TO public;");

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_SOURCE_WRITE_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains("Document=UPDATE.via.public", StringComparison.Ordinal)
                && diagnostic.ObservedValue.Contains(
                    "DocumentCache=UPDATE.via.public",
                    StringComparison.Ordinal
                )
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_HEARTBEAT_UPDATE_GRANT_MISMATCH"
                && diagnostic.ObservedValue == "UPDATE.via.public"
            );
    }

    [Test]
    public async Task It_should_honor_connector_deny_before_public_schema_select_grant()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(
            connection,
            $"""
            GRANT SELECT ON SCHEMA::[dms] TO public;
            DENY SELECT ON OBJECT::[dms].[DocumentProjectionWork] TO {QuoteIdentifier(
                _connectorPrincipalName
            )};
            """
        );

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH")
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains(".via.public", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_should_honor_public_schema_deny_before_direct_required_grants()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "DENY SELECT ON SCHEMA::[dms] TO public;");

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_REQUIRED_GRANTS_MISSING"
                && diagnostic.ObservedValue!.Contains("SELECT_dms.Document", StringComparison.Ordinal)
                && diagnostic.ObservedValue.Contains("SELECT_dms.DocumentCache", StringComparison.Ordinal)
                && diagnostic.ObservedValue.Contains("SELECT_dms.CdcHeartbeat", StringComparison.Ordinal)
            );
        result
            .GrantInventory.Should()
            .NotContain(grant =>
                grant.SafeObjectName.Value == "dms.Document"
                || grant.SafeObjectName.Value == "dms.DocumentCache"
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_required_source_column_select_is_denied()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);

        await ExecuteNonQueryAsync(
            connection,
            $"""
            DENY SELECT ON OBJECT::[dms].[Document] ([DocumentUuid]) TO {QuoteIdentifier(
                _connectorPrincipalName
            )};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_REQUIRED_GRANTS_MISSING"
                && diagnostic.ObservedValue!.Contains("SELECT_dms.Document", StringComparison.Ordinal)
            );
        validateResult
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.Grant
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation
                    .SafeObservedValues["source_select_denials"]
                    .Contains("dms.Document.DocumentUuid.via.direct", StringComparison.Ordinal)
            );
        (await HasConnectorColumnPermissionStateAsync(connection, "Document", "DocumentUuid", "SELECT", "D"))
            .Should()
            .BeTrue("validation reports the DENY without permission repair");
    }

    [Test]
    public async Task It_should_exact_match_on_rerun_and_validate_only_after_initial_setup()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var snapshot = await ReadStableMetadataSnapshotAsync(connection);

        var rerunResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        rerunResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.ExactMatch, DescribeDiagnostics(rerunResult.Diagnostics));
        AssertMatchedCaptureArtifacts(rerunResult);
        (await ReadStableMetadataSnapshotAsync(connection))
            .Should()
            .BeEquivalentTo(snapshot, options => options.WithStrictOrdering());

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.ExactMatch, DescribeDiagnostics(validateResult.Diagnostics));
        AssertMatchedCaptureArtifacts(validateResult);
        (await ReadStableMetadataSnapshotAsync(connection))
            .Should()
            .BeEquivalentTo(snapshot, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_should_report_missing_required_artifacts_in_validate_only_without_creating_them()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_DATABASE_CDC_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
        (await IsDatabaseCdcEnabledAsync(connection)).Should().BeFalse();
        (await TableExistsAsync(connection, "CdcHeartbeat")).Should().BeFalse();
        (await CaptureInstanceCountAsync(connection)).Should().Be(0);
        (await RoleExistsAsync(connection, GatingRoleName)).Should().BeFalse();
    }

    [Test]
    public async Task It_should_exact_match_created_capture_metadata_on_partial_retry()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        DropConnectorUserInDatabase(_database.DatabaseName, _connectorPrincipalName);

        var failedSetupResult = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch
        );

        failedSetupResult.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        failedSetupResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_USER_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure
            );
        var createdCaptures = await ReadCaptureColumnsAsync(connection);
        createdCaptures
            .Select(row => row.CaptureInstance)
            .Distinct()
            .Should()
            .BeEquivalentTo(
                DocumentCacheCaptureInstanceName,
                DocumentCaptureInstanceName,
                HeartbeatCaptureInstanceName
            );
        (await HasConnectorObjectPermissionAsync(connection, "Document", "SELECT")).Should().BeFalse();

        CreateConnectorLoginAndUser(_database.DatabaseName, _connectorPrincipalName);

        var retryResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        retryResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(retryResult.Diagnostics));
        retryResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        AssertMatchedCaptureArtifacts(retryResult);
        (await ReadCaptureColumnsAsync(connection))
            .Should()
            .BeEquivalentTo(createdCaptures, options => options.WithStrictOrdering());
        (await HasConnectorObjectPermissionAsync(connection, "Document", "SELECT")).Should().BeTrue();
    }

    [Test]
    public async Task It_should_fail_closed_on_mismatched_grants_without_removing_them()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);

        await ExecuteNonQueryAsync(
            connection,
            $"""
            GRANT SELECT ON OBJECT::[dms].[DocumentProjectionWork] TO {QuoteIdentifier(
                _connectorPrincipalName
            )};
            GRANT UPDATE ON OBJECT::[dms].[Document] TO {QuoteIdentifier(_connectorPrincipalName)};
            GRANT UPDATE ([HeartbeatId]) ON OBJECT::[dms].[CdcHeartbeat] TO {QuoteIdentifier(
                _connectorPrincipalName
            )};
            GRANT SELECT ON OBJECT::[dms].[ResourceKey] TO {QuoteIdentifier(_connectorPrincipalName)};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_SOURCE_WRITE_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains("Document=UPDATE", StringComparison.Ordinal)
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_HEARTBEAT_UPDATE_GRANT_MISMATCH"
                && diagnostic.ObservedValue == "UPDATE.via.direct"
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.ObservedValue == "dms.ResourceKey.via.direct"
            );
        (await HasConnectorObjectPermissionAsync(connection, "DocumentProjectionWork", "SELECT"))
            .Should()
            .BeTrue("validation reports the mismatch without destructive cleanup");
        (await HasConnectorObjectPermissionAsync(connection, "Document", "UPDATE")).Should().BeTrue();
        (await HasConnectorColumnPermissionAsync(connection, "CdcHeartbeat", "HeartbeatId", "UPDATE"))
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task It_should_fail_closed_on_custom_role_access_without_removing_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var customRole = $"cdc_custom_reader_{Guid.NewGuid():N}";

        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE ROLE {QuoteIdentifier(customRole)};
            ALTER ROLE {QuoteIdentifier(customRole)} ADD MEMBER {QuoteIdentifier(_connectorPrincipalName)};

            REVOKE SELECT ON OBJECT::[dms].[Document] FROM {QuoteIdentifier(_connectorPrincipalName)};
            REVOKE SELECT ON OBJECT::[dms].[DocumentCache] FROM {QuoteIdentifier(_connectorPrincipalName)};
            REVOKE SELECT ON OBJECT::[dms].[CdcHeartbeat] FROM {QuoteIdentifier(_connectorPrincipalName)};
            REVOKE UPDATE ([HeartbeatSequence], [HeartbeatAt]) ON OBJECT::[dms].[CdcHeartbeat] FROM {QuoteIdentifier(
                _connectorPrincipalName
            )};

            GRANT SELECT ON OBJECT::[dms].[Document] TO {QuoteIdentifier(customRole)};
            GRANT SELECT ON OBJECT::[dms].[DocumentCache] TO {QuoteIdentifier(customRole)};
            GRANT SELECT ON OBJECT::[dms].[CdcHeartbeat] TO {QuoteIdentifier(customRole)};
            GRANT UPDATE ([HeartbeatSequence], [HeartbeatAt]) ON OBJECT::[dms].[CdcHeartbeat] TO {QuoteIdentifier(
                customRole
            )};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_ELEVATED_MEMBERSHIP_MISMATCH"
                && diagnostic.ObservedValue!.Contains(customRole, StringComparison.Ordinal)
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_REQUIRED_GRANTS_MISSING"
                && diagnostic.ObservedValue!.Contains("SELECT_dms.Document", StringComparison.Ordinal)
            );
        (await IsConnectorDatabaseRoleMemberAsync(connection, customRole)).Should().BeTrue();
        (await HasConnectorObjectPermissionAsync(connection, "Document", "SELECT"))
            .Should()
            .BeFalse("custom role access must not be accepted as the direct provider grant path");
    }

    [Test]
    public async Task It_should_fail_closed_on_public_forbidden_permissions_without_removing_them()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);

        await ExecuteNonQueryAsync(
            connection,
            """
            GRANT SELECT ON OBJECT::[dms].[DocumentProjectionWork] TO public;
            GRANT UPDATE ON OBJECT::[dms].[Document] TO public;
            GRANT SELECT ON OBJECT::[dms].[ResourceKey] TO public;
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH"
                && diagnostic.ObservedValue == "SELECT.via.public"
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_SOURCE_WRITE_GRANT_MISMATCH"
                && diagnostic.ObservedValue!.Contains("Document=UPDATE.via.public", StringComparison.Ordinal)
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH"
                && diagnostic.ObservedValue == "dms.ResourceKey.via.public"
            );
    }

    [Test]
    public async Task It_should_exact_match_when_public_work_table_grant_is_denied_to_connector()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);

        await ExecuteNonQueryAsync(
            connection,
            $"""
            GRANT SELECT ON OBJECT::[dms].[DocumentProjectionWork] TO public;
            DENY SELECT ON OBJECT::[dms].[DocumentProjectionWork] TO {QuoteIdentifier(
                _connectorPrincipalName
            )};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.ExactMatch, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        AssertMatchedCaptureArtifacts(validateResult);
        (await HasConnectorObjectPermissionAsync(connection, "DocumentProjectionWork", "SELECT"))
            .Should()
            .BeFalse("the connector deny still removes effective work-table access");
    }

    [Test]
    public async Task It_should_fail_closed_on_elevated_connector_membership_without_downgrading_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await AddConnectorToDatabaseRoleAsync(connection, "db_owner");

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CONNECTOR_ELEVATED_MEMBERSHIP_MISMATCH"
                && diagnostic.ObservedValue!.Contains("db_owner", StringComparison.Ordinal)
            );
        (await IsConnectorDatabaseRoleMemberAsync(connection, "db_owner")).Should().BeTrue();
        (await HasConnectorObjectPermissionAsync(connection, "Document", "SELECT"))
            .Should()
            .BeFalse("setup must not grant connector access when the principal is elevated");
    }

    [Test]
    public async Task It_should_fail_closed_on_gating_role_mismatch_without_removing_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var extraMember = $"cdc_extra_{Guid.NewGuid():N}";

        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE USER {QuoteIdentifier(extraMember)} WITHOUT LOGIN;
            ALTER ROLE {QuoteIdentifier(GatingRoleName)} ADD MEMBER {QuoteIdentifier(extraMember)};
            GRANT SELECT ON OBJECT::[dms].[Document] TO {QuoteIdentifier(GatingRoleName)};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == GatingRoleName
            );
        validateResult
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation
                    .SafeObservedValues["gating_role_direct_members"]
                    .Contains(extraMember, StringComparison.Ordinal)
                && observation.SafeObservedValues["gating_role_explicit_permissions"] == "dms.Document.SELECT"
            );
        (await ReadGatingRoleMembersAsync(connection)).Should().Contain(extraMember);
        (await HasRoleObjectPermissionAsync(connection, GatingRoleName, "Document", "SELECT"))
            .Should()
            .BeTrue("validation reports the mismatched role shape without destructive cleanup");
    }

    [Test]
    public async Task It_should_fail_closed_on_gating_role_select_for_unexpected_cdc_object_without_removing_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var unexpectedCdcObjectName = $"UnexpectedCdcRead_{Guid.NewGuid():N}";
        var expectedPermissionToken = $"cdc.{unexpectedCdcObjectName}.SELECT";

        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE TABLE [cdc].{QuoteIdentifier(unexpectedCdcObjectName)} ([Id] int NOT NULL);
            GRANT SELECT ON OBJECT::[cdc].{QuoteIdentifier(unexpectedCdcObjectName)} TO {QuoteIdentifier(
                GatingRoleName
            )};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == GatingRoleName
                && diagnostic.ObservedValue!.Contains(expectedPermissionToken, StringComparison.Ordinal)
            );
        validateResult
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["gating_role_explicit_permissions"]
                    == expectedPermissionToken
            );
        (
            await HasRoleObjectPermissionAsync(
                connection,
                GatingRoleName,
                unexpectedCdcObjectName,
                "SELECT",
                schemaName: "cdc"
            )
        )
            .Should()
            .BeTrue("validation reports the mismatched CDC role permission without destructive cleanup");
    }

    [Test]
    public async Task It_should_fail_closed_on_gating_role_deny_for_expected_cdc_object_without_removing_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var expectedPermissionToken = $"cdc.{DocumentCaptureInstanceName}_CT.DENY_SELECT";

        await ExecuteNonQueryAsync(
            connection,
            $"""
            DENY SELECT ON OBJECT::[cdc].[{DocumentCaptureInstanceName}_CT] TO {QuoteIdentifier(
                GatingRoleName
            )};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == GatingRoleName
                && diagnostic.ObservedValue!.Contains(expectedPermissionToken, StringComparison.Ordinal)
            );
        validateResult
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["gating_role_explicit_permissions"]
                    == expectedPermissionToken
            );
        (
            await HasRoleObjectPermissionStateAsync(
                connection,
                GatingRoleName,
                $"{DocumentCaptureInstanceName}_CT",
                "SELECT",
                "D",
                schemaName: "cdc"
            )
        )
            .Should()
            .BeTrue("validation reports the mismatched CDC role DENY without destructive cleanup");
    }

    [Test]
    public async Task It_should_fail_closed_on_revoked_gating_role_select_for_expected_cdc_object_without_repairing_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var revokedCdcObjectName = $"fn_cdc_get_all_changes_{DocumentCaptureInstanceName}";
        var expectedPermissionToken = $"cdc.{revokedCdcObjectName}.SELECT";

        await ExecuteNonQueryAsync(
            connection,
            $"""
            REVOKE SELECT ON OBJECT::[cdc].[{revokedCdcObjectName}] FROM {QuoteIdentifier(
                GatingRoleName
            )};
            """
        );

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_GATING_ROLE_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.SafeName.Value == GatingRoleName
                && diagnostic.ObservedValue!.Contains(
                    $"missing_cdc_selects:{expectedPermissionToken}",
                    StringComparison.Ordinal
                )
            );
        validateResult
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation
                    .SafeObservedValues["missing_gating_role_cdc_object_selects"]
                    .Contains(expectedPermissionToken, StringComparison.Ordinal)
            );
        (
            await HasRoleObjectPermissionAsync(
                connection,
                GatingRoleName,
                revokedCdcObjectName,
                "SELECT",
                schemaName: "cdc"
            )
        )
            .Should()
            .BeFalse("validation reports the missing CDC role SELECT without destructive repair");
    }

    [Test]
    public async Task It_should_fail_closed_on_work_table_capture_without_disabling_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);

        await EnableWorkTableCaptureAsync(connection);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        validateResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_WORK_TABLE_CAPTURE_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
            );
        (await CaptureInstanceExistsAsync(connection, WorkTableCaptureInstanceName))
            .Should()
            .BeTrue("validation reports the forbidden capture without destructive cleanup");
    }

    [Test]
    public async Task It_should_fail_closed_on_wrong_capture_instance_name_without_creating_replacement()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await EnableDatabaseCdcAndHeartbeatAsync(connection);
        await EnableWrongDocumentCaptureAsync(connection);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && diagnostic.SafeName.Value == DocumentCaptureInstanceName
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_UNEXPECTED_DMS_CAPTURE_INSTANCE"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && diagnostic.SafeName.Value == WrongDocumentCaptureInstanceName
            );
        (await CaptureInstanceExistsAsync(connection, WrongDocumentCaptureInstanceName)).Should().BeTrue();
        (await CaptureInstanceExistsAsync(connection, DocumentCaptureInstanceName))
            .Should()
            .BeFalse("setup must not create a replacement capture while a mismatched source capture exists");
    }

    [Test]
    public async Task It_should_fail_closed_on_heartbeat_shape_mismatch_without_repairing_it()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await CreateMismatchedHeartbeatTableAsync(connection);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
            );
        (await ColumnExistsAsync(connection, "CdcHeartbeat", "HeartbeatAt"))
            .Should()
            .BeFalse("setup must not alter a mismatched heartbeat table into shape");
        (await CaptureInstanceCountAsync(connection)).Should().Be(0);
    }

    [Test]
    public async Task It_should_fail_source_fingerprint_mismatch_before_creating_provider_artifacts()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
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
        (await IsDatabaseCdcEnabledAsync(connection)).Should().BeFalse();
        (await TableExistsAsync(connection, "CdcHeartbeat")).Should().BeFalse();
        (await CaptureInstanceCountAsync(connection)).Should().Be(0);
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        SqlConnection connection,
        CdcProviderSetupMode mode,
        string? boundSourceIdentity = null,
        ICdcConnectorPrincipalProbeFactory? connectorPrincipalProbeFactory = null
    )
    {
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: CdcProvider.SqlServer,
                mode: mode,
                boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                    CdcProvider.SqlServer,
                    boundSourceIdentity ?? await ReadDataStoreIdentityAsync(connection)
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("sa")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorPrincipalName)),
                artifactNames: CdcProviderArtifactNames.ForSqlServer(
                    new CdcSafeName(GatingRoleName),
                    new Dictionary<CdcSourceTableKind, CdcSafeName>
                    {
                        [CdcSourceTableKind.Document] = new(DocumentCaptureInstanceName),
                        [CdcSourceTableKind.DocumentCache] = new(DocumentCacheCaptureInstanceName),
                        [CdcSourceTableKind.CdcHeartbeat] = new(HeartbeatCaptureInstanceName),
                    }
                ),
                artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
                expectedSourceInventory: CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
                    SqlDialectFactory.Create(SqlDialect.Mssql)
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

    private static void AssertMatchedCaptureArtifacts(CdcProviderSetupResult result)
    {
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation =>
                observation.State == CdcProviderArtifactState.Matched
                && observation
                    .SafeObservedValues["expected_source_index"]
                    .StartsWith("none_or_source_primary_key.", StringComparison.Ordinal)
                && observation.SafeObservedValues["expected_partition_switch"]
                    == "disabled_when_source_partitioned"
                && observation.SafeObservedValues["source_is_partitioned"] == "False"
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Matched
                && observation.SafeObservedValues["gating_role_exists"] == "True"
                && observation.SafeObservedValues["gating_role_is_normal_role"] == "True"
                && observation.SafeObservedValues["expected_capture_instances_using_role"] == "3"
                && observation.SafeObservedValues["unexpected_capture_instances_using_role"] == "none"
            );
    }

    private async Task AssertConnectorPrincipalAccessAsync(SqlConnection connection)
    {
        (await ReadGatingRoleMembersAsync(connection)).Should().Equal(_connectorPrincipalName);
        (await HasConnectorDatabasePermissionAsync(connection, "CONNECT")).Should().BeTrue();
        (await HasConnectorObjectPermissionAsync(connection, "Document", "SELECT")).Should().BeTrue();
        (await HasConnectorObjectPermissionAsync(connection, "DocumentCache", "SELECT")).Should().BeTrue();
        (await HasConnectorObjectPermissionAsync(connection, "CdcHeartbeat", "SELECT")).Should().BeTrue();
        (await HasConnectorObjectPermissionAsync(connection, "DocumentProjectionWork", "SELECT"))
            .Should()
            .BeFalse();
        (await HasConnectorObjectPermissionAsync(connection, "Document", "UPDATE")).Should().BeFalse();
        (await HasConnectorObjectPermissionAsync(connection, "DocumentCache", "UPDATE")).Should().BeFalse();
        (await HasConnectorColumnPermissionAsync(connection, "CdcHeartbeat", "HeartbeatSequence", "UPDATE"))
            .Should()
            .BeTrue();
        (await HasConnectorColumnPermissionAsync(connection, "CdcHeartbeat", "HeartbeatAt", "UPDATE"))
            .Should()
            .BeTrue();
        (await HasConnectorColumnPermissionAsync(connection, "CdcHeartbeat", "HeartbeatId", "UPDATE"))
            .Should()
            .BeFalse();
        (await IsConnectorDatabaseRoleMemberAsync(connection, "db_owner")).Should().BeFalse();
        (await IsConnectorDatabaseRoleMemberAsync(connection, "db_ddladmin")).Should().BeFalse();
        (await IsConnectorDatabaseRoleMemberAsync(connection, "db_datareader")).Should().BeFalse();
        (await IsConnectorDatabaseRoleMemberAsync(connection, "db_datawriter")).Should().BeFalse();

        var expectedCdcObjectSelects = await ReadExpectedCdcObjectSelectPermissionTokensAsync(connection);
        expectedCdcObjectSelects.Should().HaveCountGreaterThanOrEqualTo(3);
        var gatingRolePermissionTokens = (await ReadRoleObjectPermissionsAsync(connection, GatingRoleName))
            .Select(PermissionToken)
            .Order(StringComparer.Ordinal)
            .ToArray();
        gatingRolePermissionTokens.Should().Equal(expectedCdcObjectSelects);
    }

    private async Task<StableMetadataSnapshot> ReadStableMetadataSnapshotAsync(SqlConnection connection) =>
        new(
            SourceIdentity: await ReadDataStoreIdentityAsync(connection),
            EffectiveSchemaHash: await ReadEffectiveSchemaHashAsync(connection),
            ConnectorLoginSid: await ReadConnectorLoginSidAsync(connection),
            DocumentCacheState: await ReadDocumentCacheStateAsync(connection),
            Heartbeat: await ReadHeartbeatSnapshotAsync(connection),
            CaptureColumns: await ReadCaptureColumnsAsync(connection),
            ConnectorDirectPermissions: await ReadConnectorDirectPermissionsAsync(connection),
            GatingRoleMembers: await ReadGatingRoleMembersAsync(connection),
            CdcJobs: await ReadCdcJobsAsync(connection)
        );

    private static async Task<string> ReadDataStoreIdentityAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(nvarchar(36), [SourceIdentity])
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1;
            """;
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<string> ReadEffectiveSchemaHashAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [EffectiveSchemaHash]
            FROM [dms].[EffectiveSchema];
            """;
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private async Task<string> ReadConnectorLoginSidAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sys.fn_varbintohexstr([sid])
            FROM sys.server_principals
            WHERE [name] = @connector_principal;
            """;
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<DocumentCacheStateSnapshot> ReadDocumentCacheStateAsync(
        SqlConnection connection
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var snapshot = new DocumentCacheStateSnapshot(reader.GetString(0), reader.GetBoolean(1));
        (await reader.ReadAsync()).Should().BeFalse();
        return snapshot;
    }

    private static async Task<HeartbeatSnapshot> ReadHeartbeatSnapshotAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [HeartbeatId], [HeartbeatSequence]
            FROM [dms].[CdcHeartbeat];
            """;

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var snapshot = new HeartbeatSnapshot(reader.GetInt16(0), reader.GetInt64(1));
        (await reader.ReadAsync()).Should().BeFalse();
        return snapshot;
    }

    private static async Task<IReadOnlyList<CaptureColumn>> ReadCaptureColumnsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                capture_info.capture_instance,
                source_schema.name AS source_schema,
                source_table.name AS source_name,
                capture_info.role_name,
                capture_info.supports_net_changes,
                COALESCE(capture_info.index_name, N'') AS index_name,
                COALESCE(capture_info.filegroup_name, N'') AS filegroup_name,
                capture_info.partition_switch,
                captured_column.column_name,
                captured_column.column_ordinal
            FROM cdc.change_tables capture_info
            INNER JOIN sys.tables source_table
                ON source_table.object_id = capture_info.source_object_id
            INNER JOIN sys.schemas source_schema
                ON source_schema.schema_id = source_table.schema_id
            INNER JOIN cdc.captured_columns captured_column
                ON captured_column.object_id = capture_info.object_id
            WHERE source_schema.name = N'dms'
            ORDER BY capture_info.capture_instance, captured_column.column_ordinal;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        List<CaptureColumn> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(
                new CaptureColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetBoolean(7),
                    reader.GetString(8),
                    reader.GetInt32(9)
                )
            );
        }

        return rows;
    }

    private async Task<IReadOnlyList<PermissionRow>> ReadConnectorDirectPermissionsAsync(
        SqlConnection connection
    ) => await ReadObjectPermissionsAsync(connection, _connectorPrincipalName);

    private static async Task<IReadOnlyList<PermissionRow>> ReadRoleObjectPermissionsAsync(
        SqlConnection connection,
        string roleName
    ) => await ReadObjectPermissionsAsync(connection, roleName);

    private static async Task<IReadOnlyList<PermissionRow>> ReadObjectPermissionsAsync(
        SqlConnection connection,
        string principalName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                schema_info.name,
                object_info.name,
                permission_info.permission_name,
                COALESCE(column_info.name, N'') AS column_name
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            LEFT JOIN sys.columns column_info
                ON column_info.object_id = permission_info.major_id
                AND column_info.column_id = permission_info.minor_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@principal_name)
            AND permission_info.state IN (N'G', N'W')
            AND permission_info.class = 1
            ORDER BY schema_info.name, object_info.name, permission_info.permission_name, column_info.name;
            """;
        command.Parameters.AddWithValue("principal_name", principalName);

        await using var reader = await command.ExecuteReaderAsync();
        List<PermissionRow> permissions = [];
        while (await reader.ReadAsync())
        {
            permissions.Add(
                new PermissionRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)
                )
            );
        }

        return permissions;
    }

    private static async Task<IReadOnlyList<string>> ReadExpectedCdcObjectSelectPermissionTokensAsync(
        SqlConnection connection
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH expected_capture_instances(capture_instance) AS (
                SELECT *
                FROM (VALUES
                    (N'{DocumentCacheCaptureInstanceName}'),
                    (N'{DocumentCaptureInstanceName}'),
                    (N'{HeartbeatCaptureInstanceName}')
                ) AS expected(capture_instance)
            ),
            expected_capture_cdc_objects AS (
                SELECT object_info.object_id
                FROM cdc.change_tables capture_info
                INNER JOIN expected_capture_instances expected
                    ON expected.capture_instance = capture_info.capture_instance
                INNER JOIN sys.schemas schema_info
                    ON schema_info.name = N'cdc'
                INNER JOIN sys.objects object_info
                    ON object_info.schema_id = schema_info.schema_id
                    AND object_info.name IN (
                        N'fn_cdc_get_all_changes_' + capture_info.capture_instance,
                        N'fn_cdc_get_net_changes_' + capture_info.capture_instance
                    )
                WHERE capture_info.role_name = N'{GatingRoleName}'
            )
            SELECT
                schema_info.name + N'.' + object_info.name + N'.SELECT'
            FROM expected_capture_cdc_objects expected_cdc_object
            INNER JOIN sys.objects object_info
                ON object_info.object_id = expected_cdc_object.object_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            ORDER BY schema_info.name, object_info.name;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        List<string> tokens = [];
        while (await reader.ReadAsync())
        {
            tokens.Add(reader.GetString(0));
        }

        return tokens;
    }

    private static string PermissionToken(PermissionRow permission) =>
        $"{permission.SchemaName}.{permission.ObjectName}.{permission.PermissionName}";

    private static async Task<IReadOnlyList<string>> ReadGatingRoleMembersAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT member_principal.name
            FROM sys.database_role_members role_member
            INNER JOIN sys.database_principals database_role
                ON database_role.principal_id = role_member.role_principal_id
            INNER JOIN sys.database_principals member_principal
                ON member_principal.principal_id = role_member.member_principal_id
            WHERE database_role.name = @role_name
            ORDER BY member_principal.name;
            """;
        command.Parameters.AddWithValue("role_name", GatingRoleName);

        await using var reader = await command.ExecuteReaderAsync();
        List<string> members = [];
        while (await reader.ReadAsync())
        {
            members.Add(reader.GetString(0));
        }

        return members;
    }

    private static async Task<IReadOnlyList<CdcJobSnapshot>> ReadCdcJobsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_cdc_help_jobs;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        List<CdcJobSnapshot> jobs = [];
        while (await reader.ReadAsync())
        {
            List<string> values = [];
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                var value = await reader.GetFieldValueAsync<object>(ordinal);
                values.Add($"{reader.GetName(ordinal)}={ValueOrEmpty(value)}");
            }

            jobs.Add(new CdcJobSnapshot(string.Join("|", values)));
        }

        return jobs.OrderBy(job => job.Values, StringComparer.Ordinal).ToArray();
    }

    private static string ValueOrEmpty(object value) => value is DBNull ? "" : value.ToString()!;

    private static async Task<bool> IsDatabaseCdcEnabledAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [is_cdc_enabled]
            FROM sys.databases
            WHERE [name] = DB_NAME();
            """;
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM information_schema.tables
            WHERE table_schema = 'dms'
            AND table_name = @table_name;
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string tableName,
        string columnName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM information_schema.columns
            WHERE table_schema = 'dms'
            AND table_name = @table_name
            AND column_name = @column_name;
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_name", columnName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<long> CaptureInstanceCountAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'cdc.change_tables', N'U') IS NULL
                SELECT CONVERT(bigint, 0);
            ELSE
                SELECT COUNT_BIG(*) FROM cdc.change_tables;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> CaptureInstanceExistsAsync(
        SqlConnection connection,
        string captureInstanceName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'cdc.change_tables', N'U') IS NULL
                SELECT CONVERT(bit, 0);
            ELSE
                SELECT CONVERT(bit, CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM cdc.change_tables
                        WHERE capture_instance = @capture_instance
                    ) THEN 1
                    ELSE 0
                END);
            """;
        command.Parameters.AddWithValue("capture_instance", captureInstanceName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private async Task<bool> HasConnectorDatabasePermissionAsync(
        SqlConnection connection,
        string permissionName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@connector_principal)
            AND permission_info.class = 0
            AND permission_info.permission_name = @permission_name
            AND permission_info.state IN (N'G', N'W');
            """;
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task<bool> HasConnectorObjectPermissionAsync(
        SqlConnection connection,
        string objectName,
        string permissionName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@connector_principal)
            AND schema_info.name = N'dms'
            AND object_info.name = @object_name
            AND permission_info.permission_name = @permission_name
            AND permission_info.minor_id = 0
            AND permission_info.state IN (N'G', N'W');
            """;
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        command.Parameters.AddWithValue("object_name", objectName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task<bool> HasConnectorColumnPermissionAsync(
        SqlConnection connection,
        string objectName,
        string columnName,
        string permissionName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            INNER JOIN sys.columns column_info
                ON column_info.object_id = permission_info.major_id
                AND column_info.column_id = permission_info.minor_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@connector_principal)
            AND schema_info.name = N'dms'
            AND object_info.name = @object_name
            AND column_info.name = @column_name
            AND permission_info.permission_name = @permission_name
            AND permission_info.state IN (N'G', N'W');
            """;
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        command.Parameters.AddWithValue("object_name", objectName);
        command.Parameters.AddWithValue("column_name", columnName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task<bool> HasConnectorColumnPermissionStateAsync(
        SqlConnection connection,
        string objectName,
        string columnName,
        string permissionName,
        string permissionState
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            INNER JOIN sys.columns column_info
                ON column_info.object_id = permission_info.major_id
                AND column_info.column_id = permission_info.minor_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@connector_principal)
            AND schema_info.name = N'dms'
            AND object_info.name = @object_name
            AND column_info.name = @column_name
            AND permission_info.permission_name = @permission_name
            AND permission_info.state = @permission_state;
            """;
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        command.Parameters.AddWithValue("object_name", objectName);
        command.Parameters.AddWithValue("column_name", columnName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        command.Parameters.AddWithValue("permission_state", permissionState);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> HasRoleObjectPermissionAsync(
        SqlConnection connection,
        string roleName,
        string objectName,
        string permissionName,
        string schemaName = "dms"
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@role_name)
            AND schema_info.name = @schema_name
            AND object_info.name = @object_name
            AND permission_info.permission_name = @permission_name
            AND permission_info.state IN (N'G', N'W');
            """;
        command.Parameters.AddWithValue("role_name", roleName);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("object_name", objectName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> HasRoleObjectPermissionStateAsync(
        SqlConnection connection,
        string roleName,
        string objectName,
        string permissionName,
        string permissionState,
        string schemaName = "dms"
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@role_name)
            AND schema_info.name = @schema_name
            AND object_info.name = @object_name
            AND permission_info.permission_name = @permission_name
            AND permission_info.state = @permission_state;
            """;
        command.Parameters.AddWithValue("role_name", roleName);
        command.Parameters.AddWithValue("schema_name", schemaName);
        command.Parameters.AddWithValue("object_name", objectName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        command.Parameters.AddWithValue("permission_state", permissionState);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task<bool> IsConnectorDatabaseRoleMemberAsync(SqlConnection connection, string roleName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ISNULL(IS_ROLEMEMBER(@role_name, @connector_principal), 0);
            """;
        command.Parameters.AddWithValue("role_name", roleName);
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> RoleExistsAsync(SqlConnection connection, string roleName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_principals
            WHERE [type] = N'R'
            AND [name] = @role_name;
            """;
        command.Parameters.AddWithValue("role_name", roleName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task AddConnectorToDatabaseRoleAsync(SqlConnection connection, string roleName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER ROLE {QuoteIdentifier(roleName)} ADD MEMBER {QuoteIdentifier(_connectorPrincipalName)};";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnableDatabaseCdcAndHeartbeatAsync(SqlConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            EXEC sys.sp_cdc_enable_db;
            """
        );
        await CreateExpectedHeartbeatTableAsync(connection);
    }

    private static async Task CreateExpectedHeartbeatTableAsync(SqlConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE [dms].[CdcHeartbeat]
            (
                [HeartbeatId] smallint NOT NULL,
                [HeartbeatSequence] bigint NOT NULL,
                [HeartbeatAt] datetime2(7) NOT NULL,
                CONSTRAINT [PK_CdcHeartbeat] PRIMARY KEY CLUSTERED ([HeartbeatId]),
                CONSTRAINT [CK_CdcHeartbeat_Singleton] CHECK ([HeartbeatId] = 1),
                CONSTRAINT [CK_CdcHeartbeat_Sequence] CHECK ([HeartbeatSequence] >= 0)
            );

            INSERT INTO [dms].[CdcHeartbeat] ([HeartbeatId], [HeartbeatSequence], [HeartbeatAt])
            VALUES (1, 0, sysutcdatetime());
            """
        );
    }

    private static async Task CreateMismatchedHeartbeatTableAsync(SqlConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE [dms].[CdcHeartbeat]
            (
                [HeartbeatId] smallint NOT NULL,
                [HeartbeatSequence] bigint NOT NULL,
                CONSTRAINT [PK_CdcHeartbeat] PRIMARY KEY CLUSTERED ([HeartbeatId])
            );

            INSERT INTO [dms].[CdcHeartbeat] ([HeartbeatId], [HeartbeatSequence])
            VALUES (1, 0);
            """
        );
    }

    private static async Task EnableWrongDocumentCaptureAsync(SqlConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            $"""
            EXEC sys.sp_cdc_enable_table
                @source_schema = N'dms',
                @source_name = N'Document',
                @capture_instance = N'{WrongDocumentCaptureInstanceName}',
                @supports_net_changes = 0,
                @role_name = N'{GatingRoleName}',
                @index_name = NULL,
                @captured_column_list = N'[DocumentId], [DocumentUuid]',
                @filegroup_name = NULL,
                @allow_partition_switch = 0;
            """
        );
    }

    private static async Task EnableWorkTableCaptureAsync(SqlConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            $"""
            EXEC sys.sp_cdc_enable_table
                @source_schema = N'dms',
                @source_name = N'DocumentProjectionWork',
                @capture_instance = N'{WorkTableCaptureInstanceName}',
                @supports_net_changes = 0,
                @role_name = N'{GatingRoleName}',
                @index_name = NULL,
                @captured_column_list = N'[DocumentId], [RequiredContentVersion], [FirstEnqueuedAt], [LastEnqueuedAt]',
                @filegroup_name = NULL,
                @allow_partition_switch = 0;
            """
        );
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void CreateConnectorLoginAndUser(string databaseName, string connectorPrincipalName)
    {
        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        var quotedDatabase = QuoteIdentifier(databaseName);
        var quotedPrincipal = QuoteIdentifier(connectorPrincipalName);
        command.CommandText = $"""
            IF SUSER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NULL
            BEGIN
                CREATE LOGIN {quotedPrincipal} WITH PASSWORD = '{ConnectorPassword}', CHECK_POLICY = OFF;
            END;

            USE {quotedDatabase};

            IF USER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NULL
            BEGIN
                CREATE USER {quotedPrincipal} FOR LOGIN {quotedPrincipal};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DropConnectorUserInDatabase(string databaseName, string connectorPrincipalName)
    {
        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            USE {QuoteIdentifier(databaseName)};

            IF USER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NOT NULL
            BEGIN
                DROP USER {QuoteIdentifier(connectorPrincipalName)};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DropConnectorLoginIfExists(string connectorPrincipalName)
    {
        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF SUSER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NOT NULL
            BEGIN
                DROP LOGIN {QuoteIdentifier(connectorPrincipalName)};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string DescribeDiagnostics(IReadOnlyList<CdcProviderDiagnostic> diagnostics) =>
        string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.ArtifactKind}:{diagnostic.SafeName.Value}:{diagnostic.ExpectedValue}->{diagnostic.ObservedValue}:{diagnostic.ProviderErrorClass}"
            )
        );

    private sealed record StableMetadataSnapshot(
        string SourceIdentity,
        string EffectiveSchemaHash,
        string ConnectorLoginSid,
        DocumentCacheStateSnapshot DocumentCacheState,
        HeartbeatSnapshot Heartbeat,
        IReadOnlyList<CaptureColumn> CaptureColumns,
        IReadOnlyList<PermissionRow> ConnectorDirectPermissions,
        IReadOnlyList<string> GatingRoleMembers,
        IReadOnlyList<CdcJobSnapshot> CdcJobs
    );

    private sealed record DocumentCacheStateSnapshot(
        string ProjectionLifecycleState,
        bool CacheAheadRecoveryRequired
    );

    private sealed record HeartbeatSnapshot(short HeartbeatId, long HeartbeatSequence);

    private sealed record CaptureColumn(
        string CaptureInstance,
        string SourceSchema,
        string SourceName,
        string RoleName,
        bool SupportsNetChanges,
        string IndexName,
        string FilegroupName,
        bool PartitionSwitch,
        string ColumnName,
        int ColumnOrdinal
    );

    private sealed record PermissionRow(
        string SchemaName,
        string ObjectName,
        string PermissionName,
        string ColumnName
    );

    private sealed record CdcJobSnapshot(string Values);
}

internal sealed class SqlServerConnectorPrincipalBoundaryProbeFactory(
    string connectionString,
    string connectorPrincipalName,
    string connectorPassword
) : ICdcConnectorPrincipalProbeFactory
{
    public async Task<CdcConnectorPrincipalProbeResult> ProbeAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    )
    {
        var connectionStringBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            UserID = connectorPrincipalName,
            Password = connectorPassword,
            IntegratedSecurity = false,
        };

        try
        {
            await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)
                await connection.BeginTransactionAsync(cancellationToken);

            List<string> failures = [];
            await ExpectSucceedsAsync(
                connection,
                transaction,
                "SELECT TOP (0) 1 FROM [dms].[Document];",
                "read-dms.Document",
                failures,
                cancellationToken
            );
            await ExpectSucceedsAsync(
                connection,
                transaction,
                "SELECT TOP (0) 1 FROM [dms].[DocumentCache];",
                "read-dms.DocumentCache",
                failures,
                cancellationToken
            );
            await ExpectSucceedsAsync(
                connection,
                transaction,
                "SELECT TOP (0) 1 FROM [dms].[CdcHeartbeat];",
                "read-dms.CdcHeartbeat",
                failures,
                cancellationToken
            );
            await ExpectSucceedsAsync(
                connection,
                transaction,
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1;",
                "update-heartbeat-progress-columns",
                failures,
                cancellationToken
            );

            await ExpectFailsAsync(
                connection,
                transaction,
                "SELECT TOP (0) 1 FROM [dms].[DocumentProjectionWork];",
                "deny-read-dms.DocumentProjectionWork",
                failures,
                cancellationToken
            );
            await ExpectFailsAsync(
                connection,
                transaction,
                "UPDATE [dms].[Document] SET [ContentVersion] = [ContentVersion] WHERE 1 = 0;",
                "deny-write-dms.Document",
                failures,
                cancellationToken
            );
            await ExpectFailsAsync(
                connection,
                transaction,
                "UPDATE [dms].[DocumentCache] SET [ContentVersion] = [ContentVersion] WHERE 1 = 0;",
                "deny-write-dms.DocumentCache",
                failures,
                cancellationToken
            );
            await ExpectFailsAsync(
                connection,
                transaction,
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatId] = [HeartbeatId] WHERE [HeartbeatId] = 1;",
                "deny-update-dms.CdcHeartbeat.HeartbeatId",
                failures,
                cancellationToken
            );

            await transaction.RollbackAsync(cancellationToken);

            return failures.Count == 0
                ? new CdcConnectorPrincipalProbeResult()
                : BoundaryFailure(
                    request,
                    observedValue: string.Join(",", failures),
                    providerErrorClass: null
                );
        }
        catch (DbException exception)
        {
            return BoundaryFailure(
                request,
                observedValue: "probe-error",
                providerErrorClass: exception.GetType().Name
            );
        }
        catch (InvalidOperationException exception)
        {
            return BoundaryFailure(
                request,
                observedValue: "probe-error",
                providerErrorClass: exception.GetType().Name
            );
        }
    }

    private static async Task ExpectSucceedsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        string label,
        List<string> failures,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await ExecuteProbeSqlAsync(connection, transaction, sql, cancellationToken);
        }
        catch (DbException)
        {
            failures.Add($"{label}:denied");
        }
    }

    private static async Task ExpectFailsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        string label,
        List<string> failures,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await ExecuteProbeSqlAsync(connection, transaction, sql, cancellationToken);
            failures.Add($"{label}:allowed");
        }
        catch (DbException)
        {
            // Expected denial.
        }
    }

    private static async Task ExecuteProbeSqlAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CdcConnectorPrincipalProbeResult BoundaryFailure(
        CdcProviderSetupRequest request,
        string observedValue,
        string? providerErrorClass
    ) =>
        new(
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
                    ObservedValue: observedValue,
                    ProviderErrorClass: providerErrorClass,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );
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
