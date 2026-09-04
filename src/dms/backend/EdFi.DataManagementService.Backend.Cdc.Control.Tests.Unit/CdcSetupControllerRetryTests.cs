// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FakeItEasy.Configuration;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The acceptance-evidence matrix for the initial enablement sequence. What the durable state and one
/// read-only eligibility observation say together decides whether the sequence proceeds, and a
/// rejection provisions nothing: no binding, no provider artifact, no topic, and no ACL.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerRetry")]
public class Given_CdcSetupControllerInitialEnable
{
    [Test]
    public async Task It_retries_the_guarded_activation_for_an_exact_binding_that_is_still_disabled()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding()),
            Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Disabled"),
        };

        await harness.EnableAsync();

        using var _ = new AssertionScope();
        BindingExactMatched(harness).MustHaveHappenedOnceExactly();
        BindingCreated(harness).MustNotHaveHappened();
        Activation(harness).MustHaveHappenedOnceExactly();
        ProviderSetup(harness).MustHaveHappened();
    }

    [Test]
    public async Task It_resumes_provider_topic_and_connector_setup_for_a_binding_already_tracking()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding()),
            Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Tracking"),
        };

        await harness.EnableAsync();

        using var _ = new AssertionScope();

        // A committed activation is recognized from the classifier's resume decision, and the guarded
        // command is not run a second time against a target that is already tracking.
        Activation(harness).MustNotHaveHappened();
        ProviderSetup(harness).MustHaveHappened();
        OffsetStore(harness).MustHaveHappenedOnceExactly();
        KafkaPolicy(harness).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_rejects_a_tracking_target_that_has_no_binding()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.Missing(),
            Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Tracking"),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Rejection(admission)
            .Observed.Should()
            .StartWith(nameof(CdcRetryClassification.RejectUnboundTracking));
        ProvisionedNothing(harness);
    }

    [Test]
    public async Task It_rejects_a_published_cache_ahead_latch()
    {
        CdcSetupControllerHarness harness = new()
        {
            Eligibility = CdcSetupControllerHarness.Reading(cacheAheadRecoveryRequired: true),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Rejection(admission)
            .Observed.Should()
            .StartWith(nameof(CdcRetryClassification.RejectCacheAheadLatch));
        ProvisionedNothing(harness);
    }

    [TestCase("Resetting", nameof(CdcRetryClassification.RejectResettingLifecycle))]
    [TestCase("Rebuilding", nameof(CdcRetryClassification.RejectRebuildingLifecycle))]
    public async Task It_rejects_a_lifecycle_that_is_not_an_initial_enable(
        string lifecycleStateToken,
        string expectedClassification
    )
    {
        CdcSetupControllerHarness harness = new()
        {
            Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: lifecycleStateToken),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Rejection(admission).Observed.Should().StartWith(expectedClassification);
        ProvisionedNothing(harness);
    }

    [Test]
    public async Task It_rejects_a_binding_that_does_not_exact_match_the_target()
    {
        CdcSetupControllerHarness harness = new() { BindingRead = CdcSetupControllerHarness.Mismatched() };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Rejection(admission)
            .Observed.Should()
            .StartWith(nameof(CdcRetryClassification.RejectBindingMismatch));
        ProvisionedNothing(harness);
    }

    [TestCase("canonical")]
    [TestCase("cache")]
    [TestCase("work")]
    public async Task It_rejects_rows_that_exist_before_capture_begins(string rowSet)
    {
        CdcSetupControllerHarness harness = new()
        {
            Eligibility = CdcSetupControllerHarness.Reading(
                canonicalRowsPresent: rowSet == "canonical",
                cacheRowsPresent: rowSet == "cache",
                workRowsPresent: rowSet == "work"
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Rejection(admission).Observed.Should().StartWith(nameof(CdcRetryClassification.RejectUnexpectedRows));
        ProvisionedNothing(harness);
    }

    /// <summary>
    /// The first attempt at an already-provisioned database is the case the whole gate exists for: it
    /// is rejected before a binding record, a provider artifact, a topic, or an ACL exists.
    /// </summary>
    [Test]
    public async Task It_rejects_an_already_provisioned_database_on_an_unbound_first_attempt()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.Missing(),
            Eligibility = CdcSetupControllerHarness.Reading(
                lifecycleStateToken: "Tracking",
                canonicalRowsPresent: true,
                cacheRowsPresent: true
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        ProvisionedNothing(harness);
        BindingExactMatched(harness).MustNotHaveHappened();
    }

    [Test]
    public async Task It_stops_when_the_durable_binding_state_cannot_be_read()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.StateStoreUnavailable(),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableBindingStateUnavailable").Should().NotBeNull();
        ProvisionedNothing(harness);

        // The eligibility probe reads the instance database, so it is not run against a target whose
        // durable state is unknown.
        A.CallTo(() => harness.Probe.ProbeAsync(A<CdcEligibilityProbeRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_stops_when_the_instance_database_could_not_be_read()
    {
        CdcSetupControllerHarness harness = new()
        {
            Eligibility = CdcSetupControllerHarness.UnreadableDatabase(),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        ProvisionedNothing(harness);
    }

    [Test]
    public async Task It_stops_when_the_operator_evidence_is_absent()
    {
        CdcSetupControllerHarness harness = new();
        CdcEnableRequest request = CdcSetupControllerHarness.Request() with
        {
            ProvisioningEvidence = new(CdcSetupControllerHarness.SetupControllerRunId, null, null),
        };

        CdcAdmission admission = await harness.Controller().EnableAsync(request, CancellationToken.None);

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableProvisioningEvidenceRefused").Should().NotBeNull();
        ProvisionedNothing(harness);
    }

    [Test]
    public async Task It_stops_when_the_target_is_not_a_configured_projection_target()
    {
        CdcSetupControllerHarness harness = new() { ConfiguredProjectionTargets = [("", 2)] };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableProjectionTargetUnproven").Should().NotBeNull();
        ProvisionedNothing(harness);
        A.CallTo(() => harness.Bindings.ReadBindingAsync(A<CdcBindingIdentity>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_makes_the_binding_durable_before_any_external_artifact_exists()
    {
        CdcSetupControllerHarness harness = new();

        await harness.EnableAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly()
            .Then(
                A.CallTo(() =>
                        harness.Activation.ExecuteAsync(
                            A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(ProviderSetup(harness).MustHaveHappened())
            .Then(OffsetStore(harness).MustHaveHappenedOnceExactly())
            .Then(KafkaPolicy(harness).MustHaveHappenedOnceExactly());
    }

    [Test]
    public async Task It_binds_the_physical_source_the_eligibility_read_identified()
    {
        CdcSetupControllerHarness harness = new();
        CdcBinding? created = null;
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .Invokes((CdcBinding binding, CancellationToken _) => created = binding);

        await harness.EnableAsync();

        created.Should().Be(CdcSetupControllerHarness.Binding());
    }

    [Test]
    public async Task It_stops_when_the_guarded_activation_does_not_complete()
    {
        CdcSetupControllerHarness harness = new()
        {
            ActivationResult = CdcSetupControllerHarness.Activated(
                DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
                DocumentCacheAdministrativeCommandClassification.MissingOrInvalidInventory
            ),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableGuardedActivationIncomplete").Should().NotBeNull();
        ProviderSetup(harness).MustNotHaveHappened();
        OffsetStore(harness).MustNotHaveHappened();
        KafkaPolicy(harness).MustNotHaveHappened();
    }

    [Test]
    public async Task It_stops_when_the_provider_capture_artifacts_could_not_be_provisioned()
    {
        CdcSetupControllerHarness harness = new()
        {
            ProviderSetupOutcome = Ddl.CdcProviderSetupOutcome.Failed,
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableProviderSetupFailed").Should().NotBeNull();
        OffsetStore(harness).MustNotHaveHappened();
        KafkaPolicy(harness).MustNotHaveHappened();
    }

    /// <summary>
    /// The provider-setup step owns a budget, and spending it is a failed step rather than a wait with
    /// nothing above it: the CLI adds no wall clock of its own, so a provider pass that never answers
    /// would otherwise hold the enablement open indefinitely.
    /// </summary>
    [Test]
    public async Task It_stops_when_the_provider_capture_artifacts_outlive_the_provider_setup_budget()
    {
        CdcSetupControllerHarness harness = new();
        harness.Timeouts.ProviderSetup = TimeSpan.FromMilliseconds(50);
        // A pass that never answers on its own: only the step's budget ends this.
        Func<CdcProviderSetupRequest, CancellationToken, Task<CdcProviderSetupResult>> neverAnswers = async (
            _,
            cancellationToken
        ) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The provider-setup budget must end this wait.");
        };
        ProviderSetup(harness).ReturnsLazily(neverAnswers);

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableProviderSetupFailed").Should().NotBeNull();
        OffsetStore(harness).MustNotHaveHappened();
        KafkaPolicy(harness).MustNotHaveHappened();

        // The refusal carries the provider's own account of the step, not just its outcome: a refused
        // grant, an absent principal, and a spent budget are all one outcome, and only the diagnostics
        // tell them apart.
        admission.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "providerSetupTimedOut");
    }

    /// <summary>
    /// The validate-only pass is the evidence every later status check reads the provider artifacts
    /// through, so a pass that is not an exact match ends the sequence before any Kafka or Connect side
    /// effect. A connector registered against nonconforming artifacts would already be capturing from
    /// them by the time the final evaluation rejected the state.
    /// </summary>
    [TestCase(Ddl.CdcProviderSetupOutcome.Failed)]
    [TestCase(Ddl.CdcProviderSetupOutcome.CreatedOrMatched)]
    public async Task It_stops_when_the_provider_artifacts_do_not_validate_after_they_are_created(
        Ddl.CdcProviderSetupOutcome validateOnlyOutcome
    )
    {
        CdcSetupControllerHarness harness = new() { ValidateOnlyProviderSetupOutcome = validateOnlyOutcome };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        NotAdmitted(admission);
        Diagnostic(admission, "enableProviderSetupNotSatisfied").Should().NotBeNull();

        // Nothing was provisioned or registered against artifacts that did not validate.
        OffsetStore(harness).MustNotHaveHappened();
        KafkaPolicy(harness).MustNotHaveHappened();
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
    public async Task It_provisions_the_shared_offset_store_before_the_binding_topics()
    {
        CdcSetupControllerHarness harness = new();

        await harness.EnableAsync();

        OffsetStore(harness)
            .MustHaveHappenedOnceExactly()
            .Then(KafkaPolicy(harness).MustHaveHappenedOnceExactly());
    }

    /// <summary>
    /// A retry finds its own record in the deployment listing, because the interrupted attempt made it
    /// durable before it stopped. That is the generation being enabled, not a second one, so the rule
    /// against another live generation of the target does not reach it.
    /// </summary>
    [Test]
    public async Task It_retries_when_the_only_bound_generation_is_the_one_being_enabled()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding()),
            BindingListing = CdcSetupControllerHarness.ListedBindings(CdcSetupControllerHarness.Binding()),
            Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Disabled"),
        };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        BindingExactMatched(harness).MustHaveHappenedOnceExactly();
    }

    private static void NotAdmitted(CdcAdmission admission) =>
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);

    /// <summary>Nothing outside the control plane's own state store was created.</summary>
    private static void ProvisionedNothing(CdcSetupControllerHarness harness)
    {
        BindingCreated(harness).MustNotHaveHappened();
        Activation(harness).MustNotHaveHappened();
        ProviderSetup(harness).MustNotHaveHappened();
        OffsetStore(harness).MustNotHaveHappened();
        KafkaPolicy(harness).MustNotHaveHappened();
    }

    private static IReturnValueArgumentValidationConfiguration<
        Task<CdcBindingLifecycleResult>
    > BindingCreated(CdcSetupControllerHarness harness) =>
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._));

    private static IReturnValueArgumentValidationConfiguration<
        Task<CdcBindingLifecycleResult>
    > BindingExactMatched(CdcSetupControllerHarness harness) =>
        A.CallTo(() => harness.Bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._));

    private static IReturnValueArgumentValidationConfiguration<
        Task<DocumentCacheAdministrativeCommandResult>
    > Activation(CdcSetupControllerHarness harness) =>
        A.CallTo(() =>
            harness.Activation.ExecuteAsync(
                A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                A<CancellationToken>._
            )
        );

    private static IReturnValueArgumentValidationConfiguration<Task<CdcProviderSetupResult>> ProviderSetup(
        CdcSetupControllerHarness harness
    ) =>
        A.CallTo(() =>
            harness.ProviderSetup.SetupAsync(A<CdcProviderSetupRequest>._, A<CancellationToken>._)
        );

    private static IReturnValueArgumentValidationConfiguration<
        Task<CdcConnectOffsetStorePolicyObservation>
    > OffsetStore(CdcSetupControllerHarness harness) =>
        A.CallTo(() =>
            harness.Kafka.EnsureConnectOffsetStoreAsync(A<CdcObservationContext>._, A<CancellationToken>._)
        );

    private static IReturnValueArgumentValidationConfiguration<Task<CdcKafkaPolicyObservation>> KafkaPolicy(
        CdcSetupControllerHarness harness
    ) =>
        A.CallTo(() =>
            harness.Kafka.EnsureBindingKafkaPolicyAsync(
                A<CdcObservationContext>._,
                A<CdcArtifactInventory>._,
                A<CancellationToken>._
            )
        );

    private static CdcDiagnostic Rejection(CdcAdmission admission) =>
        Diagnostic(admission, "enableEligibilityRejected");

    private static CdcDiagnostic Diagnostic(CdcAdmission admission, string code) =>
        admission.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == code).Subject;
}
