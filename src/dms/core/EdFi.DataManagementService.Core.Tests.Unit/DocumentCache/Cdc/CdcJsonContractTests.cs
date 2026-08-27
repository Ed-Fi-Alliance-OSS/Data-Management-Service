// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcJsonContract")]
public class Given_CdcJsonContract
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static CdcTargetIdentity SampleTargetIdentity =>
        new("deployment-a", "default", "7", "instance-a", 3, CdcProvider.SqlServer);

    private static SampleCdcContract SampleContract =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcReadiness.NotReady,
            SampleObservedAt,
            SampleTargetIdentity
        );

    private static CdcBindingIdentity SampleBindingIdentity =>
        CdcBindingIdentity.FromTargetIdentity(SampleTargetIdentity);

    private static CdcComponent SampleComponent =>
        CdcComponent.NotSatisfied(
            CdcBlockingCategory.ProjectionBacklog,
            SampleObservedAt,
            "projection backlog"
        );

    private static CdcDiagnostic SampleDiagnostic =>
        new(
            "invalidObservation",
            CdcDiagnosticCategory.InvalidObservation,
            CdcDiagnosticSeverity.Error,
            CdcDiagnosticComponent.ObservationValidation,
            SampleObservedAt,
            "CDC diagnostic.",
            false,
            artifactKind: "connector",
            artifactName: "connector-a",
            expected: "expected",
            observed: "observed"
        );

    private static CdcIncidentPositionMetadata SamplePositionMetadata =>
        new(
            "connector-a",
            "topic-a",
            "progress-a",
            "schema-history-a",
            "artifact-a",
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            "0/16B6C50",
            "00000027:00000758:0004",
            "00000027:00000758:0005",
            2,
            "0/16B6C00",
            "0/16B6CFF",
            [CdcIncidentUnavailableFact.ConnectOffset]
        );

    [Test]
    public void It_serializes_lower_camel_properties_and_lower_camel_enum_strings()
    {
        string json = CdcJsonContract.Serialize(SampleContract);

        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["readiness"]!.GetValue<string>().Should().Be("notReady");
        root["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("sqlServer");
        root["targetIdentity"]!["dataStoreId"]!.GetValue<string>().Should().Be("7");
        json.Should().NotContain("Readiness");
        json.Should().NotContain("CdcReadiness");
        json.Should().NotContain("\"readiness\":1");
    }

    [Test]
    public void It_deserializes_valid_contract_payloads()
    {
        string json = CdcJsonContract.Serialize(SampleContract);

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().Be(SampleContract);
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase("1")]
    [TestCase("\"readyNow\"")]
    public void It_rejects_invalid_enum_values_with_typed_diagnostics(string readinessJson)
    {
        string json = $$"""
            {
              "contractVersion": 1,
              "readiness": {{readinessJson}},
              "observedAt": "2026-08-17T13:10:11+00:00",
              "targetIdentity": {
                "deploymentKey": "deployment-a",
                "tenantKey": "default",
                "dataStoreId": "7",
                "instanceKey": "instance-a",
                "generation": 3,
                "provider": "sqlServer"
              }
            }
            """;

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.InvalidEnumValue);
    }

    [Test]
    public void It_reports_missing_required_contract_version()
    {
        string json = $$"""
            {
              "readiness": "notReady",
              "observedAt": "2026-08-17T13:10:11+00:00",
              "targetIdentity": {
                "deploymentKey": "deployment-a",
                "tenantKey": "default",
                "dataStoreId": "7",
                "instanceKey": "instance-a",
                "generation": 3,
                "provider": "sqlServer"
              }
            }
            """;

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.MissingRequiredField);
    }

    [TestCase("deploymentKey")]
    [TestCase("tenantKey")]
    [TestCase("dataStoreId")]
    [TestCase("instanceKey")]
    [TestCase("generation")]
    [TestCase("provider")]
    public void It_reports_missing_required_target_identity_members(string fieldName)
    {
        string json = RemoveProperty(
            new TargetIdentityContract(CdcJsonContract.CurrentContractVersion, SampleTargetIdentity),
            "targetIdentity",
            fieldName
        );

        AssertMissingRequiredField<TargetIdentityContract>(json);
    }

    [TestCase("deploymentKey")]
    [TestCase("tenantKey")]
    [TestCase("dataStoreId")]
    [TestCase("instanceKey")]
    [TestCase("generation")]
    public void It_reports_missing_required_binding_identity_members(string fieldName)
    {
        string json = RemoveProperty(
            new BindingIdentityContract(CdcJsonContract.CurrentContractVersion, SampleBindingIdentity),
            "bindingIdentity",
            fieldName
        );

        AssertMissingRequiredField<BindingIdentityContract>(json);
    }

    [TestCase("state")]
    [TestCase("category")]
    public void It_reports_missing_required_component_members(string fieldName)
    {
        string json = RemoveProperty(
            new ComponentContract(CdcJsonContract.CurrentContractVersion, SampleComponent),
            "component",
            fieldName
        );

        AssertMissingRequiredField<ComponentContract>(json);
    }

    [Test]
    public void It_deserializes_component_without_optional_observed_at_or_message()
    {
        JsonObject root = SerializeToObject(
            new ComponentContract(CdcJsonContract.CurrentContractVersion, SampleComponent)
        );
        RemoveProperty(root, "component", "observedAt");
        RemoveProperty(root, "component", "message");

        CdcContractReadResult<ComponentContract> result = CdcJsonContract.Deserialize<ComponentContract>(
            root.ToJsonString(CdcJsonContract.SerializerOptions)
        );

        result.Succeeded.Should().BeTrue();
        result.Contract!.Component.State.Should().Be(CdcComponentState.NotSatisfied);
        result.Contract.Component.Category.Should().Be(CdcBlockingCategory.ProjectionBacklog);
        result.Contract.Component.ObservedAt.Should().BeNull();
        result.Contract.Component.Message.Should().BeNull();
    }

    [TestCase("code")]
    [TestCase("category")]
    [TestCase("severity")]
    [TestCase("component")]
    [TestCase("observedAt")]
    [TestCase("message")]
    [TestCase("retryable")]
    public void It_reports_missing_required_diagnostic_members(string fieldName)
    {
        string json = RemoveProperty(
            new DiagnosticContract(CdcJsonContract.CurrentContractVersion, SampleDiagnostic),
            "diagnostic",
            fieldName
        );

        AssertMissingRequiredField<DiagnosticContract>(json);
    }

    [Test]
    public void It_deserializes_diagnostic_without_optional_artifact_and_detail_members()
    {
        JsonObject root = SerializeToObject(
            new DiagnosticContract(CdcJsonContract.CurrentContractVersion, SampleDiagnostic)
        );
        RemoveProperty(root, "diagnostic", "artifactKind");
        RemoveProperty(root, "diagnostic", "artifactName");
        RemoveProperty(root, "diagnostic", "expected");
        RemoveProperty(root, "diagnostic", "observed");

        CdcContractReadResult<DiagnosticContract> result = CdcJsonContract.Deserialize<DiagnosticContract>(
            root.ToJsonString(CdcJsonContract.SerializerOptions)
        );

        result.Succeeded.Should().BeTrue();
        result.Contract!.Diagnostic.ArtifactKind.Should().BeNull();
        result.Contract.Diagnostic.ArtifactName.Should().BeNull();
        result.Contract.Diagnostic.Expected.Should().BeNull();
        result.Contract.Diagnostic.Observed.Should().BeNull();
    }

    [Test]
    public void It_reports_missing_required_incident_position_unavailable_facts()
    {
        string json = RemoveProperty(
            new IncidentPositionContract(CdcJsonContract.CurrentContractVersion, SamplePositionMetadata),
            "positionMetadata",
            "unavailableFacts"
        );

        AssertMissingRequiredField<IncidentPositionContract>(json);
    }

    [Test]
    public void It_deserializes_incident_position_metadata_without_optional_artifact_and_position_members()
    {
        JsonObject root = SerializeToObject(
            new IncidentPositionContract(CdcJsonContract.CurrentContractVersion, SamplePositionMetadata)
        );
        RemoveProperty(root, "positionMetadata", "connectorName");
        RemoveProperty(root, "positionMetadata", "topicName");
        RemoveProperty(root, "positionMetadata", "progressTopicName");
        RemoveProperty(root, "positionMetadata", "schemaHistoryTopicName");
        RemoveProperty(root, "positionMetadata", "providerArtifactName");
        RemoveProperty(root, "positionMetadata", "connectSourcePartitionHash");
        RemoveProperty(root, "positionMetadata", "lsnProc");
        RemoveProperty(root, "positionMetadata", "commitLsn");
        RemoveProperty(root, "positionMetadata", "changeLsn");
        RemoveProperty(root, "positionMetadata", "eventSerialNo");
        RemoveProperty(root, "positionMetadata", "retainedRangeStart");
        RemoveProperty(root, "positionMetadata", "retainedRangeEnd");

        CdcContractReadResult<IncidentPositionContract> result =
            CdcJsonContract.Deserialize<IncidentPositionContract>(
                root.ToJsonString(CdcJsonContract.SerializerOptions)
            );

        result.Succeeded.Should().BeTrue();
        result.Contract!.PositionMetadata.ConnectorName.Should().BeNull();
        result.Contract.PositionMetadata.TopicName.Should().BeNull();
        result.Contract.PositionMetadata.UnavailableFacts.Should().ContainSingle();
    }

    [Test]
    public void It_does_not_default_missing_nested_target_provider_during_provisioning_proof_deserialization()
    {
        InitialCdcProvisioningProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            "proof-1",
            "operation-1",
            SampleTargetIdentity,
            CdcProvider.SqlServer,
            "setup-run-1",
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            SampleObservedAt
        );
        string json = RemoveProperty(proof, "targetIdentity", "provider");

        AssertMissingRequiredField<InitialCdcProvisioningProof>(json);
    }

    [Test]
    public void It_reports_invalid_contract_versions()
    {
        string json = CdcJsonContract.Serialize(SampleContract with { ContractVersion = 2 });

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.InvalidContractVersion);
    }

    [Test]
    public void It_reports_malformed_payloads()
    {
        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            "{ \"contractVersion\": 1,"
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.MalformedPayload);
    }

    [Test]
    public void It_reports_future_utc_timestamps()
    {
        DateTimeOffset now = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

        CdcContractValidationResult result = CdcJsonContract.ValidateNotFutureUtcTimestamp(
            now.AddTicks(1),
            now,
            "$.observedAt"
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.FutureUtcTimestamp);
    }

    private sealed record SampleCdcContract(
        [property: JsonRequired] int ContractVersion,
        CdcReadiness Readiness,
        DateTimeOffset ObservedAt,
        CdcTargetIdentity TargetIdentity
    ) : ICdcJsonContract;

    private sealed record TargetIdentityContract(
        [property: JsonRequired] int ContractVersion,
        [property: JsonRequired] CdcTargetIdentity TargetIdentity
    ) : ICdcJsonContract;

    private sealed record BindingIdentityContract(
        [property: JsonRequired] int ContractVersion,
        [property: JsonRequired] CdcBindingIdentity BindingIdentity
    ) : ICdcJsonContract;

    private sealed record ComponentContract(
        [property: JsonRequired] int ContractVersion,
        [property: JsonRequired] CdcComponent Component
    ) : ICdcJsonContract;

    private sealed record DiagnosticContract(
        [property: JsonRequired] int ContractVersion,
        [property: JsonRequired] CdcDiagnostic Diagnostic
    ) : ICdcJsonContract;

    private sealed record IncidentPositionContract(
        [property: JsonRequired] int ContractVersion,
        [property: JsonRequired] CdcIncidentPositionMetadata PositionMetadata
    ) : ICdcJsonContract;

    private static void AssertMissingRequiredField<TContract>(string json)
    {
        CdcContractReadResult<TContract> result = CdcJsonContract.Deserialize<TContract>(json);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.MissingRequiredField);
    }

    private static string RemoveProperty<TContract>(TContract contract, params string[] propertyPath)
    {
        JsonObject root = SerializeToObject(contract);
        RemoveProperty(root, propertyPath);

        return root.ToJsonString(CdcJsonContract.SerializerOptions);
    }

    private static JsonObject SerializeToObject<TContract>(TContract contract) =>
        JsonNode.Parse(CdcJsonContract.Serialize(contract))!.AsObject();

    private static void RemoveProperty(JsonObject root, params string[] propertyPath)
    {
        JsonObject parent = root;
        for (int index = 0; index < propertyPath.Length - 1; index++)
        {
            parent = parent[propertyPath[index]]!.AsObject();
        }

        parent.Remove(propertyPath[^1]).Should().BeTrue();
    }
}
