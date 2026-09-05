// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The combined status of an enabled target: every observation the shared evaluators decide readiness
/// from, collected once and reported as it was observed. Absent evidence keeps readiness false, and the
/// source-history continuity check runs on this interval like every other one.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerStatus")]
public class Given_CdcSetupControllerStatus
{
    [Test]
    public async Task It_reports_the_target_ready_when_every_observation_is_satisfied()
    {
        CdcSetupControllerHarness harness = EnabledBinding();

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.Ready);
        status.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);

        CdcTargetStatus target = Target(status);
        Satisfied(target.Binding, nameof(target.Binding));
        Satisfied(target.Projection, nameof(target.Projection));
        Satisfied(target.ProviderSetup, nameof(target.ProviderSetup));
        Satisfied(target.ProviderBarrier, nameof(target.ProviderBarrier));
        target.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        Satisfied(target.KafkaPolicy, nameof(target.KafkaPolicy));
        Satisfied(target.ConnectOffsetStore, nameof(target.ConnectOffsetStore));
        Satisfied(target.ConnectorConfig, nameof(target.ConnectorConfig));
        Satisfied(target.ConnectorRuntime, nameof(target.ConnectorRuntime));
        Satisfied(target.Lag, nameof(target.Lag));
    }

    /// <summary>
    /// A target with no binding record names no governed artifact, so nothing is read from the broker,
    /// the worker, or the provider on its behalf.
    /// </summary>
    [Test]
    public async Task It_reports_the_binding_missing_without_observing_any_governed_artifact()
    {
        CdcSetupControllerHarness harness = new();

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        Target(status).Binding.Category.Should().Be(CdcBlockingCategory.BindingMissing);
        A.CallTo(() =>
                harness.Kafka.EnsureBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.GetConnectorConfigAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The Connect offset store is shared worker state every binding's committed positions live in, so
    /// a store that does not conform is the binding's problem too.
    /// </summary>
    [Test]
    public async Task It_reports_the_target_not_ready_when_the_shared_connect_offset_store_does_not_conform()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.OffsetStorePolicy = NonconformingOffsetStore();

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        Target(status).ConnectOffsetStore.State.Should().Be(CdcComponentState.NotSatisfied);
        Target(status).ConnectOffsetStore.Category.Should().Be(CdcBlockingCategory.ConnectOffsetStoreInvalid);
    }

    /// <summary>
    /// A status is an observation of the target as it is: it creates no absent topic, re-grants no
    /// missing ACL, does not create the shared Connect offset store, and captures no provider artifact.
    /// A pass that provisioned would report artifacts it had just created itself, and would put back
    /// what a failed retirement had already removed.
    /// </summary>
    [Test]
    public async Task It_reports_the_target_status_without_provisioning_any_governed_artifact()
    {
        CdcSetupControllerHarness harness = EnabledBinding();

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.Ready);
        A.CallTo(() =>
                harness.Kafka.EnsureConnectOffsetStoreAsync(
                    A<CdcObservationContext>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.EnsureBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.ProviderSetup.SetupAsync(
                    A<Ddl.CdcProviderSetupRequest>.That.Matches(request =>
                        request.Mode != Ddl.CdcProviderSetupMode.ValidateOnly
                    ),
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.DescribeConnectOffsetStoreAsync(
                    A<CdcObservationContext>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                harness.Kafka.DescribeBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// The generation's partitioning is fixed by its binding record. A status that verified the topic
    /// against configuration would report a repartitioned topic satisfied as soon as the configuration
    /// was edited to match, which is exactly the drift the recorded count exists to catch.
    /// </summary>
    [Test]
    public async Task It_verifies_the_kafka_policy_against_the_partition_count_the_binding_recorded()
    {
        CdcSetupControllerHarness harness = new(CdcProvider.Postgresql)
        {
            BindingRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.Binding() with
                {
                    PartitionCount = CdcSetupControllerHarness.ConfiguredPartitionCount + 4,
                }
            ),
        };

        await harness.StatusAsync();

        harness
            .VerifiedPartitionCounts.Should()
            .AllBeEquivalentTo(
                CdcSetupControllerHarness.ConfiguredPartitionCount + 4,
                "the binding record fixes the generation's partitioning, not the current configuration"
            );
        harness.VerifiedPartitionCounts.Should().NotBeEmpty();
    }

    /// <summary>
    /// Lag that could not be read is unknown lag, and unknown lag keeps readiness false: an unreachable
    /// metrics bridge is not evidence that the connector is close to its source.
    /// </summary>
    [Test]
    public async Task It_reports_the_target_not_ready_when_the_connector_lag_is_unknown()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.LagReading = new(CdcConnectorLagReadOutcome.Unavailable, null, "unreachable");

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().NotBe(CdcReadiness.Ready);
        Target(status).Lag.State.Should().Be(CdcComponentState.Unknown);
    }

    /// <summary>
    /// After initial admission a missing schema-history topic is a proved continuity loss: the run that
    /// writes that history has already happened. The loss is latched durably and the connector is
    /// fenced so it commits no further offsets against a source it can no longer resume from exactly.
    /// </summary>
    [Test]
    public async Task It_latches_a_proved_source_history_loss_and_stops_the_connector()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        Target(status).SourceHistory.IncidentLatched.Should().BeTrue();
        harness
            .LatchedIncident.Should()
            .NotBeNull()
            .And.Subject.As<CdcIncident>()
            .FailureCategory.Should()
            .Be(CdcIncidentFailureCategory.SchemaHistoryMissing);
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The latch asserts a fence as well as a loss. A stop the worker refuses leaves the connector
    /// committing offsets against a source it can no longer resume from exactly, so the status reports
    /// the fence that did not take rather than only the incident it latched.
    /// </summary>
    [Test]
    public async Task It_reports_a_latched_loss_whose_connector_fence_the_worker_refused()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.Stop = new(CdcConnectOutcome.Conflict, new(409, "a rebalance is in progress", true));

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        Target(status).SourceHistory.IncidentLatched.Should().BeTrue();
        CdcDiagnostic fence = Target(status)
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "statusIncidentFenceNotApplied")
            .Subject;
        fence.Component.Should().Be(CdcDiagnosticComponent.ConnectorRuntime);
        fence.Observed.Should().Be(nameof(CdcConnectOutcome.Conflict));
        Target(status).Binding.State.Should().NotBe(CdcComponentState.Unknown);
    }

    /// <summary>
    /// A fence the worker applied is not reported as one that did not: the diagnostic is evidence of a
    /// failure, not a record that the stop was attempted.
    /// </summary>
    [Test]
    public async Task It_reports_no_fence_failure_when_the_worker_stops_the_connector()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;

        CdcStatus status = await harness.StatusAsync();

        Target(status)
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Code == "statusIncidentFenceNotApplied");
    }

    /// <summary>
    /// The latch is written once and is never cleared by what a later interval observes: a recreated
    /// artifact, a healthy-looking connector, and a lag inside the threshold all leave the binding
    /// terminally lost.
    /// </summary>
    [Test]
    public async Task It_latches_the_loss_once_and_no_later_poll_clears_it()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;

        await harness.StatusAsync();

        // The schema-history topic is recreated and everything else answers as a healthy binding would.
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Valid;

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        Target(status).SourceHistory.Category.Should().Be(CdcBlockingCategory.SourceHistoryLost);
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A binding whose continuity is healthy on this interval latches nothing: only a proved loss is
    /// durable state.
    /// </summary>
    [Test]
    public async Task It_latches_nothing_when_continuity_is_healthy()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);

        await harness.StatusAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A latch the state store did not accept is reported, not dropped. The loss is terminal and the
    /// record that has to outlive this poll does not carry it, so the status reports the binding state
    /// as unavailable rather than composing one from a state it could not write - and the store's own
    /// account of why is carried with it.
    /// </summary>
    [Test]
    public async Task It_reports_a_source_history_latch_that_did_not_become_durable()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.LatchResult = CdcSetupControllerHarness.StateStoreUnavailable();

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);

        CdcDiagnostic latch = Target(status)
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "statusSourceHistoryLatchNotDurable")
            .Subject;
        latch.Component.Should().Be(CdcDiagnosticComponent.SourceHistory);
        latch.Category.Should().Be(CdcDiagnosticCategory.SourceHistoryLost);

        Target(status)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable);

        // The connector is fenced whether or not the latch became durable: the loss was proved either
        // way, and a connector left committing offsets against it is the outcome the fence exists for.
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        // Nothing reports the incident as latched, because nothing durable holds it.
        Target(status).SourceHistory.IncidentLatched.Should().BeFalse();
    }

    /// <summary>
    /// Latching is idempotent by design — a poll that reads an already-latched incident raises no
    /// second candidate — so a fence driven by the candidate would give the connector exactly one
    /// chance to be stopped. Here the first stop is refused, and the connector is still running when
    /// the next poll arrives; that poll must ask again rather than report a contained incident over a
    /// connector that is still committing offsets against the lost source.
    /// </summary>
    [Test]
    public async Task It_fences_again_on_a_later_poll_when_the_first_stop_was_refused()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.Stop = new(CdcConnectOutcome.Unavailable, new(503, "worker unavailable", true));

        await harness.StatusAsync();

        // The worker is reachable again, but nothing has changed about the connector: it is still
        // running, and the loss is still latched.
        harness.Stop = new(CdcConnectOutcome.Succeeded, null);

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);

        // Latched once across both polls, fenced on both.
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    /// <summary>
    /// The scenario the loss classification must not claim: an enablement that timed out after
    /// registering its connector and before it committed its first offset. The binding is durable —
    /// it is written at step 3, deliberately ahead of the artifacts it governs — but nothing has been
    /// published, so there is no committed position to have lost. Latching here would be terminal and
    /// would close the documented retry, which refuses an incident-latched binding.
    /// </summary>
    [Test]
    public async Task It_latches_no_loss_for_an_enablement_interrupted_before_its_first_offset()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.CommittedOffsets = new(CdcConnectOutcome.Succeeded, new([]), null);
        harness.PublicTopicPublication = new(true, false);

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        Target(status).SourceHistory.IncidentLatched.Should().BeFalse();
        status.Readiness.Should().NotBe(CdcReadiness.Ready);

        // Nothing irreversible: no incident written, and the connector left for the retry to finish.
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The same absent offset over a stream that HAS published is the terminal loss the design
    /// describes: a committed position existed and no longer does, and consumers hold state from it.
    /// </summary>
    [Test]
    public async Task It_latches_the_offset_loss_when_the_public_topic_proves_an_established_stream()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.CommittedOffsets = new(CdcConnectOutcome.Succeeded, new([]), null);
        harness.PublicTopicPublication = new(true, true);

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        Target(status).SourceHistory.IncidentLatched.Should().BeTrue();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappened();
    }

    /// <summary>
    /// A worker that never answered the offsets query proves nothing about the connector's committed
    /// position, so no loss is latched however established the stream is.
    /// </summary>
    [Test]
    public async Task It_latches_no_loss_when_the_worker_did_not_answer_the_offsets_query()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.CommittedOffsets = new(
            CdcConnectOutcome.Unavailable,
            null,
            new(503, "worker unavailable", true)
        );

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        Target(status).SourceHistory.IncidentLatched.Should().BeFalse();
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A status that cannot reach the instance database returns before the continuity classifier, so
    /// nothing on that path re-attempts a stop the worker refused. The incident is already latched in
    /// the binding record, which this poll did read, so containment is re-attempted from that record
    /// alone — otherwise a connector with working credentials of its own keeps publishing against the
    /// lost source for as long as the control plane's own database connection stays down.
    /// </summary>
    [Test]
    public async Task It_fences_a_latched_incident_when_the_provider_connection_cannot_be_opened()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.Stop = new(CdcConnectOutcome.Unavailable, new(503, "worker unavailable", true));

        // The poll that proves and latches the loss. Its fence is refused, so the connector is still
        // running and nothing later raises a second incident candidate.
        await harness.StatusAsync();

        // The instance database is now unreachable, so this poll returns before the classifier.
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("the instance database is unreachable"));

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "statusProviderConnectionUnavailable");
        CdcDiagnostic fence = Target(status)
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "statusIncidentFenceNotApplied")
            .Subject;
        fence.Component.Should().Be(CdcDiagnosticComponent.ConnectorRuntime);
        fence.Observed.Should().Be(nameof(CdcConnectOutcome.Unavailable));

        // Asked again on the second poll rather than only on the one that proved the loss.
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    /// <summary>
    /// The containment above is scoped to a binding that already carries a latched incident. A status
    /// blocked on the same unreachable database with nothing latched has proved no loss, and stopping
    /// a connector on that evidence would fence a healthy stream.
    /// </summary>
    [Test]
    public async Task It_fences_nothing_when_the_provider_connection_fails_and_no_incident_is_latched()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("the instance database is unreachable"));

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "statusProviderConnectionUnavailable");
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A connector the worker already reports STOPPED is the state the fence asks for, so the poll does
    /// not reissue a request whose answer it is already holding.
    /// </summary>
    [Test]
    public async Task It_issues_no_fence_for_a_lost_source_history_whose_connector_is_already_stopped()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.ConnectorStatus = CdcSetupControllerHarness.RunningConnector(
            connectorState: "STOPPED",
            taskState: "STOPPED"
        );

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        Target(status).SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A status collected against a binding record naming the other engine reports a provider mismatch
    /// rather than throwing. The governed names are recovered under the record's provider while the
    /// provider-setup input is selected by this deployment's, so composing one from the other engine's
    /// inventory dereferences artifact names that are absent by construction — out of an operation
    /// whose contract is to observe and report what it found.
    /// </summary>
    [Test]
    public async Task It_reports_a_provider_mismatch_rather_than_failing_to_compose_the_provider_inputs()
    {
        CdcSetupControllerHarness harness = new(CdcProvider.SqlServer)
        {
            BindingRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.Binding(CdcProvider.Postgresql)
            ),
        };

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().NotBe(CdcReadiness.Ready);

        CdcDiagnostic mismatch = Target(status)
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "statusProviderMismatch")
            .Subject;
        mismatch.Category.Should().Be(CdcDiagnosticCategory.ProviderMismatch);
        mismatch.Component.Should().Be(CdcDiagnosticComponent.Binding);

        // Refused before the instance connection is opened, so no database this control plane could not
        // have inspected anyway is reached.
        A.CallTo(() => harness.Connections.Create(A<CdcProvider>._, A<string>._)).MustNotHaveHappened();
    }

    internal static CdcSetupControllerHarness EnabledBinding(CdcProvider provider = CdcProvider.Postgresql) =>
        new(provider)
        {
            BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding(provider)),
        };

    /// <summary>A shared Connect offset store that is not compacted and is not durable enough.</summary>
    internal static CdcConnectOffsetStorePolicyObservation NonconformingOffsetStore() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcSetupControllerHarness.OperationId,
            CdcSetupControllerHarness.Now,
            CdcControlTemplateTestData.BuildBinding(Ddl.CdcProvider.Postgresql).ToTargetIdentity(),
            CdcProvider.Postgresql,
            CdcSetupControllerHarness.Fingerprint(),
            "worker-1",
            "connect-offsets",
            CdcConnectOffsetStorePolicyState.Invalid,
            "delete",
            1,
            1,
            CdcConnectOffsetStoreItemState.Satisfied,
            []
        );

    internal static CdcTargetStatus Target(CdcStatus status) =>
        status.Targets.Should().ContainSingle().Subject;

    private static void Satisfied(CdcComponent component, string componentName) =>
        component.State.Should().Be(CdcComponentState.Satisfied, "{0} must be satisfied", componentName);
}

