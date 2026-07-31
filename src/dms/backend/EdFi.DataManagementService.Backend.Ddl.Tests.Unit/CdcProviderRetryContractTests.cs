// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcProviderRetryContract_Initial_Setup
{
    [Test]
    public async Task It_should_delegate_initial_creates_only_to_allowed_create_or_exact_match_steps()
    {
        var sourceInventoryStep = new RecordingStep();
        var retainedHistoryStep = new RecordingStep(CdcProviderArtifactState.Matched);
        var publicationStep = new RecordingStep(CdcProviderArtifactState.Created);
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    sourceInventoryStep.ToSetupStep(
                        CdcProviderArtifactKind.SourceTable,
                        canCreateInInitialSetup: false
                    ),
                    retainedHistoryStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        canCreateInInitialSetup: true
                    ),
                    publicationStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        sourceInventoryStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.ExactMatchOnly);
        retainedHistoryStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.CreateOrExactMatch);
        publicationStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.CreateOrExactMatch);
        result
            .ArtifactInventory.Select(observation => observation.State)
            .Should()
            .Equal(
                CdcProviderArtifactState.Matched,
                CdcProviderArtifactState.Matched,
                CdcProviderArtifactState.Created
            );
    }

    [Test]
    public async Task It_should_return_exact_match_metadata_on_same_mode_rerun_without_connector_or_ordinary_schema_state()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(CdcProvider.Postgresql, SuccessfulPostgresqlExactMatchSteps()),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result.Diagnostics.Should().BeEmpty();
        result.ObservedSourceFingerprint.Should().Be(result.BoundPhysicalSourceFingerprint);
        result
            .ArtifactInventory.Should()
            .OnlyContain(observation => observation.State == CdcProviderArtifactState.Matched);
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
            )
            .Which.SafeObservedValues.Should()
            .Contain("confirmed_flush_lsn", "0/16B6C50");
        typeof(CdcProviderSetupResult)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain([
                "ConnectorCredentials",
                "ConnectorJson",
                "EffectiveSchemaHash",
                "ResourceKeySeedHash",
                "RelationalMappingVersion",
            ]);
    }

    private static IReadOnlyList<CdcProviderSetupStep> SuccessfulPostgresqlExactMatchSteps() =>
        [
            RecordingStep.Create(
                CdcProviderArtifactKind.SourceFingerprint,
                CdcSourceFingerprintMetadata.SafeArtifactName,
                canCreateInInitialSetup: false,
                observedSourceFingerprint: new CdcSourceFingerprint("dms-source-fingerprint-v1", "source-123")
            ),
            RecordingStep.Create(
                CdcProviderArtifactKind.HeartbeatTable,
                new CdcSafeName("dms.CdcHeartbeat"),
                heartbeatActionQuery: new CdcHeartbeatActionQuery(
                    """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1""",
                    "heartbeat_hash"
                )
            ),
            RecordingStep.Create(
                CdcProviderArtifactKind.PostgresqlPublication,
                new CdcSafeName("dms_binding_publication"),
                expectedMessageKeyColumns:
                [
                    new CdcExpectedMessageKeyColumns(
                        CdcSourceTableKind.Document,
                        [new DbColumnName("DocumentUuid")]
                    ),
                    new CdcExpectedMessageKeyColumns(
                        CdcSourceTableKind.DocumentCache,
                        [new DbColumnName("DocumentUuid")]
                    ),
                ]
            ),
            RecordingStep.Create(
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                new CdcSafeName("dms_binding_slot"),
                providerHistoryObservations:
                [
                    new CdcProviderHistoryObservation(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        new CdcSafeName("dms_binding_slot"),
                        new Dictionary<string, string>
                        {
                            ["plugin"] = "pgoutput",
                            ["restart_lsn"] = "0/16B6C50",
                            ["confirmed_flush_lsn"] = "0/16B6C50",
                        },
                        CdcProviderRetryContinuityClassification.None
                    ),
                ]
            ),
        ];
}

[TestFixture]
public class Given_CdcProviderRetryContract_Validate_Only
{
    [Test]
    public async Task It_should_not_recreate_missing_retained_history_artifacts()
    {
        var slotStep = new RecordingStep(
            CdcProviderArtifactState.Missing,
            new CdcSafeName("dms_binding_slot")
        );
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    slotStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(mode: CdcProviderSetupMode.ValidateOnly)
        );

        slotStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.ExactMatchOnly);
        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .NotContain(observation => observation.State == CdcProviderArtifactState.Created);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
            )
            .Which.Classification.Should()
            .Be(CdcProviderRetryContinuityClassification.FailClosed);
    }
}

[TestFixture]
public class Given_CdcProviderRetryContract_Partial_Retry
{
    [Test]
    public async Task It_should_exact_match_completed_steps_and_create_only_still_missing_initial_artifacts()
    {
        var heartbeatStep = new RecordingStep(
            CdcProviderArtifactState.Matched,
            new CdcSafeName("dms.CdcHeartbeat")
        );
        var slotStep = new RecordingStep(
            CdcProviderArtifactState.Matched,
            new CdcSafeName("dms_binding_slot")
        );
        var publicationStep = new RecordingStep(
            CdcProviderArtifactState.Created,
            new CdcSafeName("dms_binding_publication")
        );
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    heartbeatStep.ToSetupStep(CdcProviderArtifactKind.HeartbeatTable),
                    slotStep.ToSetupStep(CdcProviderArtifactKind.PostgresqlReplicationSlot),
                    publicationStep.ToSetupStep(CdcProviderArtifactKind.PostgresqlPublication),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result
            .ArtifactInventory.Select(observation => (observation.ArtifactKind, observation.State))
            .Should()
            .Equal(
                (CdcProviderArtifactKind.HeartbeatTable, CdcProviderArtifactState.Matched),
                (CdcProviderArtifactKind.PostgresqlReplicationSlot, CdcProviderArtifactState.Matched),
                (CdcProviderArtifactKind.PostgresqlPublication, CdcProviderArtifactState.Created)
            );
    }

    [Test]
    public async Task It_should_stop_after_fail_closed_mismatch_without_running_later_repair_steps()
    {
        var repairStep = new RecordingStep(CdcProviderArtifactState.Created);
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        new CdcSafeName("dms_binding_publication"),
                        CdcProviderArtifactState.Mismatched
                    ),
                    repairStep.ToSetupStep(CdcProviderArtifactKind.PostgresqlReplicationSlot),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        repairStep.ExecutionCount.Should().Be(0);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH")
            .Which.Classification.Should()
            .Be(CdcProviderRetryContinuityClassification.FailClosed);
    }
}

