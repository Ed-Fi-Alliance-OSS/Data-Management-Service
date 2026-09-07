// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The planned fence acts on the connector the durable binding record names, through Kafka Connect,
/// and depends on nothing else. Every observation a status collects is a report about the target
/// rather than permission to stop its connector — and each of those observations failing is a moment
/// at which a connector needs stopping more, not less.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerStatus")]
public class Given_CdcSetupControllerPlannedFence
{
    /// <summary>
    /// The shape a deployment shutdown issues: the target identity alone, because the caller taking
    /// the stack down may hold nothing else.
    /// </summary>
    [Test]
    public async Task It_fences_from_the_binding_record_alone()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        CdcStatus status = await harness.StopAsync();

        using AssertionScope assertions = new();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._)).MustNotHaveHappened();
        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.Code == "stopNotAttempted" || diagnostic.Code == "stopNotApplied"
            );
    }

    /// <summary>
    /// The instance database is what the observing verbs collect against, never what the fence acts
    /// through. A deployment whose database is already gone still gets its connector fenced.
    /// </summary>
    [Test]
    public async Task It_fences_though_the_instance_database_cannot_be_reached()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("the instance database is unreachable"));

        CdcStatus status = await harness.StopAsync();

        using AssertionScope assertions = new();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Code == "stopNotApplied");
    }

    /// <summary>
    /// The connector template is composed so a read-back can be compared against it. A fence compares
    /// nothing: it names the connector from the binding record and asks the worker to stop it.
    /// </summary>
    [Test]
    public async Task It_fences_though_the_connector_template_inputs_cannot_be_composed()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.MalformedConnectorTemplateInputs = true;

        CdcStatus status = await harness.StopAsync();

        using AssertionScope assertions = new();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Code == "stopNotApplied");
    }
}

/// <summary>
/// Containing a loss the binding record already latches is attempted from that record alone. Latching
/// is idempotent and raises no second incident candidate, and restart declines a lost continuity, so a
/// status that returns before the classifier is the only thing left that can re-attempt a stop the
/// worker refused.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerStatus")]
public class Given_CdcSetupControllerLatchedIncidentContainment
{
    /// <summary>
    /// Composing the connector template inputs is an observation exit like the unreachable database is,
    /// and it holds the same two things containment needs: the durable incident, and the connector name
    /// recovered from the record. Without this, a deployment whose template inputs went malformed would
    /// leave the connector publishing against a lost source indefinitely - every later status returns
    /// at that same line, and the fence that follows a classified continuity is never reached.
    /// </summary>
    [Test]
    public async Task It_retries_containment_when_the_connector_template_inputs_are_invalid()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding(
            CdcProvider.SqlServer
        );
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.Stop = new(CdcConnectOutcome.Unavailable, new(503, "worker unavailable", true));

        // The poll that proves and latches the loss. Its fence is refused, so the connector is still
        // running and nothing later raises a second incident candidate.
        await harness.StatusAsync();

        // The template inputs are now malformed, so this poll returns before the classifier.
        harness.MalformedConnectorTemplateInputs = true;

        CdcStatus status = await harness.StatusAsync();

