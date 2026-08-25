// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcAdoptionProof")]
public class Given_CdcAdoptionProof
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleNow = SampleObservedAt.AddMinutes(1);

    private static CdcBinding SampleBinding =>
        new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            CdcProvider.Postgresql,
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            "dms-local-data-store-1-g1",
            "edfi.dms.instance.data-store-1-g1.documents.v1",
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );

    [Test]
    public void It_accepts_one_exact_match_result_for_every_required_verification_kind()
    {
        CdcAdoptionProof proof = CreateCompleteProof(SampleBinding);

        CdcContractValidationResult result = CdcAdoptionProofValidator.Validate(proof, SampleNow);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_duplicate_missing_non_exact_and_unsafe_verification_results()
    {
        CdcAdoptionProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            SampleBinding,
            [
                new(
                    CdcAdoptionVerificationKind.PhysicalSource,
                    CdcAdoptionVerificationState.ExactMatch,
                    "verified"
                ),
                new(
                    CdcAdoptionVerificationKind.PhysicalSource,
                    CdcAdoptionVerificationState.ExactMatch,
                    "duplicate"
                ),
                new(
                    CdcAdoptionVerificationKind.ProviderArtifacts,
                    (CdcAdoptionVerificationState)999,
                    "not exact"
                ),
                new(CdcAdoptionVerificationKind.KafkaTopics, CdcAdoptionVerificationState.ExactMatch, "{{{"),
            ]
        );

        CdcContractValidationResult result = CdcAdoptionProofValidator.Validate(proof, SampleNow);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.VerificationIncomplete)
            .And.Contain(CdcDiagnosticCategory.UnsafeEvidence);
    }

    [Test]
    public void It_rejects_and_redacts_sensitive_evidence_summary_text()
    {
        CdcAdoptionVerificationResult[] verificationResults = [.. CompleteVerificationResults()];
        verificationResults[0] = new(
            CdcAdoptionVerificationKind.PhysicalSource,
            CdcAdoptionVerificationState.ExactMatch,
            "database.password: hidden"
        );
        verificationResults[1] = new(
            CdcAdoptionVerificationKind.ProviderArtifacts,
            CdcAdoptionVerificationState.ExactMatch,
            "p@w@d=secret"
        );
        CdcAdoptionProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            SampleBinding,
            verificationResults
        );

        string json = CdcJsonContract.Serialize(proof);
        CdcContractValidationResult result = CdcAdoptionProofValidator.Validate(proof, SampleNow);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.UnsafeEvidence);
        json.Should().NotContain("database.password").And.NotContain("pwd").And.NotContain("secret");
    }

    [Test]
    public void It_rejects_wrong_version_operation_timestamp_and_binding_artifact_mismatch()
    {
        CdcAdoptionProof proof = new(
            2,
            "bad/operation",
            SampleNow.AddSeconds(1),
            SampleBinding with
            {
                Version = 2,
                ConnectorName = "dms-local-data-store-1-g1-mismatch",
            },
            CompleteVerificationResults()
        );

        CdcContractValidationResult result = CdcAdoptionProofValidator.Validate(proof, SampleNow);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidContractVersion)
            .And.Contain(CdcDiagnosticCategory.InvalidOperationId)
            .And.Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch);
    }

    private static CdcAdoptionProof CreateCompleteProof(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding,
            CompleteVerificationResults()
        );

    private static IReadOnlyList<CdcAdoptionVerificationResult> CompleteVerificationResults() =>
        Enum.GetValues<CdcAdoptionVerificationKind>()
            .Select(kind => new CdcAdoptionVerificationResult(
                kind,
                CdcAdoptionVerificationState.ExactMatch,
                "verified"
            ))
            .ToArray();
}
