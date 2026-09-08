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
/// Guarded source replacement: the outgoing generation's connector is fenced, and the source that
/// replaces it is enabled under a new binding generation whose connector, topics, and provider
/// artifacts are all its own. Every refusal is decided before the fence, because a target that cannot
/// be replaced must not be left with its publication stopped.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerReplaceSource")]
public class Given_CdcSetupControllerReplaceSource
{
    private const long PreviousGeneration = CdcControlTemplateTestData.BindingGeneration - 1;

    [Test]
    public async Task It_fences_the_outgoing_connector_and_enables_the_replacing_generation()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        A.CallTo(() =>
                harness.Connect.StopConnectorAsync(
                    CdcSetupControllerHarness.InventoryFor(PreviousGeneration).ConnectorName,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly()
            .Then(
                A.CallTo(() =>
                        harness.Connect.PutConnectorConfigAsync(
                            CdcSetupControllerHarness.Inventory().ConnectorName,
                            A<IReadOnlyDictionary<string, string>>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            );
    }

    /// <summary>
    /// Stopping fences the outgoing connector from the source it is being replaced from while leaving
    /// its configuration and its committed offsets for the retirement that removes them in order.
    /// </summary>
    [Test]
    public async Task It_stops_the_outgoing_connector_rather_than_deleting_its_configuration()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();

        await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        A.CallTo(() => harness.Connect.DeleteConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => harness.Connect.DeleteConnectorOffsetsAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.DeleteBindingArtifactsAsync(A<CdcArtifactInventory>._, A<CancellationToken>._)
            )
            // The outgoing generation is retained until it is explicitly retired.
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The replacing generation provisions its own connector, topics, and provider artifacts. Every
    /// governed name carries the generation, so the two name sets are disjoint.
    /// </summary>
    [Test]
    public async Task It_reuses_no_governed_artifact_of_the_generation_it_replaces()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();

        await harness.ReplaceSourceAsync();

        CdcArtifactInventory previous = CdcSetupControllerHarness.InventoryFor(PreviousGeneration);
        CdcArtifactInventory provisioned = harness.ProvisionedInventory!;

        using var _ = new AssertionScope();
        provisioned.Generation.Should().Be(CdcControlTemplateTestData.BindingGeneration);
        provisioned
            .GovernedArtifacts.Select(artifact => artifact.Name)
            .Should()
            .NotIntersectWith(previous.GovernedArtifacts.Select(artifact => artifact.Name));
        provisioned.ConnectorName.Should().NotBe(previous.ConnectorName);
        provisioned.TopicName.Should().NotBe(previous.TopicName);
        provisioned.ProgressTopicName.Should().NotBe(previous.ProgressTopicName);
    }

    /// <summary>
    /// A published cache-ahead recovery latch is a projection state a replacement cannot clear, so the
    /// outgoing connector keeps publishing rather than being fenced for a replacement that cannot run.
    /// </summary>
    [Test]
    public async Task It_refuses_a_published_cache_ahead_latch_without_fencing_the_outgoing_connector()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Eligibility = CdcSetupControllerHarness.Reading(cacheAheadRecoveryRequired: true);

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// A cache-ahead latch that could not be read is not a clear one, and absent evidence never opens a
    /// replacement.
    /// </summary>
    [Test]
    public async Task It_refuses_a_cache_ahead_latch_it_could_not_read()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Eligibility = CdcSetupControllerHarness.Reading(cacheAheadRecoveryRequired: null);

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// The replacing generation's enablement proves the target is one the DMS projector is configured
    /// to project. That proof reads configuration and nothing else, so it is settled before the fence:
    /// deciding it afterwards would stop the outgoing generation for a replacement that then refused.
    /// </summary>
    [Test]
    public async Task It_refuses_an_unconfigured_projection_target_without_fencing_the_outgoing_connector()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.ConfiguredProjectionTargets = [];

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// Same reason for the projector's own status endpoint: the replacing generation's caught-up
    /// evidence is read from it, so a replacement that could never collect that evidence refuses while
    /// the outgoing generation is still publishing.
    /// </summary>
    [Test]
    public async Task It_refuses_an_unreadable_projection_status_without_fencing_the_outgoing_connector()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.ProjectionStatus = CdcSetupControllerHarness.StatusEndpointFailure(
            CdcProjectionStatusReadOutcome.EndpointNotMapped
        );

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// A replacing source that is already tracking is one the replacing generation's enablement cannot
    /// bind: its guarded activation admits a new empty target, and the pre-binding classifier rejects an
    /// unbound tracking lifecycle outright. That is the shape an operator who ran the replacement before
    /// repointing the data store presents — the connection still resolves to the database being replaced
    /// — so it is refused while the outgoing generation is still publishing.
    /// </summary>
    [Test]
    public async Task It_refuses_a_replacing_source_that_is_already_tracking_without_fencing_the_outgoing_connector()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Tracking");

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// Same for pre-capture rows the replacing generation would capture over. The classifier reads them
    /// from the observation the replacement has already taken, so this too is settled before the fence.
    /// </summary>
    [Test]
    public async Task It_refuses_a_replacing_source_holding_pre_capture_rows_without_fencing_the_outgoing_connector()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Eligibility = CdcSetupControllerHarness.Reading(canonicalRowsPresent: true);

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    [Test]
    public async Task It_refuses_a_generation_whose_source_history_loss_is_terminal()
    {
        CdcBinding previousBinding = CdcSetupControllerHarness.PreviousGenerationBinding();
        CdcSetupControllerHarness harness = new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.IncidentLatched(
                previousBinding,
                SourceHistoryLoss(previousBinding)
            ),
        };

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// Source replacement is supported only for a source this deployment enabled through the
    /// new-database path: without that generation's durable record there is nothing being replaced, and
    /// a replacement is not a first-time enablement path.
    /// </summary>
    [Test]
    public async Task It_refuses_when_the_generation_it_replaces_has_no_durable_record()
    {
        CdcSetupControllerHarness harness = new();

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        AssertRefusedBeforeFencing(harness, admission);
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Every governed artifact name carries the generation, so a generation that does not advance names
    /// the artifacts of the generation it replaces.
    /// </summary>
    [Test]
    public async Task It_refuses_a_generation_that_does_not_advance()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();

        CdcAdmission admission = await harness.ReplaceSourceAsync(
            CdcControlTemplateTestData.BindingGeneration
        );

        AssertRefusedBeforeFencing(harness, admission);
    }

    /// <summary>
    /// The fence is the cutover barrier. A connector that could not be stopped may still publish from
    /// the source being replaced, so nothing of the replacing generation is provisioned.
    /// </summary>
    /// <remarks>
    /// Reported retryable, unlike every other refusal this verb raises. The worker applies a stop
    /// asynchronously, and this outcome covers both a stop it refused outright and a stop it accepted
    /// that had not settled when the wait's budget ran out - so the outgoing generation may already
    /// have stopped publishing, and reissuing is what observes which happened. The refusals that name
    /// a fact the operator must change first are pinned non-retryable below.
    /// </remarks>
    [Test]
    public async Task It_refuses_retryably_when_the_outgoing_connector_could_not_be_fenced()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Stop = new(CdcConnectOutcome.Unavailable, new(503, "worker unavailable", true));

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission).Should().NotBeNull();
        Refusal(admission)!.Retryable.Should().BeTrue();
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
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
    /// The counterpart to the fence refusal above. A generation that does not advance past the one it
    /// replaces is a fact about the request, so no reissue of the same request can change the answer
    /// and the refusal says so.
    /// </summary>
    [Test]
    public async Task It_refuses_a_non_advancing_generation_without_offering_a_retry()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();

        // The configured generation itself, so the replacing generation does not advance past the one
        // it replaces and every governed artifact name would collide.
        CdcAdmission admission = await harness.ReplaceSourceAsync(
            previousGeneration: CdcControlTemplateTestData.BindingGeneration
        );

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission)!.Retryable.Should().BeFalse();
    }

    /// <summary>
    /// A connector the worker no longer holds is already fenced, and the replacement proceeds against
    /// the record that names it.
    /// </summary>
    [Test]
    public async Task It_treats_an_outgoing_connector_the_worker_does_not_hold_as_already_fenced()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Stop = new(CdcConnectOutcome.NotFound, new(404, "no such connector", false));

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
    }

    /// <summary>The record of the generation being replaced is never rewritten by the replacement.</summary>
    [Test]
    public async Task It_leaves_the_record_of_the_generation_it_replaces_untouched()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();

        await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        A.CallTo(() =>
                harness.Bindings.CreateBindingIfAbsentAsync(
                    A<CdcBinding>.That.Matches(binding => binding.Generation == PreviousGeneration),
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Bindings.DeleteStateAfterVerifiedCleanupAsync(
                    A<CdcCleanupProof>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A restore, rollback, or copied backup carries the replaced database's own
    /// <c>dms.DataStoreIdentity</c> row, so its fingerprint is the replaced source's until the identity
    /// is rotated. Binding a new generation to it would publish one physical source under two
    /// generations, so the replacement is refused - and refused before the fence, because nothing about
    /// the outgoing generation has to change for the answer to be known.
    /// </summary>
    [Test]
    public async Task It_refuses_a_replacing_source_whose_identity_was_never_rotated()
    {
        CdcSetupControllerHarness harness = new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.PreviousGenerationBinding(
                    physicalSourceFingerprint: CdcSetupControllerHarness.Fingerprint()
                )
            ),
        };

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        AssertRefusedBeforeFencing(harness, admission);
        Refusal(admission).Category.Should().Be(CdcDiagnosticCategory.SourceMismatch);
        Refusal(admission).Component.Should().Be(CdcDiagnosticComponent.ProviderSetup);
        Refusal(admission).Observed.Should().Be("retained");
        A.CallTo(() => harness.Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The generation a replacement replaces is still bound when the replacing one is enabled — the
    /// outgoing record and its artifacts are retained until an explicit retirement — so the enablement
    /// admits that one live generation. It is the one this replacement fenced, and the fence is what
    /// separates it from the second publisher a plain enable is refused for.
    /// </summary>
    [Test]
    public async Task It_enables_the_replacing_generation_while_the_generation_it_fenced_is_still_bound()
    {
        CdcSetupControllerHarness harness = new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.PreviousGenerationBinding()
            ),
            BindingListing = CdcSetupControllerHarness.ListedBindings(
                CdcSetupControllerHarness.PreviousGenerationBinding()
            ),
        };

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
    }

    /// <summary>
    /// Only the generation this replacement named and fenced. A third generation of the same target is
    /// still live and still publishing, and naming one previous generation says nothing about it. The
    /// rule is a read over the deployment's own bindings, so it is settled ahead of the cutover
    /// barrier: refusing it afterwards would stop the outgoing generation for a request that was never
    /// going to proceed.
    /// </summary>
    [Test]
    public async Task It_refuses_a_replacement_while_a_generation_it_did_not_fence_is_still_bound()
    {
        CdcSetupControllerHarness harness = new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.PreviousGenerationBinding()
            ),
            BindingListing = CdcSetupControllerHarness.ListedBindings(
                CdcSetupControllerHarness.PreviousGenerationBinding(),
                CdcSetupControllerHarness.PreviousGenerationBinding(PreviousGeneration - 1)
            ),
        };

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        admission
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "enableTargetGenerationAlreadyLive");
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
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
    /// The replacing generation's every governed name is new, so a topic already standing at one of
    /// them belongs to something this deployment has no binding record for. The enablement refuses that
    /// for an unbound attempt, and a replacement is unbound by construction - asked here, over reads,
    /// so the refusal does not arrive with the outgoing generation already stopped.
    /// </summary>
    [Test]
    public async Task It_refuses_a_replacement_whose_generation_already_has_a_governed_topic()
    {
        CdcSetupControllerHarness harness = new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.PreviousGenerationBinding()
            ),
            BindingListing = CdcSetupControllerHarness.ListedBindings(
                CdcSetupControllerHarness.PreviousGenerationBinding()
            ),
            GovernedTopicPresence = new(true, [CdcSetupControllerHarness.Inventory().TopicName]),
        };

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        admission
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "enableGovernedArtifactAlreadyExists");
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The binding identity carries no provider, so a record written by a control plane for the other
    /// engine is readable at these very coordinates. Refused before the artifact-name recovery and well
    /// before the cutover barrier: this operation's first change to the deployment is fencing that
    /// generation's connector, and a run that can neither validate nor retire the artifacts left behind
    /// must not stop it. Retirement and adoption refuse the same mismatch for the same reason.
    /// </summary>
    [Test]
    public async Task It_refuses_a_previous_generation_bound_under_another_provider_before_fencing_it()
    {
        CdcSetupControllerHarness harness = new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.PreviousGenerationBinding(provider: CdcProvider.SqlServer)
            ),
        };

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        AssertRefusedBeforeFencing(harness, admission);
        Refusal(admission).Category.Should().Be(CdcDiagnosticCategory.ProviderMismatch);
        Refusal(admission).Component.Should().Be(CdcDiagnosticComponent.Binding);
        Refusal(admission).Retryable.Should().BeFalse();
    }

    /// <summary>
    /// A replacement that made its binding durable and activated tracking before failing at a later
    /// step is a retry of that generation, not a first attempt at it. Classified as unbound it would be
    /// refused for the very tracking lifecycle it established — and a direct `cdc enable` cannot rescue
    /// it either, because the generation this fenced is still bound. The preflight therefore asks the
    /// classification the enablement itself will run, decided from the replacing generation's own
    /// record.
    /// </summary>
    [Test]
    public async Task It_resumes_a_replacement_whose_binding_and_tracking_activation_already_committed()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding());
        harness.Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Tracking");

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        // Resumed against the record it already holds rather than created a second time, and the
        // guarded activation is not re-run over a database that is already tracking.
        A.CallTo(() => harness.Bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                harness.Activation.ExecuteAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The retry classification is the enablement's own, so a replacing generation whose record exists
    /// but whose lifecycle the enablement would reject is still refused before the fence.
    /// </summary>
    [Test]
    public async Task It_refuses_a_bound_replacement_whose_lifecycle_the_enablement_would_reject()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding());
        harness.Eligibility = CdcSetupControllerHarness.Reading(lifecycleStateToken: "Rebuilding");

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        AssertRefusedBeforeFencing(harness, admission);
        Refusal(admission).Component.Should().Be(CdcDiagnosticComponent.Retry);
    }

    private static CdcSetupControllerHarness ReplaceableTarget() =>
        new()
        {
            PreviousGenerationRead = CdcSetupControllerHarness.Present(
                CdcSetupControllerHarness.PreviousGenerationBinding()
            ),
        };

    private static void AssertRefusedBeforeFencing(CdcSetupControllerHarness harness, CdcAdmission admission)
    {
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission).Should().NotBeNull();
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static CdcDiagnostic Refusal(CdcAdmission admission) =>
        admission
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "replaceSourceRefused")
            .Subject;

    private static CdcIncident SourceHistoryLoss(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            CdcSetupControllerHarness.Now,
            binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ProviderArtifactMissing,
            new CdcIncidentPositionMetadata(
                binding.ConnectorName,
                binding.TopicName,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
        );
}
