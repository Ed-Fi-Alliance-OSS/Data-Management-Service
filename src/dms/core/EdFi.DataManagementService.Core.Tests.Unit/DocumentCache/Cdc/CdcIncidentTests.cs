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
[Category("CdcIncident")]
public class Given_CdcIncident
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
    public void It_serializes_the_source_history_incident_as_lower_camel_v1_json()
    {
        CdcIncident incident = CreateIncident(SampleBinding);

        string json = CdcJsonContract.Serialize(incident);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        CdcContractReadResult<CdcIncident> readResult = CdcJsonContract.Deserialize<CdcIncident>(json);
        CdcContractValidationResult validationResult = CdcIncidentValidator.ValidateForBinding(
            incident,
            SampleBinding,
            SampleNow
        );

        root.Select(property => property.Key)
            .Should()
            .BeEquivalentTo(
                "contractVersion",
                "incidentType",
                "latchedAt",
                "bindingIdentity",
                "failureCategory",
                "positionMetadata"
            );
        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["incidentType"]!.GetValue<string>().Should().Be("sourceHistoryContinuityLost");
        root["failureCategory"]!.GetValue<string>().Should().Be("connectOffsetMissing");
        root["bindingIdentity"]!["physicalSourceFingerprint"]!
            .GetValue<string>()
            .Should()
            .Be(SampleBinding.PhysicalSourceFingerprint);
        root["positionMetadata"]!["connectorName"]!
            .GetValue<string>()
            .Should()
            .Be(SampleBinding.ConnectorName);
        root["positionMetadata"]!["topicName"]!.GetValue<string>().Should().Be(SampleBinding.TopicName);
        root["positionMetadata"]!["connectSourcePartitionHash"]!
            .GetValue<string>()
            .Should()
            .Be("sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40");
        root["positionMetadata"]!["unavailableFacts"]![0]!.GetValue<string>().Should().Be("schemaHistory");
        json.Should().NotContain("EdFi_DMS_CDC");
        json.Should().NotContain("connectionString");
        json.Should().NotContain("credential");

        readResult.Succeeded.Should().BeTrue();
        readResult.Contract.Should().BeEquivalentTo(incident);
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_represents_and_validates_every_design_incident_failure_category()
    {
        Enum.GetValues<CdcIncidentFailureCategory>()
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    CdcIncidentFailureCategory.ProviderArtifactMissing,
                    CdcIncidentFailureCategory.ProviderArtifactRecreated,
                    CdcIncidentFailureCategory.RetainedHistoryGap,
                    CdcIncidentFailureCategory.ConnectOffsetMissing,
                    CdcIncidentFailureCategory.ConnectOffsetMalformed,
                    CdcIncidentFailureCategory.ConnectSourcePartitionMismatch,
                    CdcIncidentFailureCategory.SchemaHistoryMissing,
                    CdcIncidentFailureCategory.SchemaHistoryEmptyWithRetainedOffset,
                    CdcIncidentFailureCategory.SchemaHistoryRequiredRecordLost,
                }
            );

        foreach (CdcIncidentFailureCategory failureCategory in Enum.GetValues<CdcIncidentFailureCategory>())
        {
            CdcIncidentValidator
                .Validate(CreateIncident(SampleBinding) with { FailureCategory = failureCategory }, SampleNow)
                .Succeeded.Should()
                .BeTrue();
        }

        CdcContractValidationResult invalidCategory = CdcIncidentValidator.Validate(
            CreateIncident(SampleBinding) with
            {
                FailureCategory = (CdcIncidentFailureCategory)999,
            },
            SampleNow
        );

        invalidCategory
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Category == CdcDiagnosticCategory.InvalidEnumValue);
    }

    [Test]
    public void It_rejects_future_non_utc_mismatched_and_unsafe_incident_metadata()
    {
        CdcIncident incident = CreateIncident(SampleBinding) with
        {
            ContractVersion = 2,
            IncidentType = (CdcIncidentType)999,
            LatchedAt = SampleNow.AddSeconds(1).ToOffset(TimeSpan.FromHours(-5)),
            BindingIdentity = SampleBinding.ToCompleteBindingIdentity() with
            {
                PhysicalSourceFingerprint = "f81d4fae7dec11d0a76500a0c91e6bf6",
                ConnectorName = "ConnectorName",
            },
            FailureCategory = (CdcIncidentFailureCategory)999,
            PositionMetadata = new(
                "connector name",
                "{\"server\":\"edfi.dms\"}",
                null,
                null,
                "server:EdFi;pwd:secret",
                "{\"server\":\"edfi.dms\"}",
                "EdFi_DMS_CDC",
                null,
                null,
                -1,
                "server-name",
                "catalog-name",
                [
                    CdcIncidentUnavailableFact.ConnectOffset,
                    CdcIncidentUnavailableFact.ConnectOffset,
                    (CdcIncidentUnavailableFact)999,
                ]
            ),
        };

        CdcContractValidationResult result = CdcIncidentValidator.Validate(incident, SampleNow);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain([
                CdcDiagnosticCategory.InvalidContractVersion,
                CdcDiagnosticCategory.InvalidEnumValue,
                CdcDiagnosticCategory.MalformedPayload,
                CdcDiagnosticCategory.FutureUtcTimestamp,
            ]);
        result
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message =>
                message.Contains("pwd:secret", StringComparison.Ordinal)
                || message.Contains("EdFi_DMS_CDC", StringComparison.Ordinal)
                || message.Contains("{\"server\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_rejects_position_metadata_that_conflicts_with_the_binding_inventory()
    {
        CdcIncident incident = CreateIncident(SampleBinding) with
        {
            PositionMetadata = CreatePositionMetadata(SampleBinding) with
            {
                ProgressTopicName = "edfi.dms.instance.other-g1.documents.v1.cdc-progress",
                SchemaHistoryTopicName = "edfi.dms.instance.data-store-1-g1.documents.v1.schema-history",
                ProviderArtifactName = "edfi_dms_dms_local_data_store_1_g1_other",
            },
        };

        CdcContractValidationResult result = CdcIncidentValidator.ValidateForBinding(
            incident,
            SampleBinding,
            SampleNow
        );

        result
            .Diagnostics.Select(diagnostic => diagnostic.Path)
            .Should()
            .Contain([
                "$.positionMetadata.progressTopicName",
                "$.positionMetadata.schemaHistoryTopicName",
                "$.positionMetadata.providerArtifactName",
            ]);
    }

    private static CdcIncident CreateIncident(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            CreatePositionMetadata(binding)
        );

    private static CdcIncidentPositionMetadata CreatePositionMetadata(CdcBinding binding) =>
        new(
            binding.ConnectorName,
            binding.TopicName,
            $"{binding.TopicName}.cdc-progress",
            null,
            "edfi_dms_dms_local_data_store_1_g1_slot",
            "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40",
            "42",
            null,
            null,
            null,
            "40",
            "50",
            [CdcIncidentUnavailableFact.SchemaHistory]
        );
}
