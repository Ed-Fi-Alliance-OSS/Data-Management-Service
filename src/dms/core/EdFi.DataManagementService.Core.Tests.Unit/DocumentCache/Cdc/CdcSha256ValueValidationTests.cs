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
[Category("CdcSha256ValueValidation")]
public class Given_CdcSha256ValueValidation
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string ValidHash =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static CdcBinding SampleBinding =>
        new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            CdcProvider.Postgresql,
            ValidHash,
            "dms-local-data-store-1-g1",
            "edfi.dms.instance.data-store-1-g1.documents.v1",
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );

    [TestCaseSource(nameof(InvalidHashCases))]
    public void It_reports_stable_diagnostics_for_invalid_binding_hashes(
        string? invalidHash,
        CdcDiagnosticCategory expectedCategory
    )
    {
        CdcContractValidationResult result = CdcBindingValidator.Validate(
            SampleBinding with
            {
                PhysicalSourceFingerprint = invalidHash!,
            }
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == expectedCategory && diagnostic.Path == "$.physicalSourceFingerprint"
            );
    }

    [Test]
    public void It_uses_the_shared_validator_through_observation_incident_and_proof_paths()
    {
        const string uppercaseHash =
            "sha256:8CAA6B0AD6DB6F60D8D7CE6E78D1E76094E2241678C6F241670319AB60810851";

        CdcDiagnosticCollector observationDiagnostics = new();
        CdcObservationValidationRules.ValidateHashValue(
            uppercaseHash,
            "$.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            true,
            observationDiagnostics
        );

        CdcContractValidationResult incidentResult = CdcIncidentValidator.Validate(
            CreateIncident(
                SampleBinding.ToCompleteBindingIdentity() with
                {
                    PhysicalSourceFingerprint = uppercaseHash,
                }
            ),
            Now
        );

        CdcDiagnosticCollector proofDiagnostics = new();
        CdcProofValidationRules.ValidateBinding(
            SampleBinding with
            {
                PhysicalSourceFingerprint = uppercaseHash,
            },
            "$.binding",
            proofDiagnostics
        );

        observationDiagnostics
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                && diagnostic.Path == "$.connectSourcePartitionHash"
            );
        incidentResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                && diagnostic.Path == "$.bindingIdentity.physicalSourceFingerprint"
            );
        proofDiagnostics
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MalformedProof
                && diagnostic.Path == "$.binding.physicalSourceFingerprint"
            );
    }

    private static IEnumerable<TestCaseData> InvalidHashCases()
    {
        yield return new(null, CdcDiagnosticCategory.MissingRequiredField) { TestName = "missing" };
        yield return new(
            "sha256:8CAA6B0AD6DB6F60D8D7CE6E78D1E76094E2241678C6F241670319AB60810851",
            CdcDiagnosticCategory.MalformedPayload
        )
        {
            TestName = "uppercase",
        };
        yield return new("sha256:8caa6b0ad6db6f60", CdcDiagnosticCategory.MalformedPayload)
        {
            TestName = "short",
        };
        yield return new(
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab608108510",
            CdcDiagnosticCategory.MalformedPayload
        )
        {
            TestName = "overlong",
        };
        yield return new(
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab6081085g",
            CdcDiagnosticCategory.MalformedPayload
        )
        {
            TestName = "non_hex",
        };
    }

    private static CdcIncident CreateIncident(CdcCompleteBindingIdentity bindingIdentity) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            ObservedAt,
            bindingIdentity,
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            new(
                SampleBinding.ConnectorName,
                SampleBinding.TopicName,
                "edfi.dms.instance.data-store-1-g1.progress.v1",
                null,
                "dms_local_data_store_1_g1_slot",
                "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40",
                null,
                null,
                null,
                null,
                null,
                null,
                [CdcIncidentUnavailableFact.ConnectOffset]
            )
        );
}