/// <summary>
/// Restart is guarded by source-history continuity, not by the connector's own reported state: the
/// connector is started or resumed only against affirmative evidence that the source it resumes from
/// still covers its committed position.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerStatus")]
public class Given_CdcSetupControllerRestart
{
    [Test]
    public async Task It_restarts_the_connector_when_continuity_is_affirmative()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        Given_CdcSetupControllerStatus
            .Target(status)
            .SourceHistory.Continuity.Should()
            .Be(CdcSourceHistoryContinuity.Healthy);
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Continuity that cannot be proved is absent evidence rather than a healthy source, so the
    /// connector is left exactly as it is: a stopped or failed connector stays stopped.
    /// </summary>
    [Test]
    public async Task It_leaves_the_connector_stopped_when_continuity_is_unknown()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding(
            CdcProvider.SqlServer
        );
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Unreadable;
        harness.ConnectorStatus = CdcSetupControllerHarness.RunningConnector(
            connectorState: "STOPPED",
            taskState: "STOPPED"
        );

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().NotBe(CdcReadiness.Ready);
        Given_CdcSetupControllerStatus
            .Target(status)
            .SourceHistory.Continuity.Should()
            .Be(CdcSourceHistoryContinuity.Unknown);
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_refuses_to_restart_a_binding_whose_source_history_is_lost()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding(
            CdcProvider.SqlServer
        );
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A proved loss whose latch never became durable still refuses the restart. The classifier raises
    /// an incident candidate only from a loss it proved, so the continuity this collection reports is
    /// lost whenever a latch was attempted at all - and the restart gate admits only affirmative
    /// continuity, which is what keeps the connector from resuming against a source it cannot resume
    /// from while the incident is missing from the binding record.
    /// </summary>
    [Test]
    public async Task It_refuses_a_restart_while_the_source_history_latch_is_not_durable()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding(
            CdcProvider.SqlServer
        );
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.LatchResult = CdcSetupControllerHarness.StateStoreUnavailable();
        harness.ConnectorStatus = CdcSetupControllerHarness.RunningConnector(
            connectorState: "STOPPED",
            taskState: "STOPPED"
        );

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().Be(CdcReadiness.NotReady);
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.ResumeConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "statusSourceHistoryLatchNotDurable");
    }

    /// <summary>
    /// A restart never resets the connector's committed position and never re-snapshots the existing
    /// public topic: a current-state snapshot cannot emit tombstones for documents deleted before it,
    /// so it would leave stale state in that topic's consumers.
    /// </summary>
    [Test]
    public async Task It_never_deletes_the_committed_offsets_or_re_registers_the_connector()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        await harness.RestartAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Connect.DeleteConnectorOffsetsAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Connect.PutConnectorConfigAsync(
                    A<string>._,
                    A<IReadOnlyDictionary<string, string>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Kafka Connect's STOPPED is a worker-owned target state that a restart does not clear — a stopped
    /// connector has no tasks to re-create — so a fenced connector is resumed instead. Without this the
    /// verb could only re-run a connector that was already running, and a connector fenced by a
    /// superseded source replacement could never be started again through any cdc verb.
    /// </summary>
    [TestCase("STOPPED")]
    [TestCase("PAUSED")]
    public async Task It_resumes_a_fenced_connector_rather_than_restarting_it(string connectorState)
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.ConnectorStatus = CdcSetupControllerHarness.RunningConnector(
            connectorState: connectorState,
            taskState: connectorState
        );

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        Given_CdcSetupControllerStatus
            .Target(status)
            .SourceHistory.Continuity.Should()
            .Be(CdcSourceHistoryContinuity.Healthy);
        A.CallTo(() => harness.Connect.ResumeConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A running connector is restarted, not resumed: resume clears a target state it is not in, and
    /// restarting its tasks is what the verb is for.
    /// </summary>
    [Test]
    public async Task It_restarts_a_running_connector_rather_than_resuming_it()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        await harness.RestartAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Connect.ResumeConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A request the worker refused is reported on the connector runtime. The state read back afterwards
    /// cannot carry it: a connector that is still not running reads identically whether the worker
    /// applied the request and it failed anyway or never accepted it, and only the second is worth
    /// reissuing.
    /// </summary>
    [Test]
    public async Task It_reports_a_restart_the_worker_did_not_apply()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.Restart = new(CdcConnectOutcome.Conflict, new(409, "rebalance in progress", true));

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        CdcDiagnostic diagnostic = Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .ContainSingle(candidate => candidate.Code == "restartNotApplied")
            .Subject;
        diagnostic.Component.Should().Be(CdcDiagnosticComponent.ConnectorRuntime);
        diagnostic.Observed.Should().Be(nameof(CdcConnectOutcome.Conflict));
        diagnostic.Retryable.Should().BeTrue();
        // The binding was read, so it stays known: the connector is what the request did not reach.
        Given_CdcSetupControllerStatus
            .Target(status)
            .Binding.State.Should()
            .NotBe(CdcComponentState.Unknown);
    }

    /// <summary>
    /// A resume the worker refused is reported the same way, so the fenced-connector path is not the one
    /// that silently drops the worker's answer.
    /// </summary>
    [Test]
    public async Task It_reports_a_resume_the_worker_did_not_apply()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.ConnectorStatus = CdcSetupControllerHarness.RunningConnector(
            connectorState: "STOPPED",
            taskState: "STOPPED"
        );
        harness.Resume = new(CdcConnectOutcome.NotFound, new(404, "no such connector", false));

        CdcStatus status = await harness.RestartAsync();

        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .ContainSingle(candidate =>
                candidate.Code == "restartNotApplied"
                && candidate.Observed == nameof(CdcConnectOutcome.NotFound)
            );
    }

    /// <summary>
    /// An applied request adds no diagnostic of its own, so the absence of one is evidence the worker
    /// accepted it rather than evidence that nothing was checked.
    /// </summary>
    [Test]
    public async Task It_reports_no_unapplied_diagnostic_when_the_worker_accepted_the_restart()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        CdcStatus status = await harness.RestartAsync();

        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .NotContain(candidate => candidate.Code == "restartNotApplied");
    }

    /// <summary>
    /// Source-history continuity is not the only thing proved before a connector is put back to work.
    /// A restart re-derives none of the artifacts the connector publishes through, so a resume issued
    /// over a nonconforming one starts publishing through it immediately — and the unhealthy status the
    /// verb would compose afterwards cannot recall what was already produced. These are the same four
    /// gates the enablement sequence applies before it will register a connector at all.
    /// </summary>
    /// <remarks>
    /// The component is not asserted here, unlike the three cases below. The only provider-setup
    /// nonconformance this harness can express - a validate-only pass reporting that it created
    /// something - also leaves the provider history unreadable, so the continuity gate reaches it
    /// first. Both gates refuse and neither issues a request, which is the property that matters; which
    /// one reports it is not.
    /// </remarks>
    [Test]
    public async Task It_issues_no_connector_request_against_a_nonconforming_provider_setup()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.ValidateOnlyProviderSetupOutcome = Ddl.CdcProviderSetupOutcome.CreatedOrMatched;

        await AssertRestartWasNotAttempted(harness);
    }

    [Test]
    public async Task It_issues_no_connector_request_against_a_nonconforming_shared_offset_store()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.OffsetStorePolicy = Given_CdcSetupControllerStatus.NonconformingOffsetStore();

        await AssertRestartWasNotAttempted(harness, CdcDiagnosticComponent.ConnectOffsetStore);
    }

    [Test]
    public async Task It_issues_no_connector_request_against_a_nonconforming_binding_kafka_policy()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.KafkaPolicyState = CdcKafkaPolicyState.Invalid;

        await AssertRestartWasNotAttempted(harness, CdcDiagnosticComponent.KafkaPolicy);
    }

    /// <summary>
    /// The connector configuration the worker is actually holding, which is the one a resume would put
    /// back to work. A transform or converter the binding no longer matches is exactly the case a
    /// restart must not paper over.
    /// </summary>
    [Test]
    public async Task It_issues_no_connector_request_against_a_mismatched_connector_configuration()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.ConnectorConfigReadBack = CdcSetupControllerHarness.RenderedConnectorConfig(drift: config =>
            config["transforms"] = "somethingElse"
        );

        await AssertRestartWasNotAttempted(harness, CdcDiagnosticComponent.ConnectorConfig);
    }

    /// <summary>
    /// The prerequisite gate reports the component that refused and leaves the deployment untouched:
    /// no restart, no resume, and the same <c>restartNotAttempted</c> code the continuity gate uses,
    /// which the dispatcher maps onto a rejection rather than a success.
    /// </summary>
    private static async Task AssertRestartWasNotAttempted(
        CdcSetupControllerHarness harness,
        CdcDiagnosticComponent? expectedComponent = null
    )
    {
        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        status.Readiness.Should().NotBe(CdcReadiness.Ready);

        CdcDiagnostic notAttempted = Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == CdcRestartDiagnosticCodes.NotAttempted)
            .Subject;
        if (expectedComponent is { } component)
        {
            notAttempted.Component.Should().Be(component);
        }

        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.ResumeConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The status a restart reports describes the connector the restart left behind, so it is read back
    /// after the worker answered rather than reported from the observation that preceded it.
    /// </summary>
    [Test]
    public async Task It_reports_the_connector_runtime_observed_after_the_restart()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .Invokes(() =>
                harness.ConnectorStatus = CdcSetupControllerHarness.RunningConnector(
                    connectorState: "RUNNING",
                    taskState: "UNASSIGNED"
                )
            );

        CdcStatus status = await harness.RestartAsync();

        using var _ = new AssertionScope();
        Given_CdcSetupControllerStatus
            .Target(status)
            .ConnectorRuntime.Category.Should()
            .Be(CdcBlockingCategory.ConnectorNotRunning);
        A.CallTo(() => harness.Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly()
            .Then(
                A.CallTo(() => harness.Connect.GetConnectorStatusAsync(A<string>._, A<CancellationToken>._))
                    .MustHaveHappened()
            );
    }
}
