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
/// Adoption repairs missing deployment state around an already complete governed-artifact set. The
/// operator supplies the binding record in full and every claim in it is verified live; nothing is
/// inferred from the topic names or connector configuration that happen to exist, nothing is
/// provisioned, and a failed or incomplete adoption changes nothing.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerAdoption")]
public class Given_CdcSetupControllerAdoption
{
    [Test]
    public async Task It_imports_the_supplied_binding_record_when_every_verification_is_an_exact_match()
    {
        CdcSetupControllerHarness harness = new();

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Diagnostics.Should().BeEmpty();
        adoption.Succeeded.Should().BeTrue();
        adoption.Contract!.Binding.Should().Be(CdcSetupControllerHarness.Binding());
        harness.ImportedProof.Should().BeSameAs(adoption.Contract);
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Every verification kind the shared proof contract defines is required, and each one that was
    /// issued is an exact match: the contract admits no other verification state.
    /// </summary>
    [Test]
    public async Task It_verifies_every_adoption_verification_kind()
    {
        CdcSetupControllerHarness harness = new();

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        foreach (CdcAdoptionVerificationKind kind in Enum.GetValues<CdcAdoptionVerificationKind>())
        {
            Verification(adoption.Contract!, kind)
                .State.Should()
                .Be(CdcAdoptionVerificationState.ExactMatch, "{0} must be verified", kind);
        }

        CdcAdoptionProofValidator
            .Validate(adoption.Contract!, CdcSetupControllerHarness.Now.AddMinutes(1))
            .Succeeded.Should()
            .BeTrue();
    }

    /// <summary>
    /// Adoption repairs deployment state around an artifact set that is already publishing, so a
    /// connector the worker is not running is not something to adopt. A task count alone cannot say
    /// that: a paused, stopped, or failed connector still declares its single task, and adopting one
    /// would mint a binding record asserting a publication that is not happening.
    /// </summary>
    [TestCase("PAUSED", "PAUSED")]
    [TestCase("STOPPED", "STOPPED")]
    [TestCase("FAILED", "FAILED")]
    [TestCase("RUNNING", "FAILED")]
    [TestCase("PAUSED", "RUNNING")]
    public async Task It_refuses_a_connector_that_is_not_running_its_single_task(
        string connectorState,
        string taskState
    )
    {
        CdcSetupControllerHarness harness = new()
        {
            ConnectorStatus = CdcSetupControllerHarness.RunningConnector(connectorState, taskState),
        };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        Refusal(adoption, CdcAdoptionVerificationKind.Connector).Should().NotBeNull();
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Each logical public topic maps to exactly one physical database, because the captured rows carry
    /// no tenant or data-store discriminator to tell two logical targets apart downstream. Adoption
    /// imports a record the operator supplies, so it is one of the two ways a second logical target
    /// could come to bind a source this deployment already publishes.
    /// </summary>
    [Test]
    public async Task It_refuses_a_record_binding_a_physical_source_another_target_already_publishes()
    {
        CdcBinding otherTarget = CdcSetupControllerHarness.Binding() with
        {
            DataStoreId = "77",
            InstanceKey = "ds77",
        };

        CdcSetupControllerHarness harness = new()
        {
            BindingListing = CdcSetupControllerHarness.ListedBindings(otherTarget),
        };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        adoption
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.SourceMismatch);
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Another generation of the same logical target is the target continuing to own its source rather
    /// than a second target arriving at it. Adoption is not held to the enablement's rule against a
    /// second live generation either: it reconstitutes the record of an artifact set that already
    /// exists and registers nothing, so refusing would block the recovery of a generation this
    /// deployment had already published.
    /// </summary>
    [Test]
    public async Task It_adopts_when_the_only_other_binding_is_an_earlier_generation_of_this_target()
    {
        CdcBinding earlierGeneration = CdcSetupControllerHarness.Binding() with { Generation = 1 };

        CdcSetupControllerHarness harness = new()
        {
            BindingListing = CdcSetupControllerHarness.ListedBindings(earlierGeneration),
        };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        adoption.Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// A live connector holding a configuration that is not the one the supplied record renders is not
    /// the binding's connector, and nothing about that record becomes durable.
    /// </summary>
    [Test]
    public async Task It_changes_nothing_when_a_live_verification_does_not_match()
    {
        CdcSetupControllerHarness harness = new()
        {
            ConnectorConfigReadBack = CdcSetupControllerHarness.RenderedConnectorConfig(config =>
                config["tasks.max"] = "2"
            ),
        };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        adoption.Contract.Should().BeNull();
        Refusal(adoption, CdcAdoptionVerificationKind.ConnectorConfig).Should().NotBeNull();
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A record naming a physical source that is not the one the instance database reports is refused:
    /// adoption proves the operator's claim rather than accepting it.
    /// </summary>
    [Test]
    public async Task It_refuses_a_record_naming_a_physical_source_the_database_does_not_report()
    {
        CdcSetupControllerHarness harness = new();

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync(
            CdcSetupControllerHarness.Binding() with
            {
                PhysicalSourceFingerprint = Ddl
                    .CdcSourceFingerprintMetadata.Compute(
                        Ddl.CdcProvider.Postgresql,
                        "6ba7b810-9dad-11d1-80b4-00c04fd430c8"
                    )
                    .Value,
            }
        );

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        Refusal(adoption, CdcAdoptionVerificationKind.PhysicalSource).Should().NotBeNull();
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Adoption is not a first-time enablement path. A deployment whose connector does not exist has no
    /// artifact set to adopt, and the refusal creates none of it.
    /// </summary>
    [Test]
    public async Task It_refuses_adoption_as_a_first_time_enablement_path()
    {
        CdcSetupControllerHarness harness = new()
        {
            ConnectorStatus = new(CdcConnectOutcome.NotFound, null, new(404, "no such connector", false)),
            CommittedOffsets = new(CdcConnectOutcome.NotFound, null, new(404, "no such connector", false)),
            ConnectorConfigReadBack = new(
                CdcConnectOutcome.NotFound,
                null,
                new(404, "no such connector", false)
            ),
        };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        Refusal(adoption, CdcAdoptionVerificationKind.Connector).Should().NotBeNull();
        Refusal(adoption, CdcAdoptionVerificationKind.ConnectorConfig).Should().NotBeNull();
        Refusal(adoption, CdcAdoptionVerificationKind.ConnectOffsets).Should().NotBeNull();
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
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
    /// Verification reads; it never provisions. A pass that created an absent topic, repaired a missing
    /// grant, or created a provider capture artifact would make a refused adoption a partial
    /// enablement.
    /// </summary>
    [Test]
    public async Task It_provisions_nothing_while_verifying()
    {
        CdcSetupControllerHarness harness = new();

        await harness.AdoptAsync();

        using var _ = new AssertionScope();
        A.CallTo(() =>
                harness.Kafka.EnsureBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.EnsureConnectOffsetStoreAsync(
                    A<CdcObservationContext>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Kafka.DescribeBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
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
                harness.Activation.ExecuteAsync(
                    A<Core.DocumentCache.DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A source history that is already lost refuses the adoption and latches nothing: there is no
    /// binding record to latch an incident against, and a refused adoption changes nothing.
    /// </summary>
    [Test]
    public async Task It_refuses_a_lost_source_history_without_latching_an_incident()
    {
        CdcSetupControllerHarness harness = new(CdcProvider.SqlServer)
        {
            SchemaHistoryState = CdcSqlServerSchemaHistoryState.Missing,
        };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        Refusal(adoption, CdcAdoptionVerificationKind.SourceHistoryContinuity).Should().NotBeNull();
        A.CallTo(() => harness.Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The record is made durable by the guarded atomic state operation, which refuses a record that
    /// does not exactly match one already stored. Adoption never rewrites a binding's fields.
    /// </summary>
    [Test]
    public async Task It_reports_a_state_store_binding_mismatch_rather_than_overwriting_the_stored_record()
    {
        CdcSetupControllerHarness harness = new() { ImportResult = CdcSetupControllerHarness.Mismatched() };

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync();

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        adoption
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.BindingMismatch);
    }

    /// <summary>
    /// The control plane's provider adapters are the deployment's own, so a record naming another
    /// provider is refused before anything is read against a source this process cannot inspect.
    /// </summary>
    [Test]
    public async Task It_refuses_a_record_naming_another_provider()
    {
        CdcSetupControllerHarness harness = new();

        CdcContractReadResult<CdcAdoptionProof> adoption = await harness.AdoptAsync(
            CdcSetupControllerHarness.Binding(CdcProvider.SqlServer)
        );

        using var _ = new AssertionScope();
        adoption.Succeeded.Should().BeFalse();
        adoption
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Category == CdcDiagnosticCategory.ProviderMismatch);
        A.CallTo(() =>
                harness.ProviderSetup.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    private static CdcAdoptionVerificationResult Verification(
        CdcAdoptionProof proof,
        CdcAdoptionVerificationKind kind
    ) => proof.VerificationResults.Should().ContainSingle(result => result.VerificationKind == kind).Subject;

    /// <summary>The refusal reported for one verification kind, located by the kind it names.</summary>
    private static CdcDiagnostic Refusal(
        CdcContractReadResult<CdcAdoptionProof> adoption,
        CdcAdoptionVerificationKind kind
    ) =>
        adoption
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "adoptVerificationNotExactMatch"
                && diagnostic.ArtifactKind == kind.ToString()
            )
            .Subject;
}
