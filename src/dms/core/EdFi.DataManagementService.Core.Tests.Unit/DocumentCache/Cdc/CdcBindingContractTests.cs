// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcBindingContract")]
public class Given_CdcBindingContract
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

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
    public void It_serializes_the_persisted_binding_as_the_design_approved_immutable_record()
    {
        string json = CdcJsonContract.Serialize(SampleBinding);

        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root.Select(property => property.Key)
            .Should()
            .BeEquivalentTo(
                "version",
                "deploymentKey",
                "tenantKey",
                "dataStoreId",
                "instanceKey",
                "generation",
                "provider",
                "physicalSourceFingerprint",
                "connectorName",
                "topicName",
                "partitionCount",
                "partitionerAlgorithm",
                "contractVersion"
            );
        root["version"]!.GetValue<int>().Should().Be(1);
        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["provider"]!.GetValue<string>().Should().Be("postgresql");
        root["partitionerAlgorithm"]!.GetValue<string>().Should().Be("kafka-murmur2-v1");
        json.Should().NotContain("maxRecordBytes");
        json.Should().NotContain("connectionString");
        json.Should().NotContain("credential");
        json.Should().NotContain("sourceUuid");

        CdcContractReadResult<CdcBinding> result = CdcJsonContract.Deserialize<CdcBinding>(json);

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().Be(SampleBinding);
    }

    [Test]
    public void It_serializes_binding_state_and_source_history_incident_contracts()
    {
        CdcIncident incident = new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            SampleObservedAt,
            SampleBinding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectSourcePartitionMismatch,
            new CdcIncidentPositionMetadata(
                SampleBinding.ConnectorName,
                SampleBinding.TopicName,
                "edfi.dms.instance.data-store-1-g1.progress.v1",
                null,
                "dms_local_data_store_1_g1_publication",
                "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40",
                "42",
                null,
                null,
                null,
                "40",
                "50",
                [CdcIncidentUnavailableFact.SchemaHistory]
            )
        );
        CdcBindingStateContract contract = new(
            CdcJsonContract.CurrentContractVersion,
            SampleObservedAt,
            CdcBindingState.IncidentLatched,
            SampleBinding,
            incident
        );

        string json = CdcJsonContract.Serialize(contract);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["state"]!.GetValue<string>().Should().Be("incidentLatched");
        root["binding"]!["connectorName"]!.GetValue<string>().Should().Be(SampleBinding.ConnectorName);
        root["incident"]!["incidentType"]!.GetValue<string>().Should().Be("sourceHistoryContinuityLost");
        root["incident"]!["failureCategory"]!
            .GetValue<string>()
            .Should()
            .Be("connectSourcePartitionMismatch");
        root["incident"]!["bindingIdentity"]!["provider"]!.GetValue<string>().Should().Be("postgresql");
        root["incident"]!["bindingIdentity"]!["physicalSourceFingerprint"]!
            .GetValue<string>()
            .Should()
            .Be(SampleBinding.PhysicalSourceFingerprint);
        root["incident"]!["positionMetadata"]!["connectSourcePartitionHash"]!
            .GetValue<string>()
            .Should()
            .StartWith("sha256:");
        root["incident"]!["positionMetadata"]!["unavailableFacts"]![0]!
            .GetValue<string>()
            .Should()
            .Be("schemaHistory");
        json.Should().NotContain("EdFi_DMS_CDC");

        CdcContractReadResult<CdcBindingStateContract> result =
            CdcJsonContract.Deserialize<CdcBindingStateContract>(json);

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().BeEquivalentTo(contract);
    }

    [Test]
    public void It_rejects_null_tenant_keys_during_binding_contract_validation()
    {
        CdcContractValidationResult result = CdcBindingValidator.Validate(
            SampleBinding with
            {
                TenantKey = null!,
            }
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MissingRequiredField
                && diagnostic.Path == "$.tenantKey"
            );
    }

    [Test]
    public void It_serializes_adoption_and_cleanup_proof_structural_contracts_without_live_verification_fields()
    {
        CdcAdoptionProof adoptionProof = new(
            CdcJsonContract.CurrentContractVersion,
            "op-20260817-001",
            SampleObservedAt,
            SampleBinding,
            Enum.GetValues<CdcAdoptionVerificationKind>()
                .Select(kind => new CdcAdoptionVerificationResult(
                    kind,
                    CdcAdoptionVerificationState.ExactMatch,
                    "{verified}<token>\r\n" + new string('x', 520)
                ))
                .ToArray()
        );
        CdcCleanupProof cleanupProof = new(
            CdcJsonContract.CurrentContractVersion,
            "op-20260817-002",
            SampleObservedAt,
            SampleBinding.ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            [
                new(
                    CdcGovernedArtifactKind.KafkaConnectConnector,
                    SampleBinding.ConnectorName,
                    CdcCleanupState.Deleted,
                    "connector absent"
                ),
                new(
                    CdcGovernedArtifactKind.PublicTopicAcls,
                    $"{SampleBinding.TopicName}-acl",
                    CdcCleanupState.NotFound,
                    "acl absent"
                ),
            ]
        );

        JsonObject adoptionRoot = JsonNode.Parse(CdcJsonContract.Serialize(adoptionProof))!.AsObject();
        JsonObject cleanupRoot = JsonNode.Parse(CdcJsonContract.Serialize(cleanupProof))!.AsObject();

        adoptionRoot["verificationResults"]!.AsArray().Should().HaveCount(8);
        adoptionRoot["verificationResults"]![0]!["verificationKind"]!
            .GetValue<string>()
            .Should()
            .Be("physicalSource");
        adoptionRoot["verificationResults"]![0]!["state"]!.GetValue<string>().Should().Be("exactMatch");
        string evidenceSummary = adoptionRoot["verificationResults"]![0]![
            "evidenceSummary"
        ]!.GetValue<string>();
        evidenceSummary.Should().HaveLength(512);
        evidenceSummary.Should().NotContain("{");
        evidenceSummary.Should().NotContain("}");
        evidenceSummary.Should().NotContain("<");
        evidenceSummary.Should().NotContain(">");
        evidenceSummary.Should().NotContain("\r");
        evidenceSummary.Should().NotContain("\n");

        cleanupRoot["cleanupMode"]!.GetValue<string>().Should().Be("retireBindingGeneration");
        cleanupRoot["bindingIdentity"]!["topicName"]!.GetValue<string>().Should().Be(SampleBinding.TopicName);
        cleanupRoot["governedArtifacts"]![0]!["artifactKind"]!
            .GetValue<string>()
            .Should()
            .Be("kafkaConnectConnector");
        cleanupRoot["governedArtifacts"]![1]!["cleanupState"]!.GetValue<string>().Should().Be("notFound");
        cleanupRoot.Select(property => property.Key).Should().NotContain("authorization");
        cleanupRoot.Select(property => property.Key).Should().NotContain("signature");
        cleanupRoot.Select(property => property.Key).Should().NotContain("providerVerification");
        cleanupRoot.Select(property => property.Key).Should().NotContain("platformPurgeEvidence");
    }
}
