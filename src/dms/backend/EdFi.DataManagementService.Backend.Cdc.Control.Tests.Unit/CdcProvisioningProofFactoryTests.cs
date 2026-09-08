// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Provisioning proof issuance. The proof records what the operator asserted, so anything the caller
/// did not supply exactly is refused rather than inferred, and a refusal issues no proof at all.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcProvisioningProof")]
public class Given_CdcProvisioningProofFactory
{
    private const string OperationId = "operation-1";
    private const string SetupControllerRunId = "run-1";

    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void It_issues_a_proof_from_complete_explicit_evidence()
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(Evidence());

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();

        InitialCdcProvisioningProof proof = result.Contract!;
        proof.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        proof.OperationId.Should().Be(OperationId);
        proof.TargetIdentity.Should().Be(TargetIdentity());
        proof.Provider.Should().Be(CdcProvider.Postgresql);
        proof.SetupControllerRunId.Should().Be(SetupControllerRunId);
        proof.ProofId.Should().Be($"{SetupControllerRunId}.proof");
        proof.DatabaseCreationMode.Should().Be(CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning);
        proof.WriteAdmissionState.Should().Be(CdcWriteAdmissionState.ClosedNeverOpened);
        proof.IssuedAt.Should().Be(IssuedAt);
        Validate(proof).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_issues_no_proof_when_no_evidence_was_supplied()
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            new(SetupControllerRunId: null, DatabaseCreationMode: null, WriteAdmissionState: null)
        );

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.Contract.Should().BeNull();
        Paths(result)
            .Should()
            .BeEquivalentTo("$.setupControllerRunId", "$.databaseCreationMode", "$.writeAdmissionState");
    }

    [Test]
    public void It_issues_no_proof_when_the_database_creation_evidence_is_absent()
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                DatabaseCreationMode = null,
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.databaseCreationMode").Code.Should().Be("provisioningEvidenceMissing");
    }

    [Test]
    public void It_issues_no_proof_when_the_write_admission_evidence_is_absent()
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                WriteAdmissionState = null,
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.writeAdmissionState").Code.Should().Be("provisioningEvidenceMissing");
    }

    [Test]
    public void It_issues_no_proof_when_the_setup_controller_run_is_absent()
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                SetupControllerRunId = null,
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.setupControllerRunId").Code.Should().Be("provisioningProofRunIdMissing");
    }

    [TestCase("")]
    [TestCase(" created-for-initial-cdc-provisioning")]
    [TestCase("created-for-initial-cdc-provisioning ")]
    [TestCase("CREATED-FOR-INITIAL-CDC-PROVISIONING")]
    [TestCase("createdForInitialCdcProvisioning")]
    [TestCase("yes")]
    [TestCase("true")]
    public void It_refuses_a_database_creation_token_that_is_not_the_exact_token(string supplied)
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                DatabaseCreationMode = supplied,
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.databaseCreationMode").Code.Should().Be("provisioningEvidenceRejected");
        Diagnostic(result, "$.databaseCreationMode")
            .Expected.Should()
            .Be(CdcProvisioningProofFactory.CreatedForInitialCdcProvisioningToken);
    }

    [TestCase("")]
    [TestCase("closed-never-opened ")]
    [TestCase("CLOSED-NEVER-OPENED")]
    [TestCase("closedNeverOpened")]
    [TestCase("closed")]
    public void It_refuses_a_write_admission_token_that_is_not_the_exact_token(string supplied)
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                WriteAdmissionState = supplied,
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.writeAdmissionState").Code.Should().Be("provisioningEvidenceRejected");
        Diagnostic(result, "$.writeAdmissionState")
            .Expected.Should()
            .Be(CdcProvisioningProofFactory.ClosedNeverOpenedToken);
    }

    [Test]
    public void It_never_repeats_the_refused_value_back_in_its_diagnostics()
    {
        const string Supplied = "database=edfi_datastore;password=sentinel";

        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                WriteAdmissionState = Supplied,
            }
        );

        using var _ = new AssertionScope();
        Diagnostic(result, "$.writeAdmissionState").Observed.Should().Be("unrecognized");
        System.Text.Json.JsonSerializer.Serialize(result.Diagnostics).Should().NotContain("sentinel");
    }

    [TestCase("run 1")]
    [TestCase("Run-1")]
    [TestCase("-run-1")]
    [TestCase("run--1")]
    [TestCase("..")]
    public void It_refuses_a_setup_controller_run_that_is_not_a_safe_token(string setupControllerRunId)
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                SetupControllerRunId = setupControllerRunId,
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.setupControllerRunId").Code.Should().Be("provisioningProofRunIdUnusable");
    }

    [Test]
    public void It_refuses_a_setup_controller_run_the_proof_id_cannot_be_derived_from()
    {
        // The proof id is the run id plus its suffix, so a run id that fills the shared token bound
        // leaves no room for one. It is refused here rather than reported as an unusable proof id,
        // which would name nothing the caller passed in.
        CdcContractReadResult<InitialCdcProvisioningProof> result = Issue(
            Evidence() with
            {
                SetupControllerRunId = new string('a', 128),
            }
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.setupControllerRunId").Code.Should().Be("provisioningProofRunIdUnusable");
    }

    [Test]
    public void It_issues_no_proof_that_could_not_pass_the_shared_contract()
    {
        CdcTargetIdentity invalidTarget = TargetIdentity() with { Generation = 0 };

        CdcContractReadResult<InitialCdcProvisioningProof> result = CdcProvisioningProofFactory.Issue(
            new(OperationId, invalidTarget, Fingerprint()),
            Evidence(),
            IssuedAt
        );

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.Contract.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public void It_issues_no_proof_from_a_timestamp_that_is_not_utc()
    {
        CdcContractReadResult<InitialCdcProvisioningProof> result = CdcProvisioningProofFactory.Issue(
            Context(),
            Evidence(),
            new DateTimeOffset(2026, 8, 28, 11, 0, 0, TimeSpan.FromHours(2))
        );

        using var _ = new AssertionScope();
        result.Contract.Should().BeNull();
        Diagnostic(result, "$.issuedAt").Category.Should().Be(CdcDiagnosticCategory.InvalidTimestamp);
    }

    [Test]
    public void It_rejects_a_missing_context_or_evidence()
    {
        using var _ = new AssertionScope();
        FluentActions
            .Invoking(() => CdcProvisioningProofFactory.Issue(null!, Evidence(), IssuedAt))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => CdcProvisioningProofFactory.Issue(Context(), null!, IssuedAt))
            .Should()
            .Throw<ArgumentNullException>();
    }

    private static CdcContractReadResult<InitialCdcProvisioningProof> Issue(
        CdcProvisioningProofEvidence evidence
    ) => CdcProvisioningProofFactory.Issue(Context(), evidence, IssuedAt);

    private static CdcProvisioningProofEvidence Evidence() =>
        new(
            SetupControllerRunId,
            CdcProvisioningProofFactory.CreatedForInitialCdcProvisioningToken,
            CdcProvisioningProofFactory.ClosedNeverOpenedToken
        );

    private static CdcObservationContext Context() => new(OperationId, TargetIdentity(), Fingerprint());

    private static CdcTargetIdentity TargetIdentity() =>
        CdcControlTemplateTestData.BuildTargetIdentity(Ddl.CdcProvider.Postgresql);

    private static string Fingerprint() =>
        CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value;

    private static CdcContractValidationResult Validate(InitialCdcProvisioningProof proof) =>
        InitialCdcProvisioningProofValidator.Validate(
            proof,
            new(OperationId, TargetIdentity(), Fingerprint(), IssuedAt.AddMinutes(1))
        );

    private static IEnumerable<string?> Paths(CdcContractReadResult<InitialCdcProvisioningProof> result) =>
        result.Diagnostics.Select(diagnostic => diagnostic.Path);

    private static CdcDiagnostic Diagnostic(
        CdcContractReadResult<InitialCdcProvisioningProof> result,
        string path
    ) => result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Path == path).Subject;
}
