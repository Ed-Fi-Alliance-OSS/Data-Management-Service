// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcWorkTableExclusion_Postgresql
{
    [Test]
    public async Task It_should_exclude_projection_work_from_publication_grants_metadata_and_manifest()
    {
        var result = await RunAsync(new RecordingPostgresqlCdcExecutor());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result.SourceTableInventory.Select(SourceTableName).Should().BeEquivalentTo(ExpectedSourceNames());
        result.SourceTableInventory.Select(SourceTableName).Should().NotContain("dms.DocumentProjectionWork");
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && observation.SafeObservedValues["tables"]
                    == "dms.CdcHeartbeat,dms.Document,dms.DocumentCache"
            );
        result
            .ArtifactInventory.SelectMany(observation => observation.SafeObservedValues.Values)
            .Should()
            .NotContain(value => value.Contains("DocumentProjectionWork", StringComparison.Ordinal));
        result
            .GrantInventory.Should()
            .NotContain(grant => grant.SafeObjectName.Value == "dms.DocumentProjectionWork");
        result.ManifestPayload!.Json.Should().NotContain("DocumentProjectionWork");
    }

    [Test]
    public async Task It_should_fail_closed_when_publication_captures_projection_work()
    {
        var result = await RunAsync(
            new RecordingPostgresqlCdcExecutor(
                heartbeatTableExists: true,
                heartbeatSingletonExists: true,
                documentReplicaIdentityFull: true,
                publicationExists: true,
                publicationCapturesWorkTable: true
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_POSTGRESQL_WORK_TABLE_PUBLICATION_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.FailClosed
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_connector_principal_has_projection_work_grants()
    {
        var result = await RunAsync(
            new RecordingPostgresqlCdcExecutor(
                heartbeatTableExists: true,
                heartbeatSingletonExists: true,
                documentReplicaIdentityFull: true,
                publicationExists: true,
                slotExists: true,
                connectorDatabaseConnect: true,
                connectorSchemaUsage: true,
                connectorDocumentSelect: true,
                connectorDocumentCacheSelect: true,
                connectorHeartbeatSelect: true,
                connectorHeartbeatSequenceUpdate: true,
                connectorHeartbeatAtUpdate: true,
                connectorWorkTablePrivileges: "SELECT,UPDATE"
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

    private static async Task<CdcProviderSetupResult> RunAsync(RecordingPostgresqlCdcExecutor executor)
    {
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);
        return await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );
    }

    private static string SourceTableName(CdcSourceTableInventory table) =>
        $"{table.TableName.Schema.Value}.{table.TableName.Name}";

    private static IReadOnlyList<string> ExpectedSourceNames() =>
        ["dms.Document", "dms.DocumentCache", "dms.CdcHeartbeat"];
}

[TestFixture]
public class Given_CdcWorkTableExclusion_SqlServer
{
    [Test]
    public async Task It_should_exclude_projection_work_from_capture_grants_metadata_and_manifest()
    {
        var result = await RunAsync(new RecordingSqlServerCdcExecutor());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result.SourceTableInventory.Select(SourceTableName).Should().BeEquivalentTo(ExpectedSourceNames());
        result.SourceTableInventory.Select(SourceTableName).Should().NotContain("dms.DocumentProjectionWork");
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation => observation.State == CdcProviderArtifactState.Created)
            .And.NotContain(observation =>
                observation.SafeObservedValues.Values.Any(value =>
                    value.Contains("DocumentProjectionWork", StringComparison.Ordinal)
                )
            );
        result
            .GrantInventory.Should()
            .NotContain(grant => grant.SafeObjectName.Value == "dms.DocumentProjectionWork");
        result
            .ProviderHistoryObservations.SelectMany(observation => observation.SafeObservedValues.Values)
            .Should()
            .NotContain(value => value.Contains("DocumentProjectionWork", StringComparison.Ordinal));
        result.ManifestPayload!.Json.Should().NotContain("DocumentProjectionWork");
    }

    [Test]
    public async Task It_should_fail_closed_when_capture_instance_targets_projection_work()
    {
        var result = await RunAsync(
            RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
                captureJobPresent: true,
                cleanupJobPresent: true,
                captureInstances: SqlServerCaptureInstanceTestData
                    .Expected()
                    .Append(RecordingSqlServerCaptureInstance.WorkTable())
                    .ToArray()
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_WORK_TABLE_CAPTURE_FORBIDDEN"
                && diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableCaptureViolation
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.FailClosed
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_connector_principal_has_projection_work_grants()
    {
        var result = await RunAsync(
            RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
                captureJobPresent: true,
                cleanupJobPresent: true,
                captureInstances: SqlServerCaptureInstanceTestData.Expected(),
                connectorAccess: new RecordingSqlServerConnectorAccess
                {
                    GatingRoleExists = true,
                    GatingRoleDirectMembers = ["connector_principal"],
                    DatabaseConnect = true,
                    DocumentSelect = true,
                    DocumentCacheSelect = true,
                    HeartbeatSelect = true,
                    HeartbeatSequenceUpdate = true,
                    HeartbeatAtUpdate = true,
                    WorkTablePrivileges = ["SELECT", "UPDATE"],
                }
            )
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

    private static async Task<CdcProviderSetupResult> RunAsync(RecordingSqlServerCdcExecutor executor)
    {
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        return await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: executor)
        );
    }

    private static string SourceTableName(CdcSourceTableInventory table) =>
        $"{table.TableName.Schema.Value}.{table.TableName.Name}";

    private static IReadOnlyList<string> ExpectedSourceNames() =>
        ["dms.Document", "dms.DocumentCache", "dms.CdcHeartbeat"];
}
