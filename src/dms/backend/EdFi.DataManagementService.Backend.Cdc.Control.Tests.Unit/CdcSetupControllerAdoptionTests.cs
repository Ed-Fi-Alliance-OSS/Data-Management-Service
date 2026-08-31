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
