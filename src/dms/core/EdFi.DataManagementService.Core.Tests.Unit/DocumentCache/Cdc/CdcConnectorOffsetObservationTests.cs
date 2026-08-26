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
[Category("CdcConnectorOffsetObservation")]
public class Given_CdcConnectorOffsetObservation
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    [Test]
    public void It_accepts_postgresql_exact_source_partition_offsets_for_binding_derived_names()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        string sourcePartitionHash = CdcSourcePartitionHashCalculator
            .ComputePostgresql(inventory.ConnectorName)
            .Hash!;
        CdcConnectorOffsetObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicPrefix,
            CdcConnectorOffsetMatchResult.Exact,
            sourcePartitionHash,
            false,
            false,
            23817297,
            null,
            null,
            null,
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["connectorName"]!.GetValue<string>().Should().Be(inventory.ConnectorName);
        root["topicPrefix"]!.GetValue<string>().Should().Be("edfi.dms");
        root["sourcePartitionMatchResult"]!.GetValue<string>().Should().Be("exact");
        root["connectSourcePartitionHash"]!.GetValue<string>().Should().Be(sourcePartitionHash);
        root["isSnapshot"]!.GetValue<bool>().Should().BeFalse();
        root["lsnProc"]!.GetValue<long>().Should().Be(23817297);
        root["commitLsn"].Should().BeNull();

        CdcContractReadResult<CdcConnectorOffsetObservation> readResult =
            CdcJsonContract.Deserialize<CdcConnectorOffsetObservation>(json);
        CdcContractValidationResult validationResult =
            CdcConnectorOffsetObservationValidator.ValidateForBinding(
                readResult.Contract!,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now),
                sourcePartitionHash
            );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_sql_server_offsets_without_serializing_the_raw_database_hash_input()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        string rawCatalogName = "EdFi \"DMS\"\\CDC";
        string sourcePartitionHash = CdcSourcePartitionHashCalculator
            .ComputeSqlServer(inventory.TopicPrefix, rawCatalogName)
            .Hash!;
        CdcConnectorOffsetObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicPrefix,
            CdcConnectorOffsetMatchResult.Exact,
            sourcePartitionHash,
            false,
            false,
            null,
            "00000023:00000138:0002",
            "00000023:00000139:0001",
            2,
            []
        );

        string json = CdcJsonContract.Serialize(observation);

        json.Should().NotContain("EdFi");
        json.Should().NotContain("database");
        json.Should().NotContain("rawCatalogName");
        json.Should().Contain("connectSourcePartitionHash");

        CdcContractValidationResult validationResult =
            CdcConnectorOffsetObservationValidator.ValidateForBinding(
                observation,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now),
                sourcePartitionHash
            );

        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_negative_sql_server_event_serial_values()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        string sourcePartitionHash = CdcSourcePartitionHashCalculator
            .ComputeSqlServer(inventory.TopicPrefix, "EdFi_DMS_CDC")
            .Hash!;
        CdcConnectorOffsetObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicPrefix,
            CdcConnectorOffsetMatchResult.Exact,
            sourcePartitionHash,
            false,
            false,
            null,
            "00000023:00000138:0002",
            "00000023:00000139:0001",
            -1,
            []
        );

        CdcContractValidationResult result = CdcConnectorOffsetObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now),
            sourcePartitionHash
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                && diagnostic.Path == "$.eventSerialNo"
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("-1"));
    }

    [Test]
    public void It_rejects_non_exact_partitions_mismatched_hash_and_provider_inapplicable_offsets()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcConnectorOffsetObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            $"{inventory.ConnectorName}-wrong",
            "other.prefix",
            CdcConnectorOffsetMatchResult.Multiple,
            "sha256:9caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            true,
            true,
            null,
            "00000023:00000138:0002",
            null,
            1,
            []
        );

        CdcContractValidationResult result = CdcConnectorOffsetObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now),
            CdcSourcePartitionHashCalculator.ComputePostgresql(inventory.ConnectorName).Hash!
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.SourceMismatch)
            .And.Contain(CdcDiagnosticCategory.MalformedPayload)
            .And.Contain(CdcDiagnosticCategory.MissingRequiredField)
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
