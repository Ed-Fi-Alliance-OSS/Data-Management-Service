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
    [Test]
    public async Task It_refuses_when_the_outgoing_connector_could_not_be_fenced()
    {
        CdcSetupControllerHarness harness = ReplaceableTarget();
        harness.Stop = new(CdcConnectOutcome.Unavailable, new(503, "worker unavailable", true));

        CdcAdmission admission = await harness.ReplaceSourceAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission).Should().NotBeNull();
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
