// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The initial readiness sequence end to end: the connector is registered and read back, the projector
/// is observed caught up, the connector commits past a provider barrier captured after that
/// observation, source history is continuous, the projector is observed caught up again, and the
/// connector's own lag is inside the threshold. Every one of those is evidence; an enablement that
/// cannot collect one of them ends there with write admission closed.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerSequence")]
public class Given_CdcSetupControllerInitialReadinessSequence
{
    [Test]
    public async Task It_admits_writes_when_every_step_produced_its_evidence()
    {
        CdcSetupControllerHarness harness = new();

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.Diagnostics.Should().BeEmpty();
        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        Satisfied(admission.Steps.Binding, nameof(admission.Steps.Binding));
        Satisfied(
            admission.Steps.GuardedTrackingActivation,
            nameof(admission.Steps.GuardedTrackingActivation)
        );
        Satisfied(admission.Steps.ProviderSetup, nameof(admission.Steps.ProviderSetup));
        Satisfied(
            admission.Steps.ConnectorAndTopicValidation,
            nameof(admission.Steps.ConnectorAndTopicValidation)
        );
        Satisfied(admission.Steps.FirstProjectionCaughtUp, nameof(admission.Steps.FirstProjectionCaughtUp));
        Satisfied(admission.Steps.ProviderBarrier, nameof(admission.Steps.ProviderBarrier));
        Satisfied(admission.Steps.SourceHistory, nameof(admission.Steps.SourceHistory));
        Satisfied(admission.Steps.SecondProjectionCaughtUp, nameof(admission.Steps.SecondProjectionCaughtUp));
        Satisfied(admission.Steps.Lag, nameof(admission.Steps.Lag));
    }

