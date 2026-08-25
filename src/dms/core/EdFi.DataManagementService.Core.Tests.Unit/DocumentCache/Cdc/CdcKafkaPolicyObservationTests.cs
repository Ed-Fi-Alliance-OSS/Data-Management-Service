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
[Category("CdcKafkaPolicyObservation")]
public class Given_CdcKafkaPolicyObservation
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    [Test]
    public void It_accepts_postgresql_topic_acl_and_record_size_policy_for_binding_derived_artifacts()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcKafkaPolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcKafkaPolicyState.Satisfied,
            "local-single-broker",
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            null,
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
            null,
            new(CdcKafkaPolicyItemState.Satisfied, 1_048_576, 1_048_576),
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["policyState"]!.GetValue<string>().Should().Be("satisfied");
        root["durabilityProfile"]!.GetValue<string>().Should().Be("local-single-broker");
        root["publicTopic"]!["topicName"]!.GetValue<string>().Should().Be(inventory.TopicName);
        root["schemaHistoryTopic"].Should().BeNull();
        root["recordSizePolicy"]!["state"]!.GetValue<string>().Should().Be("satisfied");
        json.Should().NotContain("bootstrap.servers");
        json.Should().NotContain("security.protocol");
        json.Should().NotContain("connectionString");

        CdcContractReadResult<CdcKafkaPolicyObservation> readResult =
            CdcJsonContract.Deserialize<CdcKafkaPolicyObservation>(json);
        CdcContractValidationResult validationResult = CdcKafkaPolicyObservationValidator.ValidateForBinding(
            readResult.Contract!,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_sql_server_schema_history_topic_and_acl_policy()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcKafkaPolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            CdcKafkaPolicyState.Satisfied,
            "production",
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 3, 2),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 3, 2),
            new(inventory.SchemaHistoryTopicName!, CdcKafkaPolicyItemState.Satisfied, 1, "delete", 3, 2),
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.SchemaHistoryTopicName!, CdcKafkaPolicyItemState.Satisfied),
            new(CdcKafkaPolicyItemState.Satisfied, 4_194_304, 4_194_304),
            []
        );

        string json = CdcJsonContract.Serialize(observation);

        json.Should().Contain("schemaHistoryTopic");
        json.Should().Contain("schemaHistoryTopicAcls");

        CdcContractValidationResult validationResult = CdcKafkaPolicyObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_future_provider_inapplicable_mismatched_and_inconsistent_policy_evidence()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcKafkaPolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            Now.AddSeconds(1),
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcKafkaPolicyState.Satisfied,
            "local-single-broker",
            new($"{inventory.TopicName}-wrong", CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Unknown, null, null, null, null),
            new(
                $"{inventory.TopicName}.schema-history",
                CdcKafkaPolicyItemState.Satisfied,
                1,
                "delete",
                1,
                1
            ),
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
            new($"{inventory.TopicName}.schema-history", CdcKafkaPolicyItemState.Satisfied),
            new(CdcKafkaPolicyItemState.Satisfied, 2_000_000, 1_000_000),
            []
        );

        CdcContractValidationResult result = CdcKafkaPolicyObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch);
    }

    private static CdcBinding CreateBinding(CdcProvider provider)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider))
            .Inventory!;

        return new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            provider,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );
    }
}