        using AssertionScope assertions = new();
        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "statusConnectorInputsInvalid");
        CdcDiagnostic fence = Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "statusIncidentFenceNotApplied")
            .Subject;
        fence.Observed.Should().Be(nameof(CdcConnectOutcome.Unavailable));

        // Asked again on the second poll rather than only on the one that proved the loss.
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    /// <summary>
    /// The same obligation, reached by a caller that never entered a collection at all. An observing
    /// verb whose instance database, Configuration Service, or schema inputs cannot be resolved is
    /// refused before this control plane runs anything, and the loss it would otherwise have contained
    /// is one nothing else re-attempts.
    /// </summary>
    [Test]
    public async Task It_contains_a_latched_incident_for_a_caller_that_collected_no_observation()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding(
            CdcProvider.SqlServer
        );
        harness.SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing;
        harness.Stop = new(CdcConnectOutcome.Unavailable, new(503, "worker unavailable", true));

        // The poll that proves and latches the loss. Its fence is refused, so the connector is still
        // running and nothing later raises a second incident candidate.
        await harness.StatusAsync();

        IReadOnlyList<CdcDiagnostic> containment = await harness.ContainLatchedIncidentAsync();

        using AssertionScope assertions = new();
        containment.Should().ContainSingle(diagnostic => diagnostic.Code == "statusIncidentFenceNotApplied");
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();

        // The instance database is what the refused caller could not reach, and the containment does
        // not need it: the incident and the connector name both came from the binding record.
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A binding that latches nothing has proved no loss, so the containment leaves its connector
    /// alone. This is not a planned fence, and fencing a healthy stream is what a caller asks for with
    /// the verb that does.
    /// </summary>
    [Test]
    public async Task It_fences_nothing_for_a_binding_that_latches_no_incident()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();

        IReadOnlyList<CdcDiagnostic> containment = await harness.ContainLatchedIncidentAsync();

        using AssertionScope assertions = new();
        containment.Should().BeEmpty();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The containment above is scoped to a binding that already carries a latched incident. A status
    /// blocked at the same exit with nothing latched has proved no loss, and stopping a connector on
    /// that evidence would fence a healthy stream.
    /// </summary>
    [Test]
    public async Task It_fences_nothing_when_the_template_inputs_are_invalid_and_no_incident_is_latched()
    {
        CdcSetupControllerHarness harness = Given_CdcSetupControllerStatus.EnabledBinding();
        harness.MalformedConnectorTemplateInputs = true;

        CdcStatus status = await harness.StatusAsync();

        using AssertionScope assertions = new();
        Given_CdcSetupControllerStatus
            .Target(status)
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "statusConnectorInputsInvalid");
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}

/// <summary>
/// A polling step's budget bounds the real time it may spend, including the very first observation. A
/// step that spends the whole budget without a single answer has observed nothing, and the one thing
/// it may not report is the satisfied state it was waiting for.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerStatus")]
public class Given_CdcSetupControllerPollingBudget
{
    /// <summary>
    /// The first read used to be issued under the caller's token alone, so a status endpoint that
    /// accepted the request and never answered left the step waiting for as long as its own transport
    /// allowed - the configured budget bounded nothing at all on that path.
    /// </summary>
    [Test]
    public async Task It_reports_unavailable_projection_evidence_when_the_budget_expires_before_any_read()
    {
        CdcSetupControllerHarness harness = new();
        harness.Timeouts.ProjectionCaughtUp = TimeSpan.FromSeconds(3);
        harness.ProjectionReadsBlockAfterPreflight = true;

        CdcAdmission admission = await harness.EnableAsync();

        using AssertionScope assertions = new();
        admission.Steps.FirstProjectionCaughtUp.State.Should().NotBe(CdcComponentState.Satisfied);
        admission
            .Steps.FirstProjectionCaughtUp.Category.Should()
            .Be(
                CdcBlockingCategory.StatusObservationUnavailable,
                "a step that read nothing reports absent evidence rather than a backlog it never observed"
            );
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
    }

    /// <summary>
    /// An answer that arrives outside the budget is not the answer the step was allowed to wait for,
    /// and the fallback may not recover it either. The barrier is observed FROM a committed offset, and
    /// the offset the poll body mapped came from the very read the deadline rejected - so composing the
    /// fallback out of it would report a barrier reached on evidence the step had already refused.
    /// </summary>
    [Test]
    public async Task It_reports_an_unreached_barrier_when_its_only_observation_returned_outside_the_budget()
    {
        CdcSetupControllerHarness harness = new() { BarrierObservationsOverrunTheBudget = true };

        CdcAdmission admission = await harness.EnableAsync();

        using AssertionScope assertions = new();
        admission
            .Steps.ProviderBarrier.State.Should()
            .NotBe(
                CdcComponentState.Satisfied,
                "a late observation is not the one the step was allowed to wait for"
            );
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
    }

    /// <summary>
    /// The step's own budget and the caller's cancellation are linked but never confused. Only a
    /// cancellation the caller did not ask for is turned into step evidence; one the caller did ask for
    /// is theirs to receive.
    /// </summary>
    [Test]
    public async Task It_propagates_caller_cancellation_rather_than_composing_evidence_from_it()
    {
        CdcSetupControllerHarness harness = new();
        harness.Timeouts.ProjectionCaughtUp = TimeSpan.FromMinutes(5);
        harness.ProjectionReadsBlockAfterPreflight = true;

        using CancellationTokenSource caller = new();
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        Func<Task> enable = () => harness.EnableAsync(caller.Token);

        await enable.Should().ThrowAsync<OperationCanceledException>();
    }
}