    /// <summary>
    /// The order the design fixes: the connector is registered only after the binding record and the
    /// governed artifacts exist, the barrier is captured only after the projector reported caught up,
    /// and continuity is checked only after the connector committed past the barrier.
    /// </summary>
    [Test]
    public async Task It_collects_the_readiness_evidence_in_sequence()
    {
        CdcSetupControllerHarness harness = new();

        await harness.EnableAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly()
            .Then(
                A.CallTo(() =>
                        harness.Kafka.EnsureBindingKafkaPolicyAsync(
                            A<CdcObservationContext>._,
                            A<CdcArtifactInventory>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() =>
                        harness.Connect.PutConnectorConfigAsync(
                            A<string>._,
                            A<IReadOnlyDictionary<string, string>>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() =>
                        harness.SourcePositions.CaptureBarrierAsync(
                            A<CdcProviderBarrierCaptureRequest>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() =>
                        harness.SourcePositions.ObserveSourceHistoryAsync(
                            A<CdcSourceHistoryObservationRequest>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() => harness.Lag.ReadAsync(A<CdcProvider>._, A<string>._, A<CancellationToken>._))
                    .MustHaveHappenedOnceExactly()
            );
    }

    /// <summary>
    /// The connector is registered only once the plugin has accepted the rendered configuration, and
    /// only after the binding-governed topics and ACLs exist.
    /// </summary>
    [Test]
    public async Task It_validates_the_rendered_configuration_with_the_plugin_before_registering_it()
    {
        CdcSetupControllerHarness harness = new()
        {
            PluginValidation = new(CdcConnectOutcome.Succeeded, new(1, ["slot.name"]), null),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableConnectorPluginValidationRejected").Should().NotBeNull();
        A.CallTo(() =>
                harness.Connect.PutConnectorConfigAsync(
                    A<string>._,
                    A<IReadOnlyDictionary<string, string>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_stops_when_the_connector_could_not_be_registered()
    {
        CdcSetupControllerHarness harness = new()
        {
            Registration = new(CdcConnectOutcome.Conflict, new(409, "rebalance in progress", true)),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableConnectorRegistrationFailed").Should().NotBeNull();
        A.CallTo(() => harness.Projection.CollectAsync(A<CdcObservationContext>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A worker holding a configuration that is not the rendered one is drift, and the sequence reports
    /// it rather than waiting on evidence from a connector that is not the binding's.
    /// </summary>
    [Test]
    public async Task It_stops_when_the_live_connector_configuration_does_not_match_what_was_rendered()
    {
        CdcSetupControllerHarness harness = new()
        {
            ConnectorConfigReadBack = CdcSetupControllerHarness.RenderedConnectorConfig(config =>
                config["tasks.max"] = "2"
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        admission.Steps.ConnectorAndTopicValidation.State.Should().NotBe(CdcComponentState.Satisfied);
        A.CallTo(() =>
                harness.SourcePositions.CaptureBarrierAsync(
                    A<CdcProviderBarrierCaptureRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Interruption at the queue drain: the projector never reports the target caught up, so the
    /// sequence never reaches the barrier and admission stays closed.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_when_the_projection_queue_never_drains()
    {
        CdcSetupControllerHarness harness = new()
        {
            ProjectionStatus = CdcSetupControllerHarness.ProjectingTheTarget(
                caughtUp: DocumentCacheCaughtUpStatus.NotCaughtUp,
                queuePresence: DocumentCacheStatusQueuePresence.NotEmpty
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        admission.Steps.FirstProjectionCaughtUp.State.Should().NotBe(CdcComponentState.Satisfied);
        admission.Steps.FirstProjectionCaughtUp.Category.Should().Be(CdcBlockingCategory.ProjectionBacklog);
        A.CallTo(() =>
                harness.SourcePositions.CaptureBarrierAsync(
                    A<CdcProviderBarrierCaptureRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Interruption at the provider barrier: the connector never commits past the captured position, so
    /// the sequence never reaches continuity, the second caught-up observation, or lag.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_when_the_connector_never_commits_past_the_barrier()
    {
        CdcSetupControllerHarness harness = new()
        {
            CommittedOffsets = CdcSetupControllerHarness.StreamingOffsets(
                CdcSetupControllerHarness.CommittedLsnProc - 0x100
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        admission.Steps.ProviderBarrier.State.Should().NotBe(CdcComponentState.Satisfied);
        A.CallTo(() =>
                harness.SourcePositions.ObserveSourceHistoryAsync(
                    A<CdcSourceHistoryObservationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => harness.Lag.ReadAsync(A<CdcProvider>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A barrier the provider could not report at all is absent evidence, not a barrier that was
    /// reached, and nothing further is observed against it.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_when_the_provider_barrier_could_not_be_captured()
    {
        CdcSetupControllerHarness harness = new()
        {
            CapturedBarrier = CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                CdcSetupControllerHarness.Now,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        CdcSetupControllerHarness.Now,
                        "$.postgresqlBarrierLsn",
                        "CDC PostgreSQL provider barrier capture failed."
                    ),
                ]
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableProviderBarrierNotCaptured").Should().NotBeNull();
        A.CallTo(() =>
                harness.SourcePositions.ObserveProviderBarrier(A<CdcProviderBarrierObservationRequest>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Interruption at the second caught-up observation: the projector fell behind again after the
    /// barrier, so the enablement reports a backlog rather than admitting writes.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_when_the_second_caught_up_observation_reports_a_backlog()
    {
        CdcSetupControllerHarness harness = new()
        {
            ProjectionStatusAfterBarrier = CdcSetupControllerHarness.ProjectingTheTarget(
                caughtUp: DocumentCacheCaughtUpStatus.NotCaughtUp,
                queuePresence: DocumentCacheStatusQueuePresence.NotEmpty
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Satisfied(admission.Steps.ProviderBarrier, nameof(admission.Steps.ProviderBarrier));
        Satisfied(admission.Steps.SourceHistory, nameof(admission.Steps.SourceHistory));
        admission.Steps.SecondProjectionCaughtUp.State.Should().NotBe(CdcComponentState.Satisfied);
        A.CallTo(() => harness.Lag.ReadAsync(A<CdcProvider>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Lag that cannot be read is unknown lag, and unknown lag keeps readiness false: elapsed time and
    /// an unreachable metrics bridge are not evidence that the connector is close to its source.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_when_the_connector_lag_is_unknown()
    {
        CdcSetupControllerHarness harness = new()
        {
            LagReading = new(CdcConnectorLagReadOutcome.Unavailable, null, "unreachable"),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        admission.Steps.Lag.State.Should().Be(CdcComponentState.Unknown);
    }

    [Test]
    public async Task It_leaves_admission_closed_when_the_connector_lag_exceeds_the_threshold()
    {
        CdcSetupControllerHarness harness = new()
        {
            LagReading = new(
                CdcConnectorLagReadOutcome.Succeeded,
                new(120_000, 90_000, 110_000, 118_000),
                null
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        admission.Steps.Lag.Category.Should().Be(CdcBlockingCategory.LagExceeded);
    }

    /// <summary>
    /// A connector running more than one task is not the topology the binding's ordering guarantee rests
    /// on, so the connector evidence is rejected rather than accepted as running.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_when_the_connector_is_not_running_exactly_one_task()
    {
        CdcSetupControllerHarness harness = new()
        {
            ConnectorStatus = new(
                CdcConnectOutcome.Succeeded,
                new("RUNNING", [new(0, "RUNNING", null), new(1, "RUNNING", null)]),
                null
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        admission.Steps.ConnectorAndTopicValidation.State.Should().NotBe(CdcComponentState.Satisfied);
    }

    /// <summary>
    /// The activation is carried by what the instance database shows afterwards, not by the command's
    /// own report: a command that answered without leaving the database tracking provisions nothing
    /// further.
    /// </summary>
    [Test]
    public async Task It_stops_when_the_activation_left_the_database_untracked()
    {
        CdcSetupControllerHarness harness = new()
        {
            PostActivationEligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Disabled"),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableGuardedActivationNotObserved").Should().NotBeNull();
        A.CallTo(() =>
                harness.Kafka.EnsureConnectOffsetStoreAsync(
                    A<CdcObservationContext>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A SQL Server binding reaches the same admission, which it can only do once the schema-history
    /// topic has been read: the continuity classifier requires that evidence for the provider and
    /// reports unknown continuity without it.
    /// </summary>
    [Test]
    public async Task It_admits_writes_for_a_sql_server_binding_whose_schema_history_holds_records()
    {
        CdcSetupControllerHarness harness = new(CdcProvider.SqlServer);

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.Diagnostics.Should().BeEmpty();
        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        Satisfied(admission.Steps.ProviderBarrier, nameof(admission.Steps.ProviderBarrier));
        Satisfied(admission.Steps.SourceHistory, nameof(admission.Steps.SourceHistory));
        harness
            .SourceHistoryRequest!.SqlServerSchemaHistory!.EnablementPhase.Should()
            .Be(CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission);
        harness
            .SourceHistoryRequest.SqlServerSchemaHistory.State.Should()
            .Be(CdcSqlServerSchemaHistoryState.Valid);
        A.CallTo(() =>
                harness.Kafka.ReadSqlServerSchemaHistoryAsync(
                    A<CdcArtifactInventory>._,
                    CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
                    true,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// An empty schema-history topic ends the enablement with admission closed, but latches nothing: the
    /// first enablement is the run that writes that history, so the phase leaves continuity unknown
    /// rather than terminally lost.
    /// </summary>
    [Test]
    public async Task It_leaves_admission_closed_without_latching_an_incident_on_an_empty_schema_history()
    {
        CdcSetupControllerHarness harness = new(CdcProvider.SqlServer)
        {
            SchemaHistoryState = CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset,
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Satisfied(admission.Steps.ProviderBarrier, nameof(admission.Steps.ProviderBarrier));
        admission.Steps.SourceHistory.State.Should().NotBe(CdcComponentState.Satisfied);
        harness
            .SourceHistoryClassification!.Observation.Continuity.Should()
            .Be(
                CdcSourceHistoryContinuity.Unknown,
                "a first enablement's empty schema history is not yet a continuity loss"
            );
        harness.SourceHistoryClassification.Observation.IncidentLatched.Should().BeFalse();
        harness
            .SourceHistoryClassification.IncidentCandidate.Should()
            .BeNull("no terminal incident is raised before initial admission");
        A.CallTo(() => harness.Lag.ReadAsync(A<CdcProvider>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// PostgreSQL carries no schema-history evidence at all: the classifier returns before consulting
    /// the field for that provider, so none is composed for it.
    /// </summary>
    [Test]
    public async Task It_supplies_no_schema_history_evidence_for_a_postgresql_binding()
    {
        CdcSetupControllerHarness harness = new();

        await harness.EnableAsync();

        harness.SourceHistoryRequest!.SqlServerSchemaHistory.Should().BeNull();
    }

    private static void Satisfied(CdcComponent component, string stepName) =>
        component.State.Should().Be(CdcComponentState.Satisfied, "{0} must be satisfied", stepName);

    private static void NotAdmitted(CdcAdmission admission) =>
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);

    private static CdcDiagnostic Diagnostic(CdcAdmission admission, string code) =>
        admission.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == code).Subject;
}
