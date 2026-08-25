// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcTargetStatus")]
public class Given_CdcTargetStatusEvaluator
{
    [Test]
    public void It_returns_ready_when_every_required_current_observation_is_satisfied()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding)
        );

        status.Readiness.Should().Be(CdcReadiness.Ready);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);
        status.Binding.State.Should().Be(CdcComponentState.Satisfied);
        status.Projection.State.Should().Be(CdcComponentState.Satisfied);
        status.ProviderSetup.State.Should().Be(CdcComponentState.Satisfied);
        status.ProviderBarrier.State.Should().Be(CdcComponentState.Satisfied);
        status.SourceHistory.State.Should().Be(CdcComponentState.Satisfied);
        status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        status.SourceHistory.IncidentLatched.Should().BeFalse();
        status.KafkaPolicy.State.Should().Be(CdcComponentState.Satisfied);
        status.ConnectOffsetStore.State.Should().Be(CdcComponentState.Satisfied);
        status.ConnectorConfig.State.Should().Be(CdcComponentState.Satisfied);
        status.ConnectorRuntime.State.Should().Be(CdcComponentState.Satisfied);
        status.Lag.State.Should().Be(CdcComponentState.Satisfied);
        status.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_treats_missing_current_source_fingerprint_as_unavailable_status_evidence()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                PhysicalSourceFingerprint = null,
            }
        );

        status.Readiness.Should().Be(CdcReadiness.Unknown);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status.Binding.State.Should().Be(CdcComponentState.Unknown);
        status.Binding.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.StatusObservationUnavailable)
            .And.NotContain(CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_keeps_explicit_current_source_mismatch_as_source_mismatch()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                PhysicalSourceFingerprint = CdcTargetStatusFixture.OtherSourceFingerprint,
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SourceMismatch);
        status.Binding.State.Should().Be(CdcComponentState.NotSatisfied);
        status.Binding.Category.Should().Be(CdcBlockingCategory.SourceMismatch);
    }

    [Test]
    public void It_selects_a_known_not_ready_blocker_before_missing_current_source_evidence()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                PhysicalSourceFingerprint = null,
                Projection = CdcTargetStatusFixture.Projection(binding) with
                {
                    OperationalHealthStatus = DocumentCacheOperationalHealthStatus.NonOperational,
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProjectionNonOperational);
        status.Binding.State.Should().Be(CdcComponentState.Unknown);
        status.Projection.State.Should().Be(CdcComponentState.NotSatisfied);
    }

    [Test]
    public void It_maps_component_observation_states_to_their_design_blockers()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        CdcKafkaPolicyObservation kafkaPolicy = CdcTargetStatusFixture.KafkaPolicy(binding);

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                ProviderSetup = CdcTargetStatusFixture.ProviderSetup(binding) with
                {
                    SetupOutcome = CdcProviderSetupOutcome.Invalid,
                    ArtifactInventoryState = CdcProviderSetupState.Mismatched,
                },
                ProviderBarrier = CdcTargetStatusFixture.ProviderBarrier(binding) with
                {
                    BarrierState = CdcProviderBarrierState.NotReached,
                    CommittedPosition = null,
                },
                SourceHistory = CdcTargetStatusFixture.SourceHistory(binding) with
                {
                    Continuity = CdcSourceHistoryContinuity.Lost,
                    ProviderArtifactState = CdcProviderArtifactContinuityState.Missing,
                    RetainedRangeState = CdcProviderRetainedRangeState.Unknown,
                    IncidentFailureCategory = CdcIncidentFailureCategory.ProviderArtifactMissing,
                },
                KafkaPolicy = kafkaPolicy with
                {
                    PolicyState = CdcKafkaPolicyState.Invalid,
                    PublicTopic = kafkaPolicy.PublicTopic with { State = CdcKafkaPolicyItemState.Invalid },
                },
                ConnectOffsetStore = CdcTargetStatusFixture.ConnectOffsetStore(binding) with
                {
                    PolicyState = CdcConnectOffsetStorePolicyState.Invalid,
                    AclState = CdcConnectOffsetStoreItemState.Invalid,
                },
                ConnectorConfig = CdcTargetStatusFixture.ConnectorConfig(binding) with
                {
                    ConfigurationState = CdcConnectorConfigurationState.Invalid,
                    TransformState = CdcConnectorConfigurationItemState.Invalid,
                },
                ConnectorRuntime = CdcTargetStatusFixture.ConnectorRuntime(binding) with
                {
                    ConnectorState = CdcConnectorRuntimeState.Paused,
                    RunningTaskCount = 0,
                    SoleTaskState = CdcConnectorRuntimeState.Paused,
                },
                Lag = CdcTargetStatusFixture.Lag(binding) with
                {
                    LagState = CdcConnectorLagState.Exceeded,
                    CurrentLagMilliseconds = 2_000,
                    ThresholdMilliseconds = 1_000,
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SourceHistoryLost);
        status.ProviderSetup.Category.Should().Be(CdcBlockingCategory.ProviderSetupInvalid);
        status.ProviderBarrier.Category.Should().Be(CdcBlockingCategory.ProviderBarrierNotReached);
        status.SourceHistory.Category.Should().Be(CdcBlockingCategory.SourceHistoryLost);
        status.KafkaPolicy.Category.Should().Be(CdcBlockingCategory.KafkaPolicyInvalid);
        status.ConnectOffsetStore.Category.Should().Be(CdcBlockingCategory.ConnectOffsetStoreInvalid);
        status.ConnectorConfig.Category.Should().Be(CdcBlockingCategory.ConnectorConfigInvalid);
        status.ConnectorRuntime.Category.Should().Be(CdcBlockingCategory.ConnectorNotRunning);
        status.Lag.Category.Should().Be(CdcBlockingCategory.LagExceeded);
    }

    [Test]
    public void It_maps_snapshot_progress_to_snapshot_incomplete_without_using_lag_as_substitute()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                ConnectorRuntime = CdcTargetStatusFixture.ConnectorRuntime(binding) with
                {
                    SnapshotState = CdcConnectorSnapshotState.Running,
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SnapshotIncomplete);
        status.ConnectorRuntime.State.Should().Be(CdcComponentState.NotSatisfied);
        status.ConnectorRuntime.Category.Should().Be(CdcBlockingCategory.SnapshotIncomplete);
        status.Lag.State.Should().Be(CdcComponentState.Satisfied);
    }

    [Test]
    public void It_maps_projection_source_mismatch_to_source_mismatch()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                Projection = CdcTargetStatusFixture.Projection(binding) with
                {
                    PhysicalSourceFingerprint = CdcTargetStatusFixture.OtherSourceFingerprint,
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SourceMismatch);
        status.Projection.State.Should().Be(CdcComponentState.NotSatisfied);
        status.Projection.Category.Should().Be(CdcBlockingCategory.SourceMismatch);
        status
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.SourceMismatch);
    }

    [TestCase(CdcDiagnosticCategory.SourceMismatch)]
    [TestCase(CdcDiagnosticCategory.ProviderMismatch)]
    public void It_maps_provider_barrier_source_or_provider_mismatch_diagnostics_to_source_mismatch(
        CdcDiagnosticCategory diagnosticCategory
    )
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                ProviderBarrier = CdcTargetStatusFixture.ProviderBarrier(binding) with
                {
                    BarrierState = CdcProviderBarrierState.Unknown,
                    CommittedPosition = null,
                    Diagnostics = [new(diagnosticCategory, "$.providerBarrier", "source mismatch")],
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SourceMismatch);
        status.ProviderBarrier.State.Should().Be(CdcComponentState.NotSatisfied);
        status.ProviderBarrier.Category.Should().Be(CdcBlockingCategory.SourceMismatch);
    }

    [Test]
    public void It_maps_other_provider_barrier_diagnostics_to_unavailable_status_evidence()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                ProviderBarrier = CdcTargetStatusFixture.ProviderBarrier(binding) with
                {
                    BarrierState = CdcProviderBarrierState.Unknown,
                    CommittedPosition = null,
                    Diagnostics =
                    [
                        new(
                            CdcDiagnosticCategory.ArtifactNameMismatch,
                            "$.connectorName",
                            "connector mismatch"
                        ),
                    ],
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.Unknown);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status.ProviderBarrier.State.Should().Be(CdcComponentState.Unknown);
        status.ProviderBarrier.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
    }

    [Test]
    public void It_maps_state_store_unavailability_to_unknown_status_observation_unavailable()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                BindingState = null,
                StateStoreDiagnostics =
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        "$.bindingState",
                        "local state unavailable"
                    ),
                ],
            }
        );

        status.Readiness.Should().Be(CdcReadiness.Unknown);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status.Binding.State.Should().Be(CdcComponentState.Unknown);
        status.Binding.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.LocalStateUnavailable)
            .And.NotContain(CdcDiagnosticCategory.BindingIdentityMismatch);
    }

    [Test]
    public void It_ignores_a_previous_binding_state_when_current_state_store_diagnostics_exist()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                StateStoreDiagnostics =
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        "$.bindingState",
                        "local state unavailable"
                    ),
                ],
            }
        );

        status.Readiness.Should().Be(CdcReadiness.Unknown);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status.Binding.State.Should().Be(CdcComponentState.Unknown);
        status.Binding.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        status
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.bindingState"
            );
        status
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .NotContain(CdcDiagnosticCategory.BindingIdentityMismatch);
    }

    [Test]
    public void It_keeps_a_valid_incident_latch_terminal_even_when_live_history_looks_healthy()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
        CdcIncident incident = CdcTargetStatusFixture.Incident(binding);

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                BindingState = new(
                    CdcJsonContract.CurrentContractVersion,
                    CdcTargetStatusFixture.ObservationObservedAt,
                    CdcBindingState.IncidentLatched,
                    binding,
                    incident
                ),
                SourceHistory = CdcTargetStatusFixture.SourceHistory(binding),
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.SourceHistoryLost);
        status.Binding.State.Should().Be(CdcComponentState.Satisfied);
        status.SourceHistory.State.Should().Be(CdcComponentState.NotSatisfied);
        status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        status.SourceHistory.IncidentLatched.Should().BeTrue();
    }
}

