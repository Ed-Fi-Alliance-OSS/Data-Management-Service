// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Retirement removes every governed artifact one binding generation owns, in the order that keeps each
/// removal decidable, and deletes the binding record last. The shared cluster-scoped Connect offset
/// store is never touched, and a partial teardown issues no proof so the retry finds the record that
/// names what is left.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerTeardown")]
public class Given_CdcSetupControllerRetirement
{
    /// <summary>
    /// The connector is stopped before its committed offsets are deleted, and its configuration is
    /// deleted only afterwards: the worker accepts an offsets deletion only for a connector that exists
    /// and is stopped, and deleting the configuration does not remove the offsets.
    /// </summary>
    [Test]
    public async Task It_removes_every_governed_artifact_in_the_required_order()
    {
        CdcSetupControllerHarness harness = EnabledBinding();

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        using var _ = new AssertionScope();
        retirement.Succeeded.Should().BeTrue();
        A.CallTo(() => harness.Connect.StopConnectorAsync(Connector(), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly()
            .Then(
                A.CallTo(() =>
                        harness.Connect.DeleteConnectorOffsetsAsync(Connector(), A<CancellationToken>._)
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() => harness.Connect.DeleteConnectorAsync(Connector(), A<CancellationToken>._))
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() =>
                        harness.Kafka.DeleteBindingArtifactsAsync(
                            A<CdcArtifactInventory>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() =>
                        harness.ProviderTeardown.DeleteAsync(
                            A<CdcProviderArtifactTeardownRequest>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            )
            .Then(
                A.CallTo(() =>
                        harness.Bindings.DeleteStateAfterVerifiedCleanupAsync(
                            A<CdcCleanupProof>._,
                            A<CancellationToken>._
                        )
                    )
                    .MustHaveHappenedOnceExactly()
            );
    }

    /// <summary>
    /// The proof accounts for every artifact the binding governs, which is what authorizes the record's
    /// removal.
    /// </summary>
    [Test]
    public async Task It_names_every_governed_artifact_of_the_binding_in_the_proof()
    {
        CdcSetupControllerHarness harness = EnabledBinding();

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        CdcCleanupProof proof = retirement.Contract!;

        using var _ = new AssertionScope();
        proof.CleanupMode.Should().Be(CdcCleanupMode.RetireBindingGeneration);
        foreach (CdcGovernedArtifactName expected in CdcSetupControllerHarness.Inventory().GovernedArtifacts)
        {
            Artifact(proof, expected.Kind).ArtifactName.Should().Be(expected.Name);
        }

        CdcCleanupProofValidator
            .Validate(proof, CdcSetupControllerHarness.Binding(), CdcSetupControllerHarness.Now.AddMinutes(1))
            .Succeeded.Should()
            .BeTrue();
        harness.CleanupProof.Should().BeSameAs(proof);
    }

    /// <summary>
    /// The shared Connect offset store is worker state for every binding, not a binding artifact: it is
    /// neither resolved nor removed, and it never appears in the proof.
    /// </summary>
    [Test]
    public async Task It_never_touches_or_names_the_shared_connect_offset_store()
    {
        CdcSetupControllerHarness harness = EnabledBinding();

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        using var _ = new AssertionScope();
        retirement
            .Contract!.GovernedArtifacts.Should()
            .NotContain(artifact => artifact.ArtifactName == OffsetStorageTopic);
        A.CallTo(() =>
                harness.Kafka.EnsureConnectOffsetStoreAsync(
                    A<CdcObservationContext>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The operator can take the ambiguity on themselves for a generation whose connector was never
    /// registered, or whose earlier retirement removed it before being interrupted. The retirement then
    /// completes, and the proof records the offsets as the operator's assertion rather than the worker's
    /// observation.
    /// </summary>
    [Test]
    public async Task It_retires_an_acknowledged_absent_connector_on_the_operator_s_own_assertion()
    {
        CdcArtifactInventory inventory = CdcSetupControllerHarness.Inventory();
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.ConnectorAlreadyAbsent = true;
        harness.Stop = NotFound();
        harness.DeleteOffsets = NotFound();
        harness.DeleteConnector = NotFound();
        harness.DeletedKafkaArtifacts = CdcSetupControllerHarness.RemovedKafkaArtifacts(
            inventory,
            CdcCleanupState.NotFound
        );
        harness.DeletedProviderArtifacts = CdcSetupControllerHarness.RemovedProviderArtifacts(
            inventory,
            CdcCleanupState.NotFound
        );

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        using var _ = new AssertionScope();
        retirement.Succeeded.Should().BeTrue();
        retirement
            .Contract!.GovernedArtifacts.Should()
            .OnlyContain(artifact => artifact.CleanupState == CdcCleanupState.NotFound);

        // The worker was never asked, so the proof must not claim it answered.
        Artifact(retirement.Contract!, CdcGovernedArtifactKind.ConnectSourceOffsets)
            .EvidenceSummary.Should()
            .Contain("the operator asserted");
        A.CallTo(() => harness.Connect.DeleteConnectorOffsetsAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();

        A.CallTo(() =>
                harness.Bindings.DeleteStateAfterVerifiedCleanupAsync(
                    A<CdcCleanupProof>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A connector the worker does not have says nothing about that connector's committed offsets: the
    /// offsets survive their connector's configuration and live in the cluster-scoped store, and the
    /// worker's 404 reports only that the connector is absent. Retirement refuses rather than record an
    /// absence it cannot observe, so the record that names those offsets survives for an operator to
    /// reconcile.
    /// </summary>
    [Test]
    public async Task It_refuses_a_retirement_whose_connector_the_worker_no_longer_has()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.Stop = NotFound();
        harness.DeleteOffsets = NotFound();
        harness.DeleteConnector = NotFound();

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        using var _ = new AssertionScope();
        AssertRecordSurvived(harness, retirement);
        Refusal(retirement).Category.Should().Be(CdcDiagnosticCategory.ConnectOffsetStoreInvalid);
        Refusal(retirement).Component.Should().Be(CdcDiagnosticComponent.ConnectOffsetStore);
        Refusal(retirement).Observed.Should().Be(Connector(), "the refusal names the connector at issue");

        // The offsets are where this ends. Nothing past them is attempted, so no later removal can be
        // mistaken for progress on a retirement that never established its first artifact.
        A.CallTo(() => harness.Connect.DeleteConnectorOffsetsAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.DeleteBindingArtifactsAsync(A<CdcArtifactInventory>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A connector the worker refuses to delete is an artifact that is still there, so no proof is
    /// issued and the record that names it survives for the retry.
    /// </summary>
    [Test]
    public async Task It_leaves_the_binding_record_intact_when_the_connector_could_not_be_removed()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        harness.DeleteConnector = new(CdcConnectOutcome.Conflict, new(409, "rebalance in progress", true));

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        AssertRecordSurvived(harness, retirement);
    }

    [Test]
    public async Task It_leaves_the_binding_record_intact_when_the_governed_topics_could_not_be_removed()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        A.CallTo(() =>
                harness.Kafka.DeleteBindingArtifactsAsync(A<CdcArtifactInventory>._, A<CancellationToken>._)
            )
            .Throws(new KafkaException(ErrorCode.BrokerNotAvailable));

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        AssertRecordSurvived(harness, retirement);
    }

    [Test]
    public async Task It_leaves_the_binding_record_intact_when_the_provider_artifacts_could_not_be_removed()
    {
        CdcSetupControllerHarness harness = EnabledBinding();
        A.CallTo(() =>
                harness.ProviderTeardown.DeleteAsync(
                    A<CdcProviderArtifactTeardownRequest>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new InvalidOperationException("the provider refused the removal"));

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        AssertRecordSurvived(harness, retirement);
    }

    /// <summary>
    /// The governed artifacts are the record's. Without it there is nothing this retirement may name,
    /// and automation never infers a binding from the artifacts that happen to exist.
    /// </summary>
    [Test]
    public async Task It_refuses_a_target_with_no_durable_binding_record()
    {
        CdcSetupControllerHarness harness = new();

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        using var _ = new AssertionScope();
        retirement.Succeeded.Should().BeFalse();
        Refusal(retirement).Category.Should().Be(CdcDiagnosticCategory.BindingMissing);
        A.CallTo(() => harness.Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.DeleteBindingArtifactsAsync(A<CdcArtifactInventory>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A SQL Server generation additionally governs the schema-history topic, its ACLs, three capture
    /// instances, and the gating role, and the proof must account for all of them.
    /// </summary>
    [Test]
    public async Task It_retires_a_sql_server_generation_including_its_schema_history_topic()
    {
        CdcSetupControllerHarness harness = EnabledBinding(CdcProvider.SqlServer);

        CdcContractReadResult<CdcCleanupProof> retirement = await harness.RetireAsync();

        CdcCleanupProof proof = retirement.Contract!;

        using var _ = new AssertionScope();
        retirement.Succeeded.Should().BeTrue();
        Artifact(proof, CdcGovernedArtifactKind.SchemaHistoryTopic)
            .ArtifactName.Should()
            .Be(CdcSetupControllerHarness.Inventory(CdcProvider.SqlServer).SchemaHistoryTopicName);
        Artifact(proof, CdcGovernedArtifactKind.SchemaHistoryTopicAcls).Should().NotBeNull();
        Artifact(proof, CdcGovernedArtifactKind.SqlServerCdcGatingRole).Should().NotBeNull();
        Artifact(proof, CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument).Should().NotBeNull();
        Artifact(proof, CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache).Should().NotBeNull();
        Artifact(proof, CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat).Should().NotBeNull();
        CdcCleanupProofValidator
            .Validate(
                proof,
                CdcSetupControllerHarness.Binding(CdcProvider.SqlServer),
                CdcSetupControllerHarness.Now.AddMinutes(1)
            )
            .Succeeded.Should()
            .BeTrue();
    }

    private const string OffsetStorageTopic = "connect-offsets";

    private static CdcSetupControllerHarness EnabledBinding(CdcProvider provider = CdcProvider.Postgresql) =>
        new(provider)
        {
            BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding(provider)),
        };

    private static string Connector(CdcProvider provider = CdcProvider.Postgresql) =>
        CdcSetupControllerHarness.Inventory(provider).ConnectorName;

    private static CdcConnectResult NotFound() =>
        new(CdcConnectOutcome.NotFound, new(404, "no such connector", false));

    private static void AssertRecordSurvived(
        CdcSetupControllerHarness harness,
        CdcContractReadResult<CdcCleanupProof> retirement
    )
    {
        retirement.Succeeded.Should().BeFalse();
        retirement.Contract.Should().BeNull();
        Refusal(retirement).Retryable.Should().BeTrue();
        A.CallTo(() =>
                harness.Bindings.DeleteStateAfterVerifiedCleanupAsync(
                    A<CdcCleanupProof>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private static CdcGovernedArtifact Artifact(CdcCleanupProof proof, CdcGovernedArtifactKind kind) =>
        proof.GovernedArtifacts.Should().ContainSingle(artifact => artifact.ArtifactKind == kind).Subject;

    private static CdcDiagnostic Refusal(CdcContractReadResult<CdcCleanupProof> retirement) =>
        retirement
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "retireIncomplete")
            .Subject;
}
