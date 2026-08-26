// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcProviderSetupResultMapper")]
public class Given_CdcProviderSetupResultMapper
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void It_normalizes_postgresql_safe_wal_values_to_provider_positions()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.Postgresql,
            binding,
            [
                Artifact(CdcProviderArtifactKind.PostgresqlPublication, inventory.PostgresqlPublicationName!),
                Artifact(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
            [
                History(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!,
                    new Dictionary<string, string>
                    {
                        ["restart_lsn"] = "0_16B6C50",
                        ["confirmed_flush_lsn"] = "0_16B6C60",
                        ["wal_status"] = "reserved",
                        ["invalidation_reason"] = "",
                    }
                ),
            ]
        );

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory
            .ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.ExactMatch);
        providerHistory
            .RetainedRangeState.Should()
            .Be(CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset);
        providerHistory.RetainedRangeStart.Should().Be("0/16B6C50");
        providerHistory.RetainedRangeEnd.Should().Be("0/16B6C60");
    }

    [TestCase(CdcProviderArtifactKind.PostgresqlReplicationSlot)]
    [TestCase(CdcProviderArtifactKind.PostgresqlPublication)]
    public void It_maps_absent_postgresql_required_artifact_observation_to_unknown_history(
        CdcProviderArtifactKind omittedArtifactKind
    )
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        string omittedArtifactName =
            omittedArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                ? inventory.PostgresqlLogicalSlotName!
                : inventory.PostgresqlPublicationName!;
        CdcProviderArtifactObservation[] artifacts =
        [
            Artifact(CdcProviderArtifactKind.PostgresqlPublication, inventory.PostgresqlPublicationName!),
            Artifact(CdcProviderArtifactKind.PostgresqlReplicationSlot, inventory.PostgresqlLogicalSlotName!),
        ];
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.Postgresql,
            binding,
            artifacts.Where(artifact => artifact.ArtifactKind != omittedArtifactKind).ToArray(),
            [
                History(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!,
                    new Dictionary<string, string>
                    {
                        ["restart_lsn"] = "0_16B6C50",
                        ["confirmed_flush_lsn"] = "0_16B6C60",
                        ["wal_status"] = "reserved",
                        ["invalidation_reason"] = "",
                    }
                ),
            ]
        );

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory.ProviderArtifactState.Should().Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        providerHistory
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.ProviderHistoryUnknown
                && diagnostic.ArtifactKind == omittedArtifactKind.ToString()
                && diagnostic.ArtifactName == omittedArtifactName
                && diagnostic.Observed == "absent"
            );
    }

    [TestCase(CdcProviderArtifactState.Missing, CoreCdc.CdcProviderArtifactContinuityState.Missing)]
    [TestCase(CdcProviderArtifactState.Mismatched, CoreCdc.CdcProviderArtifactContinuityState.Recreated)]
    public void It_preserves_terminal_postgresql_artifact_evidence_when_history_is_unavailable(
        CdcProviderArtifactState artifactState,
        CoreCdc.CdcProviderArtifactContinuityState expectedState
    )
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.Postgresql,
            binding,
            [
                Artifact(CdcProviderArtifactKind.PostgresqlPublication, inventory.PostgresqlPublicationName!),
                Artifact(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!,
                    artifactState
                ),
            ],
            []
        ) with
        {
            Diagnostics =
            [
                ProviderHistoryUnavailableDiagnostic(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
        };

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory.ProviderArtifactState.Should().Be(expectedState);
        providerHistory.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Unknown);
    }

    [TestCase(CdcProviderArtifactState.Missing, CoreCdc.CdcProviderArtifactContinuityState.Missing)]
    [TestCase(CdcProviderArtifactState.Mismatched, CoreCdc.CdcProviderArtifactContinuityState.Recreated)]
    public void It_preserves_terminal_postgresql_publication_evidence_when_history_is_unavailable(
        CdcProviderArtifactState artifactState,
        CoreCdc.CdcProviderArtifactContinuityState expectedState
    )
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.Postgresql,
            binding,
            [
                Artifact(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    inventory.PostgresqlPublicationName!,
                    artifactState
                ),
                Artifact(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
            []
        ) with
        {
            Diagnostics =
            [
                ProviderHistoryUnavailableDiagnostic(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
        };

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory.ProviderArtifactState.Should().Be(expectedState);
        providerHistory.ProviderArtifactName.Should().Be(inventory.PostgresqlPublicationName);
        providerHistory.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Unknown);
    }

    [Test]
    public void It_preserves_postgresql_wal_loss_when_other_history_evidence_is_unavailable()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.Postgresql,
            binding,
            [
                Artifact(CdcProviderArtifactKind.PostgresqlPublication, inventory.PostgresqlPublicationName!),
                Artifact(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
            [
                History(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!,
                    new Dictionary<string, string>
                    {
                        ["restart_lsn"] = "0_16B6C50",
                        ["confirmed_flush_lsn"] = "0_16B6C60",
                        ["wal_status"] = "lost",
                        ["invalidation_reason"] = "",
                    }
                ),
            ]
        ) with
        {
            Diagnostics =
            [
                ProviderHistoryUnavailableDiagnostic(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    inventory.PostgresqlPublicationName!
                ),
            ],
        };

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory
            .ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.ExactMatch);
        providerHistory.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Gap);
    }

    [TestCase(CdcProviderArtifactState.Missing, CoreCdc.CdcProviderArtifactContinuityState.Missing)]
    [TestCase(CdcProviderArtifactState.Mismatched, CoreCdc.CdcProviderArtifactContinuityState.Recreated)]
    public void It_preserves_terminal_sql_server_capture_evidence_when_database_history_is_unavailable(
        CdcProviderArtifactState artifactState,
        CoreCdc.CdcProviderArtifactContinuityState expectedState
    )
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.SqlServer,
            binding,
            [
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceDocumentCacheName!,
                    artifactState
                ),
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceDocumentName!
                ),
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceCdcHeartbeatName!
                ),
            ],
            [
                History(
                    CdcProviderArtifactKind.ProviderHistory,
                    "sqlserver_database_cdc",
                    new Dictionary<string, string> { ["history"] = "unavailable" }
                ),
            ]
        );

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory.ProviderArtifactState.Should().Be(expectedState);
        providerHistory.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Unknown);
        providerHistory.SqlServerJobs.Should().Be(CoreCdc.CdcSqlServerCdcJobEvidence.Unknown);
    }

    [Test]
    public void It_maps_absent_sql_server_required_capture_observation_to_unknown_history()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        string omittedCaptureName = inventory.SqlServerCaptureInstanceDocumentName!;
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.SqlServer,
            binding,
            SqlServerCaptureArtifacts(inventory)
                .Where(artifact => artifact.SafeArtifactName.Value != omittedCaptureName)
                .ToArray(),
            [SqlServerDatabaseHistory(), .. SqlServerCaptureHistories(inventory)]
        );

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory.ProviderArtifactState.Should().Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        providerHistory.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Unknown);
        providerHistory
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.ProviderHistoryUnknown
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance.ToString()
                && diagnostic.ArtifactName == omittedCaptureName
                && diagnostic.Observed == "absent"
            );
    }

    [Test]
    public void It_maps_absent_sql_server_required_retained_range_history_to_unknown_range()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        string omittedCaptureName = inventory.SqlServerCaptureInstanceDocumentCacheName!;
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.SqlServer,
            binding,
            SqlServerCaptureArtifacts(inventory),
            [
                SqlServerDatabaseHistory(),
                .. SqlServerCaptureHistories(inventory)
                    .Where(history => history.SafeArtifactName.Value != omittedCaptureName),
            ]
        );

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory
            .ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.ExactMatch);
        providerHistory.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Unknown);
        providerHistory
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.ProviderHistoryUnknown
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance.ToString()
                && diagnostic.ArtifactName == omittedCaptureName
                && diagnostic.Observed == "absent"
            );
    }

    [Test]
    public void It_maps_provider_diagnostics_with_the_explicit_observation_timestamp()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.Postgresql, binding, [], []) with
        {
            ArtifactInventory =
            [
                Artifact(CdcProviderArtifactKind.PostgresqlPublication, inventory.PostgresqlPublicationName!),
                Artifact(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
            ProviderHistoryObservations =
            [
                History(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!,
                    new Dictionary<string, string>
                    {
                        ["restart_lsn"] = "0_16B6C50",
                        ["confirmed_flush_lsn"] = "0_16B6C60",
                        ["wal_status"] = "reserved",
                        ["invalidation_reason"] = "",
                    }
                ),
            ],
            Diagnostics =
            [
                new(
                    "providerHistoryUnavailable",
                    CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                    CdcProviderDiagnosticSeverity.Warning,
                    CdcPrincipalKind.ConnectorPrincipal,
                    CdcProviderArtifactKind.ProviderHistory,
                    new CdcSafeName("postgresql_history"),
                    null,
                    "unavailable",
                    null,
                    CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                ),
            ],
        };

        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory =
            CdcProviderSetupResultMapper.ToProviderSourceHistoryEvidence(ObservedAt, binding, setupResult);

        providerHistory.Diagnostics.Should().ContainSingle().Which.ObservedAt.Should().Be(ObservedAt);
    }

    [Test]
    public void It_rejects_non_validate_only_provider_setup_results()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.Postgresql, binding, [], []) with
        {
            Mode = CdcProviderSetupMode.InitialCreateOrExactMatch,
        };

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.SetupMode.Should().Be(CoreCdc.CdcProviderSetupMode.InitialCreateOrExactMatch);
        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Invalid);
        mapping.ProviderSetup.ArtifactInventoryState.Should().Be(CoreCdc.CdcProviderSetupState.Mismatched);
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        mapping
            .ProviderSetup.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.InvalidObservation
                && diagnostic.Path == "$.providerSetup.mode"
            );
    }

    [Test]
    public void It_maps_failed_validate_only_provider_setup_results_to_unknown_evidence()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.Postgresql, binding, [], []) with
        {
            Outcome = CdcProviderSetupOutcome.Failed,
            ObservedSourceFingerprint = null,
        };

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Unknown);
        mapping.ProviderSetup.ArtifactInventoryState.Should().Be(CoreCdc.CdcProviderSetupState.Unknown);
        mapping.ProviderSetup.PhysicalSourceFingerprint.Should().BeNull();
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        mapping
            .ProviderSetup.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable
                && diagnostic.Path == "$.providerSetup.outcome"
            );
    }

    [Test]
    public void It_maps_empty_required_provider_setup_evidence_to_unknown_states()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.Postgresql,
            binding,
            [
                Artifact(CdcProviderArtifactKind.PostgresqlPublication, inventory.PostgresqlPublicationName!),
                Artifact(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!
                ),
            ],
            [
                History(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    inventory.PostgresqlLogicalSlotName!,
                    new Dictionary<string, string>
                    {
                        ["restart_lsn"] = "0_16B6C50",
                        ["confirmed_flush_lsn"] = "0_16B6C60",
                        ["wal_status"] = "reserved",
                        ["invalidation_reason"] = "",
                    }
                ),
            ],
            includeRequiredSetupEvidence: false
        );

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Unknown);
        mapping.ProviderSetup.GrantInventoryState.Should().Be(CoreCdc.CdcProviderSetupState.Unknown);
        mapping.ProviderSetup.SourceInventoryState.Should().Be(CoreCdc.CdcProviderSetupState.Unknown);
        mapping.ProviderSetup.HeartbeatState.Should().Be(CoreCdc.CdcProviderSetupState.Unknown);
        mapping
            .ProviderSetup.Diagnostics.Select(diagnostic => diagnostic.Path)
            .Should()
            .Contain(
                new[]
                {
                    "$.providerSetup.grantInventory",
                    "$.providerSetup.sourceTableInventory",
                    "$.providerSetup.artifactInventory.heartbeatTable",
                    "$.providerSetup.heartbeatActionQuery",
                }
            );
        Validate(mapping.ProviderSetup, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_preserves_wrong_provider_evidence_as_a_provider_mismatch()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.SqlServer, binding, [], []);

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.Provider.Should().Be(CoreCdc.CdcProvider.SqlServer);
        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Invalid);
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        Validate(mapping.ProviderSetup, binding)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CoreCdc.CdcDiagnosticCategory.ProviderMismatch);
    }

    [Test]
    public void It_rejects_wrong_bound_physical_source_fingerprint()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        string otherFingerprint = OtherSourceFingerprint(CdcProvider.Postgresql);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.Postgresql, binding, [], []) with
        {
            BoundPhysicalSourceFingerprint = new(CdcSourceFingerprintMetadata.Version, otherFingerprint),
        };

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Invalid);
        mapping.ProviderSetup.PhysicalSourceFingerprint.Should().Be(binding.PhysicalSourceFingerprint);
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        mapping
            .ProviderSetup.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.SourceMismatch
                && diagnostic.Path == "$.providerSetup.boundPhysicalSourceFingerprint"
                && diagnostic.Expected == binding.PhysicalSourceFingerprint
                && diagnostic.Observed == otherFingerprint
            );
    }

    [Test]
    public void It_maps_missing_observed_physical_source_fingerprint_to_unknown_evidence()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.Postgresql, binding, [], []) with
        {
            ObservedSourceFingerprint = null,
        };

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Unknown);
        mapping.ProviderSetup.PhysicalSourceFingerprint.Should().BeNull();
        mapping.ProviderSetup.ArtifactInventoryState.Should().Be(CoreCdc.CdcProviderSetupState.Unknown);
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
    }

    [Test]
    public void It_preserves_wrong_observed_physical_source_fingerprint_as_a_source_mismatch()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.Postgresql);
        string otherFingerprint = OtherSourceFingerprint(CdcProvider.Postgresql);
        CdcProviderSetupResult setupResult = BuildSetupResult(CdcProvider.Postgresql, binding, [], []) with
        {
            ObservedSourceFingerprint = new(CdcSourceFingerprintMetadata.Version, otherFingerprint),
        };

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);

        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Invalid);
        mapping.ProviderSetup.PhysicalSourceFingerprint.Should().Be(otherFingerprint);
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        Validate(mapping.ProviderSetup, binding)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CoreCdc.CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_maps_enabled_sql_server_capture_job_not_running_to_stopped_unknown_continuity()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupObservationMapping mapping = MapSqlServerProviderSetup(
            binding,
            SqlServerDatabaseHistory(captureJobRunning: false, cleanupJobRunning: true)
        );

        CoreCdc.CdcSourceHistoryClassificationResult result = ClassifySqlServer(binding, inventory, mapping);

        mapping
            .ProviderHistory.SqlServerJobs!.CaptureJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Stopped);
        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Unknown);
        result
            .Observation.RetainedRangeState.Should()
            .Be(CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset);
        result.IncidentCandidate.Should().BeNull();
    }

    [Test]
    public void It_keeps_sql_server_cleanup_job_not_running_healthy_when_last_run_did_not_fail()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupObservationMapping mapping = MapSqlServerProviderSetup(
            binding,
            SqlServerDatabaseHistory(captureJobRunning: true, cleanupJobRunning: false)
        );

        CoreCdc.CdcSourceHistoryClassificationResult result = ClassifySqlServer(binding, inventory, mapping);

        mapping
            .ProviderHistory.SqlServerJobs!.CleanupJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Healthy);
        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Healthy);
        result.Observation.SqlServerJobs.Should().Be(CoreCdc.CdcSqlServerCdcJobEvidence.Healthy);
        result.IncidentCandidate.Should().BeNull();
    }

    [Test]
    public void It_maps_sql_server_unavailable_history_to_unknown_job_evidence()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CdcProviderSetupObservationMapping mapping = MapSqlServerProviderSetup(
            binding,
            History(
                CdcProviderArtifactKind.ProviderHistory,
                "sqlserver_database_cdc",
                new Dictionary<string, string> { ["history"] = "unavailable" }
            )
        );

        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        mapping.ProviderHistory.SqlServerJobs.Should().Be(CoreCdc.CdcSqlServerCdcJobEvidence.Unknown);
    }

    [Test]
    public void It_preserves_terminal_sql_server_missing_job_evidence_when_database_history_is_unavailable()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CdcProviderSetupObservationMapping mapping = MapSqlServerProviderSetup(
            binding,
            History(
                CdcProviderArtifactKind.ProviderHistory,
                "sqlserver_database_cdc",
                new Dictionary<string, string>
                {
                    ["history"] = "unavailable",
                    ["capture_job_present"] = "False",
                }
            )
        );

        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Missing);
        mapping
            .ProviderHistory.SqlServerJobs!.CaptureJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Missing);
        mapping
            .ProviderHistory.SqlServerJobs.CleanupJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Unknown);
    }

    [Test]
    public void It_preserves_terminal_sql_server_job_evidence_from_failed_validate_only_results()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CoreCdc.CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.SqlServer,
            binding,
            [
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceDocumentCacheName!
                ),
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceDocumentName!
                ),
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceCdcHeartbeatName!
                ),
            ],
            [
                History(
                    CdcProviderArtifactKind.ProviderHistory,
                    "sqlserver_database_cdc",
                    new Dictionary<string, string>
                    {
                        ["database_cdc_enabled"] = "True",
                        ["capture_job_present"] = "False",
                        ["cleanup_job_present"] = "True",
                        ["cleanup_job_enabled"] = "True",
                        ["cleanup_job_running"] = "True",
                        ["cleanup_job_last_run_status"] = "1",
                        ["retained_max_lsn"] = "0x00000000000000000010",
                    }
                ),
            ]
        ) with
        {
            Outcome = CdcProviderSetupOutcome.Failed,
        };

        CdcProviderSetupObservationMapping mapping = MapProviderSetup(binding, setupResult);
        CoreCdc.CdcSourceHistoryClassificationResult result = ClassifySqlServer(binding, inventory, mapping);

        mapping.ProviderSetup.SetupOutcome.Should().Be(CoreCdc.CdcProviderSetupOutcome.Unknown);
        mapping
            .ProviderHistory.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Missing);
        mapping
            .ProviderHistory.SqlServerJobs!.CaptureJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Missing);
        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Lost);
        result.IncidentCandidate.Should().NotBeNull();
    }

    private static CdcProviderSetupObservationMapping MapSqlServerProviderSetup(
        CoreCdc.CdcBinding binding,
        CdcProviderHistoryObservation databaseHistory
    )
    {
        CoreCdc.CdcArtifactInventory inventory = RecoverInventory(binding);
        CdcProviderSetupResult setupResult = BuildSetupResult(
            CdcProvider.SqlServer,
            binding,
            [
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceDocumentCacheName!
                ),
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceDocumentName!
                ),
                Artifact(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    inventory.SqlServerCaptureInstanceCdcHeartbeatName!
                ),
            ],
            [
                databaseHistory,
                SqlServerCaptureHistory(
                    inventory.SqlServerCaptureInstanceDocumentCacheName!,
                    "0x00000000000000000001"
                ),
                SqlServerCaptureHistory(
                    inventory.SqlServerCaptureInstanceDocumentName!,
                    "0x00000000000000000001"
                ),
                SqlServerCaptureHistory(
                    inventory.SqlServerCaptureInstanceCdcHeartbeatName!,
                    "0x00000000000000000001"
                ),
            ]
        );

        return CdcProviderSetupResultMapper.MapValidateOnlyResult(
            "operation-id",
            ObservedAt,
            binding,
            setupResult
        );
    }

    private static CdcProviderSetupObservationMapping MapProviderSetup(
        CoreCdc.CdcBinding binding,
        CdcProviderSetupResult setupResult
    ) => CdcProviderSetupResultMapper.MapValidateOnlyResult("operation-id", ObservedAt, binding, setupResult);

    private static CoreCdc.CdcContractValidationResult Validate(
        CoreCdc.CdcProviderSetupObservation observation,
        CoreCdc.CdcBinding binding
    ) =>
        CoreCdc.CdcProviderSetupObservationValidator.Validate(
            observation,
            new(
                observation.OperationId,
                binding.ToTargetIdentity(),
                binding.PhysicalSourceFingerprint,
                ObservedAt
            )
        );

    private static string OtherSourceFingerprint(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, "11111111-1111-1111-1111-111111111111").Value;

    private static CdcProviderSetupResult BuildSetupResult(
        CdcProvider provider,
        CoreCdc.CdcBinding binding,
        IReadOnlyList<CdcProviderArtifactObservation> artifactInventory,
        IReadOnlyList<CdcProviderHistoryObservation> providerHistoryObservations,
        bool includeRequiredSetupEvidence = true
    )
    {
        IReadOnlyList<CdcProviderArtifactObservation> finalArtifactInventory = includeRequiredSetupEvidence
            ? [.. artifactInventory, HeartbeatArtifact(provider)]
            : artifactInventory;

        return new(
            provider,
            CdcProviderSetupMode.ValidateOnly,
            CdcProviderSetupOutcome.ExactMatch,
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, binding.PhysicalSourceFingerprint),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, binding.PhysicalSourceFingerprint),
            finalArtifactInventory,
            includeRequiredSetupEvidence ? RequiredGrantInventory(provider) : [],
            includeRequiredSetupEvidence
                ? CdcConnectorTemplateTestData.BuildRequiredSourceTableInventory(provider)
                : [],
            includeRequiredSetupEvidence ? CdcConnectorTemplateTestData.BuildExpectedMessageKeyColumns() : [],
            includeRequiredSetupEvidence ? new CdcHeartbeatActionQuery("select 1", "sha256-safe") : null,
            providerHistoryObservations,
            null,
            []
        );
    }

    private static CdcProviderArtifactObservation Artifact(
        CdcProviderArtifactKind artifactKind,
        string safeName,
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched
    ) => new(artifactKind, new CdcSafeName(safeName), state, new Dictionary<string, string>());

    private static CdcProviderArtifactObservation HeartbeatArtifact(CdcProvider provider) =>
        Artifact(
            CdcProviderArtifactKind.HeartbeatTable,
            provider == CdcProvider.Postgresql ? "\"dms\".\"CdcHeartbeat\"" : "[dms].[CdcHeartbeat]"
        );

    private static IReadOnlyList<CdcGrantObservation> RequiredGrantInventory(CdcProvider provider) =>
        [
            new(
                CdcPrincipalKind.ConnectorPrincipal,
                new CdcSafeName("connector_principal"),
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName(
                    provider == CdcProvider.Postgresql ? "\"dms\".\"Document\"" : "[dms].[Document]"
                ),
                ["SELECT"],
                []
            ),
        ];

    private static CdcProviderDiagnostic ProviderHistoryUnavailableDiagnostic(
        CdcProviderArtifactKind artifactKind,
        string safeName
    ) =>
        new(
            "providerHistoryUnavailable",
            CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
            CdcProviderDiagnosticSeverity.Warning,
            CdcPrincipalKind.ConnectorPrincipal,
            artifactKind,
            new CdcSafeName(safeName),
            null,
            "unavailable",
            null,
            CdcProviderRetryContinuityClassification.SourceHistoryUnknown
        );

    private static CdcProviderHistoryObservation History(
        CdcProviderArtifactKind artifactKind,
        string safeName,
        IReadOnlyDictionary<string, string> safeObservedValues
    ) =>
        new(
            artifactKind,
            new CdcSafeName(safeName),
            safeObservedValues,
            CdcProviderRetryContinuityClassification.None
        );

    private static IReadOnlyList<CdcProviderArtifactObservation> SqlServerCaptureArtifacts(
        CoreCdc.CdcArtifactInventory inventory
    ) =>
        [
            Artifact(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                inventory.SqlServerCaptureInstanceDocumentCacheName!
            ),
            Artifact(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                inventory.SqlServerCaptureInstanceDocumentName!
            ),
            Artifact(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                inventory.SqlServerCaptureInstanceCdcHeartbeatName!
            ),
        ];

    private static IReadOnlyList<CdcProviderHistoryObservation> SqlServerCaptureHistories(
        CoreCdc.CdcArtifactInventory inventory
    ) =>
        [
            SqlServerCaptureHistory(
                inventory.SqlServerCaptureInstanceDocumentCacheName!,
                "0x00000000000000000001"
            ),
            SqlServerCaptureHistory(
                inventory.SqlServerCaptureInstanceDocumentName!,
                "0x00000000000000000001"
            ),
            SqlServerCaptureHistory(
                inventory.SqlServerCaptureInstanceCdcHeartbeatName!,
                "0x00000000000000000001"
            ),
        ];

    private static CdcProviderHistoryObservation SqlServerDatabaseHistory() =>
        SqlServerDatabaseHistory(captureJobRunning: true, cleanupJobRunning: true);

    private static CdcProviderHistoryObservation SqlServerDatabaseHistory(
        bool captureJobRunning,
        bool cleanupJobRunning
    ) =>
        History(
            CdcProviderArtifactKind.ProviderHistory,
            "sqlserver_database_cdc",
            new Dictionary<string, string>
            {
                ["database_cdc_enabled"] = "True",
                ["capture_job_present"] = "True",
                ["capture_job_name"] = "cdc.edfi_datastore_capture",
                ["capture_job_enabled"] = "True",
                ["capture_job_running"] = captureJobRunning.ToString(),
                ["capture_job_last_run_status"] = "1",
                ["cleanup_job_present"] = "True",
                ["cleanup_job_name"] = "cdc.edfi_datastore_cleanup",
                ["cleanup_job_enabled"] = "True",
                ["cleanup_job_running"] = cleanupJobRunning.ToString(),
                ["cleanup_job_last_run_status"] = "1",
                ["retained_max_lsn"] = "0x00000000000000000010",
            }
        );

    private static CdcProviderHistoryObservation SqlServerCaptureHistory(
        string safeName,
        string retainedMinLsn
    ) =>
        History(
            CdcProviderArtifactKind.SqlServerCaptureInstance,
            safeName,
            new Dictionary<string, string>
            {
                ["retained_min_lsn"] = retainedMinLsn,
                ["retained_max_lsn"] = "0x00000000000000000010",
            }
        );

    private static CoreCdc.CdcSourceHistoryClassificationResult ClassifySqlServer(
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory,
        CdcProviderSetupObservationMapping mapping
    )
    {
        string sourcePartitionHash = CoreCdc
            .CdcSourcePartitionHashCalculator.ComputeSqlServer(inventory.TopicPrefix, binding.InstanceKey)
            .Hash!;

        return CoreCdc.CdcSourceHistoryContinuityClassifier.Evaluate(
            new("operation-id", ObservedAt, ObservedAt, binding)
            {
                ProviderSetup = mapping.ProviderSetup,
                ConnectorOffset = new CoreCdc.CdcConnectorOffsetObservation(
                    CoreCdc.CdcJsonContract.CurrentContractVersion,
                    "operation-id",
                    ObservedAt,
                    binding.ToTargetIdentity(),
                    CoreCdc.CdcProvider.SqlServer,
                    binding.PhysicalSourceFingerprint,
                    inventory.ConnectorName,
                    inventory.TopicPrefix,
                    CoreCdc.CdcConnectorOffsetMatchResult.Exact,
                    sourcePartitionHash,
                    false,
                    false,
                    null,
                    "0x00000000000000000001",
                    "0x00000000000000000001",
                    2,
                    []
                ),
                ProviderHistory = mapping.ProviderHistory,
                SqlServerSchemaHistory = new CoreCdc.CdcSqlServerSchemaHistoryEvidence(
                    CoreCdc.CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                    CoreCdc.CdcSqlServerSchemaHistoryState.Valid
                ),
                ExpectedConnectSourcePartitionHash = sourcePartitionHash,
            }
        );
    }

    private static CoreCdc.CdcBinding BuildBinding(CoreCdc.CdcProvider provider)
    {
        string instanceKey =
            provider == CoreCdc.CdcProvider.Postgresql ? "postgresql-datastore" : "edfi_datastore";
        CoreCdc.CdcArtifactInventory inventory = CoreCdc
            .CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", instanceKey, 1, provider))
            .Inventory!;
        string fingerprint = CoreCdc
            .CdcPhysicalSourceFingerprintCalculator.Compute(
                provider,
                Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6")
            )
            .Fingerprint!;

        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            "dms-local",
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            instanceKey,
            1,
            provider,
            fingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            3,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }

    private static CoreCdc.CdcArtifactInventory RecoverInventory(CoreCdc.CdcBinding binding) =>
        CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
}