[TestFixture]
[Parallelizable]
[Category("CdcBlockingCategory")]
public class Given_CdcBlockingCategoryPrecedence
{
    [Test]
    public void It_selects_binding_missing_before_lower_precedence_known_blockers()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                BindingState = new(
                    CdcJsonContract.CurrentContractVersion,
                    CdcTargetStatusFixture.ObservationObservedAt,
                    CdcBindingState.BindingMissing,
                    null,
                    null
                ),
                Projection = CdcTargetStatusFixture.Projection(binding) with
                {
                    OperationalHealthStatus = DocumentCacheOperationalHealthStatus.NonOperational,
                },
                Lag = CdcTargetStatusFixture.Lag(binding) with
                {
                    LagState = CdcConnectorLagState.Exceeded,
                    CurrentLagMilliseconds = 2_000,
                    ThresholdMilliseconds = 1_000,
                },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.BindingMissing);
        status.Binding.Category.Should().Be(CdcBlockingCategory.BindingMissing);
        status.Projection.Category.Should().Be(CdcBlockingCategory.ProjectionNonOperational);
        status.Lag.Category.Should().Be(CdcBlockingCategory.LagExceeded);
    }

    [Test]
    public void It_selects_known_not_ready_before_unknown_observation_failures()
    {
        CdcBinding binding = CdcTargetStatusFixture.CreateBinding();

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            CdcTargetStatusFixture.ValidInput(binding) with
            {
                ProviderBarrier = CdcTargetStatusFixture.ProviderBarrier(binding) with
                {
                    BarrierState = CdcProviderBarrierState.NotReached,
                    CommittedPosition = null,
                },
                Lag = CdcTargetStatusFixture.Lag(binding) with { OperationId = "operation-2" },
            }
        );

        status.Readiness.Should().Be(CdcReadiness.NotReady);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProviderBarrierNotReached);
        status.ProviderBarrier.State.Should().Be(CdcComponentState.NotSatisfied);
        status.Lag.State.Should().Be(CdcComponentState.Unknown);
        status.Lag.Category.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
    }
}