[TestFixture]
public class Given_CdcProviderRetryContract_Fail_Closed_Mismatches
{
    [Test]
    public async Task It_should_fail_closed_on_source_inventory_mismatch_before_later_creates()
    {
        var laterCreateStep = new RecordingStep(CdcProviderArtifactState.Created);
        var observedWithoutHeartbeat = CdcProviderSetupContractTestData
            .BuildRequiredSourceInventory()
            .Where(table => table.TableKind != CdcSourceTableKind.CdcHeartbeat)
            .ToArray();
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceTable,
                        sourceTableInventory: observedWithoutHeartbeat,
                        canCreateInInitialSetup: false
                    ),
                    laterCreateStep.ToSetupStep(CdcProviderArtifactKind.PostgresqlPublication),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        laterCreateStep.ExecutionCount.Should().Be(0);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_SOURCE_TABLE_MISSING")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.MissingRequiredSourceObject);
    }

    [Test]
    public async Task It_should_fail_closed_on_source_fingerprint_mismatch_before_later_creates()
    {
        var laterCreateStep = new RecordingStep(CdcProviderArtifactState.Created);
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: new CdcSourceFingerprint(
                            "dms-source-fingerprint-v1",
                            "other-source"
                        )
                    ),
                    laterCreateStep.ToSetupStep(CdcProviderArtifactKind.PostgresqlPublication),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        laterCreateStep.ExecutionCount.Should().Be(0);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_SOURCE_FINGERPRINT_MISMATCH")
            .Which.Classification.Should()
            .Be(CdcProviderRetryContinuityClassification.FailClosed);
    }

    [Test]
    public async Task It_should_fail_closed_on_heartbeat_mismatch()
    {
        var result = await SetupPostgresqlAsync(
            RecordingStep.Create(
                CdcProviderArtifactKind.HeartbeatTable,
                new CdcSafeName("dms.CdcHeartbeat"),
                CdcProviderArtifactState.Mismatched
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
            )
            .Which.Classification.Should()
            .Be(CdcProviderRetryContinuityClassification.FailClosed);
    }

    [Test]
    public async Task It_should_fail_closed_on_connector_source_grant_mismatch()
    {
        var result = await SetupPostgresqlAsync(
            RecordingStep.Create(
                CdcProviderArtifactKind.Grant,
                grantInventory:
                [
                    ConnectorGrant("dms.Document", ["SELECT", "UPDATE"], CdcProviderArtifactKind.Grant),
                ]
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_SOURCE_TABLE_GRANT_MISMATCH")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure);
    }

    [Test]
    public async Task It_should_fail_closed_on_work_table_grant_mismatch()
    {
        var result = await SetupPostgresqlAsync(
            RecordingStep.Create(
                CdcProviderArtifactKind.Grant,
                grantInventory:
                [
                    ConnectorGrant("dms.DocumentProjectionWork", ["SELECT"], CdcProviderArtifactKind.Grant),
                ]
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_WORK_TABLE_GRANT_FORBIDDEN")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.WorkTableGrantViolation);
    }

    [Test]
    public async Task It_should_fail_closed_when_provider_history_is_unavailable()
    {
        var result = await SetupPostgresqlAsync(
            RecordingStep.Create(
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                new CdcSafeName("dms_binding_slot"),
                CdcProviderArtifactState.Unavailable
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_UNAVAILABLE"
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
            )
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.ProviderHistoryUnavailable);
    }

    [Test]
    public async Task It_should_preserve_provider_history_loss_evidence_diagnostics()
    {
        var result = await SetupPostgresqlAsync(
            RecordingStep.Create(
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                new CdcSafeName("dms_binding_slot"),
                diagnostics:
                [
                    Diagnostic(
                        "CDC_PROVIDER_HISTORY_LOST",
                        CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence,
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        new CdcSafeName("dms_binding_slot"),
                        CdcProviderRetryContinuityClassification.SourceHistoryLost
                    ),
                ]
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_HISTORY_LOST")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
    }

    [Test]
    public async Task It_should_preserve_setup_principal_failure_diagnostics()
    {
        var result = await SetupPostgresqlAsync(
            RecordingStep.Create(
                CdcProviderArtifactKind.PostgresqlPublication,
                new CdcSafeName("dms_binding_publication"),
                diagnostics:
                [
                    Diagnostic(
                        "CDC_SETUP_PRINCIPAL_CANNOT_CREATE_PUBLICATION",
                        CdcProviderDiagnosticCategory.SetupPrincipalFailure,
                        CdcProviderArtifactKind.PostgresqlPublication,
                        new CdcSafeName("dms_binding_publication"),
                        CdcProviderRetryContinuityClassification.FailClosed,
                        CdcPrincipalKind.SetupPrincipal
                    ),
                ]
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_SETUP_PRINCIPAL_CANNOT_CREATE_PUBLICATION")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.SetupPrincipalFailure);
    }

    private static async Task<CdcProviderSetupResult> SetupPostgresqlAsync(
        params CdcProviderSetupStep[] steps
    )
    {
        var service = new CdcProviderSetupService([new TestProvider(CdcProvider.Postgresql, steps)]);

        return await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());
    }

    private static CdcGrantObservation ConnectorGrant(
        string safeObjectName,
        IReadOnlyList<string> privileges,
        CdcProviderArtifactKind artifactKind,
        IReadOnlyList<DbColumnName>? columns = null
    ) =>
        new(
            CdcPrincipalKind.ConnectorPrincipal,
            new CdcSafeName("connector_principal"),
            artifactKind,
            new CdcSafeName(safeObjectName),
            privileges,
            columns ?? []
        );

    private static CdcProviderDiagnostic Diagnostic(
        string code,
        CdcProviderDiagnosticCategory category,
        CdcProviderArtifactKind artifactKind,
        CdcSafeName safeName,
        CdcProviderRetryContinuityClassification classification,
        CdcPrincipalKind principalKind = CdcPrincipalKind.None
    ) =>
        new(
            Code: code,
            Category: category,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: principalKind,
            ArtifactKind: artifactKind,
            SafeName: safeName,
            ExpectedValue: "safe_expected",
            ObservedValue: "safe_observed",
            ProviderErrorClass: null,
            Classification: classification
        );
}