internal static class CdcTargetStatusFixture
{
    public static readonly DateTimeOffset StatusObservedAt = new(2026, 8, 17, 13, 11, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset ObservationObservedAt = StatusObservedAt.AddSeconds(-10);

    public const string OperationId = "operation-1";
    public const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";
    public const string OtherSourceFingerprint =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ConnectSourcePartitionHash =
        "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40";

    public static CdcTargetStatusEvaluationInput ValidInput(CdcBinding binding) =>
        new(OperationId, StatusObservedAt, binding.ToTargetIdentity(), SourceFingerprint)
        {
            BindingState = new(
                CdcJsonContract.CurrentContractVersion,
                ObservationObservedAt,
                CdcBindingState.BindingPresent,
                binding,
                null
            ),
            Projection = Projection(binding),
            ProviderSetup = ProviderSetup(binding),
            ProviderBarrier = ProviderBarrier(binding),
            SourceHistory = SourceHistory(binding),
            KafkaPolicy = KafkaPolicy(binding),
            ConnectOffsetStore = ConnectOffsetStore(binding),
            ConnectorConfig = ConnectorConfig(binding),
            ConnectorRuntime = ConnectorRuntime(binding),
            Lag = Lag(binding),
        };

    public static CdcBinding CreateBinding(CdcProvider provider = CdcProvider.Postgresql)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider))
            .Inventory!;

        return new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            provider,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );
    }

    public static CdcProjectionCorrelationObservation Projection(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            ObservationObservedAt.AddSeconds(-1),
            new DocumentCacheStatusTargetKey("", 1),
            CdcProjectionCorrelationState.Matched,
            DocumentCacheOperationalHealthStatus.Operational,
            DocumentCacheStatusReason.None,
            DocumentCacheCaughtUpStatus.CaughtUp,
            DocumentCacheStatusReason.None,
            DocumentCacheStatusQueuePresence.Empty,
            [],
            []
        );

    public static CdcProviderSetupObservation ProviderSetup(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            CdcProviderSetupMode.ValidateOnly,
            CdcProviderSetupOutcome.Satisfied,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            []
        );

    public static CdcProviderBarrierObservation ProviderBarrier(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            ObservationObservedAt.AddSeconds(-3),
            ObservationObservedAt.AddSeconds(-2),
            ObservationObservedAt.AddSeconds(-1),
            CdcProviderBarrierState.Reached,
            "0/16B6C50",
            null,
            null,
            null,
            "0/16B6C51",
            []
        );

    public static CdcSourceHistoryObservation SourceHistory(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            CdcSourceHistoryContinuity.Healthy,
            false,
            CdcProviderArtifactContinuityState.ExactMatch,
            CdcProviderRetainedRangeState.CoversCommittedOffset,
            PositionEvidence(binding),
            null,
            null,
            CdcSqlServerSchemaHistoryState.NotApplicable,
            []
        );

    public static CdcSourceHistoryObservation SourceHistoryProviderHistoryUnknown(CdcBinding binding) =>
        SourceHistory(binding) with
        {
            Continuity = CdcSourceHistoryContinuity.Unknown,
            ProviderArtifactState = CdcProviderArtifactContinuityState.Unknown,
            RetainedRangeState = CdcProviderRetainedRangeState.Unknown,
            PositionEvidence = null,
            Diagnostics =
            [
                new(
                    CdcDiagnosticCategory.ProviderHistoryUnknown,
                    "$.sourceHistory.providerHistory",
                    "provider history unavailable"
                ),
            ],
        };

    public static CdcKafkaPolicyObservation KafkaPolicy(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            CdcKafkaPolicyState.Satisfied,
            "single-node",
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            null,
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
            null,
            new(CdcKafkaPolicyItemState.Satisfied, 1_000_000, 2_000_000),
            []
        );
    }

    public static CdcConnectOffsetStorePolicyObservation ConnectOffsetStore(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            "worker-1",
            "connect-offsets",
            CdcConnectOffsetStorePolicyState.Satisfied,
            "compact",
            1,
            1,
            CdcConnectOffsetStoreItemState.Satisfied,
            []
        );

    public static CdcConnectorConfigurationObservation ConnectorConfig(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            inventory.ConnectorName,
            CdcConnectorConfigurationState.Matched,
            inventory.TopicPrefix,
            1,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.NotApplicable,
            []
        );
    }

    public static CdcConnectorRuntimeObservation ConnectorRuntime(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            inventory.ConnectorName,
            CdcConnectorRuntimeState.Running,
            1,
            1,
            CdcConnectorRuntimeState.Running,
            CdcConnectorSnapshotState.Completed,
            null,
            null,
            []
        );
    }

    public static CdcConnectorLagObservation Lag(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservationObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            CdcConnectorLagState.WithinThreshold,
            250,
            1_000,
            100,
            200,
            400,
            []
        );

    public static CdcIncident Incident(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            ObservationObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            PositionEvidence(binding)
        );

    private static CdcIncidentPositionMetadata PositionEvidence(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            inventory.ConnectorName,
            inventory.TopicName,
            inventory.ProgressTopicName,
            inventory.SchemaHistoryTopicName,
            inventory.PostgresqlLogicalSlotName,
            ConnectSourcePartitionHash,
            "0/16B6C51",
            null,
            null,
            null,
            "0/16B6C50",
            "0/16B6C52",
            []
        );
    }
}
